using System;
using Microsoft.Win32;

namespace Savor.Core;

/// <summary>Launch-at-login via the per-user Run key (no admin needed).</summary>
public static class StartupManager
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Savor";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(Name) is string;
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                k.SetValue(Name, $"\"{exe}\" --minimized");
            }
            else if (k.GetValue(Name) != null)
            {
                k.DeleteValue(Name);
            }
        }
        catch { }
    }
}
