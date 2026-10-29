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
            if (r.Fast)
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
            a.AddRange(Ffmpeg.EncoderArgs(enc, q, 60, live: false));
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
