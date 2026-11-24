using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using D = System.Drawing;
using M = System.Windows.Media;
using WF = System.Windows.Forms;

namespace Asker;

public partial class App : Application
{
    public static Settings Settings = Settings.Load();
    public const string Api = "https://inference-api.nousresearch.com/v1";
    // The API sits behind Cloudflare, which answers 403 to requests without a User-Agent.
    public static HttpClient Http { get; private set; } = CreateHttp(Settings);

    // Rebuilt when settings are saved: a handler's proxy can't change after its first request.
    public static HttpClient CreateHttp(Settings s)
    {
        var handler = new HttpClientHandler(); // no proxy host = Windows system proxy, as before
        if (s.ProxyHost != "")
            handler.Proxy = new System.Net.WebProxy(ProxyUri(s.ProxyHost, s.ProxyPort))
            {
                Credentials = s.ProxyUser == "" ? null : new System.Net.NetworkCredential(s.ProxyUser, s.ProxyPassword),
            };
        return new HttpClient(handler) { DefaultRequestHeaders = { { "User-Agent", "Asker/1.0" } } };
    }

    // "proxy.example.com" -> http://proxy.example.com:port; a scheme like socks5:// is kept.
    public static Uri ProxyUri(string host, int port) =>
        new UriBuilder(host.Contains("://") ? host : "http://" + host) { Port = port }.Uri;
    const int ToggleId = 1000;

    Mutex? _mutex;
    HwndSource _msg = null!;
    WF.NotifyIcon _tray = null!;
    ResultWindow _result = null!;
    SettingsWindow? _settings;
    readonly List<int> _ids = new();

    // Design tokens: (dark, light).
    static readonly (string Key, string Dark, string Light)[] Palette =
    {
        ("Bg",           "#16161A", "#F5F5F7"),
        ("Surface",      "#202026", "#FFFFFF"),
        ("SurfaceHover", "#2A2A31", "#EDEDF1"),
        ("Line",         "#34343D", "#DEDEE4"),
        ("Text",         "#EDEDF0", "#18181B"),
        ("Muted",        "#8E8E9A", "#6B6B76"),
        ("Accent",       "#7C5CFF", "#6A4BF5"),
        ("AccentHover",  "#8F74FF", "#5A3BE0"),
        ("OnAccent",     "#FFFFFF", "#FFFFFF"),
        ("Danger",       "#FF7A7A", "#D63A3A"),
        ("Focus",        "#997C5CFF", "#996A4BF5"),
    };

    public static bool IsLight
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 1;
        }
    }

    public static void ApplyCaptureSetting(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(hwnd, Settings.HideFromCapture ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    void ApplyTheme()
    {
        bool light = IsLight;
        foreach (var (key, dark, lightHex) in Palette)
        {
            var brush = new M.SolidColorBrush((M.Color)M.ColorConverter.ConvertFromString(light ? lightHex : dark));
            brush.Freeze();
            Resources[key] = brush;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Asker.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Debug.Assert(Hotkey.TryParse("Ctrl+Alt+Q", out var m, out var vk) && m == 3 && vk == 0x51);

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, a) => { if (a.Category == UserPreferenceCategory.General) Dispatcher.Invoke(ApplyTheme); };

        _result = new ResultWindow();
        // Message-only window (HWND_MESSAGE) that receives WM_HOTKEY.
        _msg = new HwndSource(new HwndSourceParameters("Asker") { ParentWindow = new IntPtr(-3) });
        _msg.AddHook(WndProc);

        _tray = new WF.NotifyIcon { Icon = TrayIcon(), Text = "Asker", Visible = true, ContextMenuStrip = new() };
        _tray.ContextMenuStrip.Items.Add("Settings", null, (_, _) => ShowSettings());
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.MouseClick += (_, a) => { if (a.Button == WF.MouseButtons.Left) ShowSettings(); };

        RegisterHotkeys();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) _tray.Visible = false;
        _mutex?.Dispose();
    }

    void RegisterHotkeys()
    {
        var failed = new List<string>();
        for (int i = 0; i < Settings.Bindings.Count; i++)
        {
            var hk = Settings.Bindings[i].Hotkey;
            if (Hotkey.TryParse(hk, out var mods, out var vk) && Native.RegisterHotKey(_msg.Handle, i, mods | Native.MOD_NOREPEAT, vk))
                _ids.Add(i);
            else
                failed.Add(hk);
        }
        if (Settings.ToggleHotkey != "")
        {
            if (Hotkey.TryParse(Settings.ToggleHotkey, out var mods, out var vk) && Native.RegisterHotKey(_msg.Handle, ToggleId, mods | Native.MOD_NOREPEAT, vk))
                _ids.Add(ToggleId);
            else
                failed.Add(Settings.ToggleHotkey);
        }
        if (failed.Count > 0)
            _tray.ShowBalloonTip(4000, "Asker", "Could not register hotkey (in use by another app?): " + string.Join(", ", failed), WF.ToolTipIcon.Warning);
    }

    void UnregisterHotkeys()
    {
        foreach (var id in _ids) Native.UnregisterHotKey(_msg.Handle, id);
        _ids.Clear();
    }

    void ShowSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        UnregisterHotkeys(); // so pressing an existing hotkey in the editor records it instead of firing
        _settings = new SettingsWindow(Settings.Clone());
        if (_settings.ShowDialog() == true)
        {
            _settings.Result.SidebarCollapsed = Settings.SidebarCollapsed; // may have been toggled while the dialog was open
            Settings = _settings.Result;
            Settings.Save();
            Http = CreateHttp(Settings); // old client is left for any answer still streaming on it
            ApplyCaptureSetting(_result);
            _result.ApplyRetention();
        }
        _settings = null;
        RegisterHotkeys();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        int id = w.ToInt32();
        if (id == ToggleId) _result.Toggle();
        else if (id < Settings.Bindings.Count) _result.Ask(Settings.Bindings[id]);
        handled = true;
        return IntPtr.Zero;
    }

    // Tray-sized frame of the embedded Asker.ico (picks the 16/20/24px entry matching the display scale).
    static D.Icon TrayIcon()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Asker;component/Asker.ico")).Stream;
        return new D.Icon(stream, WF.SystemInformation.SmallIconSize);
    }
}
