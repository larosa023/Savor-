using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Savor.Core;

public enum AudioExport { AllTracks, MixOnly, Selected, None }

public sealed class ExportRequest
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    /// <summary>Crop in source pixels, or null for the full frame.</summary>
    public System.Windows.Int32Rect? Crop { get; init; }
    public AudioExport Audio { get; init; } = AudioExport.AllTracks;
    /// <summary>For <see cref="AudioExport.Selected"/>: audio track indices (mixed into one track, or copied as-is for fast export).</summary>
    public int[] Tracks { get; init; } = Array.Empty<int>();
    /// <summary>Stream copy: instant and lossless, but cuts land on the nearest keyframe (≤2 s).</summary>
    public bool Fast { get; init; }
    /// <summary>Playback speed of the result (0.25–2). Needs re-encoding when not 1.</summary>
    public double Speed { get; init; } = 1;
    /// <summary>Audio sync fix: shift the audio by this many seconds (positive = later). Needs re-encoding.</summary>
    public double AudioOffset { get; init; }
    /// <summary>Make an animated GIF (no audio) instead of a video.</summary>
    public bool Gif { get; init; }
    /// <summary>Keep the file at or under this many bytes (Discord's upload limits); 0 = any size.</summary>
    public long TargetBytes { get; init; }
}

/// <summary>How a clip will be encoded to fit a size limit.</summary>
public sealed record SizePlan(int Height, double Fps, int VideoKbps, int AudioKbps)
{
    public string Describe(int srcW, int srcH)
    {
        int w = srcH > 0 ? (int)Math.Round(srcW * (double)Height / srcH / 2) * 2 : 0;
        return $"{(w > 0 ? $"{w}×{Height}" : $"{Height}p")} at {Fps:0} fps, {VideoKbps / 1000.0:0.0} Mbps";
    }
}

