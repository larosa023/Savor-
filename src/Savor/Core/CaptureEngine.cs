using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Savor.Core;

/// <summary>
/// One ffmpeg process captures the screen and writes short MPEG-TS segments to a scratch folder.
/// - The replay buffer keeps only the newest N segments; saving a clip stitches them together.
/// - Recording pins every segment from the moment it started and stitches them on stop.
/// Both features share a single encoder, so running them together costs nothing extra.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    public const int SegmentSeconds = 2;

    readonly Func<AppSettings> _settings;
    readonly SemaphoreSlim _op = new(1, 1);
    readonly Timer _cleanupTimer;
    readonly LinkedList<string> _log = new();

    Process? _proc;
    AudioMixer? _mixer;
    BlockingCollection<byte[]>? _audioQueue;
    Thread? _writer;
    volatile bool _audioConnected;
    bool _audioOn;
    volatile bool _expectedExit;

    int _recordFrom = -1;
    volatile int _holdFrom = int.MaxValue;

    public string BufferDir { get; } = Path.Combine(AppSettings.LocalDataDir, "buffer");
    public EncoderChoice? Encoder { get; set; }
    public DisplayInfo? Display { get; set; }

    public bool BufferActive { get; private set; }
    public bool Recording { get; private set; }
    public DateTime RecordingStartedUtc { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public bool IsRunning => _proc is { HasExited: false };
    public AudioMixer? Mixer => _mixer;
    string[] _trackNames = Array.Empty<string>();
    /// <summary>Audio tracks in the current capture, in file order.</summary>
    public IReadOnlyList<string> TrackNames => _trackNames;

    public TimeSpan CpuTime
    {
        get { try { return _proc is { HasExited: false } p ? p.TotalProcessorTime : TimeSpan.Zero; } catch { return TimeSpan.Zero; } }
    }

    /// <summary>Raised whenever BufferActive / Recording / running state changes (any thread).</summary>
    public event Action? StateChanged;
    /// <summary>Raised when capture fails unexpectedly (any thread).</summary>
    public event Action<string>? Failed;
    /// <summary>Raised when a recording was salvaged after a crash (any thread).</summary>
    public event Action<string>? RecordingSalvaged;

    public CaptureEngine(Func<AppSettings> settings)
    {
        _settings = settings;
        _cleanupTimer = new Timer(_ => Cleanup(), null, 1000, 1000);
    }

    // ================= public operations =================

    public async Task StartBufferAsync()
    {
        await _op.WaitAsync();
        try
        {
            if (BufferActive) return;
            StartProcessIfNeeded();
            BufferActive = true;
        }
        finally { _op.Release(); StateChanged?.Invoke(); }
    }

    public async Task StopBufferAsync()
    {
        await _op.WaitAsync();
        try
        {
            if (!BufferActive) return;
            BufferActive = false;
            if (!Recording) StopProcess();
        }
        finally { _op.Release(); StateChanged?.Invoke(); }
    }

    /// <summary>Saves the last N seconds. Returns the output path, or null if nothing to save.</summary>
    public async Task<string?> SaveReplayAsync()
    {
        await _op.WaitAsync();
        try
        {
            if (!BufferActive || !IsRunning) return null;
            var s = _settings();
            var segs = Segments();
            if (segs.Count == 0) return null;

            int need = (int)Math.Ceiling(s.ReplaySeconds / (double)SegmentSeconds) + 1;
            var pick = segs.Skip(Math.Max(0, segs.Count - need)).ToList();
            _holdFrom = pick[0].Index;
            try
            {
                var output = OutputPath("Clip", s);
                return await ConcatAsync(pick.Select(p => p.Path).ToList(), output, s) ? output : null;
            }
            finally { _holdFrom = int.MaxValue; }
        }
        finally { _op.Release(); }
    }

    public async Task StartRecordingAsync()
    {
        await _op.WaitAsync();
        try
        {
            if (Recording) return;
            bool fresh = !IsRunning;
            StartProcessIfNeeded();
            var segs = Segments();
            _recordFrom = fresh || segs.Count == 0 ? 0 : segs[^1].Index;
            RecordingStartedUtc = DateTime.UtcNow;
            Recording = true;
        }
        finally { _op.Release(); StateChanged?.Invoke(); }
    }

    /// <summary>Stops recording and writes the file. Returns its path (or null on failure).</summary>
    public async Task<string?> StopRecordingAsync()
    {
        await _op.WaitAsync();
        try
        {
            if (!Recording) return null;
            int from = _recordFrom;
            _holdFrom = from;
            Recording = false;
            _recordFrom = -1;
            StateChanged?.Invoke();

            bool stopped = false;
            if (!BufferActive) { StopProcess(); stopped = true; }

            var s = _settings();
            var segs = Segments().Where(x => x.Index >= from).ToList();
            string? result = null;
            if (segs.Count > 0)
            {
                var output = OutputPath("Recording", s);
                if (await ConcatAsync(segs.Select(x => x.Path).ToList(), output, s)) result = output;
            }
            _holdFrom = int.MaxValue;
            if (stopped && result != null) ClearBufferDir();
            return result;
        }
        finally { _holdFrom = int.MaxValue; _op.Release(); StateChanged?.Invoke(); }
    }

    /// <summary>Applies new capture settings by restarting ffmpeg, unless a recording is in progress.</summary>
    public async Task<bool> RestartIfIdleAsync()
    {
        await _op.WaitAsync();
        try
        {
            if (!IsRunning) return true;
            if (Recording) return false;
            StopProcess();
            StartProcessIfNeeded();
            return true;
        }
        finally { _op.Release(); StateChanged?.Invoke(); }
    }

    public async Task ShutdownAsync()
    {
        if (Recording) await StopRecordingAsync();
        await _op.WaitAsync();
        try
        {
            BufferActive = false;
            StopProcess();
            ClearBufferDir();
        }
        finally { _op.Release(); }
    }

    public string RecentLog()
    {
        lock (_log) return string.Join(Environment.NewLine, _log);
    }

    // ================= process management =================

    void StartProcessIfNeeded()
    {
        if (IsRunning) return;
        if (Ffmpeg.Path == null) throw new InvalidOperationException("FFmpeg was not found.");
        var enc = Encoder ?? throw new InvalidOperationException("No working video encoder was found on this PC.");
        var s = _settings();

        ClearBufferDir();
        Directory.CreateDirectory(BufferDir);
        lock (_log) _log.Clear();

        // ---- audio ----
        bool audio = s.DesktopAudio || !string.IsNullOrEmpty(s.MicDeviceId);
        int rate = 48000;
        if (audio)
        {
            try
            {
                _mixer = new AudioMixer(s.DesktopAudio, s.DesktopVolume, s.MicDeviceId, s.MicVolume)
                {
                    SeparateTracks = s.SeparateAudioTracks,
                    DesktopMuted = s.DesktopMuted,
                    MicMuted = s.MicMuted,
                };
                rate = _mixer.SampleRate;
                _audioQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 1024);
                _audioConnected = false;
                _mixer.DataAvailable += OnAudio;
                _mixer.Start();
            }
            catch (Exception ex)
            {
                AddLog("Audio unavailable: " + ex.Message);
                TeardownAudio();
                audio = false;
            }
        }

        // ---- ffmpeg ----
        _trackNames = audio && _mixer != null ? _mixer.TrackNames.ToArray() : Array.Empty<string>();
        var args = BuildArgs(s, enc, audio, rate);
        var p = new Process { StartInfo = Ffmpeg.StartInfo(args, redirectInput: true), EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AddLog(e.Data!); };
        p.OutputDataReceived += (_, _) => { };
        p.Exited += OnProcessExited;
        _expectedExit = false;
        try { p.Start(); }
        catch
        {
            TeardownAudio();
            p.Dispose();
            throw;
        }
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _audioOn = audio;
        if (audio && _audioQueue != null)
        {
            // Audio goes to ffmpeg over stdin (raw 32-bit float PCM).
            var stdin = p.StandardInput.BaseStream;
            var q = _audioQueue;
            _writer = new Thread(() => WriterLoop(stdin, q)) { IsBackground = true, Name = "Savor audio feed" };
            _audioConnected = true;
            _writer.Start();
        }
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        _proc = p;
        StartedUtc = DateTime.UtcNow;
    }

    List<string> BuildArgs(AppSettings s, EncoderChoice enc, bool audio, int rate)
    {
        var d = Display;
        int fps = s.Fps;
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-y" };
        if (audio) a.Add("-nostdin"); // stdin carries audio, not keyboard commands

        if (enc.Pipeline == CapturePipeline.Gdi)
        {
            a.AddRange(new[] { "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", s.CaptureCursor ? "1" : "0" });
            if (d != null)
                a.AddRange(new[] { "-offset_x", d.X.ToString(), "-offset_y", d.Y.ToString(), "-video_size", $"{d.Width}x{d.Height}" });
            a.AddRange(new[] { "-i", "desktop" });
        }
        else
        {
            int idx = d?.OutputIndex ?? 0;
            a.AddRange(new[] { "-f", "lavfi", "-i",
                $"ddagrab=output_idx={idx}:framerate={fps}:draw_mouse={(s.CaptureCursor ? 1 : 0)}" });
        }

        if (audio)
        {
            a.AddRange(new[] { "-f", "f32le", "-ar", rate.ToString(), "-ac", (_trackNames.Length * 2).ToString(),
                               "-i", "pipe:0" });
        }

        int tracks = audio ? Math.Max(1, _trackNames.Length) : 0;
        if (tracks > 1)
        {
            // split the interleaved 6-channel input into one stereo stream per track
            var fc = new System.Text.StringBuilder($"[1:a]asplit={tracks}");
            for (int t = 0; t < tracks; t++) fc.Append($"[s{t}]");
            for (int t = 0; t < tracks; t++) fc.Append($";[s{t}]pan=stereo|c0=c{t * 2}|c1=c{t * 2 + 1}[a{t}]");
            a.AddRange(new[] { "-filter_complex", fc.ToString() });
        }
        a.AddRange(new[] { "-map", "0:v" });
        if (tracks == 1) a.AddRange(new[] { "-map", "1:a" });
        for (int t = 0; tracks > 1 && t < tracks; t++) a.AddRange(new[] { "-map", $"[a{t}]" });

        var vf = Ffmpeg.VideoFilter(enc);
        if (vf != null) a.AddRange(new[] { "-vf", vf });
        a.AddRange(Ffmpeg.EncoderArgs(enc.Def, s.Quality, fps));

        if (audio) a.AddRange(new[] { "-c:a", "aac", "-b:a", tracks > 1 ? "160k" : "192k", "-ar", "48000", "-shortest" });

        a.AddRange(new[]
        {
            "-f", "segment",
            "-segment_time", SegmentSeconds.ToString(),
            "-segment_format", "mpegts",
            Path.Combine(BufferDir, "seg_%06d.ts")
        });
        return a;
    }

    void StopProcess()
    {
        var p = _proc;
        if (p == null) return;
        _expectedExit = true;
        try
        {
            if (!p.HasExited)
            {
                if (_audioOn)
                {
                    // Ending the audio stream (closing stdin) makes ffmpeg finish cleanly thanks to -shortest.
                    _mixer?.Stop();
                    try { _audioQueue?.CompleteAdding(); } catch { }
                    _writer?.Join(2000);
                    try { p.StandardInput.Close(); } catch { }
                }
                else
                {
                    try { p.StandardInput.Write('q'); p.StandardInput.Flush(); } catch { }
                }
                if (!p.WaitForExit(8000))
                {
                    try { p.Kill(true); } catch { }
                    p.WaitForExit(2000);
                }
            }
        }
        catch { }
        finally
        {
            TeardownAudio();
            try { p.Dispose(); } catch { }
            _proc = null;
        }
    }

    void OnProcessExited(object? sender, EventArgs e)
    {
        if (_expectedExit || !ReferenceEquals(sender, _proc)) return;
        _ = Task.Run(async () =>
        {
            await _op.WaitAsync();
            bool wasRecording;
            int from;
            try
            {
                if (_expectedExit || !ReferenceEquals(sender, _proc)) return;
                wasRecording = Recording;
                from = _recordFrom;
                Recording = false;
                BufferActive = false;
                _recordFrom = -1;
                TeardownAudio();
                try { _proc?.Dispose(); } catch { }
                _proc = null;

                if (wasRecording && from >= 0)
                {
                    var s = _settings();
                    var segs = Segments().Where(x => x.Index >= from).ToList();
                    if (segs.Count > 0)
                    {
                        var output = OutputPath("Recording", s);
                        if (await ConcatAsync(segs.Select(x => x.Path).ToList(), output, s))
                            RecordingSalvaged?.Invoke(output);
                    }
                }
            }
            finally { _op.Release(); }
            StateChanged?.Invoke();
            Failed?.Invoke(FriendlyError(RecentLog()));
        });
    }

    static string FriendlyError(string log)
    {
        var lines = log.Split(Environment.NewLine).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var last = string.Join("  |  ", lines.Skip(Math.Max(0, lines.Count - 3)));
        if (log.Contains("DXGI_ERROR_ACCESS_LOST") || log.Contains("AcquireNextFrame"))
            return "Screen capture was interrupted (display mode changed or a secure screen appeared). Turn the replay buffer back on to resume.";
        if (log.Contains("DXGI_ERROR_UNSUPPORTED") || log.Contains("DuplicateOutput"))
            return "Windows refused screen capture for this display. Try another display or encoder in Settings.";
        return string.IsNullOrEmpty(last) ? "Capture stopped unexpectedly." : "Capture stopped: " + last;
    }

    // ================= audio pipe =================

    void OnAudio(byte[] data, int count)
    {
        var q = _audioQueue;
        if (!_audioConnected || q == null || q.IsAddingCompleted) return;
        var copy = new byte[count];
        Buffer.BlockCopy(data, 0, copy, 0, count);
        try { q.TryAdd(copy); } catch (InvalidOperationException) { }
    }

    void WriterLoop(Stream output, BlockingCollection<byte[]> q)
    {
        try
        {
            foreach (var buf in q.GetConsumingEnumerable())
                output.Write(buf, 0, buf.Length);
            output.Flush();
        }
        catch { /* ffmpeg exited */ }
        finally { _audioConnected = false; }
    }

    void TeardownAudio()
    {
        _audioConnected = false;
        if (_mixer != null)
        {
            _mixer.DataAvailable -= OnAudio;
            try { _mixer.Dispose(); } catch { }
            _mixer = null;
        }
        try { _audioQueue?.CompleteAdding(); } catch { }
        _writer?.Join(1500);
        _writer = null;
        _audioQueue?.Dispose();
        _audioQueue = null;
    }

    // ================= segments =================

    readonly record struct Segment(int Index, string Path);

    List<Segment> Segments()
    {
        var list = new List<Segment>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(BufferDir, "seg_*.ts"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(f);
                if (int.TryParse(name.AsSpan(4), out int idx)) list.Add(new Segment(idx, f));
            }
        }
        catch { }
        list.Sort((x, y) => x.Index.CompareTo(y.Index));
        return list;
    }

    void Cleanup()
    {
        try
        {
            if (!IsRunning) return;
            var segs = Segments();
            if (segs.Count == 0) return;
            int newest = segs[^1].Index;
            int need = (int)Math.Ceiling(_settings().ReplaySeconds / (double)SegmentSeconds) + 3;
            int keepFrom = BufferActive ? newest - need : newest;
            if (Recording && _recordFrom >= 0) keepFrom = Math.Min(keepFrom, _recordFrom);
            keepFrom = Math.Min(keepFrom, _holdFrom);
            foreach (var seg in segs)
            {
                if (seg.Index >= keepFrom) break;
                try { File.Delete(seg.Path); } catch { }
            }
        }
        catch { }
    }

    void ClearBufferDir()
    {
        try
        {
            if (!Directory.Exists(BufferDir)) return;
            foreach (var f in Directory.EnumerateFiles(BufferDir))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // ================= output =================

    static string OutputPath(string kind, AppSettings s)
    {
        Directory.CreateDirectory(s.OutputFolder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
        var path = Path.Combine(s.OutputFolder, $"{kind} {stamp}.{s.Container}");
        int n = 2;
        while (File.Exists(path))
            path = Path.Combine(s.OutputFolder, $"{kind} {stamp} ({n++}).{s.Container}");
        return path;
    }

    async Task<bool> ConcatAsync(List<string> files, string output, AppSettings s)
    {
        // The newest segment may still be being written; if it is unreadable, retry without it.
        if (await ConcatOnceAsync(files, output, s)) return true;
        if (files.Count > 1 && await ConcatOnceAsync(files.Take(files.Count - 1).ToList(), output, s)) return true;
        return false;
    }

    async Task<bool> ConcatOnceAsync(List<string> files, string output, AppSettings s)
    {
        var listFile = Path.Combine(AppSettings.LocalDataDir, $"concat_{Guid.NewGuid():N}.txt");
        try
        {
            Directory.CreateDirectory(AppSettings.LocalDataDir);
            File.WriteAllLines(listFile, files.Select(f => "file '" + f.Replace('\\', '/').Replace("'", @"'\''") + "'"));
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "concat", "-safe", "0", "-i", listFile,
                "-map", "0", "-c", "copy"
            };
            for (int t = 0; t < _trackNames.Length; t++)
            {
                args.AddRange(new[] { $"-metadata:s:a:{t}", $"title={_trackNames[t]}" });
                args.AddRange(new[] { $"-metadata:s:a:{t}", $"handler_name={_trackNames[t]}" });
                args.AddRange(new[] { $"-disposition:a:{t}", t == 0 ? "default" : "0" });
            }
            if (s.Container == "mp4")
            {
                args.AddRange(new[] { "-movflags", "+faststart" });
                if (Encoder?.Def.Codec == "hevc") args.AddRange(new[] { "-tag:v", "hvc1" });
            }
            args.Add(output);
            var (code, err) = await Ffmpeg.RunAsync(args, TimeSpan.FromMinutes(30));
            if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 0) return true;
            AddLog("Save failed: " + err.Trim());
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            return false;
        }
        finally
        {
            try { File.Delete(listFile); } catch { }
        }
    }

    void AddLog(string line)
    {
        lock (_log)
        {
            _log.AddLast(line);
            while (_log.Count > 40) _log.RemoveFirst();
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        StopProcess();
        ClearBufferDir();
    }
}
