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
        // up to 240 frames; clips up to 2 min get at least 2 frames per second
        bool keyOnly = secs > 120; // long recordings: keyframes only (every 2 s), very fast
        double fps = keyOnly ? Math.Max(0.05, 240 / secs) : Math.Clamp(240 / secs, 2, 10);
        var strip = new Filmstrip(fps);
        _ = Task.Run(() => strip.Run(path, fps, keyOnly));
        return strip;
    }

    void Run(string path, double fps, bool keyOnly)
    {
        // GPU decoding when available keeps this quick even for 1440p/4K clips
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-hwaccel", "auto", "-threads", "2" };
        if (keyOnly) args.AddRange(new[] { "-skip_frame", "nokey" });
        args.AddRange(new[]
        {
            "-i", path, "-an", "-sn",
            // 960 px wide, area downscaling keeps text and UI sharp
            "-vf", $"fps={fps.ToString("0.####", CultureInfo.InvariantCulture)},scale='min(960,iw)':-2:flags=area",
            "-c:v", "mjpeg", "-q:v", "4", "-f", "image2pipe", "pipe:1"
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

    // ---- background decoding: the UI thread never decodes a JPEG ----
    readonly AutoResetEvent _wake = new(false);
    Thread? _decoder;
    volatile int _wanted = -1;

    /// <summary>Raised on a background thread when a requested frame is decoded.</summary>
    public event Action<int, BitmapSource>? FrameReady;

    /// <summary>Index of the frame nearest to <paramref name="t"/>, or -1 if it isn't generated yet.</summary>
    public int IndexAt(TimeSpan t)
    {
        int idx = Math.Max(0, (int)Math.Round(t.TotalSeconds * Fps));
        lock (_lock)
        {
            if (_frames.Count == 0) return -1;
            if (idx >= _frames.Count)
            {
                if (idx - (_frames.Count - 1) > 2) return -1; // far beyond what's ready
                idx = _frames.Count - 1;
            }
        }
        return idx;
    }

    /// <summary>The decoded frame if <paramref name="idx"/> is the one most recently decoded.</summary>
    public BitmapSource? Cached(int idx) => idx == _lastIndex ? _lastFrame : null;

    /// <summary>Asks for a frame; only the newest request is decoded (older ones are skipped).</summary>
    public void Request(int idx)
    {
        _wanted = idx;
        if (_decoder == null)
        {
            _decoder = new Thread(DecodeLoop) { IsBackground = true, Name = "Savor filmstrip decode", Priority = ThreadPriority.AboveNormal };
            _decoder.Start();
        }
        _wake.Set();
    }

    void DecodeLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            _wake.WaitOne(500);
            int idx = _wanted;
            if (idx < 0 || idx == _lastIndex) continue;
            byte[] jpg;
            lock (_lock)
            {
                if (idx >= _frames.Count) continue;
                jpg = _frames[idx];
            }
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(jpg);
                img.EndInit();
                img.Freeze(); // frozen bitmaps can be handed to the UI thread
                _lastIndex = idx;
                _lastFrame = img;
                FrameReady?.Invoke(idx, img);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        FrameReady = null;
        try { if (_proc is { HasExited: false } p) p.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        lock (_lock) _frames.Clear();
        _lastFrame = null;
    }
}
