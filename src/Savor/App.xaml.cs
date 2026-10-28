using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Savor;

public partial class App : Application
{
    const string MutexName = "Savor.SingleInstance.v1";
    const string ShowEventName = "Savor.ShowWindow.v1";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool updated = e.Args.Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase));
        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst && updated)
        {
            // Just updated: the previous Savor is still shutting down. Wait for it.
            try { isFirst = _mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst)
        {
            // Another Savor is running: ask it to show itself and quit.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        }) { IsBackground = true };
        listener.Start();

        DispatcherUnhandledException += OnUnhandled;

        base.OnStartup(e);
        bool minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (updated) System.Threading.Tasks.Task.Run(Savor.Core.Updater.CleanupOld);
        // "Savor.exe clip.mp4" or "--edit clip.mp4" opens a file in the viewer/editor
        string? edit = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i].Equals("--edit", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length) edit = e.Args[i + 1];
            else if (!e.Args[i].StartsWith("--") && System.IO.File.Exists(e.Args[i])) edit = e.Args[i];
        }
        var window = new MainWindow(minimized, updated, edit);
        MainWindow = window;
        if (!minimized) window.Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Savor.Core.AppSettings.LocalDataDir;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "errors.log"), $"{DateTime.Now:u} Unhandled: {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "Savor", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
