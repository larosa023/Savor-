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
    /// <summary>When set, re-encode so the file fits under this many bytes (e.g. Discord's 10 MB).</summary>
    public long TargetBytes { get; init; }
    /// <summary>Height of the picture being exported (after crop), used to pick a sensible size for small targets.</summary>
    public int SourceHeight { get; init; }
    /// <summary>Stream copy: instant and lossless, but cuts land on the nearest keyframe (≤2 s).</summary>
    public bool Fast { get; init; }
}

/// <summary>Trims / crops a clip with the bundled ffmpeg, using the GPU encoder when available.</summary>
public static class ClipExporter
{
    public static async Task ExportAsync(ExportRequest r, EncoderDef? encoder, QualityPreset quality,
                                         IProgress<double>? progress, CancellationToken ct)
    {
        if (Ffmpeg.Path == null) throw new InvalidOperationException("FFmpeg isn't available.");
        var duration = r.End - r.Start;
        if (duration <= TimeSpan.Zero) throw new InvalidOperationException("The selection is empty.");

        var tmp = Path.Combine(Path.GetDirectoryName(r.Output)!, $".savor-export-{Guid.NewGuid():N}{Path.GetExtension(r.Output)}");
        try
        {
            if (r.TargetBytes > 0)
            {
                await ExportToSizeAsync(r, encoder, tmp, duration, progress, ct);
            }
            else if (r.Fast)
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

    // ===================== fit-to-size (Discord) =====================

    sealed record SizePlan(int VideoKbps, int AudioKbps, int? ScaleHeight);

    /// <summary>Picks bitrates (and a smaller resolution when the budget is tight) to land under the target size.</summary>
    static SizePlan PlanFor(ExportRequest r, TimeSpan duration, double budgetFactor)
    {
        double secs = Math.Max(0.5, duration.TotalSeconds);
        double totalKbps = r.TargetBytes * 8 / 1000.0 / secs * budgetFactor;
        int audio = r.Audio == AudioExport.None ? 0 : totalKbps > 1500 ? 128 : totalKbps > 600 ? 96 : 64;
        int video = (int)(totalKbps - audio);
        if (video < 120)
            throw new InvalidOperationException(
                $"Too long to fit in {r.TargetBytes / 1_000_000} MB with watchable quality — trim it to about {MaxSeconds(r)} seconds or less.");
        // fewer bits per second → fewer pixels, so it stays sharp instead of blocky
        int maxH = video >= 5000 ? 1440 : video >= 2500 ? 1080 : video >= 1200 ? 720 : video >= 600 ? 540 : 360;
        int? scale = r.SourceHeight > maxH ? maxH : null;
        return new SizePlan(video, audio, scale);
    }

    static int MaxSeconds(ExportRequest r) => (int)(r.TargetBytes * 8 / 1000.0 * 0.9 / (120 + 64));

    static async Task ExportToSizeAsync(ExportRequest r, EncoderDef? preferred, string tmp, TimeSpan duration,
                                        IProgress<double>? progress, CancellationToken ct)
    {
        // H.264 plays everywhere (Discord's embedded player included); prefer the same GPU vendor's H.264 encoder
        var enc = preferred is { Codec: "h264" } ? preferred
                : Ffmpeg.Encoders.FirstOrDefault(e => e.Codec == "h264" && e.Vendor == preferred?.Vendor) ?? Ffmpeg.Encoders[^1];
        double budget = 0.92; // leave room for container overhead and rate-control wobble
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var plan = PlanFor(r, duration, budget);
            try
            {
                await RunAsync(BuildSizeArgs(r, enc, plan, tmp), duration, progress, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested && enc.Vendor != "cpu")
            {
                enc = Ffmpeg.Encoders[^1]; // GPU refused: CPU x264
                progress?.Report(0);
                await RunAsync(BuildSizeArgs(r, enc, plan, tmp), duration, progress, ct);
            }
            long size = new FileInfo(tmp).Length;
            if (size <= r.TargetBytes) return;
            // overshot: aim lower in proportion and try again
            budget *= (double)r.TargetBytes / size * 0.95;
            progress?.Report(0);
        }
        throw new InvalidOperationException("Couldn't get it under the size limit — try trimming it a bit shorter.");
    }

    static List<string> BuildSizeArgs(ExportRequest r, EncoderDef enc, SizePlan plan, string output)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        a.AddRange(new[] { "-ss", Sec(r.Start), "-i", r.Input, "-t", Sec(r.End - r.Start), "-map", "0:v:0" });
        // one audio track only: that's all Discord plays
        if (r.Audio == AudioExport.Selected && r.Tracks.Length > 1)
        {
            var inputs = string.Concat(r.Tracks.Select(t => $"[0:a:{t}]"));
            a.AddRange(new[] { "-filter_complex", $"{inputs}amix=inputs={r.Tracks.Length}:normalize=0[am]", "-map", "[am]" });
        }
        else if (r.Audio == AudioExport.Selected && r.Tracks.Length == 1) a.AddRange(new[] { "-map", $"0:a:{r.Tracks[0]}" });
        else if (r.Audio != AudioExport.None) a.AddRange(new[] { "-map", "0:a:0?" });

        var filters = new List<string>();
        if (r.Crop is { } c) filters.Add($"crop={c.Width}:{c.Height}:{c.X}:{c.Y}");
        if (plan.ScaleHeight is { } h) filters.Add($"scale=-2:{h}:flags=lanczos");
        filters.Add(enc.Vendor == "cpu" ? "format=yuv420p" : "format=nv12");
        a.AddRange(new[] { "-vf", string.Join(",", filters) });

        string v = $"{plan.VideoKbps}k", max = $"{(int)(plan.VideoKbps * 1.25)}k", buf = $"{plan.VideoKbps * 2}k";
        a.AddRange(new[] { "-c:v", enc.FfmpegName });
        switch (enc.Vendor)
        {
            case "nvidia": a.AddRange(new[] { "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres", "-b:v", v, "-maxrate", max, "-bufsize", buf }); break;
            case "amd": a.AddRange(new[] { "-usage", "transcoding", "-quality", "quality", "-rc", "vbr_peak", "-b:v", v, "-maxrate", max }); break;
            case "intel": a.AddRange(new[] { "-preset", "medium", "-b:v", v, "-maxrate", max }); break;
            default: a.AddRange(new[] { "-preset", "faster", "-b:v", v, "-maxrate", max, "-bufsize", buf, "-pix_fmt", "yuv420p" }); break;
        }
        if (plan.AudioKbps > 0) a.AddRange(new[] { "-c:a", "aac", "-b:a", $"{plan.AudioKbps}k", "-ac", "2" });
        else a.Add("-an");
        a.AddRange(new[] { "-movflags", "+faststart", output });
        return a;
    }

    static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    static List<string> BuildArgs(ExportRequest r, EncoderDef? enc, QualityPreset quality, string output)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        a.AddRange(new[] { "-ss", Sec(r.Start), "-i", r.Input, "-t", Sec(r.End - r.Start) });
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
                a.AddRange(new[] { "-filter_complex", $"{inputs}amix=inputs={r.Tracks.Length}:normalize=0[am]", "-map", "[am]" });
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
            filters.Add(enc.Vendor == "cpu" ? "format=yuv420p" : "format=nv12");
            a.AddRange(new[] { "-vf", string.Join(",", filters) });
            var q = quality < QualityPreset.High ? QualityPreset.High : quality;
            a.AddRange(Ffmpeg.EncoderArgs(enc, q, 60));
            if (r.Audio != AudioExport.None) a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
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

    static async Task RunAsync(List<string> args, TimeSpan duration, IProgress<double>? progress, CancellationToken ct)
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

    public static string NextOutputPath(string input, string tag, string ext)
    {
        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        var path = Path.Combine(dir, $"{name} ({tag}){ext}");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{name} ({tag} {n}){ext}");
        return path;
    }

    public static string NextOutputPath(string input)
    {
        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        var ext = Path.GetExtension(input);
        var path = Path.Combine(dir, $"{name} (edit){ext}");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{name} (edit {n}){ext}");
        return path;
    }
}
