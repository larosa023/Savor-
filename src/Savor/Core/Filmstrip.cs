using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Savor.Core;

/// <summary>
/// Small JPEG preview frames at a fixed interval, made in the background with ffmpeg.
/// Used while scrubbing/trimming so the editor never has to make the video player seek
/// (which stalls the UI). At most ~240 frames of ~10 KB each, so only a few MB.
/// </summary>
public sealed class Filmstrip : IDisposable
{
    readonly List<byte[]> _frames = new();
    readonly object _lock = new();
    readonly CancellationTokenSource _cts = new();
    Process? _proc;
    int _lastIndex = -1;
    BitmapSource? _lastFrame;

    public double Fps { get; }
    public int Count { get { lock (_lock) return _frames.Count; } }

    Filmstrip(double fps) => Fps = fps;

    public static Filmstrip? Start(string path, TimeSpan duration)
    {
        if (Ffmpeg.Path == null || duration <= TimeSpan.Zero) return null;
        double secs = duration.TotalSeconds;
        double fps = Math.Clamp(240 / secs, 0.1, 10);
        var strip = new Filmstrip(fps);
        // long files: decode keyframes only (fast); short clips: decode everything for exact frames
        bool keyOnly = secs > 120;
        _ = Task.Run(() => strip.Run(path, fps, keyOnly));
        return strip;
    }

    void Run(string path, double fps, bool keyOnly)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "2" };
        if (keyOnly) args.AddRange(new[] { "-skip_frame", "nokey" });
        args.AddRange(new[]
        {
            "-i", path, "-an", "-sn",
            "-vf", $"fps={fps.ToString("0.####", CultureInfo.InvariantCulture)},scale=384:-2:flags=fast_bilinear",
            "-c:v", "mjpeg", "-q:v", "7", "-f", "image2pipe", "pipe:1"
        });
        try
        {
            var p = new Process { StartInfo = Ffmpeg.StartInfo(args) };
            p.ErrorDataReceived += (_, _) => { };
            p.Start();
            _proc = p;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            p.BeginErrorReadLine();
            ReadJpegs(p.StandardOutput.BaseStream);
        }
        catch { }
    }

    /// <summary>Splits ffmpeg's MJPEG stream into individual JPEG files (FFD8 … FFD9).</summary>
    void ReadJpegs(Stream s)
    {
        var buf = new byte[1 << 16];
        var cur = new MemoryStream();
        int prev = -1;
        bool inFrame = false;
        int n;
        while (!_cts.IsCancellationRequested && (n = s.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                int b = buf[i];
                if (!inFrame)
                {
                    if (prev == 0xFF && b == 0xD8)
                    {
                        inFrame = true;
                        cur.SetLength(0);
                        cur.WriteByte(0xFF);
                        cur.WriteByte(0xD8);
                    }
                }
                else
                {
                    cur.WriteByte((byte)b);
                    if (prev == 0xFF && b == 0xD9)
                    {
                        lock (_lock) _frames.Add(cur.ToArray());
                        inFrame = false;
                        b = -1;
                    }
                }
                prev = b;
            }
        }
    }

    /// <summary>The preview frame nearest to <paramref name="t"/>, or null if it isn't ready yet.</summary>
    public BitmapSource? FrameAt(TimeSpan t)
    {
        int idx = (int)Math.Round(t.TotalSeconds * Fps);
        byte[]? jpg;
        lock (_lock)
        {
            if (_frames.Count == 0) return null;
            if (idx >= _frames.Count)
            {
                // not generated yet: only fall back to the last frame if it's close
                if (idx - (_frames.Count - 1) > 2) return null;
                idx = _frames.Count - 1;
            }
            if (idx < 0) idx = 0;
            if (idx == _lastIndex) return _lastFrame;
            jpg = _frames[idx];
        }
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(jpg);
            img.EndInit();
            img.Freeze();
            _lastIndex = idx;
            _lastFrame = img;
            return img;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (_proc is { HasExited: false } p) p.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        lock (_lock) _frames.Clear();
        _lastFrame = null;
    }
}
