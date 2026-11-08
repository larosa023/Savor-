using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Savor.Core;

/// <summary>
/// Measures audio vs video sync in a capture of Savor's sync test: a white square flashed on screen at the same
/// moment as a short 1 kHz beep, several times. Finds each flash (brightness jump in the middle of the picture) and
/// each beep (sound onset), pairs them, and returns the typical offset.
/// </summary>
public static class SyncCheck
{
    public sealed record Result(double? OffsetSeconds, int Pairs, string Detail);

    static readonly Regex PtsRx = new(@"pts_time:\s*(-?[\d.]+)", RegexOptions.Compiled);

    /// <param name="lastSeconds">Only the end of the clip (where the test ran) is analysed.</param>
    public static async Task<Result> AnalyzeAsync(string clip, double lastSeconds = 9)
    {
        if (Ffmpeg.Path == null) return new(null, 0, "FFmpeg isn't available.");
        var tail = lastSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        // ---- video: average brightness of the middle of the picture, per frame, with its timestamp ----
        const int S = 16;
        var vArgs = new[] { "-hide_banner", "-nostdin", "-loglevel", "info", "-sseof", "-" + tail, "-i", clip, "-an",
            "-vf", $"crop=iw/6:ih/6:iw*5/12:ih*5/12,scale={S}:{S},format=gray,showinfo", "-fps_mode", "passthrough",
            "-f", "rawvideo", "-" };
        var frames = new List<double>();
        var times = new List<double>();
        using (var p = Process.Start(Ffmpeg.StartInfo(vArgs))!)
        {
            var errTask = p.StandardError.ReadToEndAsync();
            var buf = new byte[S * S];
            var stdout = p.StandardOutput.BaseStream;
            while (true)
            {
                int got = 0;
                while (got < buf.Length)
                {
                    int n = await stdout.ReadAsync(buf.AsMemory(got));
                    if (n <= 0) break;
                    got += n;
                }
                if (got < buf.Length) break;
                frames.Add(buf.Average(b => (double)b));
            }
            await p.WaitForExitAsync();
            foreach (Match m in PtsRx.Matches(await errTask))
                times.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        int nf = Math.Min(frames.Count, times.Count);
        if (nf < 30) return new(null, 0, "Couldn't read the test video.");

        var flashes = new List<double>();
        for (int i = 1; i < nf; i++)
            if (frames[i] - frames[i - 1] > 60 && (flashes.Count == 0 || times[i] - flashes[^1] > 0.3))
                flashes.Add(times[i]);

        // ---- audio: onsets of the 1 kHz beep (silencedetect reports them on the same timeline as the video) ----
        var (_, aerr) = await Ffmpeg.RunAsync(new[] { "-hide_banner", "-nostdin", "-loglevel", "info", "-sseof", "-" + tail, "-i", clip,
            "-vn", "-map", "0:a:0", "-af", "highpass=f=700,lowpass=f=1400,silencedetect=noise=-38dB:d=0.12", "-f", "null", "-" },
            TimeSpan.FromSeconds(60));
        var beeps = Regex.Matches(aerr, @"silence_end: (-?[\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        if (beeps.Count == 0)
            return new(null, 0, "The test beeps weren't recorded. Make sure desktop audio is on in Savor and nothing else is playing.");

        var offsets = new List<double>();
        foreach (var f in flashes)
        {
            var near = beeps.OrderBy(b => Math.Abs(b - f)).FirstOrDefault(double.NaN);
            if (!double.IsNaN(near) && Math.Abs(near - f) < 0.6) offsets.Add(near - f);
        }
        if (offsets.Count < 3)
            return new(null, offsets.Count, $"Found {flashes.Count} flashes and {beeps.Count} beeps — not enough to measure. Keep the test square visible on the display Savor captures.");
        offsets.Sort();
        double median = offsets[offsets.Count / 2];
        return new(median, offsets.Count,
            $"{offsets.Count} flash/beep pairs, offsets {string.Join(", ", offsets.Select(o => $"{o * 1000:0}"))} ms");
    }
}

/// <summary>Plays short 1 kHz beeps on demand (for the sync test), silence otherwise.</summary>
public sealed class BeepSource : NAudio.Wave.ISampleProvider
{
    public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    int _remaining; // samples (per channel) of beep still to play
    long _n;

    /// <summary>Starts a beep of the given length right away.</summary>
    public void Beep(double seconds) => System.Threading.Interlocked.Exchange(ref _remaining, (int)(seconds * 48000));

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i += 2)
        {
            float v = 0;
            if (_remaining > 0)
            {
                v = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * _n / 48000.0);
                _remaining--;
            }
            _n++;
            buffer[offset + i] = v;
            if (i + 1 < count) buffer[offset + i + 1] = v;
        }
        return count;
    }
}
