using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Savor.Core;

/// <summary>
/// Live preview of a display. Runs a separate ffmpeg that captures at 60 fps, downscales on the GPU
/// (scale_d3d11) and streams raw BGRA frames to stdout. It only runs while the Savor window is
/// visible, so it costs nothing while you're in a game.
/// </summary>
public sealed class PreviewSource : IDisposable
{
    readonly object _lock = new();
    Process? _proc;
    Thread? _reader;

    // triple buffering: reader fills _spare, publishes it as _ready; UI swaps _ready into _shown.
    byte[] _spare = Array.Empty<byte>(), _ready = Array.Empty<byte>(), _shown = Array.Empty<byte>();
    bool _fresh;
    long _frames;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }
    public bool GpuScale { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public bool IsRunning => _proc is { HasExited: false };
    public bool HasExited => _proc is { HasExited: true };
    public long FramesReceived => Interlocked.Read(ref _frames);
    public string? Key { get; private set; }

    /// <summary>Picks an even-sized output that fits the on-screen box and keeps the display's aspect.</summary>
    public static (int w, int h) FitSize(DisplayInfo d, double boxW, double boxH)
    {
        double maxW = Math.Clamp(boxW, 320, 1280), maxH = Math.Clamp(boxH, 180, 720);
        double scale = Math.Min(1.0, Math.Min(maxW / d.Width, maxH / d.Height));
        int w = Math.Max(2, (int)(d.Width * scale) / 2 * 2);
        int h = Math.Max(2, (int)(d.Height * scale) / 2 * 2);
        return (w, h);
    }

    public static string MakeKey(DisplayInfo d, EncoderChoice enc, bool cursor, int w, int h) =>
        $"{d.OutputIndex}|{d.Width}x{d.Height}|{enc.Pipeline == CapturePipeline.Gdi}|{cursor}|{w}x{h}";

    public void Start(DisplayInfo d, EncoderChoice enc, bool cursor, int width, int height, bool gpuScale)
    {
        Stop();
        if (Ffmpeg.Path == null) return;

        bool gdi = enc.Pipeline == CapturePipeline.Gdi;
        Width = width;
        Height = height;
        Fps = gdi ? 30 : 60;
        GpuScale = gpuScale && !gdi;
        int frameBytes = Width * Height * 4;
        lock (_lock)
        {
            _spare = new byte[frameBytes];
            _ready = new byte[frameBytes];
            _shown = new byte[frameBytes];
            _fresh = false;
        }
        Interlocked.Exchange(ref _frames, 0);

        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-fflags", "nobuffer" };
        string vf;
        if (gdi)
        {
            a.AddRange(new[] { "-f", "gdigrab", "-framerate", Fps.ToString(), "-draw_mouse", cursor ? "1" : "0",
                "-offset_x", d.X.ToString(), "-offset_y", d.Y.ToString(), "-video_size", $"{d.Width}x{d.Height}", "-i", "desktop" });
            vf = $"scale={Width}:{Height}:flags=bilinear,format=bgra";
        }
        else
        {
            a.AddRange(new[] { "-f", "lavfi", "-i",
                $"ddagrab=output_idx={d.OutputIndex}:framerate={Fps}:draw_mouse={(cursor ? 1 : 0)}" });
            vf = GpuScale
                // shrink on the GPU first, so only a small frame is copied back to memory
                ? $"scale_d3d11={Width}:{Height},hwdownload,format=nv12,scale=in_color_matrix=bt709:in_range=limited,format=bgra"
                : $"hwdownload,format=bgra,scale={Width}:{Height}:flags=bilinear";
        }
        a.AddRange(new[] { "-vf", vf, "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" });

        var p = new Process { StartInfo = Ffmpeg.StartInfo(a) };
        p.ErrorDataReceived += (_, _) => { };
        try { p.Start(); } catch { p.Dispose(); return; }
        ChildProcesses.Track(p);
        p.BeginErrorReadLine();
        _proc = p;
        StartedUtc = DateTime.UtcNow;
        Key = MakeKey(d, enc, cursor, Width, Height);

        var stream = p.StandardOutput.BaseStream;
        _reader = new Thread(() => ReadLoop(stream, frameBytes)) { IsBackground = true, Name = "Savor preview" };
        _reader.Start();
    }

    void ReadLoop(Stream s, int frameBytes)
    {
        try
        {
            while (true)
            {
                byte[] target;
                lock (_lock) target = _spare;
                if (target.Length != frameBytes) return;

                int got = 0;
                while (got < frameBytes)
                {
                    int n = s.Read(target, got, frameBytes - got);
                    if (n <= 0) return;
                    got += n;
                }
                lock (_lock)
                {
                    if (!ReferenceEquals(target, _spare)) return; // restarted
                    (_ready, _spare) = (_spare, _ready);
                    _fresh = true;
                }
                Interlocked.Increment(ref _frames);
            }
        }
        catch { }
    }

    /// <summary>Hands the newest frame to <paramref name="write"/> if one arrived since the last call.</summary>
    public bool TryTakeFrame(Action<byte[]> write)
    {
        byte[] frame;
        lock (_lock)
        {
            if (!_fresh) return false;
            _fresh = false;
            (_shown, _ready) = (_ready, _shown);
            frame = _shown;
        }
        write(frame); // reader never touches _shown, so no lock needed while copying
        return true;
    }

    public TimeSpan CpuTime
    {
        get { try { return _proc is { HasExited: false } p ? p.TotalProcessorTime : TimeSpan.Zero; } catch { return TimeSpan.Zero; } }
    }

    public void Stop()
    {
        var p = _proc;
        _proc = null;
        Key = null;
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { p.WaitForExit(1000); } catch { }
            p.Dispose();
        }
        _reader?.Join(500);
        _reader = null;
        lock (_lock)
        {
            // release the frame buffers (~11 MB at 720p) while the preview is off
            _spare = _ready = _shown = Array.Empty<byte>();
            _fresh = false;
        }
    }

    public void Dispose() => Stop();
}
