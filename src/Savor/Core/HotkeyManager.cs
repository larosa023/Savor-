using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Savor.Core;

/// <summary>
/// Global hotkeys via low-level keyboard/mouse hooks. Works with any key (Delete, Insert, a lone modifier,
/// mouse side buttons…) and never swallows the key — it still reaches the game, like OBS hotkeys.
///
/// The hooks live on their own thread with its own message loop. Windows silently removes a low-level hook
/// whose thread is slow to respond, so keeping them off the UI thread means hotkeys keep working even if
/// the window is busy.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B, WM_QUIT = 0x0012;
    const uint WM_REHOOK = 0x8000 + 0x51; // WM_APP + n: reinstall the hooks
    System.Threading.Timer? _rehookTimer;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    readonly Dispatcher _ui;
    volatile (HotkeyBinding binding, int vk, Action action)[] _bindings = Array.Empty<(HotkeyBinding, int, Action)>();
    readonly Dictionary<int, long> _down = new(); // key → time of its last key-down event (hook thread only)
    readonly HookProc _kbProc, _mouseProc; // keep delegates alive for the hooks' lifetime
    readonly Thread _thread;
    uint _threadId;
    IntPtr _kbHook, _mouseHook;

    /// <summary>While true (e.g. when picking a new shortcut) nothing fires.</summary>
    public volatile bool Suspended;

    public HotkeyManager(Window window)
    {
        _ui = window.Dispatcher;
        _kbProc = KeyboardHook;
        _mouseProc = MouseHook;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var mod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
            ready.Set();
            // plain Win32 message loop: low-level hooks are delivered through it
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_REHOOK)
                {
                    // Windows can silently drop a low-level hook (e.g. after the PC was busy or asleep).
                    // Re-installing it periodically means hotkeys always come back on their own.
                    var kb = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
                    var ms = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
                    if (kb != IntPtr.Zero) { if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook); _kbHook = kb; }
                    if (ms != IntPtr.Zero) { if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook); _mouseHook = ms; }
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        })
        { IsBackground = true, Name = "Savor hotkeys", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
        _rehookTimer = new System.Threading.Timer(_ =>
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_REHOOK, IntPtr.Zero, IntPtr.Zero);
        }, null, 30_000, 30_000);
    }

    public bool Register(HotkeyBinding binding, Action action)
    {
        if (binding.IsEmpty) return true;
        int vk = binding.Key == Key.None ? 0 : KeyInterop.VirtualKeyFromKey(binding.Key);
        var list = new List<(HotkeyBinding, int, Action)>(_bindings) { (binding, vk, action) };
        _bindings = list.ToArray(); // copy-on-write: the hook thread always sees a complete list
        return true;
    }

    public void UnregisterAll() => _bindings = Array.Empty<(HotkeyBinding, int, Action)>();

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
                // Held keys auto-repeat key-downs every ~30 ms (first repeat within 1 s), so a key-down more than
                // 1.1 s after the previous one is a new press even if its key-up was lost (e.g. to a UAC prompt).
                long now = Environment.TickCount64;
                bool fresh = !_down.TryGetValue(vk, out var last) || now - last > 1100;
                _down[vk] = now;
                if (fresh) Match(vk, null);
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                _down.Remove(vk);
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam); // never swallow the key
    }

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_XBUTTONDOWN)
            {
                // MSLLHOOKSTRUCT.mouseData is at offset 8; the high word says which X button
                int data = Marshal.ReadInt32(lParam, 8);
                Match(0, ((data >> 16) & 0xFFFF) == 1 ? "XButton1" : "XButton2");
            }
            else if (msg == WM_MBUTTONDOWN)
            {
                Match(0, "Middle");
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    void Match(int vk, string? mouse)
    {
        var bindings = _bindings;
        if (Suspended || bindings.Length == 0) return;
        var mods = CurrentModifiers();
        foreach (var (b, bvk, action) in bindings)
        {
            bool hit;
            if (mouse != null) hit = b.Mouse == mouse && b.Modifiers == mods;
            else if (!string.IsNullOrEmpty(b.Mouse) || bvk == 0) continue;
            else if (IsModifierVk(bvk)) hit = bvk == vk; // a lone modifier (e.g. Right Ctrl) as the hotkey
            else hit = bvk == vk && b.Modifiers == mods;
            // hand off to the UI thread and return right away: hooks must be answered quickly
            if (hit) _ui.BeginInvoke(action);
        }
    }

    public void Dispose()
    {
        _bindings = Array.Empty<(HotkeyBinding, int, Action)>();
        _rehookTimer?.Dispose();
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    // ---- interop ----

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? lpModuleName);
}
