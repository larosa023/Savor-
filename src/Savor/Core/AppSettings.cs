using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace Savor.Core;

public enum QualityPreset { Low, Medium, High, Ultra }

public sealed class HotkeyBinding
{
    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }
    /// <summary>"XButton1", "XButton2" or "Middle" for mouse-button hotkeys; null for keys.</summary>
    public string? Mouse { get; set; }

    public HotkeyBinding() { }
    public HotkeyBinding(ModifierKeys modifiers, Key key) { Modifiers = modifiers; Key = key; }
    public static HotkeyBinding ForMouse(ModifierKeys modifiers, string button) => new() { Modifiers = modifiers, Mouse = button };

    public bool IsEmpty => Key == Key.None && string.IsNullOrEmpty(Mouse);

    public override string ToString()
    {
        if (IsEmpty) return "Not set";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Mouse switch
        {
            "XButton1" => "Mouse 4",
            "XButton2" => "Mouse 5",
            "Middle" => "Middle click",
            _ => KeyName(Key)
        });
        return string.Join(" + ", parts);
    }

    static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(k - Key.NumPad0),
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.Snapshot => "PrtSc",
        Key.Back => "Backspace",
        Key.Return => "Enter",
        Key.Space => "Space",
        Key.Escape => "Esc",
        Key.Capital => "Caps Lock",
        Key.NumLock => "Num Lock",
        Key.LeftCtrl => "Left Ctrl",
        Key.RightCtrl => "Right Ctrl",
        Key.LeftAlt => "Left Alt",
        Key.RightAlt => "Right Alt",
        Key.LeftShift => "Left Shift",
        Key.RightShift => "Right Shift",
        Key.LWin => "Left Win",
        Key.RWin => "Right Win",
        Key.Multiply => "Num *",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Divide => "Num /",
        Key.Decimal => "Num .",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.MediaPlayPause => "Play/Pause",
        Key.MediaNextTrack => "Next track",
        Key.MediaPreviousTrack => "Previous track",
        Key.Scroll => "ScrLk",
        Key.Pause => "Pause",
        Key.Insert => "Ins",
        Key.Delete => "Del",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        _ => k.ToString()
    };
}

/// <summary>An application whose audio gets its own track.</summary>
public sealed class AppAudioSetting
{
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; }
}

public sealed class EncoderCache
{
    public string FfmpegStamp { get; set; } = "";
    public List<string> Working { get; set; } = new();
}

public sealed class AppSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Savor");

    public int DisplayIndex { get; set; } = 0;
    public int Fps { get; set; } = 60;
    public QualityPreset Quality { get; set; } = QualityPreset.High;
    public string EncoderId { get; set; } = "auto";
    public bool CaptureCursor { get; set; } = true;

    public int ReplaySeconds { get; set; } = 60;
    public bool StartBufferOnLaunch { get; set; } = true;

    public bool DesktopAudio { get; set; } = true;
    public double DesktopVolume { get; set; } = 1.0;
    /// <summary>null = microphone off, "default" = system default mic, otherwise an endpoint id.</summary>
    public string? MicDeviceId { get; set; } = null;
    public double MicVolume { get; set; } = 1.0;
    public bool DesktopMuted { get; set; }
    public bool MicMuted { get; set; }

    public bool ShowPreview { get; set; } = true;
    /// <summary>Write Mix + Desktop + Mic as separate audio tracks (when both sources are on).</summary>
    public bool SeparateAudioTracks { get; set; } = true;
    /// <summary>Apps that each get their own audio track (needs separate tracks on).</summary>
    public List<AppAudioSetting> AppTracks { get; set; } = new();

    public string Container { get; set; } = "mp4";

    public HotkeyBinding SaveClipHotkey { get; set; } = new(ModifierKeys.Alt, Key.F10);
    public HotkeyBinding RecordHotkey { get; set; } = new(ModifierKeys.Alt, Key.F9);
    public HotkeyBinding BufferHotkey { get; set; } = new(ModifierKeys.Alt, Key.F8);

    public bool CloseToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Quiet chime when a capture is saved (off by default).</summary>
    public bool PlaySaveSound { get; set; } = false;
    /// <summary>Save chime volume, 0–1 (0.5 = the original quiet chime).</summary>
    public double SaveSoundVolume { get; set; } = 0.5;
    /// <summary>Keep Savor's captures under this many GB by deleting the oldest (0 = no limit).</summary>
    public int StorageLimitGb { get; set; } = 0;
    /// <summary>Newest version whose notes were seen in "What's new".</summary>
    public string LastSeenVersion { get; set; } = "";

    public EncoderCache? EncoderCache { get; set; }

    // ---- persistence ----

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Savor");

    public static string LocalDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Savor");

    static string FilePath => Path.Combine(AppDataDir, "settings.json");

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s != null) return s.Sanitize();
            }
        }
        catch { /* corrupt settings: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, FilePath, true);
        }
        catch { /* non-fatal */ }
    }

    AppSettings Sanitize()
    {
        if (string.IsNullOrWhiteSpace(OutputFolder))
            OutputFolder = new AppSettings().OutputFolder;
        Fps = Math.Clamp(Fps, 10, 240);
        ReplaySeconds = Math.Clamp(ReplaySeconds, 10, 1200);
        DesktopVolume = Math.Clamp(DesktopVolume, 0, 2);
        MicVolume = Math.Clamp(MicVolume, 0, 2);
        if (Container != "mp4" && Container != "mkv") Container = "mp4";
        AppTracks ??= new();
        SaveClipHotkey ??= new();
        RecordHotkey ??= new();
        BufferHotkey ??= new();
        return this;
    }
}
