using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Savor.Core;

/// <summary>How frames get from the screen into the encoder.</summary>
public enum CapturePipeline
{
    /// <summary>Desktop Duplication, GPU frames handed straight to a hardware encoder.</summary>
    DdaDirect,
    /// <summary>Desktop Duplication, frames copied to system memory first.</summary>
    DdaDownload,
    /// <summary>Legacy GDI capture. Slow, last resort.</summary>
    Gdi
}

public sealed record EncoderDef(string Id, string Name, string Codec, string FfmpegName, string Vendor);

public sealed record EncoderChoice(EncoderDef Def, CapturePipeline Pipeline)
{
    public string Key => $"{Def.Id}:{Pipeline}";
    public string Name => Def.Name;
}

public static class Ffmpeg
{
    public static string? Path { get; private set; }

    const string EmbeddedName = "ffmpeg.exe.gz";

    /// <summary>
    /// Finds ffmpeg: the copy embedded in Savor.exe (unpacked once to %LOCALAPPDATA%\Savor\ffmpeg),
    /// otherwise one next to the exe or on PATH.
    /// </summary>
    public static async Task<bool> EnsureAsync()
    {
        var embedded = await Task.Run(ExtractEmbedded);
        if (embedded != null) { Path = embedded; return true; }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            System.IO.Path.Combine(baseDir, "ffmpeg.exe"),
            System.IO.Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            System.IO.Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"),
        };
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(System.IO.Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe")); } catch { }
        }
        Path = candidates.FirstOrDefault(File.Exists);
        return Path != null;
    }

    public static bool HasEmbedded =>
        typeof(Ffmpeg).Assembly.GetManifestResourceInfo(EmbeddedName) != null;

    static string? ExtractEmbedded()
    {
        try
        {
            var asm = typeof(Ffmpeg).Assembly;
            using var res = asm.GetManifestResourceStream(EmbeddedName);
            if (res == null) return null;

            // One folder per embedded build, so updating Savor also updates ffmpeg.
            var dir = System.IO.Path.Combine(AppSettings.LocalDataDir, "ffmpeg", $"{res.Length:x}");
            var exe = System.IO.Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(exe)) return exe;

            Directory.CreateDirectory(dir);
            var tmp = exe + ".partial";
            using (var gz = new System.IO.Compression.GZipStream(res, System.IO.Compression.CompressionMode.Decompress))
            using (var fs = File.Create(tmp))
                gz.CopyTo(fs);
            File.Move(tmp, exe, true);

            // remove older unpacked versions
            foreach (var old in Directory.GetDirectories(System.IO.Path.GetDirectoryName(dir)!))
                if (!string.Equals(old, dir, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(old, true); } catch { }
            return exe;
        }
        catch { return null; }
    }

    /// <summary>Identifies the ffmpeg build so cached encoder probes can be invalidated.</summary>
    public static string Stamp()
    {
        if (Path == null) return "";
        var fi = new FileInfo(Path);
        return $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
    }

    public static ProcessStartInfo StartInfo(IEnumerable<string> args, bool redirectInput = false)
    {
        var psi = new ProcessStartInfo(Path ?? "ffmpeg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = redirectInput,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs ffmpeg to completion. Returns exit code (or -1 on timeout) and stderr.</summary>
    public static async Task<(int code, string stderr)> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = StartInfo(args) };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.OutputDataReceived += (_, _) => { };
        p.Start();
        ChildProcesses.Track(p);
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            lock (err) return (-1, err.ToString());
        }
        lock (err) return (p.ExitCode, err.ToString());
    }

    // ---------------- encoders ----------------

    public static readonly EncoderDef[] Encoders =
    {
        new("nvenc_h264", "NVIDIA NVENC · H.264", "h264", "h264_nvenc", "nvidia"),
        new("amf_h264",   "AMD AMF · H.264",      "h264", "h264_amf",   "amd"),
        new("qsv_h264",   "Intel Quick Sync · H.264", "h264", "h264_qsv", "intel"),
        new("nvenc_hevc", "NVIDIA NVENC · HEVC",  "hevc", "hevc_nvenc", "nvidia"),
        new("amf_hevc",   "AMD AMF · HEVC",       "hevc", "hevc_amf",   "amd"),
        new("qsv_hevc",   "Intel Quick Sync · HEVC", "hevc", "hevc_qsv", "intel"),
        new("nvenc_av1",  "NVIDIA NVENC · AV1",   "av1",  "av1_nvenc",  "nvidia"),
        new("x264",       "CPU · x264",           "h264", "libx264",    "cpu"),
    };

    /// <summary>Video filter needed between capture and encoder for a given pipeline.</summary>
    public static string? VideoFilter(EncoderChoice c)
    {
        bool cpu = c.Def.Vendor == "cpu";
        return c.Pipeline switch
        {
            CapturePipeline.DdaDirect when c.Def.Vendor == "intel" => "hwmap=derive_device=qsv,format=qsv",
            CapturePipeline.DdaDirect => null,
            CapturePipeline.DdaDownload => cpu ? "hwdownload,format=bgra,format=yuv420p" : "hwdownload,format=bgra,format=nv12",
            CapturePipeline.Gdi => cpu ? "format=yuv420p" : "format=nv12",
            _ => null
        };
    }

    /// <param name="live">true for screen capture: keep the CPU encoder's memory small and steady.</param>
    /// <summary>
    /// Output size for a "record at 1080p" style setting: same aspect as the display, even numbers.
    /// Null when recording at the display's own size (setting 0 or not smaller than the display).
    /// </summary>
    public static (int W, int H)? OutputSize(DisplayInfo? d, int targetHeight)
    {
        if (d == null || targetHeight <= 0 || targetHeight >= d.Height || d.Width <= 0 || d.Height <= 0) return null;
        int w = (int)Math.Round(d.Width * (double)targetHeight / d.Height / 2) * 2;
        int h = targetHeight / 2 * 2;
        return (w, h);
    }

    /// <summary>
    /// Video filter including scaling to <paramref name="size"/>. With <paramref name="gpuScale"/> the GPU resizes
    /// frames before they reach the hardware encoder (no CPU work); otherwise frames are copied to system memory and
    /// resized there, which works with every encoder.
    /// </summary>
    public static string? VideoFilter(EncoderChoice c, (int W, int H)? size, bool gpuScale)
    {
        if (size is not { } sz) return VideoFilter(c);
        bool cpu = c.Def.Vendor == "cpu";
        string pix = cpu ? "yuv420p" : "nv12";
        string scale = $"scale={sz.W}:{sz.H}:flags=bicubic";
        return c.Pipeline switch
        {
            CapturePipeline.Gdi => $"{scale},format={pix}",
            CapturePipeline.DdaDirect when gpuScale && c.Def.Vendor == "intel" => $"scale_d3d11={sz.W}:{sz.H},hwmap=derive_device=qsv,format=qsv",
            CapturePipeline.DdaDirect when gpuScale => $"scale_d3d11={sz.W}:{sz.H}",
            _ => $"hwdownload,format=bgra,{scale},format={pix}",
        };
    }

    /// <summary>Checks that the GPU can resize frames straight into this encoder (driver dependent).</summary>
    public static async Task<bool> TestGpuScaleAsync(EncoderChoice c, (int W, int H) size)
    {
        if (c.Pipeline != CapturePipeline.DdaDirect) return false;
        return (await TestAsync(c, VideoFilter(c, size, gpuScale: true))).ok;
    }

    public static List<string> EncoderArgs(EncoderDef e, QualityPreset q, int fps, bool live = true)
    {
        int idx = (int)q; // Low, Medium, High, Ultra
        var a = new List<string> { "-c:v", e.FfmpegName };
        switch (e.Vendor)
        {
            case "nvidia":
                int cq = new[] { 32, 27, 23, 19 }[idx];
                a.AddRange(new[] { "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", cq.ToString(), "-b:v", "0" });
                break;
            case "amd":
                int qp = new[] { 30, 26, 22, 18 }[idx];
                a.AddRange(new[] { "-usage", "transcoding", "-quality", "balanced", "-rc", "cqp",
                                   "-qp_i", qp.ToString(), "-qp_p", qp.ToString() });
                if (e.Codec == "h264") a.AddRange(new[] { "-qp_b", qp.ToString() });
                break;
            case "intel":
                int gq = new[] { 30, 26, 22, 18 }[idx];
                a.AddRange(new[] { "-preset", "faster", "-global_quality", gq.ToString() });
                break;
            default:
                int crf = new[] { 28, 24, 21, 18 }[idx];
                a.AddRange(new[] { "-preset", "veryfast", "-crf", crf.ToString(), "-pix_fmt", "yuv420p" });
                // x264 by default runs ~1.5 frame threads per CPU core, each holding several full frames:
                // at 1440p that is ~1 GB with 24 threads and ~1.75 GB with 48 (measured), before any queueing.
                // Live capture uses sliced threads instead: ~250 MB at 1440p whatever the core count, and the
                // same file size (a short lookahead is kept; -tune zerolatency would nearly double file size).
                if (live)
                    a.AddRange(new[] { "-threads", Math.Clamp(Environment.ProcessorCount, 2, 16).ToString(),
                                       "-x264-params", "sliced-threads=1:rc-lookahead=10:sync-lookahead=0" });
                break;
        }
        // Keyframe every 2 seconds so the replay buffer can cut cleanly on segment boundaries.
        a.AddRange(new[] { "-g", (fps * 2).ToString(), "-bf", "0" });
        return a;
    }

    static IEnumerable<CapturePipeline> PipelinesFor(EncoderDef e) =>
        e.Vendor == "cpu"
            ? new[] { CapturePipeline.DdaDownload }
            : new[] { CapturePipeline.DdaDirect, CapturePipeline.DdaDownload };

    /// <summary>Why GPU encoders failed in the last probe (vendor → message), for the settings page.</summary>
    public static Dictionary<string, string> LastProbeFailures { get; } = new();

    /// <summary>Tries each encoder with a few real captured frames to see what this machine supports.</summary>
    public static async Task<List<EncoderChoice>> ProbeAsync(IProgress<string>? progress = null)
    {
        var working = new List<EncoderChoice>();
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var gate = new SemaphoreSlim(2); // consumer GPUs limit concurrent encode sessions
        var tasks = Encoders.Select(async def =>
        {
            await gate.WaitAsync();
            try
            {
                foreach (var pipe in PipelinesFor(def))
                {
                    var choice = new EncoderChoice(def, pipe);
                    progress?.Report($"Testing {def.Name}…");
                    var (ok, err) = await TestAsync(choice);
                    if (ok) return choice;
                    if (def.Codec == "h264") errors[def.Vendor] = err;
                }
                return null;
            }
            finally { gate.Release(); }
        }).ToList();

        foreach (var r in await Task.WhenAll(tasks))
            if (r != null) working.Add(r);

        if (!working.Any(w => w.Def.Vendor == "cpu"))
        {
            // Desktop Duplication unavailable (e.g. remote desktop) -> GDI fallback.
            var gdi = new EncoderChoice(Encoders.First(e => e.Vendor == "cpu"), CapturePipeline.Gdi);
            if ((await TestAsync(gdi)).ok) working.Add(gdi);
        }

        lock (LastProbeFailures)
        {
            LastProbeFailures.Clear();
            foreach (var (vendor, err) in errors)
                if (!working.Any(w => w.Def.Vendor == vendor)) LastProbeFailures[vendor] = err;
        }

        // keep definition order
        return working.OrderBy(w => Array.IndexOf(Encoders, w.Def)).ToList();
    }

    static async Task<(bool ok, string err)> TestAsync(EncoderChoice c, string? filterOverride = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (c.Pipeline == CapturePipeline.Gdi)
            args.AddRange(new[] { "-f", "gdigrab", "-framerate", "30", "-video_size", "640x360", "-i", "desktop" });
        else
            args.AddRange(new[] { "-f", "lavfi", "-i", "ddagrab=framerate=30" });
        args.AddRange(new[] { "-frames:v", "8" });
        var vf = filterOverride ?? VideoFilter(c);
        if (vf != null) args.AddRange(new[] { "-vf", vf });
        args.AddRange(EncoderArgs(c.Def, QualityPreset.High, 30));
        args.AddRange(new[] { "-f", "null", "-" });
        try
        {
            var (code, err) = await RunAsync(args, TimeSpan.FromSeconds(15));
            return (code == 0, code == -1 ? "timed out" : err.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Plain-language reason a GPU encoder couldn't be used, or null when the GPU simply isn't there.
    /// </summary>
    public static string? ExplainFailure(string vendor, string err, IReadOnlyList<GpuInfo.Adapter> gpus)
    {
        bool has = gpus.Any(g => g.Vendor == vendor);
        string e = err.ToLowerInvariant();
        switch (vendor)
        {
            case "nvidia":
                if (!has) return null;
                if (e.Contains("required nvenc api version") || e.Contains("driver does not support"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(err, @"[Mm]inimum required Nvidia driver for nvenc is ([\d.]+)");
                    return $"NVIDIA: your graphics driver is too old for GPU recording. Update it{(m.Success ? $" (to {m.Groups[1].Value} or newer)" : "")} from nvidia.com or the NVIDIA app.";
                }
                if (e.Contains("nvcuda") || e.Contains("cannot load"))
                    return "NVIDIA: the graphics driver isn't fully installed. Reinstall it from nvidia.com or the NVIDIA app.";
                if (e.Contains("no capable devices") || e.Contains("unsupported device"))
                    return "NVIDIA: this graphics card doesn't support NVENC hardware encoding.";
                if (e.Contains("incompatible client key") || e.Contains("out of memory") || e.Contains("too many"))
                    return "NVIDIA: the encoder is busy (another app may be using all encode sessions). Close it, then re-detect.";
                return "NVIDIA: GPU encoding couldn't start. Updating your graphics driver usually fixes this.";
            case "amd":
                if (!has) return null;
                if (e.Contains("amfrt64") || e.Contains("failed to open") || e.Contains("version"))
                    return "AMD: your graphics driver is missing or too old for GPU recording. Update it from amd.com (Adrenalin).";
                return "AMD: GPU encoding couldn't start. Updating your graphics driver usually fixes this.";
            case "intel":
                if (!has || gpus.Any(g => g.Vendor is "nvidia" or "amd")) return null; // QSV only matters without a dedicated GPU
                return "Intel: Quick Sync couldn't start. Updating your Intel graphics driver usually fixes this.";
        }
        return null;
    }

    public static List<EncoderChoice> FromCache(EncoderCache cache)
    {
        var list = new List<EncoderChoice>();
        foreach (var key in cache.Working)
        {
            var parts = key.Split(':');
            if (parts.Length != 2) continue;
            var def = Encoders.FirstOrDefault(e => e.Id == parts[0]);
            if (def == null || !Enum.TryParse<CapturePipeline>(parts[1], out var pipe)) continue;
            list.Add(new EncoderChoice(def, pipe));
        }
        return list;
    }

    /// <summary>Picks the encoder to use: the user's choice if it works, else the best H.264 option.</summary>
    public static EncoderChoice? Pick(List<EncoderChoice> available, string preferredId)
    {
        if (preferredId != "auto")
        {
            var c = available.FirstOrDefault(a => a.Def.Id == preferredId);
            if (c != null) return c;
        }
        return available.FirstOrDefault(a => a.Def.Codec == "h264") ?? available.FirstOrDefault();
    }
}
