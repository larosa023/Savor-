using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Savor.Core;

public sealed record AudioTrackInfo(int Index, string Name);

/// <summary>
/// Plays a clip's audio tracks in the viewer. Windows' built-in video player goes silent on files with
/// more than one audio track, so for those Savor renders the chosen tracks (mixed) to a temporary WAV
/// on disk with ffmpeg and plays it alongside the video. Streaming from disk keeps memory tiny.
/// </summary>
public sealed class ClipAudio : IDisposable
{
    static string TempDir => Path.Combine(AppSettings.LocalDataDir, "temp");

    Source? _reader;
    WasapiOut? _out;
    string? _file;
    float _volume = 0.8f;
    bool _muted;

    const double LatencySeconds = 0.08;

    /// <summary>Lists the audio tracks in a file with their names (from the file's track titles).</summary>
    public static async Task<List<AudioTrackInfo>> ProbeAsync(string path)
    {
        var list = new List<AudioTrackInfo>();
        if (Ffmpeg.Path == null) return list;
        var (_, err) = await Ffmpeg.RunAsync(new[] { "-hide_banner", "-i", path }, TimeSpan.FromSeconds(10));
        string? pendingName = null;
        bool inAudio = false;
        foreach (var raw in err.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Stream #"))
            {
                if (inAudio) list.Add(new AudioTrackInfo(list.Count, pendingName ?? $"Track {list.Count + 1}"));
                inAudio = line.Contains("Audio:");
                pendingName = null;
                continue;
            }
            if (!inAudio) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim();
            if (key == "title" || (key == "handler_name" && pendingName == null && val != "SoundHandler" && val.Length > 0))
                pendingName = val;
        }
        if (inAudio) list.Add(new AudioTrackInfo(list.Count, pendingName ?? $"Track {list.Count + 1}"));
        return list;
    }

    /// <summary>Renders the chosen tracks (mixed together) and gets ready to play.</summary>
    public async Task PrepareAsync(string path, IReadOnlyList<int> tracks, CancellationToken ct)
    {
        Close();
        if (tracks.Count == 0 || Ffmpeg.Path == null) return;
        Directory.CreateDirectory(TempDir);
        var file = Path.Combine(TempDir, $"listen-{Guid.NewGuid():N}.wav");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", path };
        if (tracks.Count == 1)
            args.AddRange(new[] { "-map", $"0:a:{tracks[0]}" });
        else
        {
            var inputs = string.Concat(tracks.Select(t => $"[0:a:{t}]"));
            args.AddRange(new[] { "-filter_complex", $"{inputs}amix=inputs={tracks.Count}:normalize=0[am]", "-map", "[am]" });
        }
        args.AddRange(new[] { "-vn", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", file });

        var (code, _) = await Ffmpeg.RunAsync(args, TimeSpan.FromMinutes(5), ct);
        if (code != 0 || !File.Exists(file))
        {
            TryDelete(file);
            throw new InvalidOperationException("Couldn't read this clip's audio.");
        }
        if (ct.IsCancellationRequested) { TryDelete(file); ct.ThrowIfCancellationRequested(); }

        _file = file;
        _reader = new Source(file) { Volume = _muted ? 0 : _volume };
        try
        {
            _out = new WasapiOut(AudioClientShareMode.Shared, true, 40);
            _out.Init(_reader);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Close();
            throw new InvalidOperationException("No speakers or headphones found to play the audio.");
        }
    }

    public bool Ready => _out != null;
    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

    /// <summary>What you're hearing right now (accounts for output latency).</summary>
    public TimeSpan Position =>
        _reader == null ? TimeSpan.Zero : _reader.CurrentTime - TimeSpan.FromSeconds(IsPlaying ? LatencySeconds : 0);

    public void Play(TimeSpan at)
    {
        if (_reader == null || _out == null) return;
        Seek(at);
        _out.Play();
    }

    public void Pause() { try { _out?.Pause(); } catch { } }

    public void Seek(TimeSpan t)
    {
        if (_reader == null) return;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t > _reader.TotalTime) t = _reader.TotalTime;
        _reader.CurrentTime = t; // AudioFileReader locks internally, safe while playing
    }

    public void SetVolume(float volume, bool muted)
    {
        _volume = volume;
        _muted = muted;
        if (_reader != null) _reader.Volume = muted ? 0 : volume;
    }

    void Close()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _out = null;
        _reader = null;
        if (_file != null) TryDelete(_file);
        _file = null;
    }

    /// <summary>WAV file as float samples, with volume and thread-safe seeking while playing.</summary>
    sealed class Source : ISampleProvider, IDisposable
    {
        readonly WaveFileReader _wav;
        readonly ISampleProvider _samples;
        readonly object _lock = new();
        public float Volume { get; set; } = 1f;

        public Source(string file)
        {
            _wav = new WaveFileReader(file);
            _samples = _wav.ToSampleProvider();
        }

        public WaveFormat WaveFormat => _samples.WaveFormat;
        public TimeSpan TotalTime => _wav.TotalTime;

        public TimeSpan CurrentTime
        {
            get { lock (_lock) return _wav.CurrentTime; }
            set { lock (_lock) _wav.CurrentTime = value; }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n;
            lock (_lock) n = _samples.Read(buffer, offset, count);
            float v = Volume;
            if (v != 1f) for (int i = 0; i < n; i++) buffer[offset + i] *= v;
            return n;
        }

        public void Dispose() => _wav.Dispose();
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }

    public void Dispose() => Close();

    /// <summary>Removes leftovers from a previous session (e.g. after a crash).</summary>
    public static void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            foreach (var f in Directory.EnumerateFiles(TempDir, "listen-*.wav")) TryDelete(f);
        }
        catch { }
    }
}
