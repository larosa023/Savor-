using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Savor.Core;

namespace Savor;

/// <summary>
/// Built-in viewer/editor for captures: play, trim, crop and export.
/// Uses Windows' own video playback (no extra libraries) and the bundled ffmpeg for export.
/// Everything is released when the editor closes, so it costs nothing while you're not using it.
/// </summary>
public partial class ClipEditor : UserControl
{
    // smooth playhead: interpolate between the player's coarse position updates
    readonly Stopwatch _clock = new();
    TimeSpan _clockBase;
    TimeSpan? _pendingSeek;
    long _lastSeekTicks, _lastSyncTicks, _lastTextTicks, _lastTrimTextTicks;
    bool _renderHooked;
    Filmstrip? _strip;

    // multi-track audio: played by Savor (Windows' player is silent on these files)
    ClipAudio? _audio;
    List<AudioTrackInfo> _tracks = new();
    readonly List<int> _listen = new();
    CancellationTokenSource? _audioCts;
    long _lastAudioSync;
    DispatcherTimer? _hideScrub;
    string? _path;
    Func<EncoderDef?>? _encoder;
    AppSettings? _settings;

    TimeSpan _duration, _in, _out;
    bool _playing, _opened;
    int _vw, _vh;
    CancellationTokenSource? _exportCts;

    // crop state in source pixels
    string _cropMode = "none";
    double _aspect; // 0 = free
    Rect _crop;

    public event Action? CloseRequested;
    /// <summary>Raised after an export; the path of the written file.</summary>
    public event Action<string>? Exported;

    public bool IsBusy => _exportCts != null;
    readonly bool _ready;

    public ClipEditor()
    {
        InitializeComponent();
        _ready = true;
        PreviewKeyDown += OnKey;
        // per-frame updates only while the editor is actually on screen (e.g. not while Savor is in the tray)
        IsVisibleChanged += (_, _) => SetFrameHook(IsVisible && _path != null);
    }

    // ===================== open / close =====================

    public void Open(string path, AppSettings settings, Func<EncoderDef?> encoder)
    {
        _path = path;
        _resume = null;
        _settings = settings;
        _encoder = encoder;
        _opened = false;
        _playing = false;
        _duration = _in = _out = TimeSpan.Zero;
        TitleText.Text = Path.GetFileNameWithoutExtension(path);
        try
        {
            var fi = new FileInfo(path);
            MetaText.Text = $"{fi.Length / (double)(1L << 20):0.0} MB  ·  {fi.LastWriteTime:MMM d, h:mm tt}";
        }
        catch { MetaText.Text = ""; }

        PlayerMessage.Visibility = Visibility.Collapsed;
        BigPlay.Visibility = Visibility.Visible;
        ExportStatus.Visibility = Visibility.Collapsed;
        ExportProgress.Visibility = Visibility.Collapsed;
        SetCropMode("none");
        ((RadioButton)CropPresets.Children[0]).IsChecked = true;
        FastToggle.IsChecked = false;
        UpdatePlayIcon();

        Player.Volume = VolumeSlider.Value;
        // Most videos are "limited range"; Windows' player doesn't expand that, so do it on the GPU.
        Player.Effect = Shaders.ExpandRangeEffect.TryCreate();
        _ = DetectRangeAsync(path);
        Player.Source = new Uri(path);
        ListenPanel.Visibility = Visibility.Collapsed;
        ExportAudioLabel.Visibility = Visibility.Collapsed;
        AudioSelectedItem.Visibility = Visibility.Collapsed;
        _ = SetupTracksAsync(path);
        Player.Pause(); // opens the file and shows the first frame
        _clockBase = TimeSpan.Zero;
        _clock.Reset();
        SetFrameHook(IsVisible);
        Focus();
    }

    /// <summary>Stops playback and releases the decoder and the file handle.</summary>
    public void Release()
    {
        SetFrameHook(false);
        _clock.Reset();
        _pendingSeek = null;
        _strip?.Dispose();
        _strip = null;
        _audioCts?.Cancel();
        _audio?.Dispose();
        _audio = null;
        Player.IsMuted = VolumeSlider.Value <= 0.001;
        ScrubFrame.Source = null;
        ScrubFrame.Visibility = Visibility.Collapsed;
        try { Player.Stop(); } catch { }
        try { Player.Close(); } catch { }
        Player.Source = null;
        _playing = false;
        _opened = false;
    }

    void Back_Click(object sender, RoutedEventArgs e) => RequestClose();

    // ===================== rename (the title is a text box) =====================

    bool _renaming;
    (TimeSpan pos, TimeSpan inPt, TimeSpan outPt)? _resume;
    public event Action<string>? Renamed;

