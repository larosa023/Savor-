using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Savor.Core;

/// <summary>System-wide hotkeys via RegisterHotKey (work while games are focused).</summary>
public sealed class HotkeyManager : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    readonly IntPtr _hwnd;
    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 0x5A00;

    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(Hook);
    }

    /// <returns>false if another app already owns this combination.</returns>
    public bool Register(HotkeyBinding binding, Action action)
    {
        if (binding.IsEmpty) return true;
        uint mods = MOD_NOREPEAT;
        if (binding.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (binding.Modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (binding.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (binding.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
