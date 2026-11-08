using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Savor.Core;

/// <summary>
/// Shows Savor on Isle (github.com/larosa023/Isle), the Dynamic Island for Windows, when it's running:
/// a live "Recording" card with Stop and Marker buttons, "Clip saved" cards with a thumbnail, and
/// Savor's notifications — instead of Savor's own toasts. Everything stays on this PC (127.0.0.1).
/// Button presses come back through a tiny listener on a random local port.
/// </summary>
public static class IsleLink
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromMilliseconds(800) })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    static Func<bool> _enabled = () => true;
    static Dispatcher? _ui;
    static DispatcherTimer? _probe;
    static TcpListener? _listener;
    static int _port;
    static string _base = "http://127.0.0.1:47820";
    static readonly Dictionary<string, Action> Callbacks = new();
    static readonly Queue<string> CallbackOrder = new();
    static bool _recordingShown;

    /// <summary>Isle answered the last check.</summary>
    public static bool Connected { get; private set; }

    /// <summary>Call once at startup with Savor's "show on Isle" setting.</summary>
    public static void Start(Func<bool> enabled)
    {
        _enabled = enabled;
        _ui = Application.Current.Dispatcher;
        _base = $"http://127.0.0.1:{IslePort()}";
        StartListener();
        _probe = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _probe.Tick += (_, _) => _ = ProbeAsync();
        _probe.Start();
        _ = ProbeAsync();
    }

    public static void Stop()
    {
        _probe?.Stop();
        if (_recordingShown) _ = SendAsync(HttpMethod.Delete, "/v1/activities/Savor/recording", null);
        try { _listener?.Stop(); } catch { }
    }

    static bool Active => Connected && _enabled();

    /// <summary>Isle's API port, from Isle's own settings file (47820 unless changed).</summary>
    static int IslePort()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isle", "settings.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals("ApiPort", StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt32(out var port) && port is > 1024 and < 65536)
                        return port;
            }
        }
        catch { }
        return 47820;
    }

    static async Task ProbeAsync()
    {
        bool ok;
        try
        {
            using var r = await Http.GetAsync(_base + "/v1/status").ConfigureAwait(false);
            ok = r.IsSuccessStatusCode;
        }
        catch
        {
            ok = false;
        }
        if (ok != Connected && !ok) _recordingShown = false; // Isle went away; its cards went with it
        Connected = ok;
    }

    // ───────────────────────────── what Savor shows ─────────────────────────────

    /// <summary>Keeps the "Recording" card in step with Savor. Call whenever capture state changes.</summary>
    public static void Recording(bool recording, DateTime startedUtc, Action stop, Action marker)
    {
        if (recording && Active && !_recordingShown)
        {
            _recordingShown = true;
            var since = (startedUtc - DateTime.UnixEpoch).TotalMilliseconds;
            _ = SendAsync(HttpMethod.Post, "/v1/activities", new
            {
                app = "Savor",
                id = "recording",
                title = "Recording",
                subtitle = "Savor",
                glyph = "E7C8",
                accent = "#FF453A",
                live = true,
                priority = 75,
                since,
                peek = true,
                openCallback = Callback(ShowSavor),
                actions = new object[]
                {
                    new { label = "Stop", glyph = "E71A", callback = Callback(stop), primary = true },
                    new { label = "Marker", glyph = "E7C1", callback = Callback(marker) },
                },
            });
        }
        else if (!recording && _recordingShown)
        {
            _recordingShown = false;
            _ = SendAsync(HttpMethod.Delete, "/v1/activities/Savor/recording", null);
        }
    }

    /// <summary>A capture was saved. Returns false when Isle isn't showing it (so Savor shows its own toast).</summary>
    public static bool Saved(string path, bool recording, Action edit, Action reveal, Func<string?> thumbnail)
    {
        if (!Active) return false;
        var id = "saved-" + Math.Abs(path.GetHashCode()).ToString("x");
        var name = Path.GetFileNameWithoutExtension(path);
        object Body(string? image, bool peek) => new
        {
            app = "Savor",
            id,
            title = recording ? "Recording saved" : "Clip saved",
            subtitle = name,
            glyph = "E714",
            image,
            accent = "#30D158",
            priority = 82,
            ttl = 10,
            peek,
            openCallback = Callback(edit),
            actions = new object[]
            {
                new { label = "Edit", glyph = "E70F", callback = Callback(edit), primary = true, dismiss = true },
                new { label = "Copy", glyph = "E8C8", callback = Callback(() => CopyFile(path)), dismiss = true },
                new { glyph = "E838", callback = Callback(reveal), dismiss = true },
            },
        };

        _ = Task.Run(async () =>
        {
            if (!await SendAsync(HttpMethod.Post, "/v1/activities", Body(null, true)).ConfigureAwait(false))
            {
                Fallback(() => ToastWindow.Notify(recording ? "Recording saved" : "Clip saved", Path.GetFileName(path),
                    onClick: reveal, actionText: "Edit", action: edit));
                return;
            }
            // The library makes the thumbnail a moment later; add it when it's ready.
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(400).ConfigureAwait(false);
                var thumb = thumbnail();
                if (thumb != null && File.Exists(thumb))
                {
                    await SendAsync(HttpMethod.Post, "/v1/activities", Body(thumb, false)).ConfigureAwait(false);
                    return;
                }
            }
        });
        return true;
    }

    /// <summary>A Savor notification. Returns false when Isle isn't showing it.</summary>
    public static bool Notify(string title, string body, bool error, Action? onClick, string? actionText, Action? action)
    {
        if (!Active) return false;
        var actions = new List<object>();
        if (actionText != null && action != null) actions.Add(new { label = actionText, callback = Callback(action), primary = true, dismiss = true });
        var payload = new
        {
            app = "Savor",
            id = "note-" + Guid.NewGuid().ToString("N")[..8],
            title,
            subtitle = body,
            glyph = error ? "E7BA" : "E946",
            accent = error ? "#FF9F0A" : "#0A84FF",
            priority = error ? 86 : 80,
            ttl = error ? 8 : 5,
            peek = true,
            openCallback = onClick != null ? Callback(onClick) : null,
            actions,
        };
        _ = Task.Run(async () =>
        {
            if (!await SendAsync(HttpMethod.Post, "/v1/activities", payload).ConfigureAwait(false))
                Fallback(() => ToastWindow.Notify(title, body, error, onClick, actionText, action, skipIsle: true));
        });
        return true;
    }

    static void ShowSavor()
    {
        if (Application.Current.MainWindow is MainWindow w) w.ShowFromTray();
    }

    static void CopyFile(string path)
    {
        try { Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path }); } catch { }
    }

    static void Fallback(Action toast)
    {
        Connected = false;
        _ui?.BeginInvoke(toast);
    }

    static async Task<bool> SendAsync(HttpMethod method, string path, object? body)
    {
        try
        {
            using var req = new HttpRequestMessage(method, _base + path);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var r = await Http.SendAsync(req).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────────── button presses from Isle ─────────────────────────────

    static string? Callback(Action action)
    {
        if (_port == 0) return null;
        var token = Guid.NewGuid().ToString("N");
        lock (Callbacks)
        {
            Callbacks[token] = action;
            CallbackOrder.Enqueue(token);
            while (CallbackOrder.Count > 64) Callbacks.Remove(CallbackOrder.Dequeue());
        }
        return $"http://127.0.0.1:{_port}/isle/{token}";
    }

    static void StartListener()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }
        catch
        {
            _port = 0; // buttons just won't do anything
        }
    }

    static async Task AcceptLoop()
    {
        while (_listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    static async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var buf = new byte[4096];
                int n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false);
                var head = Encoding.ASCII.GetString(buf, 0, n);
                var line = head.Split("\r\n")[0].Split(' ');
                // Only POSTs without a browser Origin, i.e. from Isle itself.
                bool ok = line.Length >= 2 && line[0] == "POST" && line[1].StartsWith("/isle/")
                          && !head.Contains("\r\nOrigin:", StringComparison.OrdinalIgnoreCase);
                Action? action = null;
                if (ok)
                    lock (Callbacks) Callbacks.TryGetValue(line[1]["/isle/".Length..], out action);
                if (action != null) _ui?.BeginInvoke(action);
                var status = action != null ? "204 No Content" : "404 Not Found";
                var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(reply, cts.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }
}
