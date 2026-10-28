using System;
using System.Collections.Generic;
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
    byte[] _outBytes = Array.Empty<byte>();

    public int SampleRate { get; }
    public bool DesktopEnabled { get; set; }
    public float DesktopVolume { get; set; }
    public float MicVolume { get; set; }
    public bool HasMic => _mic != null;

    /// <summary>Peak levels 0..1, updated per audio packet (read from UI for meters).</summary>
    public volatile float DesktopLevel;
    public volatile float MicLevel;

    public event Action<byte[], int>? DataAvailable;

    public AudioMixer(bool desktopEnabled, double desktopVolume, string? micId, double micVolume)
    {
        DesktopEnabled = desktopEnabled;
        DesktopVolume = (float)desktopVolume;
        MicVolume = (float)micVolume;

        var render = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _loopback = new WasapiLoopbackCapture(render);
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
    }

    public void Start()
    {
        _keepAlive?.Play();
        _loopback.StartRecording();
        _mic?.StartRecording();
    }

    public void Stop()
    {
        try { _loopback.StopRecording(); } catch { }
        try { _mic?.StopRecording(); } catch { }
        try { _keepAlive?.Stop(); } catch { }
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
        if (e.BytesRecorded == 0) return;
        int frames = ToStereo(e.Buffer, e.BytesRecorded, _loopChannels, _loopBits, _loopFloat, ref _desk);
        int n = frames * 2;

        if (_micSamples != null)
        {
            if (_micBuf.Length < n) _micBuf = new float[n];
            int read = _micSamples.Read(_micBuf, 0, n);
            if (read < n) Array.Clear(_micBuf, read, n - read);
        }

        float dv = DesktopEnabled ? DesktopVolume : 0f;
        float mv = MicVolume;
        float dPeak = 0, mPeak = 0;
        for (int i = 0; i < n; i++)
        {
            float d = _desk[i] * dv;
            float m = _micSamples != null ? _micBuf[i] * mv : 0f;
            float ad = Math.Abs(d), am = Math.Abs(m);
            if (ad > dPeak) dPeak = ad;
            if (am > mPeak) mPeak = am;
            float s = d + m;
            _desk[i] = s > 1f ? 1f : (s < -1f ? -1f : s);
        }
        DesktopLevel = Math.Min(1f, dPeak);
        MicLevel = Math.Min(1f, mPeak);

        int bytes = n * 4;
        if (_outBytes.Length < bytes) _outBytes = new byte[bytes];
        Buffer.BlockCopy(_desk, 0, _outBytes, 0, bytes);
        DataAvailable?.Invoke(_outBytes, bytes);
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
    sealed class FloatRing : ISampleProvider
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
