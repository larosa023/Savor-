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

    /// <summary>Reads a piece's frame sizes (no decoding) and counts freezes. Null if it can't be read.</summary>
    public static async Task<int?> FreezesInFileAsync(string path, long pixels)
    {
        // buffer pieces are MPEG-TS: read them directly (no ffmpeg process); anything else goes through ffmpeg
        var sizes = path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? await Task.Run(() => TsFrameSizes(path)) : null;
        sizes ??= await FrameSizesAsync(path);
        return sizes == null ? null : Freezes(sizes, pixels);
    }

    [ThreadStatic] static byte[]? _tsBuf;

    /// <summary>
    /// Video frame sizes from an MPEG-TS file, without ffmpeg: the muxer writes each frame as one PES packet, so a
    /// frame's size is the payload carried on the video PID from one packet start to the next. Matches ffmpeg's own
    /// packet sizes (checked against framecrc on H.264, HEVC and AV1 captures). Null if no video stream is found.
    /// </summary>
    public static List<int>? TsFrameSizes(string path)
    {
        try
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                                                    System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, 1, false);
            var buf = _tsBuf ??= new byte[188 * 1024];
            var sizes = new List<int>(256);
            int vpid = -1, cur = -1;
            int got;
            while ((got = ReadFull(fs, buf)) >= 188)
            {
                for (int off = 0; off + 188 <= got; off += 188)
                {
                    if (buf[off] != 0x47) return sizes.Count > 0 ? sizes : null; // lost sync: use what was read
                    bool start = (buf[off + 1] & 0x40) != 0;
                    int pid = ((buf[off + 1] & 0x1F) << 8) | buf[off + 2];
                    int afc = (buf[off + 3] >> 4) & 3;
                    if (afc is 0 or 2) continue; // no payload
                    int p = afc == 3 ? 5 + buf[off + 4] : 4;
                    if (p >= 188) continue;
                    if (start && p + 9 <= 188 && buf[off + p] == 0 && buf[off + p + 1] == 0 && buf[off + p + 2] == 1)
                    {
                        int sid = buf[off + p + 3];
                        if (vpid < 0 && sid >= 0xE0 && sid <= 0xEF) vpid = pid;
                        if (pid == vpid)
                        {
                            if (cur >= 0) sizes.Add(cur);
                            cur = 188 - p - (9 + buf[off + p + 8]); // payload after the PES header
                            continue;
                        }
                    }
                    if (pid == vpid && cur >= 0) cur += 188 - p;
                }
                if (got < buf.Length) break;
            }
            if (cur >= 0) sizes.Add(cur);
            return vpid < 0 ? null : sizes;
        }
        catch { return null; }
    }

    static int ReadFull(System.IO.Stream s, byte[] buf)
    {
        int got = 0, n;
        while (got < buf.Length && (n = s.Read(buf, got, buf.Length - got)) > 0) got += n;
        return got;
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