    void Title_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Focus(); } // losing focus saves the name
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_path != null) TitleText.Text = Path.GetFileNameWithoutExtension(_path);
            Focus();
        }
    }

    void Title_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => _ = RenameAsync(TitleText.Text);

    async Task RenameAsync(string name)
    {
        if (_path == null || _settings == null || _renaming) return;
        var current = Path.GetFileNameWithoutExtension(_path);
        if (!IsVisible)
        {
            // leaving the editor while typing: just rename the file, don't reopen it
            if (name.Trim().Length > 0 && name.Trim() != current)
                try { var p = _path; var np = await Task.Run(() => Library.Rename(p, name)); Renamed?.Invoke(np); } catch { }
            return;
        }
        if (name.Trim() == current || name.Trim().Length == 0) { TitleText.Text = current; return; }
        if (IsBusy)
        {
            TitleText.Text = current;
            ShowStatus("Wait for the export to finish, then rename.", error: true);
            return;
        }
        _renaming = true;
        var pos = Now;
        var oldPath = _path;
        string newPath = oldPath;
        string? error = null;
        try
        {
            // the player keeps the file open, so let go of it while it's renamed
            Release();
            try { newPath = await Task.Run(() => Library.Rename(oldPath, name)); }
            catch (Exception ex) { error = ex.Message; }
            var trim = (_in, _out);
            Open(newPath, _settings, _encoder!);
            _resume = (pos, trim.Item1, trim.Item2);
            if (error != null) ShowStatus(error, error: true);
            else
            {
                ShowStatus($"Renamed to “{Path.GetFileNameWithoutExtension(newPath)}”.");
                Renamed?.Invoke(newPath);
            }
        }
        finally { _renaming = false; }
    }

    void RequestClose()
    {
        if (IsBusy) { _exportCts?.Cancel(); return; }
        Release();
        CloseRequested?.Invoke();
    }

    // ===================== player =====================

    void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _opened = true;
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _vw = Player.NaturalVideoWidth;
        _vh = Player.NaturalVideoHeight;
        if (_in == TimeSpan.Zero && _out == TimeSpan.Zero) { _in = TimeSpan.Zero; _out = _duration; }
        var meta = MetaText.Text;
        if (!meta.Contains('×')) MetaText.Text = $"{_vw}×{_vh}  ·  {Fmt(_duration)}  ·  {meta}";
        _clockBase = TimeSpan.Zero;
        FlushSeek(TimeSpan.Zero);
        if (_resume is { } r)
        {
            // reopened after a rename: keep the trim and the playhead where they were
            _resume = null;
            if (r.outPt > r.inPt && r.outPt <= _duration + TimeSpan.FromMilliseconds(50)) { _in = r.inPt; _out = Clamp(r.outPt, r.inPt, _duration); }
            Seek(Clamp(r.pos, TimeSpan.Zero, _duration));
        }
        StartFilmstrip();
        UpdateTimeline();
        UpdateTrimText();
        LayoutCrop();
    }

    void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _opened = false;
        BigPlay.Visibility = Visibility.Collapsed;
        PlayerMessage.Visibility = Visibility.Visible;
        PlayerMessageText.Text = "Windows can't play this file here (often MKV, HEVC or AV1 without the codec installed). " +
                                 "You can still trim and export it, or use Open externally.";
        // still allow trimming: get duration from ffmpeg
        _ = ProbeDurationAsync();
    }

    async Task ProbeDurationAsync()
    {
        if (_path == null || Ffmpeg.Path == null) return;
        var (_, err) = await Ffmpeg.RunAsync(new[] { "-hide_banner", "-i", _path }, TimeSpan.FromSeconds(10));
        var m = System.Text.RegularExpressions.Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)");
        var v = System.Text.RegularExpressions.Regex.Match(err, @"Video:.*?,\s(\d{2,5})x(\d{2,5})");
        if (m.Success)
        {
            _duration = new TimeSpan(0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                                     int.Parse(m.Groups[4].Value.PadRight(3, '0')[..3]));
            _in = TimeSpan.Zero;
            _out = _duration;
        }
        if (v.Success) { _vw = int.Parse(v.Groups[1].Value); _vh = int.Parse(v.Groups[2].Value); }
        StartFilmstrip();
        UpdateTimeline();
        UpdateTrimText();
    }

    void Player_MediaEnded(object sender, RoutedEventArgs e) => Pause();

    /// <summary>Full-range files (rare) must not be expanded again.</summary>
    async Task DetectRangeAsync(string path)
    {
        if (Ffmpeg.Path == null) return;
        try
        {
            var (_, err) = await Ffmpeg.RunAsync(new[] { "-hide_banner", "-i", path }, TimeSpan.FromSeconds(10));
            var video = err.Split('\n').FirstOrDefault(l => l.Contains("Video:")) ?? "";
            bool full = video.Contains("(pc") || video.Contains("yuvj");
            if (_path == path && full) Player.Effect = null;
        }
        catch { }
    }

    /// <summary>Files with several audio tracks: show track toggles and play the audio ourselves.</summary>
    async Task SetupTracksAsync(string path)
    {
        try
        {
            var tracks = await ClipAudio.ProbeAsync(path);
            if (_path != path) return;
            _tracks = tracks;
            if (tracks.Count < 2) return; // Windows' player handles single-track files fine

            Player.IsMuted = true;
            _listen.Clear();
            _listen.Add(0); // track 1 = the mix
            ListenTracks.Children.Clear();
            foreach (var t in tracks)
            {
                var tb = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Style = (Style)FindResource("SegToggle"),
                    Content = t.Index == 0 && t.Name == "Mix" ? "Mix (everything)" : t.Name,
                    IsChecked = t.Index == 0,
                    Tag = t.Index,
                };
                tb.Click += ListenTrack_Click;
                ListenTracks.Children.Add(tb);
            }
            ListenPanel.Visibility = Visibility.Visible;
            ExportAudioLabel.Visibility = Visibility.Visible;
            AudioSelectedItem.Visibility = Visibility.Visible;
            await PrepareAudioAsync();
        }
        catch { }
    }

    void ListenTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: int idx } tb) return;
        if (tb.IsChecked == true) { if (!_listen.Contains(idx)) _listen.Add(idx); }
        else _listen.Remove(idx);
        _listen.Sort();
        _ = PrepareAudioAsync();
        Focus();
    }

    async Task PrepareAudioAsync()
    {
        if (_path == null) return;
        _audioCts?.Cancel();
        var cts = _audioCts = new CancellationTokenSource();
        bool resume = _playing;
        _audio?.Dispose();
        _audio = null;
        if (_listen.Count == 0)
        {
            ListenStatus.Text = "No tracks selected — muted.";
            return;
        }
        ListenStatus.Text = "Loading audio…";
        var audio = new ClipAudio();
        audio.SetVolume((float)VolumeSlider.Value, VolumeSlider.Value <= 0.001);
        try
        {
            await audio.PrepareAsync(_path, _listen.ToArray(), cts.Token);
            if (cts.IsCancellationRequested) { audio.Dispose(); return; }
            _audio = audio;
            ListenStatus.Text = _listen.Count == 1 ? "" : $"{_listen.Count} tracks mixed together.";
            if (resume && _playing) _audio.Play(Now);
            else _audio.Seek(Now);
        }
        catch (OperationCanceledException) { audio.Dispose(); }
        catch (Exception ex)
        {
            audio.Dispose();
            ListenStatus.Text = ex.Message;
        }
    }

    void StartFilmstrip()
    {
        _strip?.Dispose();
        _strip = _path != null ? Filmstrip.Start(_path, _duration) : null;
        if (_strip != null) _strip.FrameReady += OnStripFrame;
    }

    int _wantedStripIndex = -1;

    void OnStripFrame(int idx, System.Windows.Media.Imaging.BitmapSource img) =>
        Dispatcher.BeginInvoke(() =>
        {
            // still dragging and still moving: show it (a newer request may already be on its way)
            if (_drag != Drag.None && Environment.TickCount64 - _lastMoveTicks < 120) ShowStripFrame(img);
        }, DispatcherPriority.Render);

    void ShowStripFrame(System.Windows.Media.Imaging.BitmapSource img)
    {
        if (!ReferenceEquals(ScrubFrame.Source, img)) ScrubFrame.Source = img;
        ScrubFrame.Visibility = Visibility.Visible;
    }

    void SetFrameHook(bool on)
    {
        if (on == _renderHooked) return;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
        _renderHooked = on;
    }

    /// <summary>Where playback is right now (smooth, not limited to the player's update rate).</summary>
    TimeSpan Now => _clock.IsRunning ? _clockBase + _clock.Elapsed : _clockBase;

    void Play()
    {
        if (!_opened) return;
        var pos = Now;
        if (pos < _in || pos >= _out - TimeSpan.FromMilliseconds(50)) pos = _in;
        FlushSeek(pos);
        Player.Play();
        _audio?.Play(pos);
        _playing = true;
        _hideScrub?.Stop();
        ScrubFrame.Visibility = Visibility.Collapsed;
        _clockBase = pos;
        _clock.Restart();
        BigPlay.Visibility = Visibility.Collapsed;
        UpdatePlayIcon();
    }

    void Pause()
    {
        var pos = Now;
        Player.Pause();
        _audio?.Pause();
        _playing = false;
        _clock.Reset();
        _clockBase = pos;
        UpdatePlayIcon();
    }

    void TogglePlay() { if (_playing) Pause(); else Play(); }

    public void PauseIfPlaying() { if (_playing) Pause(); }

    void UpdatePlayIcon() => PlayIcon.Text = _playing ? "\uF8AE" : "\uF5B0";

    /// <summary>Moves the playhead now; the actual (expensive) video seek is rate-limited.</summary>
    void Seek(TimeSpan t)
    {
        if (_duration <= TimeSpan.Zero) return;
        t = Clamp(t, TimeSpan.Zero, _duration);
        _clockBase = t;
        if (_clock.IsRunning) _clock.Restart();
        _pendingSeek = t;
        BigPlay.Visibility = Visibility.Collapsed;
        UpdatePlayhead(force: true);
    }

    void FlushSeek(TimeSpan t)
    {
        _pendingSeek = null;
        _lastSeekTicks = Environment.TickCount64;
        if (_opened) Player.Position = t;
        _audio?.Seek(t);
    }

    /// <summary>Runs once per screen refresh while the editor is open.</summary>
    void OnFrame(object? sender, EventArgs e)
    {
        if (!_opened && _duration <= TimeSpan.Zero) return;
        long now = Environment.TickCount64;

        if (_drag != Drag.None) { ApplyDrag(now); UpdatePlayhead(force: false); return; }
        if (_pendingSeek is { } ps && now - _lastSeekTicks >= 40) FlushSeek(ps);

        if (_playing)
        {
            if (Now >= _out)
            {
                Pause();
                Seek(_out);
            }
            else if (now - _lastSyncTicks > 300 && _pendingSeek == null)
            {
                // keep Savor's audio lined up with the video
                if (_audio is { Ready: true } && now - _lastAudioSync > 500)
                {
                    _lastAudioSync = now;
                    if (Math.Abs((_audio.Position - Now).TotalMilliseconds) > 120) _audio.Seek(Now);
                }
                // re-anchor to the real position if we've drifted noticeably
                _lastSyncTicks = now;
                var real = Player.Position;
                if (Math.Abs((real - Now).TotalMilliseconds) > 120) { _clockBase = real; _clock.Restart(); }
            }
        }
        UpdatePlayhead(force: false);
    }

    void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void Stage_Click(object sender, MouseButtonEventArgs e) { if (_cropMode == "none") TogglePlay(); }
    void Back5_Click(object sender, RoutedEventArgs e) => Seek(Now - TimeSpan.FromSeconds(5));
    void Fwd5_Click(object sender, RoutedEventArgs e) => Seek(Now + TimeSpan.FromSeconds(5));

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        Player.Volume = VolumeSlider.Value;
        Player.IsMuted = _tracks.Count > 1 || VolumeSlider.Value <= 0.001;
        _audio?.SetVolume((float)VolumeSlider.Value, VolumeSlider.Value <= 0.001);
        VolIcon.Text = Player.IsMuted ? "" : "";
    }

    double _volBeforeMute = 0.8;
    void VolIcon_Click(object sender, MouseButtonEventArgs e)
    {
        if (VolumeSlider.Value > 0.001) { _volBeforeMute = VolumeSlider.Value; VolumeSlider.Value = 0; }
        else VolumeSlider.Value = _volBeforeMute;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        // typing a name: keys belong to the text box
        if (e.OriginalSource is TextBox) return;
        // let an open dropdown use the keys; otherwise shortcuts work wherever focus is
        if (e.OriginalSource is ComboBoxItem || e.OriginalSource is ComboBox { IsDropDownOpen: true }) return;
        if (e.OriginalSource is ComboBox && e.Key is Key.Up or Key.Down) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Seek(Now - TimeSpan.FromSeconds(shift ? 5 : 1.0 / 30)); break;
            case Key.Right: Seek(Now + TimeSpan.FromSeconds(shift ? 5 : 1.0 / 30)); break;
            case Key.I: SetIn(Now); break;
            case Key.O: SetOut(Now); break;
            case Key.Home: Seek(_in); break;
            case Key.End: Seek(_out); break;
            case Key.Escape: RequestClose(); break;
            default: return;
        }
        e.Handled = true;
    }

    void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        Pause();
        try { Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }); } catch { }
    }

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{_path}\""); } catch { }
    }

    // ===================== trim / timeline =====================

    void SetIn(TimeSpan t)
    {
        if (_duration <= TimeSpan.Zero) return;
        _in = Clamp(t, TimeSpan.Zero, _out - TimeSpan.FromMilliseconds(100));
        UpdateTimeline();
        UpdateTrimText();
    }

    void SetOut(TimeSpan t)
    {
        if (_duration <= TimeSpan.Zero) return;
        _out = Clamp(t, _in + TimeSpan.FromMilliseconds(100), _duration);
        UpdateTimeline();
        UpdateTrimText();
    }

    void SetIn_Click(object sender, RoutedEventArgs e) => SetIn(Now);
    void SetOut_Click(object sender, RoutedEventArgs e) => SetOut(Now);

    void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        _in = TimeSpan.Zero;
        _out = _duration;
        UpdateTimeline();
        UpdateTrimText();
    }

    static TimeSpan Clamp(TimeSpan v, TimeSpan lo, TimeSpan hi) => v < lo ? lo : v > hi ? hi : v;

    static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");

    bool _trimTextDirty;

    void UpdateTrimText()
    {
        if (_drag is Drag.In or Drag.Out) { _trimTextDirty = true; return; } // drawn from OnFrame at ~20 fps
        _trimTextDirty = false;
        InText.Text = Fmt(_in);
        OutText.Text = Fmt(_out);
        LenText.Text = Fmt(_out - _in);
        bool trimmed = _in > TimeSpan.Zero || _out < _duration - TimeSpan.FromMilliseconds(50);
        ExportText.Text = trimmed || _cropMode != "none" ? "Export" : "Export copy";
    }

    const double Pad = 18; // room for the handles at both ends

    double TrackWidth => Math.Max(1, Timeline.ActualWidth - Pad * 2);

    double X(TimeSpan t) => Pad + (_duration.TotalMilliseconds <= 0 ? 0 : t.TotalMilliseconds / _duration.TotalMilliseconds * TrackWidth);

    TimeSpan T(double x) => TimeSpan.FromMilliseconds(Math.Clamp((x - Pad) / TrackWidth, 0, 1) * _duration.TotalMilliseconds);

    /// <summary>Trim band + handles (only changes when trimming).</summary>
    void UpdateTimeline()
    {
        double xi = X(_in), xo = X(_out);
        // transforms only: moving the trim never triggers a layout pass
        SelX.X = xi;
        SelScale.ScaleX = Math.Max(0, xo - xi) / Math.Max(1, SelBand.Width);
        InX.X = xi - InHandle.Width;
        OutX.X = xo;
        UpdatePlayhead(force: _drag == Drag.None);
    }

    TimeSpan _shownPos = TimeSpan.MinValue;

    /// <summary>Playhead + time readout (every frame; transform only, no layout).</summary>
    void UpdatePlayhead(bool force)
    {
        var pos = Clamp(Now, TimeSpan.Zero, _duration);
        PlayheadX.X = X(pos) - Playhead.Width / 2;
        long now = Environment.TickCount64;
        if (force || (pos != _shownPos && now - _lastTextTicks > 50))
        {
            _lastTextTicks = now;
            _shownPos = pos;
            TimeText.Text = $"{Fmt(pos)} / {Fmt(_duration)}";
        }
    }

    void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SelBand.Width = Math.Max(1, TrackWidth);
        UpdateTimeline();
    }

    enum Drag { None, In, Out, Scrub }
    Drag _drag;
    bool _wasPlaying;
    double _grabOffset;

    /// <summary>Which handle (if any) is under x — generous 14 px slop around each handle.</summary>
    Drag HandleAt(double x)
    {
        double xi = X(_in), xo = X(_out);
        double inCenter = xi - InHandle.Width / 2, outCenter = xo + OutHandle.Width / 2;
        double dIn = Math.Abs(x - inCenter), dOut = Math.Abs(x - outCenter);
        const double grab = 9 + 14;
        if (dIn > grab && dOut > grab) return Drag.None;
        return dIn <= dOut ? Drag.In : Drag.Out;
    }

    void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= TimeSpan.Zero) return;
        double x = e.GetPosition(Timeline).X;
        _drag = HandleAt(x);
        if (_drag == Drag.In) _grabOffset = x - X(_in);
        else if (_drag == Drag.Out) _grabOffset = x - X(_out);
        else
        {
            _drag = Drag.Scrub;
            _wasPlaying = _playing;
            if (_playing) Pause();
            Seek(T(x));
        }
        if (_drag != Drag.Scrub)
        {
            _wasPlaying = _playing;
            if (_playing) Pause();
            (_drag == Drag.In ? InHandle : OutHandle).Opacity = 0.85;
        }
        Timeline.CaptureMouse();
        Focus();
        e.Handled = true;
    }

    void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        double x = e.GetPosition(Timeline).X;
        if (_drag == Drag.None)
        {
            Timeline.Cursor = HandleAt(x) != Drag.None ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        // Everything here is transform-only and cheap; the video itself is only touched once per frame (OnFrame).
        _dragX = x;
        ApplyDragPosition();
    }

    void ApplyDragPosition()
    {
        if (_dragX is not { } x || x == _lastAppliedX) return;
        _dragX = null;
        _lastAppliedX = x;
        _lastMoveTicks = Environment.TickCount64;
        switch (_drag)
        {
            case Drag.In: SetIn(T(x - _grabOffset)); MovePlayheadOnly(_in); break;
            case Drag.Out: SetOut(T(x - _grabOffset)); MovePlayheadOnly(_out); break;
            case Drag.Scrub: MovePlayheadOnly(T(x)); break;
        }
    }

    double? _dragX;
    double _lastAppliedX = double.NaN;
    long _lastMoveTicks;

    /// <summary>Once per screen refresh while dragging: show the matching preview frame.</summary>
    void ApplyDrag(long now)
    {
        if (_drag == Drag.None) return;
        ApplyDragPosition();

        if (_pendingSeek is { } ps)
        {
            int idx = _strip?.IndexAt(ps) ?? -1;
            if (idx >= 0)
            {
                _strip!.Request(idx); // decoded in the background, shown by OnStripFrame
                // instant: a pre-made preview frame, no video seek at all
                if (now - _lastMoveTicks < 120)
                {
                    _hideScrub?.Stop();
                    _wantedStripIndex = idx;
                    if (_strip.Cached(idx) is { } cached) ShowStripFrame(cached);
                }
                else
                {
                    // you've paused on a spot: show the real full-quality frame
                    FlushSeek(ps);
                    EndScrubPreview();
                }
            }
            else
            {
                // preview frames not ready yet: fall back to occasional real seeks
                bool settled = now - _lastMoveTicks >= 60;
                bool due = now - _lastSeekTicks >= 150;
                if (settled || due) FlushSeek(ps);
            }
        }

        if (_trimTextDirty && now - _lastTrimTextTicks > 50)
        {
            _lastTrimTextTicks = now;
            _trimTextDirty = false;
            InText.Text = Fmt(_in);
            OutText.Text = Fmt(_out);
            LenText.Text = Fmt(_out - _in);
        }
    }

    /// <summary>After a drag: one exact seek, then fade out the preview frame once the video has caught up.</summary>
    void EndScrubPreview()
    {
        if (ScrubFrame.Visibility != Visibility.Visible) return;
        _hideScrub ??= new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, (_, _) =>
        {
            _hideScrub!.Stop();
            // hide once the video has caught up, unless the drag started moving again
            if (_drag == Drag.None || Environment.TickCount64 - _lastMoveTicks >= 150)
                ScrubFrame.Visibility = Visibility.Collapsed;
        }, Dispatcher);
        _hideScrub.Stop();
        _hideScrub.Start();
    }

    /// <summary>Moves the playhead without seeking the video yet.</summary>
    void MovePlayheadOnly(TimeSpan t)
    {
        t = Clamp(t, TimeSpan.Zero, _duration);
        _clockBase = t;
        _pendingSeek = t;
        BigPlay.Visibility = Visibility.Collapsed;
        UpdatePlayhead(force: false);
    }

    void Timeline_MouseUp(object sender, MouseButtonEventArgs e) => Timeline.ReleaseMouseCapture();

    void Timeline_LostCapture(object sender, MouseEventArgs e)
    {
        if (_drag == Drag.None) return;
        ApplyDragPosition();
        if (_pendingSeek is { } ps) FlushSeek(ps);
        EndScrubPreview();
        InHandle.Opacity = OutHandle.Opacity = 1;
        _lastAppliedX = double.NaN;
        bool resume = _drag == Drag.Scrub && _wasPlaying;
        _drag = Drag.None;
        UpdateTrimText();
        UpdatePlayhead(force: true);
        if (resume) Play();
    }

    /// <summary>CI only: what the viewer found in the file's audio.</summary>
    public string AudioDiag() =>
        $"audio tracks={_tracks.Count} [{string.Join(", ", _tracks.Select(t => t.Name))}], listening={string.Join("+", _listen)}, " +
        $"player ready={_audio?.Ready == true}, status=\"{ListenStatus.Text}\", windows player muted={Player.IsMuted}";

    /// <summary>CI only: simulates a fast drag of the start handle and reports per-frame cost.</summary>
    public async Task<string> SelfTestScrubAsync()
    {
        for (int i = 0; i < 40 && (_strip == null || _strip.Count < 20); i++) await Task.Delay(250);
        int frames = _strip?.Count ?? 0;
        var sw = new Stopwatch();
        double worst = 0, total = 0;
        int shown = 0;
        _drag = Drag.In;
        _grabOffset = 0;
        double w = TrackWidth;
        for (int i = 0; i <= 120; i++)
        {
            // sweep right and back like a fast hand movement
            double f = i <= 60 ? i / 60.0 : (120 - i) / 60.0;
            sw.Restart();
            _dragX = Pad + f * w * 0.8;
            ApplyDrag(Environment.TickCount64);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            worst = Math.Max(worst, ms);
            total += ms;
            if (ScrubFrame.Visibility == Visibility.Visible) shown++;
            await Task.Delay(8); // ~120 Hz of input
        }
        Timeline_LostCapture(this, null!);
        return $"filmstrip frames={frames}, drag steps=121, preview frame shown on {shown}, avg {total / 121:0.00} ms, worst {worst:0.00} ms per step";
    }

    // ===================== crop =====================

    void CropPreset_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return; // fires once while the XAML is still loading
        if (sender is RadioButton { Tag: string tag }) SetCropMode(tag);
    }

    void SetCropMode(string mode)
    {
        _cropMode = mode;
        _aspect = mode switch { "16:9" => 16.0 / 9, "9:16" => 9.0 / 16, "1:1" => 1, "4:3" => 4.0 / 3, _ => 0 };
        if (mode != "none" && _vw > 0)
        {
            if (_aspect > 0)
            {
                double w = _vw, h = _vw / _aspect;
                if (h > _vh) { h = _vh; w = _vh * _aspect; }
                _crop = new Rect((_vw - w) / 2, (_vh - h) / 2, w, h);
            }
            else
            {
                _crop = new Rect(_vw * 0.1, _vh * 0.1, _vw * 0.8, _vh * 0.8);
            }
        }
        CropLayer.Visibility = mode == "none" ? Visibility.Collapsed : Visibility.Visible;
        FastToggle.IsEnabled = mode == "none";
        if (mode != "none") FastToggle.IsChecked = false;
        if (mode != "none") { Pause(); BigPlay.Visibility = Visibility.Collapsed; }
        LayoutCrop();
        UpdateTrimText();
    }

    void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCrop();

    /// <summary>Where the video actually sits inside the stage (letterboxed).</summary>
    Rect VideoRect()
    {
        double W = Stage.ActualWidth, H = Stage.ActualHeight;
        if (_vw <= 0 || _vh <= 0 || W <= 0 || H <= 0) return Rect.Empty;
        double s = Math.Min(W / _vw, H / _vh);
        double w = _vw * s, h = _vh * s;
        return new Rect((W - w) / 2, (H - h) / 2, w, h);
    }

    double Scale => VideoRect() is { IsEmpty: false } r ? r.Width / _vw : 1;

    Rect ToScreen(Rect src)
    {
        var v = VideoRect();
        double s = Scale;
        return new Rect(v.X + src.X * s, v.Y + src.Y * s, src.Width * s, src.Height * s);
    }

    Point ToSource(Point p)
    {
        var v = VideoRect();
        double s = Scale;
        return new Point(Math.Clamp((p.X - v.X) / s, 0, _vw), Math.Clamp((p.Y - v.Y) / s, 0, _vh));
    }

    void LayoutCrop()
    {
        if (_cropMode == "none" || _vw <= 0) return;
        var v = VideoRect();
        if (v.IsEmpty) return;
        var r = ToScreen(_crop);

        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(new RectangleGeometry(v));
        g.Children.Add(new RectangleGeometry(r));
        CropShade.Data = g;

        Place(CropBox, r.X, r.Y, r.Width, r.Height);
        Place(CropThirdsV, r.X + r.Width / 3, r.Y, r.Width / 3, r.Height);
        Place(CropThirdsH, r.X, r.Y + r.Height / 3, r.Width, r.Height / 3);
        PlaceHandle(HandleTL, r.Left, r.Top);
        PlaceHandle(HandleTR, r.Right, r.Top);
        PlaceHandle(HandleBL, r.Left, r.Bottom);
        PlaceHandle(HandleBR, r.Right, r.Bottom);

        var (cw, ch) = EvenSize();
        CropSizeText.Text = $"{cw}×{ch}";
        Canvas.SetLeft(CropSizeChip, r.X + 8);
        Canvas.SetTop(CropSizeChip, r.Y + 8);
    }

    static void Place(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
    }

    static void PlaceHandle(FrameworkElement h, double x, double y)
    {
        Canvas.SetLeft(h, x - h.Width / 2);
        Canvas.SetTop(h, y - h.Height / 2);
    }

    (int w, int h) EvenSize() => ((int)_crop.Width / 2 * 2, (int)_crop.Height / 2 * 2);

    enum CropDrag { None, Move, Resize }
    CropDrag _cropDrag;
    Point _dragStart;   // source coords
    Rect _cropStart;
    Point _anchor;      // fixed corner while resizing (source coords)

    void Crop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vw <= 0) return;
        var p = e.GetPosition(CropLayer);
        var r = ToScreen(_crop);
        var corners = new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight };
        var opposite = new[] { _crop.BottomRight, _crop.BottomLeft, _crop.TopRight, _crop.TopLeft };
        _cropDrag = CropDrag.None;
        for (int i = 0; i < 4; i++)
        {
            if ((corners[i] - p).Length <= 14)
            {
                _cropDrag = CropDrag.Resize;
                _anchor = opposite[i];
                break;
            }
        }
        if (_cropDrag == CropDrag.None && r.Contains(p)) _cropDrag = CropDrag.Move;
        if (_cropDrag == CropDrag.None && _aspect == 0)
        {
            // free mode: draw a new box
            var s = ToSource(p);
            _anchor = s;
            _crop = new Rect(s, new Size(1, 1));
            _cropDrag = CropDrag.Resize;
        }
        _dragStart = ToSource(p);
        _cropStart = _crop;
        CropLayer.CaptureMouse();
        Focus();
        e.Handled = true;
    }

    void Crop_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(CropLayer);
        if (_cropDrag == CropDrag.None)
        {
            var r = ToScreen(_crop);
            bool nearCorner = (r.TopLeft - p).Length <= 14 || (r.BottomRight - p).Length <= 14;
            bool nearOther = (r.TopRight - p).Length <= 14 || (r.BottomLeft - p).Length <= 14;
            CropLayer.Cursor = nearCorner ? Cursors.SizeNWSE : nearOther ? Cursors.SizeNESW
                             : r.Contains(p) ? Cursors.SizeAll : (_aspect == 0 ? Cursors.Cross : Cursors.Arrow);
            return;
        }
        var s = ToSource(p);
        if (_cropDrag == CropDrag.Move)
        {
            double x = Math.Clamp(_cropStart.X + (s.X - _dragStart.X), 0, _vw - _cropStart.Width);
            double y = Math.Clamp(_cropStart.Y + (s.Y - _dragStart.Y), 0, _vh - _cropStart.Height);
            _crop = new Rect(x, y, _cropStart.Width, _cropStart.Height);
        }
        else
        {
            double dx = s.X - _anchor.X, dy = s.Y - _anchor.Y;
            double w = Math.Abs(dx), h = Math.Abs(dy);
            if (_aspect > 0)
            {
                // follow whichever axis the mouse moved more, keep the ratio, stay inside the frame
                if (w / _aspect > h) h = w / _aspect; else w = h * _aspect;
                double maxW = dx >= 0 ? _vw - _anchor.X : _anchor.X;
                double maxH = dy >= 0 ? _vh - _anchor.Y : _anchor.Y;
                if (w > maxW) { w = maxW; h = w / _aspect; }
                if (h > maxH) { h = maxH; w = h * _aspect; }
            }
            w = Math.Max(w, 32);
            h = Math.Max(h, _aspect > 0 ? 32 / _aspect : 32);
            double x = dx >= 0 ? _anchor.X : _anchor.X - w;
            double y = dy >= 0 ? _anchor.Y : _anchor.Y - h;
            x = Math.Clamp(x, 0, _vw - w);
            y = Math.Clamp(y, 0, _vh - h);
            _crop = new Rect(x, y, Math.Min(w, _vw), Math.Min(h, _vh));
        }
        LayoutCrop();
    }

    void Crop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _cropDrag = CropDrag.None;
        CropLayer.ReleaseMouseCapture();
    }

    // ===================== export =====================

    void Fast_Click(object sender, RoutedEventArgs e) { }


    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null || _settings == null) return;
        if (IsBusy) { _exportCts?.Cancel(); return; }
        if (_duration <= TimeSpan.Zero)
        {
            ShowStatus("This file couldn't be read.", error: true);
            return;
        }

        bool replace = SaveModeCombo.SelectedItem is ComboBoxItem { Tag: "replace" };
        var audioTag = (AudioCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var audio = audioTag switch
        {
            "mix" => AudioExport.MixOnly,
            "selected" => AudioExport.Selected,
            "none" => AudioExport.None,
            _ => AudioExport.AllTracks
        };
        Int32Rect? crop = null;
        if (_cropMode != "none" && _vw > 0)
        {
            var (cw, ch) = EvenSize();
            int cx = Math.Clamp((int)Math.Round(_crop.X) / 2 * 2, 0, _vw - cw);
            int cy = Math.Clamp((int)Math.Round(_crop.Y) / 2 * 2, 0, _vh - ch);
            crop = new Int32Rect(cx, cy, cw, ch);
        }

        var source = _path;
        var output = replace ? source : ClipExporter.NextOutputPath(source);
        var meta = ClipMeta.Read(source); // the edit keeps its game and clip/recording kind
        var req = new ExportRequest
        {
            Input = source,
            Output = output,
            Start = _in,
            End = _out,
            Crop = crop,
            Audio = audio == AudioExport.Selected && _listen.Count == 0 ? AudioExport.None : audio,
            Tracks = _listen.ToArray(),
            Fast = FastToggle.IsChecked == true && crop == null,
        };

        Pause();
        var keepPos = Now;
        if (replace) Release(); // the player holds the file open

        _exportCts = new CancellationTokenSource();
        ExportText.Text = "Cancel";
        ExportProgress.Visibility = Visibility.Visible;
        ExportBar.Width = 0;
        ShowStatus(req.Fast ? "Exporting (fast)…" : "Exporting…");
        var progress = new Progress<double>(p =>
        {
            ExportBar.Width = Math.Max(0, ExportProgress.ActualWidth * p);
            ExportStatus.Text = $"Exporting… {p * 100:0}%";
        });
        try
        {
            await Task.Run(() => ClipExporter.ExportAsync(req, _encoder?.Invoke(), _settings.Quality, progress, _exportCts.Token));
            meta?.Write(output);
            ShowStatus(replace ? "Saved over the original." : $"Saved as “{Path.GetFileName(output)}”.");
            Exported?.Invoke(output);
        }
        catch (OperationCanceledException) { ShowStatus("Export cancelled."); }
        catch (Exception ex) { ShowStatus(ex.Message, error: true); }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            ExportProgress.Visibility = Visibility.Collapsed;
            UpdateTrimText();
            if (replace && _path != null)
            {
                // reopen the (new) file
                var path = _path;
                Open(path, _settings, _encoder!);
                if (crop == null && req.Start == TimeSpan.Zero) Seek(Clamp(keepPos, TimeSpan.Zero, _out));
            }
        }
    }

    void ShowStatus(string text, bool error = false)
    {
        ExportStatus.Text = text;
        ExportStatus.Foreground = (Brush)FindResource(error ? "RecBrush" : "MutedBrush");
        ExportStatus.Visibility = Visibility.Visible;
    }
}
