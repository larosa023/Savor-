using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Savor.Core;

/// <summary>
/// Global hotkeys via low-level keyboard/mouse hooks. Unlike RegisterHotKey this works with any key
/// (Delete, Insert, a lone modifier, mouse side buttons…) and never swallows the key — it still
/// reaches whatever app you're using, the same way OBS hotkeys behave.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    readonly Dispatcher _dispatcher;
    readonly List<(HotkeyBinding binding, Action action)> _bindings = new();
    readonly HashSet<int> _down = new();
    readonly HookProc _kbProc, _mouseProc; // keep delegates alive for the hook's lifetime
    IntPtr _kbHook, _mouseHook;

    /// <summary>While true (e.g. when picking a new shortcut) nothing fires.</summary>
    public bool Suspended { get; set; }

    public HotkeyManager(Window window)
    {
        _dispatcher = window.Dispatcher;
        _kbProc = KeyboardHook;
        _mouseProc = MouseHook;
        var mod = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
    }

    public bool Register(HotkeyBinding binding, Action action)
    {
        if (binding.IsEmpty) return true;
        _bindings.Add((binding, action));
        return true;
    }

    public void UnregisterAll() => _bindings.Clear();

    static bool IsModifierVk(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or VK_LWIN or VK_RWIN;

    static ModifierKeys CurrentModifiers()
    {
        var m = ModifierKeys.None;
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) m |= ModifierKeys.Control;
        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) m |= ModifierKeys.Alt;
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) m |= ModifierKeys.Shift;
        if ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) m |= ModifierKeys.Windows;
        return m;
    }

    IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam);
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (_down.Add(vk)) Match(vk, null); // only on the first press, not auto-repeat
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                _down.Remove(vk);
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam); // never swallow the key
    }

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_XBUTTONDOWN)
            {
                // MSLLHOOKSTRUCT.mouseData is at offset 8; the high word says which X button.
                int data = Marshal.ReadInt32(lParam, 8);
                int which = (data >> 16) & 0xFFFF;
                Match(0, which == 1 ? "XButton1" : "XButton2");
            }
            else if (msg == WM_MBUTTONDOWN)
            {
                Match(0, "Middle");
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    void Match(int vk, string? mouse)
    {
        if (Suspended || _bindings.Count == 0) return;
        var mods = CurrentModifiers();
        foreach (var (b, action) in _bindings)
        {
            bool hit;
            if (mouse != null)
            {
                hit = b.Mouse == mouse && b.Modifiers == mods;
            }
            else
            {
                if (!string.IsNullOrEmpty(b.Mouse) || b.Key == Key.None) continue;
                int bvk = KeyInterop.VirtualKeyFromKey(b.Key);
                if (IsModifierVk(bvk))
                    hit = bvk == vk; // a lone modifier (e.g. Right Ctrl) as the hotkey
                else
                    hit = bvk == vk && b.Modifiers == mods;
            }
            // Run outside the hook callback: hooks must return quickly or Windows drops them.
            if (hit) _dispatcher.BeginInvoke(action);
        }
    }

    public void Dispose()
    {
        if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _bindings.Clear();
    }

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);
}
