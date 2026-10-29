using System;
using System.Drawing;
using System.Windows.Forms;

namespace Savor;

/// <summary>Notification-area icon with a dark context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _bufferItem, _saveItem, _recordItem;

    public event Action? ShowRequested;
    public event Action? ToggleBufferRequested;
    public event Action? SaveClipRequested;
    public event Action? ToggleRecordRequested;
    public event Action? OpenFolderRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()),
            ShowImageMargin = false,
            BackColor = Color.FromArgb(0x1E, 0x1F, 0x23),
            ForeColor = Color.FromArgb(0xED, 0xED, 0xEF),
            Font = new Font("Segoe UI", 9.5f),
        };
        var open = Item("Open Savor", () => ShowRequested?.Invoke());
        open.Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);
        _bufferItem = Item("Start replay buffer", () => ToggleBufferRequested?.Invoke());
        _saveItem = Item("Save clip", () => SaveClipRequested?.Invoke());
        _recordItem = Item("Start recording", () => ToggleRecordRequested?.Invoke());
        menu.Items.AddRange(new ToolStripItem[]
        {
            open,
            new ToolStripSeparator(),
            _bufferItem, _saveItem, _recordItem,
            new ToolStripSeparator(),
            Item("Open captures folder", () => OpenFolderRequested?.Invoke()),
            Item("Exit", () => ExitRequested?.Invoke()),
        });

        _icon = new NotifyIcon
        {
            Text = "Savor",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowRequested?.Invoke(); };
    }

    Icon? _normalIcon, _liveIcon;

    public void Update(bool bufferActive, bool recording)
    {
        bool live = bufferActive || recording;
        _normalIcon ??= _icon.Icon;
        if (live) _liveIcon ??= WithDot(_normalIcon);
        var want = live ? _liveIcon : _normalIcon;
        if (want != null && !ReferenceEquals(_icon.Icon, want)) _icon.Icon = want;
        _bufferItem.Text = bufferActive ? "Stop replay buffer" : "Start replay buffer";
        _saveItem.Enabled = bufferActive;
        _recordItem.Text = recording ? "Stop recording" : "Start recording";
        _icon.Text = recording ? "Savor — recording" : bufferActive ? "Savor — replay buffer on" : "Savor";
    }

    static ToolStripMenuItem Item(string text, Action onClick)
    {
        var i = new ToolStripMenuItem(text) { Padding = new Padding(6, 4, 6, 4) };
        i.Click += (_, _) => onClick();
        return i;
    }

    /// <summary>The app icon with a small red dot in the bottom-right corner.</summary>
    static Icon? WithDot(Icon baseIcon)
    {
        try
        {
            int size = SystemInformation.SmallIconSize.Width;
            using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawIcon(new Icon(baseIcon, size, size), new Rectangle(0, 0, size, size));
                float d = size * 0.5f, x = size - d, y = size - d;
                using var ring = new SolidBrush(Color.FromArgb(255, 0x0E, 0x0F, 0x11));
                using var red = new SolidBrush(Color.FromArgb(255, 0xFF, 0x3B, 0x4E));
                g.FillEllipse(ring, x - 1, y - 1, d + 1, d + 1);
                g.FillEllipse(red, x + 1, y + 1, d - 3, d - 3);
            }
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    static Icon LoadIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/savor.ico"));
            if (res != null) return new Icon(res.Stream, SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(0x1E, 0x1F, 0x23);
        static readonly Color Hover = Color.FromArgb(0x2C, 0x2D, 0x33);
        static readonly Color Line = Color.FromArgb(0x33, 0x35, 0x3B);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
    }
}
