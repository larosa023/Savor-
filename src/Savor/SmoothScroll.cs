using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Savor;

/// <summary>
/// Smooth mouse-wheel scrolling for a ScrollViewer: each wheel notch sets a target and the view glides there with an
/// ease-out over a few frames instead of jumping 48 px at once. Quick flicks add up, scrollbar drags and keyboard
/// scrolling keep working as before. Use: local:SmoothScroll.Enabled="True".
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);

    const double PixelsPerDelta = 0.9;   // 108 px per wheel notch (WPF's default is 48)
    const double Speed = 16;             // higher = arrives faster; ~150 ms to settle

    sealed class State
    {
        public double Target;
        public bool Animating;
        public TimeSpan Last;
    }

    static readonly Dictionary<ScrollViewer, State> Active = new();
    static bool _hooked;

    static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue) sv.PreviewMouseWheel += OnWheel;
        else sv.PreviewMouseWheel -= OnWheel;
    }

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.ScrollableHeight <= 0 || Keyboard.Modifiers != ModifierKeys.None) return; // nothing to scroll / Ctrl+wheel etc.
        if (!Active.TryGetValue(sv, out var st))
        {
            st = new State { Target = sv.VerticalOffset };
            Active[sv] = st;
        }
        else if (!st.Animating) st.Target = sv.VerticalOffset; // moved meanwhile by the scrollbar or keyboard
        st.Target = Math.Clamp(st.Target - e.Delta * PixelsPerDelta, 0, sv.ScrollableHeight);
        st.Animating = true;
        st.Last = TimeSpan.Zero;
        if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
        e.Handled = true;
    }

    static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        bool any = false;
        foreach (var (sv, st) in Active)
        {
            if (!st.Animating) continue;
            if (!sv.IsLoaded || !sv.IsVisible) { st.Animating = false; continue; }
            double dt = st.Last == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - st.Last).TotalSeconds, 0, 0.05);
            if (dt <= 0) { any = true; continue; } // same frame reported twice
            st.Last = now;
            st.Target = Math.Clamp(st.Target, 0, sv.ScrollableHeight); // content may have changed size
            double cur = sv.VerticalOffset, diff = st.Target - cur;
            if (Math.Abs(diff) < 0.5) { sv.ScrollToVerticalOffset(st.Target); st.Animating = false; continue; }
            sv.ScrollToVerticalOffset(cur + diff * (1 - Math.Exp(-Speed * dt)));
            any = true;
        }
        if (!any)
        {
            // nothing moving: stop per-frame work and forget closed windows' scroll viewers
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
            var gone = new List<ScrollViewer>();
            foreach (var sv in Active.Keys) if (!sv.IsLoaded) gone.Add(sv);
            foreach (var sv in gone) Active.Remove(sv);
        }
    }
}
