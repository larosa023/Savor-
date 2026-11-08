using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Savor;

/// <summary>Small non-activating notification shown bottom-right (doesn't steal focus from games).</summary>
public partial class ToastWindow : Window
{
    static ToastWindow? _current;
    readonly Action? _onClick;
    readonly DispatcherTimer _timer;

    readonly Action? _action;

    ToastWindow(string title, string body, bool error, Action? onClick, string? actionText = null, Action? action = null)
    {
        InitializeComponent();
        _action = action;
        if (action != null && actionText != null)
        {
            ActionText.Text = actionText;
            ActionButton.Visibility = Visibility.Visible;
        }
        // stay put while the mouse is on it, so the button can be clicked
        MouseEnter += (_, _) => { _timer?.Stop(); BeginAnimation(OpacityProperty, null); Opacity = 1; };
        MouseLeave += (_, _) => _timer?.Start();
        TitleText.Text = title;
        BodyText.Text = body;
        _onClick = onClick;
        if (error)
        {
            Badge.Background = (Brush)FindResource("RecSoftBrush");
            Glyph.Foreground = (Brush)FindResource("RecBrush");
            Glyph.Text = "";
        }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 6 : action != null ? 5 : 3.5) };
        _timer.Tick += (_, _) => FadeOut();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth;
            Top = area.Bottom - ActualHeight;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            _timer.Start();
        };
    }

    public static void Notify(string title, string body, bool error = false, Action? onClick = null,
                              string? actionText = null, Action? action = null, bool skipIsle = false)
    {
        // With Isle running, Savor's notifications show on the island instead.
        if (!skipIsle && Core.IsleLink.Notify(title, body, error, onClick, actionText, action)) return;
        _current?.Close();
        _current = new ToastWindow(title, body, error, onClick, actionText, action);
        _current.Show();
    }

    void Action_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // not also the toast's own click
        _action?.Invoke();
        FadeOut();
    }

    void FadeOut()
    {
        _timer.Stop();
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        anim.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, anim);
    }

    void OnClick(object sender, MouseButtonEventArgs e)
    {
        _onClick?.Invoke();
        FadeOut();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (_current == this) _current = null;
        base.OnClosed(e);
    }
}
