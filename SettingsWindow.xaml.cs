using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Asker;

public partial class SettingsWindow : Window
{
    public Settings Result { get; }
    // Shown even before the live list loads; the live list adds the rest.
    public ObservableCollection<string> Models { get; } = new()
    {
        "openai/gpt-6.1-sol", "anthropic/claude-sonnet-5.5", "google/gemini-3.8-flash", "openai/gpt-6.1-terra",
        "anthropic/claude-haiku-4.5", "openai/gpt-6-astra", "openai/gpt-6-luna",
    };
    string _loadedKey = "";
    static readonly int[] RetentionDays = { 0, 1, 7, 30, 90 }; // matches RetentionBox items

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        Result = settings;
        KeyBox.Password = settings.ApiKey;
        ToggleBox.Text = settings.ToggleHotkey;
        StartupBox.IsChecked = Startup.IsEnabled;
        HideCaptureBox.IsChecked = settings.HideFromCapture;
        ProxyHostBox.Text = settings.ProxyHost;
        ProxyPortBox.Text = settings.ProxyPort == 0 ? "" : settings.ProxyPort.ToString();
        ProxyUserBox.Text = settings.ProxyUser;
        ProxyPasswordBox.Password = settings.ProxyPassword;
        // A value not in the list (hand-edited settings.json) shows as the nearest longer choice.
        int retention = Array.FindIndex(RetentionDays, d => d >= settings.HistoryDays);
        RetentionBox.SelectedIndex = retention < 0 ? RetentionDays.Length - 1 : retention;
        List.ItemsSource = settings.Bindings;
        SourceInitialized += (_, _) =>
        {
            Native.SetDwm(new WindowInteropHelper(this).Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
            App.ApplyCaptureSetting(this);
        };
        LoadModels();
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    void OnKeyChanged(object sender, RoutedEventArgs e) => LoadModels();

    async void LoadModels()
    {
        var key = KeyBox.Password.Trim();
        if (ProxyError() is { } proxyError) { Error.Text = proxyError; return; }
        var proxy = new Settings();
        ApplyProxy(proxy);
        // Reload when the key or the proxy changes, so a wrong proxy shows up here instead of on the first hotkey.
        var state = string.Join("|", key, proxy.ProxyHost, proxy.ProxyPort, proxy.ProxyUser, ProxyPasswordBox.Password);
        if (key == "" || state == _loadedKey) return;
        _loadedKey = state;
        try
        {
            using var http = App.CreateHttp(proxy); // the form's proxy, not the saved one
            using var req = new HttpRequestMessage(HttpMethod.Get, App.Api + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var ids = doc.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).Order();
            // Add instead of Clear(): clearing would wipe the text in the editable comboboxes.
            foreach (var id in ids) if (!Models.Contains(id)) Models.Add(id);
            Error.Text = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _loadedKey = "";
            Error.Text = "Could not load models: " + ex.Message;
        }
    }

    string? ProxyError()
    {
        var host = ProxyHostBox.Text.Trim();
        if (host == "") return null;
        if (!int.TryParse(ProxyPortBox.Text.Trim(), out var port) || port is < 1 or > 65535) return "Proxy port must be a number from 1 to 65535.";
        try { App.ProxyUri(host, port); return null; }
        catch (UriFormatException) { return "Proxy host is not valid."; }
    }

    void ApplyProxy(Settings s)
    {
        s.ProxyHost = ProxyHostBox.Text.Trim();
        s.ProxyPort = s.ProxyHost == "" ? 0 : int.Parse(ProxyPortBox.Text.Trim());
        s.ProxyUser = s.ProxyHost == "" ? "" : ProxyUserBox.Text.Trim();
        s.ProxyPassword = s.ProxyHost == "" ? "" : ProxyPasswordBox.Password;
    }

    void OnAdd(object sender, RoutedEventArgs e) =>
        Result.Bindings.Add(new HotkeyPrompt { Model = Result.Bindings.LastOrDefault()?.Model ?? "" });

    void OnRemove(object sender, RoutedEventArgs e) => Result.Bindings.Remove((HotkeyPrompt)((FrameworkElement)sender).DataContext);

    void OnHotkeyKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var box = (TextBox)sender;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Tab) { e.Handled = false; return; }
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            if (key is Key.Back or Key.Delete) box.Text = "";
            return; // global hotkeys need at least one modifier
        }
        try { box.Text = new KeyGesture(key, Keyboard.Modifiers).GetDisplayStringForCulture(CultureInfo.InvariantCulture); }
        catch (NotSupportedException) { }
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        var seen = new HashSet<(uint, uint)>();
        if (ToggleBox.Text != "" && Hotkey.TryParse(ToggleBox.Text, out var tm, out var tvk)) seen.Add((tm, tvk));
        foreach (var b in Result.Bindings)
        {
            if (!Hotkey.TryParse(b.Hotkey, out var m, out var vk)) { Error.Text = "Set a hotkey with a modifier (Ctrl/Alt/Shift/Win) for every row."; return; }
            if (!seen.Add((m, vk))) { Error.Text = $"{b.Hotkey} is used twice."; return; }
            if (string.IsNullOrWhiteSpace(b.Model)) { Error.Text = $"{b.Hotkey} needs a model."; return; }
            if (string.IsNullOrWhiteSpace(b.Prompt)) { Error.Text = $"{b.Hotkey} needs a prompt."; return; }
        }
        if (ProxyError() is { } proxyError) { Error.Text = proxyError; return; }
        ApplyProxy(Result);
        Result.ToggleHotkey = ToggleBox.Text;
        Result.ApiKey = KeyBox.Password.Trim();
        Startup.IsEnabled = StartupBox.IsChecked == true;
        Result.HideFromCapture = HideCaptureBox.IsChecked == true;
        Result.HistoryDays = RetentionDays[Math.Max(0, RetentionBox.SelectedIndex)];
        DialogResult = true;
    }
}
