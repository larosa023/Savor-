using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Savor.Core;

/// <summary>
/// Joins captures into one video, in the order given. Captures made with the same settings are joined
/// instantly and losslessly (stream copy, every audio track kept); anything else is re-encoded to the first
/// clip's size and frame rate, keeping the main audio track.
/// </summary>
public static class ClipJoiner
{
    sealed record Info(string Codec, int W, int H, double Fps, int AudioTracks, double Duration);

    static readonly Regex VideoRx = new(@"Stream #\d+:\d+.*?: Video: (\w+).*?, (\d{2,5})x(\d{2,5})", RegexOptions.Compiled);
    static readonly Regex FpsRx = new(@"([\d.]+) fps", RegexOptions.Compiled);
    static readonly Regex DurRx = new(@"Duration: (\d+):(\d+):([\d.]+)", RegexOptions.Compiled);

    static async Task<Info?> ProbeAsync(string path, CancellationToken ct)
    {
        var (_, err) = await Ffmpeg.RunAsync(new[] { "-hide_banner", "-nostdin", "-i", path }, TimeSpan.FromSeconds(15), ct);
        var v = VideoRx.Match(err);
        if (!v.Success) return null;
        var line = err.Split('\n').FirstOrDefault(l => l.Contains("Video:")) ?? "";
        var f = FpsRx.Match(line);
        var d = DurRx.Match(err);
        double dur = d.Success
            ? int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;
        int audio = Regex.Matches(err, @"Stream #\d+:\d+.*?: Audio:").Count;
        return new Info(v.Groups[1].Value, int.Parse(v.Groups[2].Value), int.Parse(v.Groups[3].Value),
            f.Success ? double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture) : 60, audio, dur);
    }

    /// <summary>Returns the path of the joined video (next to the first clip).</summary>
    public static async Task<string> JoinAsync(List<string> inputs, EncoderDef? encoder, QualityPreset quality,
                                               IProgress<double>? progress, CancellationToken ct)
    {
        if (Ffmpeg.Path == null) throw new InvalidOperationException("FFmpeg isn't available.");
        if (inputs.Count < 2) throw new InvalidOperationException("Pick at least two clips.");

        var infos = new List<Info>();
        foreach (var f in inputs)
            infos.Add(await ProbeAsync(f, ct) ?? throw new InvalidOperationException($"Couldn't read “{Path.GetFileName(f)}”."));

        var first = inputs[0];
        var dir = Path.GetDirectoryName(first)!;
        var ext = Path.GetExtension(first).ToLowerInvariant() is ".mkv" ? ".mkv" : ".mp4";
        var baseName = Path.GetFileNameWithoutExtension(first) + " (joined)";
        var output = Path.Combine(dir, baseName + ext);
        for (int n = 2; File.Exists(output); n++) output = Path.Combine(dir, $"{baseName} {n}{ext}");
        var tmp = Path.Combine(dir, $".savor-join-{Guid.NewGuid():N}{ext}");
        var total = TimeSpan.FromSeconds(infos.Sum(i => i.Duration));

        var a0 = infos[0];
        bool same = infos.All(i => i.Codec == a0.Codec && i.W == a0.W && i.H == a0.H &&
                                   Math.Abs(i.Fps - a0.Fps) < 0.5 && i.AudioTracks == a0.AudioTracks);
        try
        {
            if (same) await CopyJoinAsync(inputs, tmp, ext, total, progress, ct);
            else
            {
                var enc = encoder ?? Ffmpeg.Encoders[^1];
                try { await ReencodeJoinAsync(inputs, infos, enc, quality, tmp, ext, total, progress, ct); }
                catch (Exception) when (!ct.IsCancellationRequested && enc.Vendor != "cpu")
                {
                    progress?.Report(0);
                    await ReencodeJoinAsync(inputs, infos, Ffmpeg.Encoders[^1], quality, tmp, ext, total, progress, ct);
                }
            }
            File.Move(tmp, output);
            return output;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    static async Task CopyJoinAsync(List<string> inputs, string output, string ext, TimeSpan total,
                                    IProgress<double>? progress, CancellationToken ct)
    {
        var list = Path.Combine(AppSettings.LocalDataDir, $"join_{Guid.NewGuid():N}.txt");
        try
        {
            Directory.CreateDirectory(AppSettings.LocalDataDir);
            File.WriteAllLines(list, inputs.Select(f => "file '" + f.Replace('\\', '/').Replace("'", @"'\''") + "'"));
            var a = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats",
                "-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-c", "copy"
            };
            if (ext == ".mp4") a.AddRange(new[] { "-movflags", "+faststart" });
            a.Add(output);
            await ClipExporter.RunAsync(a, total, progress, ct);
        }
        finally { try { File.Delete(list); } catch { } }
    }

    static async Task ReencodeJoinAsync(List<string> inputs, List<Info> infos, EncoderDef enc, QualityPreset quality,
                                        string output, string ext, TimeSpan total, IProgress<double>? progress, CancellationToken ct)
    {
        int w = infos[0].W / 2 * 2, h = infos[0].H / 2 * 2;
        int fps = (int)Math.Clamp(Math.Round(infos[0].Fps), 24, 240);
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        foreach (var f in inputs) a.AddRange(new[] { "-i", f });

        var fc = new StringBuilder();
        for (int i = 0; i < inputs.Count; i++)
        {
            fc.Append($"[{i}:v:0]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps}[v{i}];");
            if (infos[i].AudioTracks > 0)
                fc.Append($"[{i}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
            else // silent clip: fill with silence of the same length
                fc.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={infos[i].Duration.ToString("0.###", CultureInfo.InvariantCulture)}[a{i}];");
        }
        for (int i = 0; i < inputs.Count; i++) fc.Append($"[v{i}][a{i}]");
        fc.Append($"concat=n={inputs.Count}:v=1:a=1[cv][ca];[cv]format={(enc.Vendor == "cpu" ? "yuv420p" : "nv12")}[ov]");
        a.AddRange(new[] { "-filter_complex", fc.ToString(), "-map", "[ov]", "-map", "[ca]" });
        var q = quality < QualityPreset.High ? QualityPreset.High : quality;
        a.AddRange(Ffmpeg.EncoderArgs(enc, q, fps, live: false));
        a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        if (ext == ".mp4")
        {
            a.AddRange(new[] { "-movflags", "+faststart" });
            if (enc.Codec == "hevc") a.AddRange(new[] { "-tag:v", "hvc1" });
        }
        a.Add(output);
        await ClipExporter.RunAsync(a, total, progress, ct);
    }
}
