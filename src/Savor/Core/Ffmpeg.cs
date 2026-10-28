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

    public static bool Locate()
    {
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

    public static List<string> EncoderArgs(EncoderDef e, QualityPreset q, int fps)
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

    /// <summary>Tries each encoder with a few real captured frames to see what this machine supports.</summary>
    public static async Task<List<EncoderChoice>> ProbeAsync(IProgress<string>? progress = null)
    {
        var working = new List<EncoderChoice>();
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
                    if (await TestAsync(choice))
                        return choice;
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
            if (await TestAsync(gdi)) working.Add(gdi);
        }

        // keep definition order
        return working.OrderBy(w => Array.IndexOf(Encoders, w.Def)).ToList();
    }

    static async Task<bool> TestAsync(EncoderChoice c)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (c.Pipeline == CapturePipeline.Gdi)
            args.AddRange(new[] { "-f", "gdigrab", "-framerate", "30", "-video_size", "640x360", "-i", "desktop" });
        else
            args.AddRange(new[] { "-f", "lavfi", "-i", "ddagrab=framerate=30" });
        args.AddRange(new[] { "-frames:v", "8" });
        var vf = VideoFilter(c);
        if (vf != null) args.AddRange(new[] { "-vf", vf });
        args.AddRange(EncoderArgs(c.Def, QualityPreset.High, 30));
        args.AddRange(new[] { "-f", "null", "-" });
        try
        {
            var (code, _) = await RunAsync(args, TimeSpan.FromSeconds(15));
            return code == 0;
        }
        catch { return false; }
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
