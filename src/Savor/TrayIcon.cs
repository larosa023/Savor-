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

    public void Update(bool bufferActive, bool recording)
    {
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
