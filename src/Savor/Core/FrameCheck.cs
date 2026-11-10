using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Savor.Core;

/// <summary>
/// Spots dropped frames in a finished capture piece without decoding it. When the capture misses screen frames,
/// the encoder repeats the last picture, and a repeated picture compresses to almost nothing; when capture resumes,
/// the next frame jumps ahead and is large. So a short run of near-empty frames between two full-size ones is a
/// freeze. A still screen (only tiny frames) or slower video (single small frames) doesn't look like that.
/// Measured on real clips: choppy captures show 40–60 freezes per 30 s, smooth ones none.
/// </summary>
public static class FrameCheck
{
    /// <summary>Freezes of 4–45 frames (about 1/15 s to 3/4 s at 60 fps) count.</summary>
    public const int MinRun = 4, MaxRun = 45;

    /// <summary>Number of freezes in a piece, from its frame sizes in bytes.</summary>
    /// <param name="pixels">Picture size (width × height), to tell a busy picture from a still one.</param>
    public static int Freezes(IReadOnlyList<int> sizes, long pixels)
    {
        int n = sizes.Count;
        if (n < MinRun + 2) return 0;
        var sorted = sizes.OrderBy(x => x).ToArray();
        double big = sorted[(int)Math.Min(n - 1, Math.Round(0.8 * (n - 1)))];
        if (big < 0.004 * pixels) return 0; // mostly still picture: nothing to judge
        double tiny = Math.Max(1500, 0.03 * big), full = 0.3 * big;
        int freezes = 0;
        for (int i = 1; i < n; i++)
        {
            if (sizes[i] >= tiny || sizes[i - 1] < full) continue; // a run starts right after a full-size frame
            int j = i;
            while (j < n && sizes[j] < tiny) j++;
            int len = j - i;
            if (len >= MinRun && len <= MaxRun && j < n && sizes[j] >= full) freezes++;
            i = j - 1;
        }
        return freezes;
    }

    /// <summary>Reads a piece's frame sizes (stream copy, no decoding) and counts freezes. Null if it can't be read.</summary>
    public static async Task<int?> FreezesInFileAsync(string path, long pixels)
    {
        var sizes = await FrameSizesAsync(path);
        return sizes == null ? null : Freezes(sizes, pixels);
    }

    public static async Task<List<int>?> FrameSizesAsync(string path)
    {
        if (Ffmpeg.Path == null) return null;
        try
        {
            using var p = Process.Start(Ffmpeg.StartInfo(new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", path,
                "-map", "0:v:0", "-c", "copy", "-f", "framecrc", "-" }))!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _ = p.StandardError.ReadToEndAsync();
            var sizes = new List<int>();
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                // "0,      dts,      pts, duration, size, crc"
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split(',');
                if (parts.Length >= 5 && int.TryParse(parts[4].Trim(), out var size)) sizes.Add(size);
            }
            await p.WaitForExitAsync();
            return sizes;
        }
        catch { return null; }
    }
}
