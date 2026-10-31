using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Savor.Core;

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Captures desktop audio (WASAPI loopback) and an optional microphone, mixes them to
/// 32-bit float stereo, and raises <see cref="DataAvailable"/> with the mixed PCM.
/// The loopback stream is the master clock; a silent output stream keeps it ticking
/// when nothing is playing so the audio track never has gaps.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    readonly MMDeviceEnumerator _enumerator = new();
    readonly string _renderId;
    DeviceWatcher? _watcher;
    Timer? _stallTimer;
    long _lastLoopTicks;
    volatile bool _stopping;
    int _lostRaised;

    /// <summary>
    /// The output device went away, changed, or stopped delivering audio (headphones unplugged, default
    /// device switched, driver reset). The owner should rebuild the mixer. Raised at most once, on any thread.
    /// </summary>
    public event Action? DeviceLost;

    void RaiseDeviceLost()
    {
        if (_stopping || Interlocked.Exchange(ref _lostRaised, 1) == 1) return;
        DeviceLost?.Invoke();
    }
    readonly WasapiLoopbackCapture _loopback;
    readonly WasapiOut? _keepAlive;
    readonly int _loopChannels, _loopBits;
    readonly bool _loopFloat;

    readonly WasapiCapture? _mic;
    readonly FloatRing? _micRing;
    readonly ISampleProvider? _micSamples;
    readonly int _micChannels, _micBits;
    readonly bool _micFloat;

    float[] _desk = Array.Empty<float>();
    float[] _micBuf = Array.Empty<float>();
    float[] _scratch = Array.Empty<float>();
    float[] _out = Array.Empty<float>();
    byte[] _outBytes = Array.Empty<byte>();

    /// <summary>
    /// When true, output is several stereo tracks interleaved: track 1 = mix, then desktop audio,
    /// microphone and one track per chosen app (only the ones that are on). Otherwise a single stereo mix.
    /// Fixed for the life of the mixer (ffmpeg's input layout depends on it).
    /// </summary>
    public bool SeparateTracks { get; init; }

    /// <summary>
    /// Mic noise suppression is done by ffmpeg, so the mic is sent on its own and mixed in there:
    /// the Mix slot then carries everything except the mic, and the mic always has a slot (MicSlot).
    /// </summary>
    public bool MicMixedLater { get; init; }

    /// <summary>Stereo pairs sent to ffmpeg: one per output track, plus a hidden mic pair when needed.</summary>
    public int InputSlots => TrackNames.Count + (MicMixedLater && _micConfigured && _micTrack < 0 ? 1 : 0);
    /// <summary>Which input pair holds the mic (−1 when it isn't sent separately).</summary>
    public int MicSlot => !(MicMixedLater && _micConfigured) ? -1 : (TrackNames.Count > 0 && _micTrack > 0 ? _micTrack : TrackNames.Count);
    /// <summary>True when the mic slot is also an output track (otherwise it only feeds the mix).</summary>
    public bool MicIsTrack => TrackNames.Count > 0 && _micTrack > 0;
    public int OutputChannels => InputSlots * 2;

    string[]? _trackNames;
    int _deskTrack = -1, _micTrack = -1;

    public IReadOnlyList<string> TrackNames
    {
        get
        {
            if (_trackNames != null) return _trackNames;
            var names = new List<string> { "Mix" };
            int sources = (DesktopEnabled ? 1 : 0) + (_micConfigured ? 1 : 0) + _apps.Count;
            if (SeparateTracks && (sources >= 2 || _apps.Count > 0))
            {
                if (DesktopEnabled) { _deskTrack = names.Count; names.Add("Desktop audio"); }
                if (_micConfigured) { _micTrack = names.Count; names.Add("Microphone"); }
                foreach (var a in _apps) { a.TrackIndex = names.Count; names.Add(a.Name); }
            }
            return _trackNames = names.ToArray();
        }
    }

    // ---- per-application tracks ----
    public sealed class AppTrack
    {
        public required string Exe { get; init; }
        public required string Name { get; init; }
        public float Volume { get; set; } = 1f;
        public bool Muted { get; set; }
        public int TrackIndex { get; internal set; } = -1;
        public bool Live => Capture?.IsAlive == true;
        public volatile float Level;
        internal volatile float Max;
        internal FloatRing Ring = null!;
        internal ProcessAudioCapture? Capture;
        internal float[] Buf = Array.Empty<float>();
        public float TakePeak() { var v = Max; Max = 0; return Math.Max(v, Level); }
    }

    readonly List<AppTrack> _apps = new();
    public IReadOnlyList<AppTrack> Apps => _apps;
    Timer? _appWatcher;
    int _attaching;

    public int SampleRate { get; }
    public bool DesktopEnabled { get; set; }
    public float DesktopVolume { get; set; }
    public float MicVolume { get; set; }
    public bool DesktopMuted { get; set; }
    public bool MicMuted { get; set; }
    public bool HasMic => _mic != null;

    /// <summary>Peak levels 0..1, updated per audio packet (read from UI for meters).</summary>
    public volatile float DesktopLevel;
    public volatile float MicLevel;
    // highest peak since the UI last looked, so short transients are never missed between frames
    volatile float _deskMax, _micMax;

    public float TakeDesktopPeak() { var v = _deskMax; _deskMax = 0; return Math.Max(v, DesktopLevel); }
    public float TakeMicPeak() { var v = _micMax; _micMax = 0; return Math.Max(v, MicLevel); }

    public event Action<byte[], int>? DataAvailable;

    readonly bool _micConfigured;

    public AudioMixer(bool desktopEnabled, double desktopVolume, string? micId, double micVolume,
                      IEnumerable<AppAudioSetting>? apps = null)
    {
        // The track layout follows the settings, not whether a device opened right now, so a capture that
        // reconnects after a device change always has the same tracks (and its pieces join cleanly).
        _micConfigured = !string.IsNullOrEmpty(micId);
        DesktopEnabled = desktopEnabled;
        DesktopVolume = (float)desktopVolume;
        MicVolume = (float)micVolume;

        var render = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _renderId = render.ID;
        _loopback = new WasapiLoopbackCapture(render);
        _loopback.RecordingStopped += (_, _) => RaiseDeviceLost();
        var lf = _loopback.WaveFormat;
        SampleRate = lf.SampleRate;
        _loopChannels = lf.Channels;
        _loopBits = lf.BitsPerSample;
        _loopFloat = IsFloat(lf);
        _loopback.DataAvailable += OnLoopback;

        try
        {
            _keepAlive = new WasapiOut(render, AudioClientShareMode.Shared, false, 200);
            _keepAlive.Init(new SilenceProvider(lf));
        }
        catch { _keepAlive = null; }

        if (!string.IsNullOrEmpty(micId))
        {
            try
            {
                var dev = micId == "default"
                    ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    : _enumerator.GetDevice(micId);
                _mic = new WasapiCapture(dev, true, 20);
                var mf = _mic.WaveFormat;
                _micChannels = mf.Channels;
                _micBits = mf.BitsPerSample;
                _micFloat = IsFloat(mf);
                _micRing = new FloatRing(mf.SampleRate, 2);
                ISampleProvider sp = _micRing;
                if (mf.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);
                _micSamples = sp;
                _mic.DataAvailable += OnMic;
            }
            catch
            {
                _mic?.Dispose();
                _mic = null;
                _micRing = null;
                _micSamples = null;
            }
        }

        if (apps != null && ProcessAudioCapture.IsSupported)
        {
            foreach (var a in apps.Take(5))
            {
                _apps.Add(new AppTrack
                {
                    Exe = a.Exe,
                    Name = string.IsNullOrWhiteSpace(a.Name) ? a.Exe : a.Name,
                    Volume = (float)a.Volume,
                    Muted = a.Muted,
                    Ring = new FloatRing(SampleRate, 2),
                });
            }
        }
    }

    /// <summary>Attaches to chosen apps that are running; retried every few seconds so apps opened later get picked up.</summary>
    void AttachApps()
    {
        if (Interlocked.Exchange(ref _attaching, 1) == 1) return;
        try
        {
            foreach (var a in _apps)
            {
                if (a.Live) continue;
                try { a.Capture?.Dispose(); } catch { }
                a.Capture = null;
                if (_stopping) return;
                var pid = ProcessAudioCapture.FindRootProcess(a.Exe);
                if (pid == null) continue;
                try
                {
                    var cap = ProcessAudioCapture.StartAsync(pid.Value, SampleRate).GetAwaiter().GetResult();
                    if (_stopping) { cap.Dispose(); return; } // stopped while we were attaching
                    var ring = a.Ring;
                    cap.DataAvailable += (buf, n) =>
                    {
                        // keep latency bounded if this stream runs ahead of the desktop clock
                        if (ring.Count > SampleRate * 2 / 4) ring.Clear();
                        ring.Write(buf, n);
                    };
                    a.Capture = cap;
                }
                catch { }
            }
        }
        finally { Interlocked.Exchange(ref _attaching, 0); }
    }

    public void Start()
    {
        _keepAlive?.Play();
        _lastLoopTicks = Environment.TickCount64;
        _loopback.StartRecording();
        _mic?.StartRecording();
        // notice device switches and stalls (the silent keep-alive stream means loopback normally never goes quiet)
        try
        {
            _watcher = new DeviceWatcher(_renderId, RaiseDeviceLost);
            _enumerator.RegisterEndpointNotificationCallback(_watcher);
        }
        catch { _watcher = null; }
        // only meaningful when the silent keep-alive stream is running; otherwise quiet periods are normal
        if (_keepAlive != null) _stallTimer = new Timer(_ =>
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastLoopTicks) > 3000) RaiseDeviceLost();
        }, null, 3000, 1000);
        if (_apps.Count > 0)
            _appWatcher = new Timer(_ => AttachApps(), null, 0, 3000);
    }

    public void Stop()
    {
        _stopping = true;
        _stallTimer?.Dispose();
        _stallTimer = null;
        if (_watcher != null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
            _watcher = null;
        }
        try { _loopback.StopRecording(); } catch { }
        try { _mic?.StopRecording(); } catch { }
        try { _keepAlive?.Stop(); } catch { }
        _appWatcher?.Dispose();
        _appWatcher = null;
        foreach (var a in _apps)
        {
            try { a.Capture?.Dispose(); } catch { }
            a.Capture = null;
            a.Level = 0;
        }
        DesktopLevel = 0;
        MicLevel = 0;
    }

    void OnMic(object? sender, WaveInEventArgs e)
    {
        if (_micRing == null || e.BytesRecorded == 0) return;
        int frames = ToStereo(e.Buffer, e.BytesRecorded, _micChannels, _micBits, _micFloat, ref _scratch);
        // Keep mic latency bounded if its clock runs ahead of the loopback clock.
        if (_micRing.Count > _micRing.SampleRate * 2 / 4) _micRing.Clear();
        _micRing.Write(_scratch, frames * 2);
    }

    void OnLoopback(object? sender, WaveInEventArgs e)
    {
        Interlocked.Exchange(ref _lastLoopTicks, Environment.TickCount64);
        if (e.BytesRecorded == 0) return;
        int frames = ToStereo(e.Buffer, e.BytesRecorded, _loopChannels, _loopBits, _loopFloat, ref _desk);
        int n = frames * 2;

        if (_micSamples != null)
        {
            if (_micBuf.Length < n) _micBuf = new float[n];
            int read = _micSamples.Read(_micBuf, 0, n);
            if (read < n) Array.Clear(_micBuf, read, n - read);
        }

        float dv = DesktopEnabled && !DesktopMuted ? DesktopVolume : 0f;
        float mv = MicMuted ? 0f : MicVolume;
        float dPeak = 0, mPeak = 0;
        int ch = OutputChannels;
        int outN = frames * ch;
        if (_out.Length < outN) _out = new float[outN];
        Array.Clear(_out, 0, outN);

        // per-app streams (same sample rate; padded with silence if an app is quiet or closed)
        foreach (var a in _apps)
        {
            if (a.Buf.Length < n) a.Buf = new float[n];
            a.Ring.Read(a.Buf, 0, n);
            float av = a.Muted ? 0f : a.Volume, aPeak = 0;
            for (int i = 0; i < n; i++)
            {
                float v = a.Buf[i] * av;
                a.Buf[i] = v;
                float abs = Math.Abs(v);
                if (abs > aPeak) aPeak = abs;
                if (a.TrackIndex > 0) _out[(i >> 1) * ch + a.TrackIndex * 2 + (i & 1)] = Clip(v);
            }
            a.Level = Math.Min(1f, aPeak);
            if (a.Level > a.Max) a.Max = a.Level;
        }

        bool micLater = MicSlot >= 0;            // mic goes to ffmpeg on its own (noise suppression)
        int micSlot = micLater ? MicSlot : _micTrack;
        for (int i = 0; i < n; i++)
        {
            float d = _desk[i] * dv;
            float m = _micSamples != null ? _micBuf[i] * mv : 0f;
            float ad = Math.Abs(d), am = Math.Abs(m);
            if (ad > dPeak) dPeak = ad;
            if (am > mPeak) mPeak = am;
            int frame = i >> 1, side = i & 1, o = frame * ch + side;
            // desktop audio already contains every app; with desktop off, the mix is built from the chosen apps
            float baseMix = DesktopEnabled ? d : SumApps(i);
            _out[o] = Clip(micLater ? baseMix : baseMix + m);
            if (_deskTrack > 0) _out[o + _deskTrack * 2] = Clip(d);
            if (micSlot > 0) _out[o + micSlot * 2] = Clip(m);
        }
        DesktopLevel = Math.Min(1f, dPeak);
        MicLevel = Math.Min(1f, mPeak);
        if (DesktopLevel > _deskMax) _deskMax = DesktopLevel;
        if (MicLevel > _micMax) _micMax = MicLevel;

        int bytes = outN * 4;
        if (_outBytes.Length < bytes) _outBytes = new byte[bytes];
        Buffer.BlockCopy(_out, 0, _outBytes, 0, bytes);
        DataAvailable?.Invoke(_outBytes, bytes);
    }

    /// <summary>Default-output-device changes (from Windows, on a COM thread).</summary>
    [System.Runtime.InteropServices.ComVisible(true)]
    sealed class DeviceWatcher : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
    {
        readonly string _deviceId;
        readonly Action _lost;
        public DeviceWatcher(string deviceId, Action lost) { _deviceId = deviceId; _lost = lost; }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia && !string.Equals(defaultDeviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
                _lost();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (string.Equals(deviceId, _deviceId, StringComparison.OrdinalIgnoreCase) && newState != DeviceState.Active) _lost();
        }
        public void OnDeviceRemoved(string deviceId)
        {
            if (string.Equals(deviceId, _deviceId, StringComparison.OrdinalIgnoreCase)) _lost();
        }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    static float Clip(float s) => s > 1f ? 1f : (s < -1f ? -1f : s);

    float SumApps(int i)
    {
        float s = 0;
        foreach (var a in _apps) s += a.Buf[i];
        return s;
    }

    static bool IsFloat(WaveFormat f) =>
        f.Encoding == WaveFormatEncoding.IeeeFloat ||
        (f is WaveFormatExtensible ext && ext.SubFormat == FloatSubtype) ||
        (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32 && f is not WaveFormatExtensible);

    /// <summary>Converts raw interleaved audio of any common layout to float stereo. Returns frame count.</summary>
    static int ToStereo(byte[] src, int count, int channels, int bits, bool isFloat, ref float[] dst)
    {
        int bytesPer = bits / 8;
        int frameBytes = bytesPer * channels;
        if (frameBytes == 0) return 0;
        int frames = count / frameBytes;
        if (dst.Length < frames * 2) dst = new float[frames * 2];

        for (int f = 0; f < frames; f++)
        {
            int baseOff = f * frameBytes;
            float l = Sample(src, baseOff, bits, isFloat);
            float r = channels > 1 ? Sample(src, baseOff + bytesPer, bits, isFloat) : l;
            if (channels >= 3)
            {
                // fold the center channel into both sides (5.1 / 7.1 layouts)
                float c = Sample(src, baseOff + 2 * bytesPer, bits, isFloat) * 0.707f;
                l += c; r += c;
            }
            dst[f * 2] = l;
            dst[f * 2 + 1] = r;
        }
        return frames;
    }

    static float Sample(byte[] b, int o, int bits, bool isFloat)
    {
        switch (bits)
        {
            case 32 when isFloat: return BitConverter.ToSingle(b, o);
            case 32: return BitConverter.ToInt32(b, o) / 2147483648f;
            case 24: return ((b[o] << 8) | (b[o + 1] << 16) | (b[o + 2] << 24)) / 2147483648f;
            case 16: return BitConverter.ToInt16(b, o) / 32768f;
            case 8: return (b[o] - 128) / 128f;
            default: return 0f;
        }
    }

    public void Dispose()
    {
        Stop();
        _loopback.Dispose();
        _mic?.Dispose();
        _keepAlive?.Dispose();
        _enumerator.Dispose();
    }

    // ---- device listing ----

    public static string DefaultOutputName()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
        }
        catch { return "No output device"; }
    }

    public static string DefaultMicName()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).FriendlyName;
        }
        catch { return "No microphone"; }
    }

    public static List<AudioDevice> Microphones()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }

    /// <summary>Thread-safe float ring buffer exposed as a sample provider that pads with silence.</summary>
    internal sealed class FloatRing : ISampleProvider
    {
        readonly float[] _buf;
        int _read, _write, _count;
        readonly object _lock = new();

        public FloatRing(int sampleRate, int channels)
        {
            SampleRate = sampleRate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _buf = new float[sampleRate * channels * 2];
        }

        public int SampleRate { get; }
        public WaveFormat WaveFormat { get; }
        public int Count { get { lock (_lock) return _count; } }

        public void Clear() { lock (_lock) { _read = _write = _count = 0; } }

        public void Write(float[] data, int n)
        {
            lock (_lock)
            {
                for (int i = 0; i < n; i++)
                {
                    _buf[_write] = data[i];
                    _write = (_write + 1) % _buf.Length;
                    if (_count == _buf.Length) _read = (_read + 1) % _buf.Length;
                    else _count++;
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                int n = Math.Min(count, _count);
                for (int i = 0; i < n; i++)
                {
                    buffer[offset + i] = _buf[_read];
                    _read = (_read + 1) % _buf.Length;
                }
                _count -= n;
                if (n < count) Array.Clear(buffer, offset + n, count - n);
            }
            return count;
        }
    }
}
