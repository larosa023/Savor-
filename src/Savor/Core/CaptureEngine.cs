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

    Process? _proc;      // writes the replay pieces (with audio: adds the sound to the video coming from _capProc)
    Process? _capProc;   // with audio: grabs and encodes the screen on its own, so nothing can hold it back
    Thread? _relayIn, _relayOut;
    long _relayBytes;    // encoded video waiting between the two processes
    System.IO.Pipes.NamedPipeServerStream? _audioPipe;
    AudioMixer? _mixer;
    BlockingCollection<(byte[] data, long heard)>? _audioQueue; // chunk + when its first sample was heard (Stopwatch ticks)
    int _audioRate = 48000, _audioFrameBytes = 8;
    Thread? _writer;
    volatile bool _audioConnected;
    bool _audioOn;
    volatile bool _expectedExit;

    int _recordFrom = -1;
    volatile int _holdFrom = int.MaxValue;

    // ---- self-healing: reconnect after display/audio/driver interruptions, keeping the buffer ----
    int _recoverScheduled;
    int _failures;               // consecutive starts that died quickly
    bool _failNotified;
    int _lastNewest = -1;
    long _lastSegmentTicks;
    const int StallSeconds = 12; // no new 2 s segment for this long = stuck

    /// <summary>Raised when capture is working again after an interruption that was reported (any thread).</summary>
    public event Action? Recovered;

    public string BufferDir { get; } = Path.Combine(AppSettings.LocalDataDir, "buffer");
    public EncoderChoice? Encoder { get; set; }
    public DisplayInfo? Display { get; set; }
    /// <summary>The GPU can resize frames for the current encoder and output size (checked by the app).</summary>
    public bool GpuScale { get; set; }

    public bool BufferActive { get; private set; }
    public bool Recording { get; private set; }
    public DateTime RecordingStartedUtc { get; private set; }

    // ---- markers (marker hotkey) ----
    readonly List<DateTime> _markers = new();

    /// <summary>Marks this moment for the next clip / the recording. False when nothing is being captured.</summary>
    public bool AddMarker()
    {
        if (!BufferActive && !Recording) return false;
        lock (_markers)
        {
            var now = DateTime.UtcNow;
            _markers.Add(now);
            // keep what a clip or the running recording could still include
            var keepAfter = Recording ? RecordingStartedUtc.AddMinutes(-1) : now.AddMinutes(-15);
            _markers.RemoveAll(m => m < keepAfter);
        }
        return true;
    }

    static readonly System.Text.RegularExpressions.Regex DurationRx = new(@"Duration: (\d+):(\d+):([\d.]+)");

    /// <summary>
    /// Marker times as seconds into a saved file. The file ends when its last piece was last written, and its
    /// length comes from the file itself (piece creation times can't be trusted: Windows reuses them for
    /// re-created file names).
    /// </summary>
    async Task<double[]> MarkersInAsync(string output, string lastSegment)
    {
        lock (_markers) if (_markers.Count == 0) return Array.Empty<double>();
        try
        {
            var end = File.GetLastWriteTimeUtc(lastSegment);
            var (_, err) = await Ffmpeg.RunAsync(new[] { "-hide_banner", "-nostdin", "-i", output }, TimeSpan.FromSeconds(10));
            var m = DurationRx.Match(err);
            if (!m.Success) return Array.Empty<double>();
            double dur = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                         double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            var start = end - TimeSpan.FromSeconds(dur);
            lock (_markers)
                return _markers.Where(t => t >= start && t <= end.AddSeconds(1))
                               .Select(t => Math.Clamp((t - start).TotalSeconds, 0, dur)).ToArray();
        }
        catch { return Array.Empty<double>(); }
    }
    string? _recordGame; // the game in focus when recording started (names the file)
    public DateTime StartedUtc { get; private set; }
    public bool IsRunning => _proc is { HasExited: false };
    public AudioMixer? Mixer => _mixer;
    string[] _trackNames = Array.Empty<string>();
    int _inputSlots, _micSlot = -1; // stereo pairs sent to ffmpeg, and which one is the mic (noise suppression)
    bool _micIsTrack;
    /// <summary>Audio tracks in the current capture, in file order.</summary>
    public IReadOnlyList<string> TrackNames => _trackNames;

    public TimeSpan CpuTime
    {
        get
        {
            TimeSpan t = TimeSpan.Zero;
            foreach (var p in new[] { _proc, _capProc })
                try { if (p is { HasExited: false }) t += p.TotalProcessorTime; } catch { }
            return t;
        }
    }

    /// <summary>Raised whenever BufferActive / Recording / running state changes (any thread).</summary>
    public event Action? StateChanged;
    /// <summary>Raised when capture fails unexpectedly (any thread).</summary>
    public event Action<string>? Failed;

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
            StartProcessIfNeeded(fresh: !Recording);
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
            if (!BufferActive) return null; // works during a reconnect too: the buffered pieces are still there
            var s = _settings();
            var segs = Segments();
            if (segs.Count == 0) return null;

            int need = (int)Math.Ceiling(s.ReplaySeconds / (double)SegmentSeconds) + 1;
            var pick = segs.Skip(Math.Max(0, segs.Count - need)).ToList();
            _holdFrom = pick[0].Index;
            try
            {
                var game = ClipMeta.CleanGame(s.GameName(ForegroundApp.CurrentName()));
                var output = OutputPath(game ?? "Clip", s);
                if (!await ConcatAsync(pick.Select(p => p.Path).ToList(), output, s)) return null;
                var markers = await MarkersInAsync(output, pick[^1].Path);
                new ClipMeta(ClipMeta.Clip, game) { Markers = markers }.Write(output);
                return output;
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
            bool fresh = !IsRunning && !BufferActive;
            StartProcessIfNeeded(fresh: fresh);
            var segs = Segments();
            _recordFrom = fresh || segs.Count == 0 ? 0 : segs[^1].Index;
            RecordingStartedUtc = DateTime.UtcNow;
            _recordGame = ClipMeta.CleanGame(_settings().GameName(ForegroundApp.CurrentName()));
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
                var game = _recordGame ?? ClipMeta.CleanGame(s.GameName(ForegroundApp.CurrentName()));
                var output = OutputPath(game != null ? game + " Recording" : "Recording", s);
                if (await ConcatAsync(segs.Select(x => x.Path).ToList(), output, s))
                {
                    result = output;
                    new ClipMeta(ClipMeta.Recording, game) { Markers = await MarkersInAsync(output, segs[^1].Path) }.Write(output);
                }
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
            StartProcessIfNeeded(fresh: true); // new settings (tracks, size) can't be joined to old pieces
            return true;
        }
        finally { _op.Release(); StateChanged?.Invoke(); }
    }

    public async Task ShutdownAsync()
    {
        Interlocked.Exchange(ref _recoverScheduled, 1); // no reconnects while closing
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

    int _startNumber;

    /// <param name="fresh">Start a new buffer (true) or continue after an interruption, keeping what's buffered (false).</param>
    void StartProcessIfNeeded(bool fresh)
    {
        if (IsRunning) return;
        if (Ffmpeg.Path == null) throw new InvalidOperationException("FFmpeg was not found.");
        var enc = Encoder ?? throw new InvalidOperationException("No working video encoder was found on this PC.");
        var s = _settings();

        if (fresh) ClearBufferDir();
        Directory.CreateDirectory(BufferDir);
        var existing = Segments();
        _startNumber = existing.Count > 0 ? existing[^1].Index + 1 : 0; // continue numbering so pieces stay in order
        _lastNewest = -1;
        _lastSegmentTicks = Environment.TickCount64;
        lock (_log) _log.Clear();

        // ---- audio ----
        bool audio = s.DesktopAudio || !string.IsNullOrEmpty(s.MicDeviceId) || (s.SeparateAudioTracks && s.AppTracks.Count > 0);
        int rate = 48000;
        if (audio)
        {
            try
            {
                _mixer = new AudioMixer(s.DesktopAudio, s.DesktopVolume, s.MicDeviceId, s.MicVolume,
                                        s.SeparateAudioTracks ? s.AppTracks : null)
                {
                    SeparateTracks = s.SeparateAudioTracks,
                    MicMixedLater = s.MicNoiseSuppression && !string.IsNullOrEmpty(s.MicDeviceId) && NoiseModel.Directory != null,
                    DesktopMuted = s.DesktopMuted,
                    MicMuted = s.MicMuted,
                };
                rate = _mixer.SampleRate;
                _mixer.DeviceLost += () => ScheduleRecovery("audio device changed", 800);
                _audioQueue = new BlockingCollection<(byte[], long)>(new ConcurrentQueue<(byte[], long)>(), 1024);
                _audioRate = rate;
                _audioFrameBytes = _mixer.OutputChannels * 4;
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
        _inputSlots = audio && _mixer != null ? _mixer.InputSlots : 0;
        _micSlot = audio && _mixer != null ? _mixer.MicSlot : -1;
        _micIsTrack = audio && _mixer != null && _mixer.MicIsTrack;
        bool split = audio && _audioQueue != null; // with audio: separate screen-capture process (see ArgsMode)
        string? pipeName = split ? $"savor-audio-{Environment.ProcessId}-{Guid.NewGuid():N}" : null;
        Process? cap = null;
        if (split)
        {
            try
            {
                _audioPipe = new System.IO.Pipes.NamedPipeServerStream(pipeName!, System.IO.Pipes.PipeDirection.Out, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 0, 1 << 16);
            }
            catch (Exception ex)
            {
                AddLog("Audio pipe unavailable: " + ex.Message);
                TeardownAudio();
                throw;
            }
        }
        var args = BuildArgs(s, enc, audio, rate, split ? ArgsMode.Mux : ArgsMode.Single, pipeName != null ? @"\\.\pipe\" + pipeName : null);
        var p = new Process { StartInfo = Ffmpeg.StartInfo(args, redirectInput: true), EnableRaisingEvents = true };
        if (_micSlot >= 0 && NoiseModel.Directory is { } nd) p.StartInfo.WorkingDirectory = nd; // arnndn finds its model here
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AddLog(e.Data!); };
        p.OutputDataReceived += (_, _) => { };
        p.Exited += OnProcessExited;
        _expectedExit = false;
        try
        {
            if (split)
            {
                cap = new Process { StartInfo = Ffmpeg.StartInfo(BuildArgs(s, enc, audio, rate, ArgsMode.Capture), redirectInput: true), EnableRaisingEvents = true };
                cap.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AddLog(e.Data!); };
                cap.Exited += OnProcessExited;
                cap.Start();
                ChildProcesses.Track(cap);
            }
            p.Start();
        }
        catch
        {
            if (cap != null) { try { if (!cap.HasExited) cap.Kill(true); } catch { } cap.Dispose(); }
            TeardownAudio();
            p.Dispose();
            throw;
        }
        ChildProcesses.Track(p);
        foreach (var pr in new[] { p, cap })
            if (pr != null)
                try { pr.PriorityClass = ProcessPriorityClass.High; } catch { try { pr.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { } }
        // Windows only accepts a GPU priority once the process has opened the GPU (the screen capture does that
        // a moment after start), so try a few times.
        var gpuProc = cap ?? p;
        GpuPriority = "pending";
        _ = Task.Run(async () =>
        {
            foreach (var wait in new[] { 1500, 2500, 4000, 8000 })
            {
                await Task.Delay(wait);
                if (gpuProc.HasExited || !ReferenceEquals(_proc, p)) return;
                var r = RaiseGpuPriority(gpuProc);
                GpuPriority = r;
                if (!r.StartsWith("normal")) break;
            }
            AddLog($"Capture GPU priority: {GpuPriority}");
        });
        _audioOn = audio;
        if (split && cap != null && _audioPipe != null && _audioQueue != null)
        {
            // encoded screen: capture process → Savor → mux process
            Interlocked.Exchange(ref _relayBytes, 0);
            (_relayIn, _relayOut) = VideoRelay.Start(cap.StandardOutput.BaseStream, p.StandardInput.BaseStream,
                                                     n => Interlocked.Add(ref _relayBytes, n));

            // audio goes over the named pipe once the mux process opens it
            var pipe = _audioPipe;
            var q = _audioQueue;
            _writer = new Thread(() =>
            {
                try { pipe.WaitForConnectionAsync().Wait(); }
                catch { _audioConnected = false; return; }
                while (q.TryTake(out _)) { } // audio from before the pipe opened can no longer be placed correctly
                WriterLoop(pipe, q);
            }) { IsBackground = true, Name = "Savor audio feed", Priority = ThreadPriority.Highest };
            _audioConnected = true;
            _writer.Start();
        }
        else if (audio && _audioQueue != null)
        {
            // Audio goes to ffmpeg over stdin (raw 32-bit float PCM).
            var stdin = p.StandardInput.BaseStream;
            var q = _audioQueue;
            _writer = new Thread(() => WriterLoop(stdin, q)) { IsBackground = true, Name = "Savor audio feed", Priority = ThreadPriority.Highest };
            _audioConnected = true;
            _writer.Start();
        }
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        if (cap != null) cap.BeginErrorReadLine();
        _capProc = cap;
        _proc = p;
        StartedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Audio graph for mic noise suppression: splits the interleaved input into stereo pairs, runs RNNoise on the
    /// mic pair and mixes the cleaned mic into track 1. Outputs [a0] (mix), [mt] (mic track, if any) and [r{n}].
    /// </summary>
    public static string DenoiseGraph(int slots, int micSlot, bool micIsTrack, string input = "[1:a]")
    {
        var fc = new System.Text.StringBuilder($"{input}asplit={slots}");
        for (int t = 0; t < slots; t++) fc.Append($"[s{t}]");
        for (int t = 0; t < slots; t++) fc.Append($";[s{t}]pan=stereo|c0=c{t * 2}|c1=c{t * 2 + 1},aresample=48000[r{t}]");
        fc.Append($";[r{micSlot}]arnndn=m={NoiseModel.FileName}");
        fc.Append(micIsTrack ? ",asplit=2[mt][mm]" : "[mm]");
        fc.Append(";[r0][mm]amix=inputs=2:normalize=0:duration=first[a0]");
        return fc.ToString();
    }

    /// <summary>Extra shift measured by "Check audio sync" on this PC (seconds; positive moves audio earlier).</summary>
    public double AudioSyncCorrection { get; set; }

    // ---- GPU priority ----
    // When a game keeps the GPU at 100%, Windows schedules the capture engine's GPU work (grabbing the screen,
    // converting frames for the encoder) behind the game's, so frames get late or skipped and clips stutter.
    // Like OBS, raise the capture engine's GPU scheduling priority: "realtime" needs Savor to run as administrator,
    // "high" works without it.

    /// <summary>GPU scheduling priority the capture engine got: "realtime", "high" or "normal".</summary>
    public string GpuPriority { get; private set; } = "normal";

    static string RaiseGpuPriority(Process p)
    {
        int rt = -1, hi = -1;
        try
        {
            if ((rt = D3DKMTSetProcessSchedulingPriorityClass(p.Handle, 5 /* REALTIME */)) == 0) return "realtime";
            if ((hi = D3DKMTSetProcessSchedulingPriorityClass(p.Handle, 4 /* HIGH */)) == 0) return "high";
        }
        catch (Exception ex) { return "normal (" + ex.GetType().Name + ")"; }
        return $"normal (Windows said 0x{rt:X8} / 0x{hi:X8})";
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

    // ---- capture method ----
    /// <summary>Capture the screen with Windows Graphics Capture instead of Desktop Duplication.</summary>
    public bool UseGraphicsCapture { get; set; }
    /// <summary>Raised when Windows Graphics Capture keeps failing on this PC and Savor went back to Desktop Duplication.</summary>
    public event Action? GraphicsCaptureFailed;

    static ulong? MonitorHandle(DisplayInfo d)
    {
        try { return (ulong)MonitorFromPoint(new POINT { X = d.X + d.Width / 2, Y = d.Y + d.Height / 2 }, 2 /* NEAREST */); }
        catch { return null; }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    /// <summary>Keeps the piped audio locked to its timestamps (fills or trims tiny gaps smoothly).</summary>
    public const string AudioSync = "aresample=async=1000";

    /// <summary>
    /// The piped raw-audio input: stamped when ffmpeg reads it, minus the fixed release delay (see WriterLoop) and
    /// the 1024-sample packet ffmpeg waits for, so each sample's time is when it was heard.
    /// </summary>
    public static string[] AudioInputArgs(int rate, int channels, double correction = 0, string input = "pipe:0") => new[]
    {
        "-itsoffset", (-(AudioDelay + 1024.0 / rate + correction)).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        "-use_wallclock_as_timestamps", "1", "-probesize", "32", "-analyzeduration", "0",
        "-f", "f32le", "-ar", rate.ToString(), "-ac", channels.ToString(), "-i", input
    };

    // ---- two-process capture (with audio) ----
    // Audio is deliberately written AudioDelay late (see WriterLoop), and ffmpeg never lets one stream get more than
    // 0.1 s ahead of another: in a single process it kept pausing the screen grab to wait for the audio, so screen
    // frames were lost (clips froze ~0.3 s every ~0.4 s). Now one ffmpeg grabs and encodes the screen with nothing
    // to wait for, and Savor hands the encoded video (NUT over a pipe, timestamps intact) to a second ffmpeg that adds
    // the audio and writes the pieces. The video waits there instead, already encoded, so no frame is lost.
    public enum ArgsMode { Single, Capture, Mux }

    List<string> BuildArgs(AppSettings s, EncoderChoice enc, bool audio, int rate, ArgsMode mode = ArgsMode.Single, string? audioPipe = null)
    {
        var d = Display;
        int fps = s.Fps;
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-y" };
        if (audio && mode != ArgsMode.Capture) a.Add("-nostdin"); // stdin carries data, not keyboard commands
        // With audio, screen frames and audio are both stamped on the system clock and kept as-is (-copyts), so they
        // line up exactly; -output_ts_offset below brings the timeline back to start near zero.
        if (audio) a.Add("-copyts");
        var clock = audio ? new[] { "-use_wallclock_as_timestamps", "1" } : Array.Empty<string>();

        if (mode == ArgsMode.Mux)
        {
            // the encoded screen from the capture process, timestamps as captured
            a.AddRange(new[] { "-probesize", "32", "-analyzeduration", "0", "-f", "nut", "-i", "pipe:0" });
        }
        else if (enc.Pipeline == CapturePipeline.Gdi)
        {
            a.AddRange(clock);
            a.AddRange(new[] { "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", s.CaptureCursor ? "1" : "0" });
            if (d != null)
                a.AddRange(new[] { "-offset_x", d.X.ToString(), "-offset_y", d.Y.ToString(), "-video_size", $"{d.Width}x{d.Height}" });
            a.AddRange(new[] { "-i", "desktop" });
        }
        else
        {
            int idx = d?.OutputIndex ?? 0;
            a.AddRange(clock);
            if (UseGraphicsCapture && d != null && MonitorHandle(d) is { } hmon && hmon != 0)
            {
                // Windows Graphics Capture: sees video players / browsers that draw on hardware overlay planes, which
                // Desktop Duplication can miss (choppy captures of streams). Same D3D11 frames, same encoder paths.
                a.AddRange(new[] { "-f", "lavfi", "-i",
                    $"gfxcapture=hmonitor={hmon}:max_framerate={fps}:capture_cursor={(s.CaptureCursor ? 1 : 0)}" });
            }
            else
                a.AddRange(new[] { "-f", "lavfi", "-i",
                    $"ddagrab=output_idx={idx}:framerate={fps}:draw_mouse={(s.CaptureCursor ? 1 : 0)}" });
        }

        if (audio)
        {
            // A/V sync: raw audio over a pipe has no timestamps of its own, so ffmpeg would just count samples from the
            // first one it reads. Audio buffered while ffmpeg was starting up then made the whole soundtrack late, and
            // sound-card vs system clock differences drifted over long recordings. Instead each chunk is stamped with
            // the time it arrives (same clock as the screen capture) and aresample=async keeps it locked to that:
            // start-up backlog is dropped, drift is gently corrected. -30 ms covers the remaining capture/pipe latency
            // (measured: ~±8 ms afterwards, vs a constant 300 ms off before).
            if (mode != ArgsMode.Capture)
                a.AddRange(AudioInputArgs(rate, Math.Max(_inputSlots, _trackNames.Length) * 2, AudioSyncCorrection, audioPipe ?? "pipe:0"));
        }

        if (mode == ArgsMode.Capture)
        {
            // encode only, as smooth constant-rate video (system-clock timestamps start far from zero, so the fps
            // filter rather than -fps_mode cfr, which would try to fill from zero)
            var cvf = Ffmpeg.VideoFilter(enc, Ffmpeg.OutputSize(d, s.OutputHeight), GpuScale);
            a.AddRange(new[] { "-map", "0:v", "-vf", $"fps={fps}" + (cvf != null ? "," + cvf : "") });
            a.AddRange(Ffmpeg.EncoderArgs(enc.Def, s.Quality, fps));
            a.AddRange(new[] { "-f", "nut", "pipe:1" });
            return a;
        }

        int tracks = audio ? Math.Max(1, _trackNames.Length) : 0;
        if (audio && _micSlot > 0)
        {
            // noise suppression: clean the mic with RNNoise, then mix it into track 1 here
            a.AddRange(new[] { "-filter_complex", DenoiseGraph(_inputSlots, _micSlot, _micIsTrack, $"[1:a]{AudioSync},") });
            a.AddRange(new[] { "-map", "0:v" });
            for (int t = 0; t < tracks; t++)
                a.AddRange(new[] { "-map", t == 0 ? "[a0]" : t == _micSlot ? "[mt]" : $"[r{t}]" });
        }
        else if (tracks > 1)
        {
            // split the interleaved 6-channel input into one stereo stream per track
            var fc = new System.Text.StringBuilder($"[1:a]{AudioSync},asplit={tracks}");
            for (int t = 0; t < tracks; t++) fc.Append($"[s{t}]");
            for (int t = 0; t < tracks; t++) fc.Append($";[s{t}]pan=stereo|c0=c{t * 2}|c1=c{t * 2 + 1}[a{t}]");
            a.AddRange(new[] { "-filter_complex", fc.ToString() });
        }
        else if (tracks == 1)
        {
            a.AddRange(new[] { "-filter_complex", $"[1:a]{AudioSync}[a0]" });
        }
        if (!(audio && _micSlot > 0))
        {
            a.AddRange(new[] { "-map", "0:v" });
            if (tracks == 1) a.AddRange(new[] { "-map", "[a0]" });
            for (int t = 0; tracks > 1 && t < tracks; t++) a.AddRange(new[] { "-map", $"[a{t}]" });
        }

        if (mode == ArgsMode.Mux)
            a.AddRange(new[] { "-c:v", "copy" });
        else
        {
            var vf = Ffmpeg.VideoFilter(enc, Ffmpeg.OutputSize(d, s.OutputHeight), GpuScale);
            if (vf != null) a.AddRange(new[] { "-vf", vf });
            a.AddRange(Ffmpeg.EncoderArgs(enc.Def, s.Quality, fps));
        }

        // (no -shortest: for live capture it only adds a queue that can hold raw video frames while waiting on audio)
        if (audio) a.AddRange(new[] { "-c:a", "aac", "-b:a", tracks > 1 ? "160k" : "192k", "-ar", "48000" });

        if (audio)
        {
            // system-clock timestamps are seconds since 1970: shift so this run starts near zero
            var epoch = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
            a.AddRange(new[] { "-output_ts_offset", (-epoch).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) });
        }
        a.AddRange(new[]
        {
            "-f", "segment",
            "-segment_time", SegmentSeconds.ToString(),
            "-segment_format", "mpegts",
            "-segment_start_number", _startNumber.ToString(),
            Path.Combine(BufferDir, "seg_%06d.ts")
        });
        return a;
    }

    void StopProcess()
    {
        var p = _proc;
        var cap = _capProc;
        if (p == null && cap == null) return;
        _expectedExit = true;
        try
        {
            if (cap != null)
            {
                // two processes: stop the screen capture ('q' finishes its video cleanly), end the audio, and the
                // mux process finishes its last piece once both inputs have ended
                try { if (!cap.HasExited) { cap.StandardInput.Write('q'); cap.StandardInput.Flush(); } } catch { }
                _mixer?.Stop();
                try { _audioQueue?.CompleteAdding(); } catch { }
                if (_audioPipe is { IsConnected: false }) try { _audioPipe.Dispose(); } catch { } // writer still waiting to connect
                _writer?.Join(2000);
                try { _audioPipe?.Dispose(); } catch { }
                if (!cap.WaitForExit(8000)) { try { cap.Kill(true); } catch { } cap.WaitForExit(2000); }
                _relayIn?.Join(2000);
                _relayOut?.Join(3000);
                if (p != null && !p.HasExited && !p.WaitForExit(8000)) { try { p.Kill(true); } catch { } p.WaitForExit(2000); }
            }
            else if (p != null && !p.HasExited)
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
            try { p?.Dispose(); } catch { }
            try { cap?.Dispose(); } catch { }
            _proc = null;
            _capProc = null;
            _relayIn = _relayOut = null;
        }
    }

    void OnProcessExited(object? sender, EventArgs e)
    {
        if (_expectedExit || !(ReferenceEquals(sender, _proc) || (_capProc != null && ReferenceEquals(sender, _capProc)))) return;
        // died within 10 s of starting = a failed start; otherwise a one-off interruption
        bool quick = (DateTime.UtcNow - StartedUtc).TotalSeconds < 10;
        if (quick) Interlocked.Increment(ref _failures);
        int f = Volatile.Read(ref _failures);
        int delay = f == 0 ? 500 : (int)Math.Min(30_000, 1000 * Math.Pow(2, f - 1));
        ScheduleRecovery(FriendlyError(RecentLog()), delay);
    }

    /// <summary>
    /// Restarts capture after an interruption (display mode change, secure screen, audio device switch,
    /// driver reset, sleep). Keeps the replay buffer and an ongoing recording; new pieces continue where
    /// the old ones stopped, so saved clips and recordings just have a short gap.
    /// </summary>
    void ScheduleRecovery(string reason, int delayMs)
    {
        if (Interlocked.Exchange(ref _recoverScheduled, 1) == 1) return;
        AddLog("Reconnecting: " + reason);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            bool notify = false;
            string message = reason;
            await _op.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref _recoverScheduled, 0);
                if (!BufferActive && !Recording) return; // turned off meanwhile
                StopProcess();
                try { StartProcessIfNeeded(fresh: false); }
                catch (Exception ex)
                {
                    message = ex.Message;
                    Interlocked.Increment(ref _failures);
                    int f = Volatile.Read(ref _failures);
                    if (f >= 2 && UseGraphicsCapture)
                    {
                        // Graphics Capture isn't working here: go back to Desktop Duplication
                        UseGraphicsCapture = false;
                        AddLog("Windows Graphics Capture failed twice: switching back to Desktop Duplication.");
                        GraphicsCaptureFailed?.Invoke();
                    }
                    ScheduleRecovery(message, (int)Math.Min(30_000, 1000 * Math.Pow(2, f - 1)));
                }
                if (Volatile.Read(ref _failures) >= 3 && !_failNotified) { _failNotified = true; notify = true; }
            }
            finally { _op.Release(); }
            StateChanged?.Invoke();
            if (notify) Failed?.Invoke(message + " Savor keeps retrying and will resume on its own.");
        });
    }

    // ---- memory guard ----
    const long MemoryLimit = 2L << 30;  // normal is roughly 100–400 MB; anything near this means frames are piling up
    long _lastMemCheck;
    /// <summary>The capture engine's private memory at the last check (for diagnostics).</summary>
    public long EngineMemory { get; private set; }
    /// <summary>Raised (once per session) when the capture engine was restarted for using too much memory.</summary>
    public event Action<long>? MemoryRestarted;
    /// <summary>Raised when an encoder is moved off direct GPU capture because of memory use (id of the encoder).</summary>
    public event Action<string>? DirectCaptureDisabled;
    bool _memNotified;

    void CheckMemory()
    {
        long now = Environment.TickCount64;
        if (now - _lastMemCheck < 3000) return;
        _lastMemCheck = now;
        var p = _proc;
        if (p == null) return;
        try
        {
            long mem = 0;
            foreach (var pr in new[] { p, _capProc })
            {
                if (pr == null) continue;
                pr.Refresh();
                if (pr.HasExited) return;
                mem += pr.PrivateMemorySize64;
            }
            // encoded video waiting between the two processes counts too (normally a few MB)
            EngineMemory = mem + Interlocked.Read(ref _relayBytes);
        }
        catch { return; }
        if (EngineMemory < MemoryLimit) return;
        AddLog($"Capture engine using {EngineMemory >> 20} MB of memory: restarting it.");
        long mem = EngineMemory;
        if (Encoder is { Pipeline: CapturePipeline.DdaDirect } enc && enc.Def.Vendor != "cpu")
        {
            // direct GPU frames are what piles up on some cards/drivers: use the compatibility path from now on
            Encoder = new EncoderChoice(enc.Def, CapturePipeline.DdaDownload);
            AddLog($"Switching {enc.Def.Name} to compatibility capture (frames through system memory).");
            DirectCaptureDisabled?.Invoke(enc.Def.Id);
        }
        ScheduleRecovery("The capture engine was using too much memory.", 0);
        if (!_memNotified) { _memNotified = true; MemoryRestarted?.Invoke(mem); }
    }

    /// <summary>Called every second: notices when capture is producing again, or has silently stalled.</summary>
    void Watchdog(List<Segment> segs)
    {
        if (!IsRunning) return;
        CheckMemory();
        int newest = segs.Count > 0 ? segs[^1].Index : -1;
        long now = Environment.TickCount64;
        if (newest != _lastNewest)
        {
            if (_lastNewest >= 0 || newest > _startNumber)
            {
                // new pieces are arriving: capture is healthy
                if (Interlocked.Exchange(ref _failures, 0) > 0 && _failNotified)
                {
                    _failNotified = false;
                    Recovered?.Invoke();
                }
            }
            _lastNewest = newest;
            _lastSegmentTicks = now;
            return;
        }
        if (now - _lastSegmentTicks > StallSeconds * 1000 && (DateTime.UtcNow - StartedUtc).TotalSeconds > StallSeconds)
        {
            _lastSegmentTicks = now; // don't fire again while recovering
            AddLog("Capture stalled (no new video for 12 s).");
            ScheduleRecovery("Capture stopped responding (for example after sleep).", 0);
        }
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

    // ---- A/V sync ----
    // Audio reaches Savor in bursts, 50–150+ ms after it was heard (Windows hands loopback audio over in batches).
    // ffmpeg stamps each piece with the time it reads it, on the same clock as the screen frames. So Savor releases
    // every 10 ms piece exactly AudioDelay after it was heard, and ffmpeg subtracts that known delay (-itsoffset):
    // the audio lands where it was heard, whatever the batching. (Measured in a simulation of bursty capture: within
    // ~15 ms, versus 110–180 ms late when audio was written as it arrived.)
    const double AudioDelay = 0.4;      // seconds: comfortably more than the capture latency (even under heavy load)
    const double DeviceLatency = 0.010; // the newest sample in a batch was heard about this long before it arrived
    const int PieceFrames = 480;        // 10 ms at 48 kHz

    void OnAudio(byte[] data, int count)
    {
        var q = _audioQueue;
        if (!_audioConnected || q == null || q.IsAddingCompleted) return;
        var copy = new byte[count];
        Buffer.BlockCopy(data, 0, copy, 0, count);
        double secs = count / (double)_audioFrameBytes / _audioRate + DeviceLatency;
        long heard = Stopwatch.GetTimestamp() - (long)(secs * Stopwatch.Frequency);
        try { q.TryAdd((copy, heard)); } catch (InvalidOperationException) { }
    }

    void WriterLoop(Stream output, BlockingCollection<(byte[] data, long heard)> q)
    {
        timeBeginPeriod(1); // 1 ms sleeps while capturing, so pieces go out on time
        try
        {
            int pieceBytes = PieceFrames * _audioFrameBytes;
            long delay = (long)(AudioDelay * Stopwatch.Frequency);
            foreach (var (buf, heard) in q.GetConsumingEnumerable())
            {
                for (int off = 0, k = 0; off < buf.Length; off += pieceBytes, k++)
                {
                    long due = heard + (long)((double)k * PieceFrames / _audioRate * Stopwatch.Frequency) + delay;
                    while (true)
                    {
                        long wait = due - Stopwatch.GetTimestamp();
                        if (wait <= 0) break;
                        int ms = (int)(wait * 1000 / Stopwatch.Frequency);
                        if (ms >= 2) Thread.Sleep(ms - 1); else Thread.SpinWait(200);
                    }
                    output.Write(buf, off, Math.Min(pieceBytes, buf.Length - off));
                }
                output.Flush();
            }
        }
        catch { /* ffmpeg exited */ }
        finally { _audioConnected = false; timeEndPeriod(1); }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

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
        try { _audioPipe?.Dispose(); } catch { } // also ends a writer still waiting for the pipe to open
        _audioPipe = null;
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
            Watchdog(segs);
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
        kind = ClipMeta.SafeFileName(kind);
        if (kind.Length == 0) kind = "Clip";
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

/// <summary>
/// Copies the capture process's encoded video to the mux process. Reading and writing run on separate threads with
/// a queue between them, so the capture process never waits, however long the mux process holds its input.
/// </summary>
public static class VideoRelay
{
    /// <param name="queued">Called with +bytes when a chunk is queued and -bytes when it has been passed on.</param>
    public static (Thread reader, Thread writer) Start(Stream from, Stream to, Action<long>? queued = null)
    {
        var chunks = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        var reader = new Thread(() =>
        {
            var buf = new byte[1 << 16];
            try
            {
                int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                {
                    chunks.Add(buf.AsSpan(0, n).ToArray());
                    queued?.Invoke(n);
                }
            }
            catch { }
            finally { chunks.CompleteAdding(); }
        }) { IsBackground = true, Name = "Savor video relay in", Priority = ThreadPriority.Highest };
        var writer = new Thread(() =>
        {
            try
            {
                foreach (var c in chunks.GetConsumingEnumerable())
                {
                    to.Write(c, 0, c.Length);
                    to.Flush();
                    queued?.Invoke(-c.Length);
                }
            }
            catch { }
            finally { try { to.Close(); } catch { } } // end of video: the mux process finishes its last piece
        }) { IsBackground = true, Name = "Savor video relay out", Priority = ThreadPriority.AboveNormal };
        reader.Start();
        writer.Start();
        return (reader, writer);
    }
}
