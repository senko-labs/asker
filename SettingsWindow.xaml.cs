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
        if (key == "" || key == _loadedKey) return;
        _loadedKey = key;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, App.Api + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await App.Http.SendAsync(req);
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
        Result.ToggleHotkey = ToggleBox.Text;
        Result.ApiKey = KeyBox.Password.Trim();
        Startup.IsEnabled = StartupBox.IsChecked == true;
        Result.HideFromCapture = HideCaptureBox.IsChecked == true;
        Result.HistoryDays = RetentionDays[Math.Max(0, RetentionBox.SelectedIndex)];
        DialogResult = true;
    }
}
