using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Savor.Core;

namespace Savor;

public partial class MainWindow : Window
{
    readonly AppSettings _settings;
    readonly CaptureEngine _engine;
    readonly ObservableCollection<ClipItem> _clips = new();
    readonly DispatcherTimer _uiTimer;

    HotkeyManager? _hotkeys;
    TrayIcon? _tray;
    List<EncoderChoice> _encoders = new();
    List<DisplayInfo> _displays = new();
    bool _loadingUi;
    bool _captureDirty;
    bool _exiting;
    bool _busy;
    Button? _capturingHotkey;
    Storyboard? _pulseLive, _pulseRec;
    bool _liveAnimating, _recAnimating;

    public MainWindow(bool startHidden)
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _engine = new CaptureEngine(() => _settings);
        _engine.StateChanged += () => Dispatcher.BeginInvoke(RefreshState);
        _engine.Failed += msg => Dispatcher.BeginInvoke(() => ShowError(msg));
        _engine.RecordingSalvaged += path => Dispatcher.BeginInvoke(() => OnSaved(path, recording: true));

        ClipList.ItemsSource = _clips;
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _uiTimer.Tick += (_, _) => Tick();

        SourceInitialized += (_, _) => OnSourceInitialized();
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Activated += (_, _) => { if (HomePage.Visibility == Visibility.Visible) ReloadClips(); };

