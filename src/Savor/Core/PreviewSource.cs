using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Savor.Core;

/// <summary>
/// Live preview of a display. Runs a separate, lightweight ffmpeg (15 fps, downscaled raw BGRA to stdout)
/// only while the Savor window is visible, so it costs nothing while you're in a game.
/// </summary>
public sealed class PreviewSource : IDisposable
{
    public const int Fps = 15;

    readonly object _lock = new();
    Process? _proc;
    Thread? _reader;
    byte[] _front = Array.Empty<byte>();
    byte[] _back = Array.Empty<byte>();
    bool _fresh;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsRunning => _proc is { HasExited: false };
    public string? Key { get; private set; }

    public static string MakeKey(DisplayInfo d, EncoderChoice enc, bool cursor) =>
        $"{d.OutputIndex}|{d.Width}x{d.Height}|{enc.Pipeline == CapturePipeline.Gdi}|{cursor}";

    public void Start(DisplayInfo d, EncoderChoice enc, bool cursor)
    {
        Stop();
        if (Ffmpeg.Path == null) return;

        // Fit within 960x540, even dimensions.
        double scale = Math.Min(960.0 / d.Width, 540.0 / d.Height);
        Width = Math.Max(2, (int)(d.Width * scale) / 2 * 2);
        Height = Math.Max(2, (int)(d.Height * scale) / 2 * 2);
        int frameBytes = Width * Height * 4;
        lock (_lock)
        {
            _front = new byte[frameBytes];
            _back = new byte[frameBytes];
            _fresh = false;
        }

        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        string vf;
        if (enc.Pipeline == CapturePipeline.Gdi)
        {
            a.AddRange(new[] { "-f", "gdigrab", "-framerate", Fps.ToString(), "-draw_mouse", cursor ? "1" : "0",
                "-offset_x", d.X.ToString(), "-offset_y", d.Y.ToString(), "-video_size", $"{d.Width}x{d.Height}", "-i", "desktop" });
            vf = $"scale={Width}:{Height}:flags=fast_bilinear,format=bgra";
        }
        else
        {
            a.AddRange(new[] { "-f", "lavfi", "-i",
                $"ddagrab=output_idx={d.OutputIndex}:framerate={Fps}:draw_mouse={(cursor ? 1 : 0)}" });
            vf = $"hwdownload,format=bgra,scale={Width}:{Height}:flags=fast_bilinear";
        }
        a.AddRange(new[] { "-vf", vf, "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" });

        var psi = Ffmpeg.StartInfo(a);
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, _) => { };
        try { p.Start(); } catch { p.Dispose(); return; }
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginErrorReadLine();
        _proc = p;
        Key = MakeKey(d, enc, cursor);

        var stream = p.StandardOutput.BaseStream;
        _reader = new Thread(() => ReadLoop(stream, frameBytes)) { IsBackground = true, Name = "Savor preview" };
        _reader.Start();
    }

    void ReadLoop(Stream s, int frameBytes)
    {
        var buf = new byte[frameBytes];
        try
        {
            while (true)
            {
                int got = 0;
                while (got < frameBytes)
                {
                    int n = s.Read(buf, got, frameBytes - got);
                    if (n <= 0) return;
                    got += n;
                }
                lock (_lock)
                {
                    if (_back.Length != frameBytes) return;
                    Buffer.BlockCopy(buf, 0, _back, 0, frameBytes);
                    (_front, _back) = (_back, _front);
                    _fresh = true;
                }
            }
        }
        catch { }
    }

    /// <summary>Copies the newest frame into <paramref name="write"/> if one arrived since the last call.</summary>
    public bool TryTakeFrame(Action<byte[]> write)
    {
        lock (_lock)
        {
            if (!_fresh) return false;
            _fresh = false;
            write(_front);
            return true;
        }
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
    }

    public void Dispose() => Stop();
}