/// <summary>Trims / crops a clip with the bundled ffmpeg, using the GPU encoder when available.</summary>
public static class ClipExporter
{
    public static async Task ExportAsync(ExportRequest r, EncoderDef? encoder, QualityPreset quality,
                                         IProgress<double>? progress, CancellationToken ct)
    {
        if (Ffmpeg.Path == null) throw new InvalidOperationException("FFmpeg isn't available.");
        if (r.End - r.Start <= TimeSpan.Zero) throw new InvalidOperationException("The selection is empty.");
        var duration = TimeSpan.FromTicks((long)((r.End - r.Start).Ticks / Math.Clamp(r.Speed, 0.1, 10))); // length of the result

        var tmp = Path.Combine(Path.GetDirectoryName(r.Output)!, $".savor-export-{Guid.NewGuid():N}{Path.GetExtension(r.Output)}");
        try
        {
            if (r.Gif)
            {
                await RunAsync(BuildGifArgs(r, tmp), duration, progress, ct);
            }
            else if (r.TargetBytes > 0)
            {
                await ExportToSizeAsync(r, encoder, tmp, duration, progress, ct);
            }
            else if (r.Fast && Math.Abs(r.Speed - 1) < 0.001 && Math.Abs(r.AudioOffset) < 0.0005)
            {
                await RunAsync(BuildArgs(r, null, quality, tmp), duration, progress, ct);
            }
            else
            {
                var enc = encoder ?? Ffmpeg.Encoders[^1];
                try
                {
                    await RunAsync(BuildArgs(r, enc, quality, tmp), duration, progress, ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested && enc.Vendor != "cpu")
                {
                    // GPU encoder refused this input (size limits, driver) — fall back to the CPU encoder.
                    progress?.Report(0);
                    await RunAsync(BuildArgs(r, Ffmpeg.Encoders[^1], quality, tmp), duration, progress, ct);
                }
            }
            File.Move(tmp, r.Output, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    // ---------- fit to a size limit (Discord) ----------

    /// <summary>Discord's upload limits (MiB): free, Nitro Basic, Nitro. Raised to 20 MB for free accounts in August 2026.</summary>
    public static readonly (string Name, int Mb)[] DiscordLimits = { ("Discord", 20), ("Discord Nitro Basic", 50), ("Discord Nitro", 500) };

    const double SizeSafety = 0.95;    // container overhead and encoder overshoot
    const double MinBitsPerPixel = 0.07; // below this H.264 gets visibly blocky in fast game footage

    /// <summary>
    /// Picks the sharpest resolution / frame rate that still gets enough bits per pixel at the bitrate the limit
    /// allows. Keeps 60 fps down to 720p before dropping to 30. Null when even 360p at 30 fps won't fit.
    /// </summary>
    public static SizePlan? PlanForSize(long targetBytes, double seconds, int srcW, int srcH, double srcFps)
    {
        if (seconds <= 0 || srcW <= 0 || srcH <= 0) return null;
        double totalKbps = targetBytes * 8 / 1000.0 * SizeSafety / seconds;
        int audio = totalKbps > 1500 ? 128 : totalKbps > 600 ? 96 : 64;
        double video = totalKbps - audio;
        if (video < 150) return null;
        double fpsTop = srcFps > 0 ? Math.Min(60, srcFps) : 60;
        var ladder = new List<(int h, double fps)>();
        foreach (var h in new[] { srcH, 1440, 1080, 720 }) if (h <= srcH) ladder.Add((h, fpsTop));
        foreach (var h in new[] { 1080, 720, 540, 480, 360 }) if (h <= srcH) ladder.Add((h, Math.Min(30, fpsTop)));
        if (ladder.Count == 0) ladder.Add((srcH, Math.Min(30, fpsTop)));
        foreach (var (h, fps) in ladder.Distinct())
        {
            double w = srcW * (double)h / srcH;
            if (video * 1000 / (w * h * fps) >= MinBitsPerPixel) return new SizePlan(h, fps, (int)video, audio);
        }
        // the smallest size, even if under the ideal bits per pixel, as long as it's not hopeless
        var (lh, lf) = ladder[^1];
        double lw = srcW * (double)lh / srcH;
        return video * 1000 / (lw * lh * lf) >= MinBitsPerPixel / 3 ? new SizePlan(lh, lf, (int)video, audio) : null;
    }

    /// <summary>The longest selection (in seconds, of the result) that can still fit.</summary>
    public static double MaxSecondsForSize(long targetBytes, int srcW, int srcH)
    {
        double lo = 1, hi = 36000;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            if (PlanForSize(targetBytes, mid, srcW, srcH, 30) != null) lo = mid; else hi = mid;
        }
        return lo;
    }

    static async Task<(int w, int h, double fps)> ProbeVideoAsync(string path, CancellationToken ct)
    {
        var (_, err) = await Ffmpeg.RunAsync(new[] { "-hide_banner", "-nostdin", "-i", path }, TimeSpan.FromSeconds(15), ct);
        var line = err.Split('\n').FirstOrDefault(l => l.Contains("Video:")) ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(line, @", (\d{2,5})x(\d{2,5})");
        var f = System.Text.RegularExpressions.Regex.Match(line, @"([\d.]+) fps");
        return (m.Success ? int.Parse(m.Groups[1].Value) : 0, m.Success ? int.Parse(m.Groups[2].Value) : 0,
                f.Success ? double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture) : 60);
    }

    /// <summary>H.264 (plays everywhere Discord does) with an average bitrate, from the same vendor as the usual encoder.</summary>
    static EncoderDef H264For(EncoderDef? preferred)
    {
        var all = Ffmpeg.Encoders;
        return all.FirstOrDefault(e => e.Codec == "h264" && e.Vendor == preferred?.Vendor)
            ?? all.FirstOrDefault(e => e.Codec == "h264" && e.Vendor == "cpu")
            ?? all[^1];
    }

    static string[] BitrateArgs(EncoderDef enc, int kbps)
    {
        string b = $"{kbps}k", max = $"{(int)(kbps * 1.3)}k", buf = $"{kbps * 2}k";
        return enc.Vendor switch
        {
            "nvidia" => new[] { "-c:v", enc.FfmpegName, "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres",
                                "-b:v", b, "-maxrate", max, "-bufsize", buf, "-profile:v", "high" },
            "amd" => new[] { "-c:v", enc.FfmpegName, "-quality", "quality", "-rc", "vbr_peak", "-b:v", b, "-maxrate", max, "-bufsize", buf },
            "intel" => new[] { "-c:v", enc.FfmpegName, "-preset", "slower", "-b:v", b, "-maxrate", max, "-bufsize", buf },
            _ => new[] { "-c:v", enc.FfmpegName, "-preset", "medium", "-b:v", b, "-maxrate", max, "-bufsize", buf, "-profile:v", "high" },
        };
    }

    static async Task ExportToSizeAsync(ExportRequest r, EncoderDef? preferred, string tmp, TimeSpan duration,
                                       IProgress<double>? progress, CancellationToken ct)
    {
        var (w, h, fps) = await ProbeVideoAsync(r.Input, ct);
        if (r.Crop is { } c) { w = c.Width; h = c.Height; }
        var plan = PlanForSize(r.TargetBytes, duration.TotalSeconds, w, h, fps)
            ?? throw new InvalidOperationException(
                $"Too long to fit in {r.TargetBytes / 1048576} MB. Trim it to under {FormatSeconds(MaxSecondsForSize(r.TargetBytes, w, h))}.");
        var enc = H264For(preferred);
        int video = plan.VideoKbps;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            progress?.Report(0);
            try { await RunAsync(BuildArgs(r, enc, QualityPreset.High, tmp, plan with { VideoKbps = video }, h), duration, progress, ct); }
            catch (Exception) when (!ct.IsCancellationRequested && enc.Vendor != "cpu")
            {
                enc = H264For(null); // GPU encoder refused: CPU
                await RunAsync(BuildArgs(r, enc, QualityPreset.High, tmp, plan with { VideoKbps = video }, h), duration, progress, ct);
            }
            long size = new FileInfo(tmp).Length;
            if (size <= r.TargetBytes) return;
            // over: aim lower by the overshoot, plus a margin
            video = (int)(video * (double)r.TargetBytes / size * 0.93) - plan.AudioKbps / 4;
            if (video < 100) break;
        }
        throw new InvalidOperationException($"Couldn't get it under {r.TargetBytes / 1048576} MB. Try a shorter selection.");
    }

    static string FormatSeconds(double s) => s >= 60 ? $"{(int)(s / 60)}:{(int)(s % 60):00}" : $"{s:0} seconds";

    static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Shifts audio in time (fixes clips whose sound is out of sync). Positive = audio later, negative = earlier.
    /// Null when no shift is needed.
    /// </summary>
    public static string? AudioShift(double seconds)
    {
        if (Math.Abs(seconds) < 0.0005) return null;
        var v = Math.Abs(seconds).ToString("0.###", CultureInfo.InvariantCulture);
        return seconds > 0
            ? $"adelay={Math.Round(seconds * 1000)}:all=1"
            : $"atrim=start={v},asetpts=PTS-STARTPTS";
    }

    /// <summary>atempo only goes from 0.5× to 2×, so slower speeds chain it.</summary>
    static string Tempo(double speed)
    {
        var parts = new List<string>();
        while (speed < 0.5) { parts.Add("atempo=0.5"); speed /= 0.5; }
        while (speed > 2) { parts.Add("atempo=2"); speed /= 2; }
        parts.Add("atempo=" + speed.ToString("0.###", CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    static string SpeedFilter(double speed) =>
        $"setpts=PTS/{speed.ToString("0.###", CultureInfo.InvariantCulture)}";

    /// <summary>Animated GIF: 15 fps, up to 640 px wide, with its own colour palette for a clean look.</summary>
    static List<string> BuildGifArgs(ExportRequest r, string output)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        a.AddRange(new[] { "-ss", Sec(r.Start), "-t", Sec(r.End - r.Start), "-i", r.Input, "-map", "0:v:0", "-an" });
        var f = new List<string>();
        if (r.Crop is { } c) f.Add($"crop={c.Width}:{c.Height}:{c.X}:{c.Y}");
        if (Math.Abs(r.Speed - 1) > 0.001) f.Add(SpeedFilter(r.Speed));
        f.Add("fps=15");
        f.Add("scale='min(640,iw)':-2:flags=lanczos");
        a.AddRange(new[] { "-vf", string.Join(",", f) + ",split[g0][g1];[g0]palettegen=stats_mode=diff[gp];[g1][gp]paletteuse=dither=bayer:bayer_scale=4",
                           "-loop", "0", "-f", "gif", output });
        return a;
    }

    static List<string> BuildArgs(ExportRequest r, EncoderDef? enc, QualityPreset quality, string output,
                                  SizePlan? plan = null, int srcH = 0)
    {
        if (plan != null && r.Audio == AudioExport.AllTracks)
            r = new ExportRequest // one track (the mix): what Discord plays, and fewer bits
            {
                Input = r.Input, Output = r.Output, Start = r.Start, End = r.End, Crop = r.Crop, Audio = AudioExport.MixOnly,
                Tracks = r.Tracks, Speed = r.Speed, AudioOffset = r.AudioOffset, TargetBytes = r.TargetBytes
            };
        bool speed = Math.Abs(r.Speed - 1) > 0.001 && enc != null;
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        // -t as an input option limits what is read, so a slowed-down result isn't cut short
        a.AddRange(new[] { "-ss", Sec(r.Start), "-t", Sec(r.End - r.Start), "-i", r.Input });
        a.AddRange(new[] { "-map", "0:v:0" });
        switch (r.Audio)
        {
            case AudioExport.AllTracks: a.AddRange(new[] { "-map", "0:a?" }); break;
            case AudioExport.MixOnly: a.AddRange(new[] { "-map", "0:a:0?" }); break;
            case AudioExport.Selected when r.Tracks.Length == 1 || enc == null:
                foreach (var t in r.Tracks) a.AddRange(new[] { "-map", $"0:a:{t}" });
                break;
            case AudioExport.Selected:
                var inputs = string.Concat(r.Tracks.Select(t => $"[0:a:{t}]"));
                var shiftM = enc != null ? AudioShift(r.AudioOffset) : null;
                a.AddRange(new[] { "-filter_complex", $"{inputs}amix=inputs={r.Tracks.Length}:normalize=0{(shiftM != null ? "," + shiftM : "")}{(speed ? "," + Tempo(r.Speed) : "")}[am]", "-map", "[am]" });
                break;
        }

        if (enc == null)
        {
            a.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero" });
        }
        else
        {
            var filters = new List<string>();
            if (r.Crop is { } c) filters.Add($"crop={c.Width}:{c.Height}:{c.X}:{c.Y}");
            if (speed) filters.Add(SpeedFilter(r.Speed));
            if (plan != null)
            {
                int ch = r.Crop?.Height ?? srcH;
                if (ch <= 0 || plan.Height < ch) filters.Add($"scale=-2:{plan.Height}:flags=lanczos");
                filters.Add($"fps={plan.Fps.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
            filters.Add(enc.Vendor == "cpu" ? "format=yuv420p" : "format=nv12");
            a.AddRange(new[] { "-vf", string.Join(",", filters) });
            var q = quality < QualityPreset.High ? QualityPreset.High : quality;
            a.AddRange(plan != null ? BitrateArgs(enc, plan.VideoKbps) : Ffmpeg.EncoderArgs(enc, q, 60, live: false));
            if (r.Audio != AudioExport.None)
                a.AddRange(plan != null ? new[] { "-c:a", "aac", "-b:a", $"{plan.AudioKbps}k", "-ac", "2" } : new[] { "-c:a", "aac", "-b:a", "192k" });
            // each kept track gets the same tempo change (the mixed "selected" track already has it)
            var af = new List<string>();
            if (AudioShift(r.AudioOffset) is { } shift) af.Add(shift);
            if (speed) af.Add(Tempo(r.Speed));
            if (af.Count > 0 && r.Audio != AudioExport.None && !(r.Audio == AudioExport.Selected && r.Tracks.Length > 1))
                a.AddRange(new[] { "-af", string.Join(",", af) });
        }
        if (r.Audio == AudioExport.None) a.Add("-an");
        if (output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            a.AddRange(new[] { "-movflags", "+faststart" });
            if (enc?.Codec == "hevc") a.AddRange(new[] { "-tag:v", "hvc1" });
        }
        a.Add(output);
        return a;
    }

    internal static async Task RunAsync(List<string> args, TimeSpan duration, IProgress<double>? progress, CancellationToken ct)
    {
        using var p = new Process { StartInfo = Ffmpeg.StartInfo(args, redirectInput: true) };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.OutputDataReceived += (_, e) =>
        {
            // -progress lines: out_time_us=12345678
            if (e.Data != null && e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(e.Data.AsSpan(12), out var us) && duration.TotalMilliseconds > 0)
                progress?.Report(Math.Clamp(us / 1000.0 / duration.TotalMilliseconds, 0, 1));
        };
        p.Start();
        ChildProcesses.Track(p);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string msg;
            lock (err) msg = err.ToString().Trim();
            var last = msg.Split('\n')[^1].Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(last) ? "Export failed." : "Export failed: " + last);
        }
        progress?.Report(1);
    }

    public static string NextOutputPath(string input, string? extension = null)
    {
        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        var ext = extension ?? Path.GetExtension(input);
        var path = Path.Combine(dir, $"{name} (edit){ext}");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{name} (edit {n}){ext}");
        return path;
    }
}