        // Make sure the window handle exists even when starting in the tray, so hotkeys work.
        new WindowInteropHelper(this).EnsureHandle();
        _ = InitializeAsync();
    }

    // ===================== startup =====================

    void OnSourceInitialized()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // immersive dark mode
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // Win11 rounded corners
    }

    async Task InitializeAsync()
    {
        _pulseLive = (Storyboard)FindResource("PulseLive");
        _pulseRec = (Storyboard)FindResource("PulseRec");

        _tray = new TrayIcon();
        _tray.ShowRequested += ShowFromTray;
        _tray.ToggleBufferRequested += () => _ = ToggleBufferAsync();
        _tray.SaveClipRequested += () => _ = SaveClipAsync();
        _tray.ToggleRecordRequested += () => _ = ToggleRecordAsync();
        _tray.OpenFolderRequested += OpenOutputFolder;
        _tray.ExitRequested += () => _ = ExitAsync();

        _hotkeys = new HotkeyManager(this);
        RegisterHotkeys();

        _displays = Displays.Get();
        _engine.Display = _displays.FirstOrDefault(d => d.OutputIndex == _settings.DisplayIndex) ?? _displays.FirstOrDefault();

        LoadSettingsIntoUi();
        ReloadClips();
        RefreshState();
        _uiTimer.Start();

        if (!Ffmpeg.Locate())
        {
            CaptureInfo.Text = "FFmpeg missing";
            ShowError("FFmpeg wasn't found. Put ffmpeg.exe next to Savor.exe (the release zip includes it), then restart Savor.");
            BufferToggle.IsEnabled = false;
            RecordButton.IsEnabled = false;
            return;
        }

        await DetectEncodersAsync(force: false);

        if (_settings.StartBufferOnLaunch && _engine.Encoder != null)
            await ToggleBufferAsync(forceOn: true);
    }

    async Task DetectEncodersAsync(bool force)
    {
        var stamp = Ffmpeg.Stamp();
        if (!force && _settings.EncoderCache is { } cache && cache.FfmpegStamp == stamp && cache.Working.Count > 0)
        {
            _encoders = Ffmpeg.FromCache(cache);
        }
        else
        {
            CaptureInfo.Text = "Detecting encoders…";
            BufferToggle.IsEnabled = false;
            RecordButton.IsEnabled = false;
            var progress = new Progress<string>(s => CaptureInfo.Text = s);
            _encoders = await Task.Run(() => Ffmpeg.ProbeAsync(progress));
            _settings.EncoderCache = new EncoderCache { FfmpegStamp = stamp, Working = _encoders.Select(e => e.Key).ToList() };
            _settings.Save();
            BufferToggle.IsEnabled = true;
            RecordButton.IsEnabled = true;
        }

        _engine.Encoder = Ffmpeg.Pick(_encoders, _settings.EncoderId);
        if (_engine.Encoder == null)
        {
            ShowError("No working video encoder was found. Make sure your graphics drivers are up to date, then use Settings → Encoder → re-detect.");
            BufferToggle.IsEnabled = false;
            RecordButton.IsEnabled = false;
        }
        PopulateEncoderCombo();
        RefreshState();
    }

    // ===================== core actions =====================

    async Task ToggleBufferAsync(bool? forceOn = null)
    {
        bool on = forceOn ?? !_engine.BufferActive;
        try
        {
            if (on) await _engine.StartBufferAsync();
            else await _engine.StopBufferAsync();
            HideError();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        RefreshState();
    }

    async Task SaveClipAsync()
    {
        if (_busy) return;
        if (!_engine.BufferActive)
        {
            Notify("Replay buffer is off", "Turn it on first, then save a clip.", error: true);
            return;
        }
        _busy = true;
        SaveClipText.Text = "Saving…";
        SaveClipButton.IsEnabled = false;
        try
        {
            var path = await _engine.SaveReplayAsync();
            if (path != null) OnSaved(path, recording: false);
            else Notify("Couldn't save clip", "Give the buffer a moment to fill up, then try again.", error: true);
        }
        catch (Exception ex) { Notify("Couldn't save clip", ex.Message, error: true); }
        finally
        {
            _busy = false;
            SaveClipText.Text = "Save clip";
            RefreshState();
        }
    }

    async Task ToggleRecordAsync()
    {
        try
        {
            if (_engine.Recording)
            {
                RecordText.Text = "Finishing…";
                RecordButton.IsEnabled = false;
                var path = await _engine.StopRecordingAsync();
                if (path != null) OnSaved(path, recording: true);
                else Notify("Recording failed", "Nothing could be written. Check Settings → Output.", error: true);
                if (_captureDirty) await ApplyCaptureChangesAsync();
            }
            else
            {
                await _engine.StartRecordingAsync();
                HideError();
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            RecordButton.IsEnabled = _engine.Encoder != null;
            RefreshState();
        }
    }

    void OnSaved(string path, bool recording)
    {
        ReloadClips();
        if (_settings.ShowNotifications)
            Notify(recording ? "Recording saved" : "Clip saved", Path.GetFileName(path), onClick: () => Reveal(path));
    }

    void Notify(string title, string body, bool error = false, Action? onClick = null) =>
        ToastWindow.Notify(title, body, error, onClick);

    // ===================== UI state =====================

    void RefreshState()
    {
        bool buffer = _engine.BufferActive, rec = _engine.Recording;

        _loadingUi = true;
        BufferToggle.IsChecked = buffer;
        _loadingUi = false;

        BufferStatus.Text = buffer ? "Live" : "Off";
        BufferDot.Fill = (Brush)FindResource(buffer ? "LiveBrush" : "FaintBrush");
        if (buffer != _liveAnimating)
        {
            _liveAnimating = buffer;
            if (buffer) _pulseLive?.Begin(this, true); else { _pulseLive?.Stop(this); BufferDotHalo.Opacity = 0; }
        }
        BufferHint.Text = buffer
            ? $"Holding the last {FormatSeconds(_settings.ReplaySeconds)}. Press {_settings.SaveClipHotkey} anytime to save it."
            : $"Keeps the last {FormatSeconds(_settings.ReplaySeconds)} of your screen ready to save as a clip.";
        if (!_busy) SaveClipButton.IsEnabled = buffer;

        RecordText.Text = rec ? "Stop recording" : "Start recording";
        RecordIcon.Text = rec ? "" : "";
        RecDot.Fill = (Brush)FindResource(rec ? "RecBrush" : "FaintBrush");
        if (rec != _recAnimating)
        {
            _recAnimating = rec;
            if (rec) _pulseRec?.Begin(this, true); else { _pulseRec?.Stop(this); RecDot.Opacity = 1; }
        }
        HeaderRecBadge.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        if (!rec) RecTimer.Text = "00:00:00";

        SaveClipHotkeyText.Text = _settings.SaveClipHotkey.ToString();
        RecordHotkeyText.Text = _settings.RecordHotkey.ToString();
        EmptyHint.Text = $"Turn on the replay buffer, then press {_settings.SaveClipHotkey} to save a clip.";

        var enc = _engine.Encoder;
        var disp = _engine.Display;
        if (enc != null)
        {
            var parts = new List<string>();
            if (disp != null) parts.Add($"Display {disp.OutputIndex + 1}");
            parts.Add($"{_settings.Fps} fps");
            parts.Add(enc.Name);
            parts.Add(_settings.Quality.ToString());
            CaptureInfo.Text = string.Join("  ·  ", parts);
        }

        MicIcon.Opacity = string.IsNullOrEmpty(_settings.MicDeviceId) ? 0.35 : 1;
        _tray?.Update(buffer, rec);
    }

    void Tick()
    {
        if (_engine.Recording)
        {
            var t = DateTime.UtcNow - _engine.RecordingStartedUtc;
            RecTimer.Text = t.ToString(@"hh\:mm\:ss");
            HeaderRecText.Text = "REC " + (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss"));
        }

        var mixer = _engine.IsRunning ? _engine.Mixer : null;
        SetMeter(DesktopMeter, mixer?.DesktopLevel ?? 0);
        SetMeter(MicMeter, mixer?.MicLevel ?? 0);
    }

    static void SetMeter(Border meter, float level)
    {
        // perceptual-ish curve so normal speech/game audio is visible
        double target = Math.Sqrt(Math.Clamp(level, 0, 1)) * 90;
        double cur = meter.Width;
        meter.Width = target > cur ? target : Math.Max(0, cur - 4); // fast attack, slow release
    }

    void ShowError(string message)
    {
        BannerText.Text = message;
        Banner.Visibility = Visibility.Visible;
        if (!IsVisible) Notify("Savor", message, error: true);
    }

    void HideError() => Banner.Visibility = Visibility.Collapsed;

    static string FormatSeconds(int s) =>
        s < 60 ? $"{s} seconds" : s % 60 == 0 ? (s == 60 ? "minute" : $"{s / 60} minutes") : $"{s / 60}:{s % 60:00}";

    // ===================== clips =====================

    void ReloadClips()
    {
        var items = Library.Scan(_settings.OutputFolder);
        var known = _clips.ToDictionary(c => c.FilePath + c.Size, c => c);
        _clips.Clear();
        foreach (var it in items)
        {
            if (known.TryGetValue(it.FilePath + it.Size, out var existing)) { _clips.Add(existing); continue; }
            _clips.Add(it);
            _ = Library.LoadThumbnailAsync(it);
        }
        EmptyState.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipCount.Text = _clips.Count == 0 ? "" : _clips.Count.ToString();
    }

    static ClipItem? ClipFrom(object sender) => (sender as FrameworkElement)?.DataContext as ClipItem;

    void ClipCard_Click(object sender, MouseButtonEventArgs e) { if (ClipFrom(sender) is { } c) OpenFile(c.FilePath); }
    void ClipOpen_Click(object sender, RoutedEventArgs e) { if (ClipFrom(sender) is { } c) OpenFile(c.FilePath); }
    void ClipReveal_Click(object sender, RoutedEventArgs e) { if (ClipFrom(sender) is { } c) Reveal(c.FilePath); }

    void ClipCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ClipFrom(sender) is not { } c) return;
        try
        {
            var files = new System.Collections.Specialized.StringCollection { c.FilePath };
            Clipboard.SetFileDropList(files);
            Notify("Copied", "Paste it into Discord, a chat, or a folder.");
        }
        catch { }
    }

    void ClipDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ClipFrom(sender) is not { } c) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(c.FilePath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) { Notify("Couldn't delete", ex.Message, error: true); }
        ReloadClips();
    }

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            Process.Start(new ProcessStartInfo(_settings.OutputFolder) { UseShellExecute = true });
        }
        catch { }
    }

    // ===================== hotkeys =====================

    void RegisterHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        if (!_hotkeys.Register(_settings.SaveClipHotkey, () => _ = SaveClipAsync())) failed.Add(_settings.SaveClipHotkey.ToString());
        if (!_hotkeys.Register(_settings.RecordHotkey, () => _ = ToggleRecordAsync())) failed.Add(_settings.RecordHotkey.ToString());
        if (!_hotkeys.Register(_settings.BufferHotkey, () => _ = ToggleBufferAsync())) failed.Add(_settings.BufferHotkey.ToString());
        HotkeyNote.Text = failed.Count == 0
            ? "Click a shortcut, then press the new keys. Backspace clears it, Esc cancels."
            : $"Already used by another app: {string.Join(", ", failed)}. Pick a different combination.";
        HotkeyNote.Foreground = (Brush)FindResource(failed.Count == 0 ? "MutedBrush" : "RecBrush");
    }

    HotkeyBinding BindingFor(Button b) => (string)b.Tag switch
    {
        "save" => _settings.SaveClipHotkey,
        "record" => _settings.RecordHotkey,
        _ => _settings.BufferHotkey
    };

    void SetBinding(Button b, HotkeyBinding hk)
    {
        switch ((string)b.Tag)
        {
            case "save": _settings.SaveClipHotkey = hk; break;
            case "record": _settings.RecordHotkey = hk; break;
            default: _settings.BufferHotkey = hk; break;
        }
    }

    void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        if (_capturingHotkey == b) return;
        // Ignore clicks synthesized by Space/Enter right after binding a combo.
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) return;
        if (_capturingHotkey != null) EndHotkeyCapture();
        _capturingHotkey = b;
        _hotkeys?.UnregisterAll(); // so pressing the current combo doesn't fire it
        b.Content = "Press keys…";
        b.BorderBrush = (Brush)FindResource("AccentBrush");
        b.Focus();
    }

    void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey != sender) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
            return; // wait for a real key

        var b = (Button)sender;
        if (key == Key.Escape) { EndHotkeyCapture(); return; }
        if (key == Key.Back) { SetBinding(b, new HotkeyBinding()); }
        else
        {
            var mods = Keyboard.Modifiers;
            bool isFKey = key >= Key.F1 && key <= Key.F24;
            if (mods == ModifierKeys.None && !isFKey && key is not (Key.Snapshot or Key.Scroll or Key.Pause))
            {
                HotkeyNote.Text = "Add Ctrl, Alt or Shift (plain F-keys are fine on their own).";
                HotkeyNote.Foreground = (Brush)FindResource("RecBrush");
                return;
            }
            SetBinding(b, new HotkeyBinding(mods, key));
        }
        _settings.Save();
        EndHotkeyCapture();
    }

    void Hotkey_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey == sender) EndHotkeyCapture();
    }

    void EndHotkeyCapture()
    {
        var b = _capturingHotkey;
        _capturingHotkey = null;
        if (b != null)
        {
            b.ClearValue(BorderBrushProperty);
            b.Content = BindingFor(b).ToString();
        }
        RegisterHotkeys();
        RefreshState();
    }

    // ===================== settings page =====================

    void LoadSettingsIntoUi()
    {
        _loadingUi = true;
        try
        {
            DisplayCombo.Items.Clear();
            foreach (var d in _displays) DisplayCombo.Items.Add(new ComboBoxItem { Content = d.Label, Tag = d.OutputIndex });
            SelectByTag(DisplayCombo, _engine.Display?.OutputIndex ?? 0);

            FpsCombo.Items.Clear();
            foreach (var f in new[] { 30, 60, 120, 144, 165, 240 })
                FpsCombo.Items.Add(new ComboBoxItem { Content = $"{f} fps", Tag = f });
            if (!SelectByTag(FpsCombo, _settings.Fps)) SelectByTag(FpsCombo, 60);

            QualityCombo.Items.Clear();
            foreach (var (q, label) in new[] {
                (QualityPreset.Low, "Low · smallest files"), (QualityPreset.Medium, "Medium"),
                (QualityPreset.High, "High · recommended"), (QualityPreset.Ultra, "Ultra · near lossless") })
                QualityCombo.Items.Add(new ComboBoxItem { Content = label, Tag = q });
            SelectByTag(QualityCombo, _settings.Quality);

            PopulateEncoderCombo();
            CursorToggle.IsChecked = _settings.CaptureCursor;

            ReplaySlider.Value = _settings.ReplaySeconds;
            ReplayValue.Text = FormatShort(_settings.ReplaySeconds);
            AutoBufferToggle.IsChecked = _settings.StartBufferOnLaunch;

            DesktopAudioToggle.IsChecked = _settings.DesktopAudio;
            DesktopVolumeSlider.Value = _settings.DesktopVolume * 100;
            DesktopVolumeValue.Text = $"{_settings.DesktopVolume * 100:0}%";
            MicCombo.Items.Clear();
            MicCombo.Items.Add(new ComboBoxItem { Content = "Off", Tag = "" });
            MicCombo.Items.Add(new ComboBoxItem { Content = "Default microphone", Tag = "default" });
            foreach (var m in AudioMixer.Microphones()) MicCombo.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
            if (!SelectByTag(MicCombo, _settings.MicDeviceId ?? "")) SelectByTag(MicCombo, "");
            MicVolumeSlider.Value = _settings.MicVolume * 100;
            MicVolumeValue.Text = $"{_settings.MicVolume * 100:0}%";

            OutputFolderText.Text = _settings.OutputFolder;
            ContainerCombo.Items.Clear();
            ContainerCombo.Items.Add(new ComboBoxItem { Content = "MP4", Tag = "mp4" });
            ContainerCombo.Items.Add(new ComboBoxItem { Content = "MKV", Tag = "mkv" });
            SelectByTag(ContainerCombo, _settings.Container);

            SaveClipHotkeyButton.Content = _settings.SaveClipHotkey.ToString();
            RecordHotkeyButton.Content = _settings.RecordHotkey.ToString();
            BufferHotkeyButton.Content = _settings.BufferHotkey.ToString();

            StartupToggle.IsChecked = StartupManager.IsEnabled;
            TrayToggle.IsChecked = _settings.CloseToTray;
            NotifyToggle.IsChecked = _settings.ShowNotifications;

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            AboutText.Text = $"Savor {ver?.ToString(3)}  ·  FFmpeg: {Ffmpeg.Path ?? "not found"}";
        }
        finally { _loadingUi = false; }
    }

    void PopulateEncoderCombo()
    {
        bool wasLoading = _loadingUi;
        _loadingUi = true;
        try
        {
            EncoderCombo.Items.Clear();
            var auto = Ffmpeg.Pick(_encoders, "auto");
            EncoderCombo.Items.Add(new ComboBoxItem { Content = auto != null ? $"Auto ({auto.Name})" : "Auto", Tag = "auto" });
            foreach (var e in _encoders) EncoderCombo.Items.Add(new ComboBoxItem { Content = e.Name, Tag = e.Def.Id });
            if (!SelectByTag(EncoderCombo, _settings.EncoderId)) SelectByTag(EncoderCombo, "auto");
            EncoderDesc.Text = _encoders.Count == 0
                ? "No encoders detected yet."
                : $"{_encoders.Count} working encoder{(_encoders.Count == 1 ? "" : "s")} found. GPU encoders keep games running smoothly.";
        }
        finally { _loadingUi = wasLoading; }
    }

    static bool SelectByTag(ComboBox combo, object tag)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (Equals(item.Tag, tag)) { combo.SelectedItem = item; return true; }
        return false;
    }

    static T? SelectedTag<T>(ComboBox combo) => combo.SelectedItem is ComboBoxItem { Tag: T t } ? t : default;

    static string FormatShort(int s) => s < 60 ? $"{s}s" : $"{s / 60}:{s % 60:00}";

    void Capture_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.DisplayIndex = SelectedTag<int>(DisplayCombo);
        int fps = SelectedTag<int>(FpsCombo);
        _settings.Fps = fps > 0 ? fps : 60;
        if (QualityCombo.SelectedItem is ComboBoxItem { Tag: QualityPreset q }) _settings.Quality = q;
        _settings.EncoderId = SelectedTag<string>(EncoderCombo) ?? "auto";
        _settings.CaptureCursor = CursorToggle.IsChecked == true;
        _settings.DesktopAudio = DesktopAudioToggle.IsChecked == true;
        var mic = SelectedTag<string>(MicCombo);
        _settings.MicDeviceId = string.IsNullOrEmpty(mic) ? null : mic;
        _settings.Save();

        _engine.Display = _displays.FirstOrDefault(d => d.OutputIndex == _settings.DisplayIndex) ?? _displays.FirstOrDefault();
        _engine.Encoder = Ffmpeg.Pick(_encoders, _settings.EncoderId);
        _captureDirty = true;
        ApplyNote.Visibility = _engine.Recording ? Visibility.Visible : Visibility.Collapsed;
        RefreshState();
    }

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingUi || DesktopVolumeValue == null || MicVolumeValue == null) return;
        _settings.DesktopVolume = DesktopVolumeSlider.Value / 100;
        _settings.MicVolume = MicVolumeSlider.Value / 100;
        DesktopVolumeValue.Text = $"{DesktopVolumeSlider.Value:0}%";
        MicVolumeValue.Text = $"{MicVolumeSlider.Value:0}%";
        // volume applies live, no restart needed
        if (_engine.Mixer is { } m)
        {
            m.DesktopVolume = (float)_settings.DesktopVolume;
            m.MicVolume = (float)_settings.MicVolume;
        }
        _settings.Save();
    }

    void ReplaySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingUi || ReplayValue == null) return;
        _settings.ReplaySeconds = (int)ReplaySlider.Value;
        ReplayValue.Text = FormatShort(_settings.ReplaySeconds);
        _settings.Save();
        RefreshState();
    }

    void General_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.StartBufferOnLaunch = AutoBufferToggle.IsChecked == true;
        _settings.CloseToTray = TrayToggle.IsChecked == true;
        _settings.ShowNotifications = NotifyToggle.IsChecked == true;
        var container = SelectedTag<string>(ContainerCombo) ?? "mp4";
        _settings.Container = container;
        if (ReferenceEquals(sender, StartupToggle)) StartupManager.Set(StartupToggle.IsChecked == true);
        _settings.Save();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where Savor saves captures",
            InitialDirectory = Directory.Exists(_settings.OutputFolder) ? _settings.OutputFolder : null,
        };
        if (dlg.ShowDialog(this) == true)
        {
            _settings.OutputFolder = dlg.FolderName;
            OutputFolderText.Text = dlg.FolderName;
            _settings.Save();
            _clips.Clear();
            ReloadClips();
        }
    }

    async void Redetect_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            Notify("Turn off capture first", "Stop the replay buffer and recording, then re-detect encoders.", error: true);
            return;
        }
        await DetectEncodersAsync(force: true);
    }

    async Task ApplyCaptureChangesAsync()
    {
        if (!_captureDirty) return;
        try
        {
            if (await _engine.RestartIfIdleAsync())
            {
                _captureDirty = false;
                ApplyNote.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ===================== navigation & chrome =====================

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPage.Visibility == Visibility.Visible) { _ = BackAsync(); return; }
        LoadSettingsIntoUi();
        ApplyNote.Visibility = _engine.Recording && _captureDirty ? Visibility.Visible : Visibility.Collapsed;
        SwitchPage(SettingsPage, HomePage);
    }

    void Back_Click(object sender, RoutedEventArgs e) => _ = BackAsync();

    async Task BackAsync()
    {
        if (_capturingHotkey != null) EndHotkeyCapture();
        SwitchPage(HomePage, SettingsPage);
        ReloadClips();
        await ApplyCaptureChangesAsync();
        RefreshState();
    }

    static void SwitchPage(FrameworkElement show, FrameworkElement hide)
    {
        hide.Visibility = Visibility.Collapsed;
        show.Opacity = 0;
        show.Visibility = Visibility.Visible;
        var tt = new TranslateTransform(0, 8);
        show.RenderTransform = tt;
        var dur = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
    }

    void BufferToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _ = ToggleBufferAsync(BufferToggle.IsChecked == true);
    }

    void SaveClip_Click(object sender, RoutedEventArgs e) => _ = SaveClipAsync();
    void Record_Click(object sender, RoutedEventArgs e) => _ = ToggleRecordAsync();
    void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenOutputFolder();
    void DismissBanner_Click(object sender, RoutedEventArgs e) => HideError();
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.CloseToTray) Hide();
        else _ = ExitAsync();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        ReloadClips();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            // Alt+F4 / taskbar close behave like the X button.
            e.Cancel = true;
            if (_settings.CloseToTray) Hide();
            else _ = ExitAsync();
            return;
        }
        base.OnClosing(e);
    }

    async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _uiTimer.Stop();
        if (_engine.Recording)
        {
            Notify("Finishing recording…", "Savor will close once it's saved.");
        }
        try { await _engine.ShutdownAsync(); } catch { }
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _engine.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
