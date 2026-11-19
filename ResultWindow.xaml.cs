using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using System.Windows.Threading;

namespace Asker;

public class ResultItem : INotifyPropertyChanged
{
    public long Id { get; set; } // HistoryDb row id; 0 = not saved
    public string Source { get; init; } = "";
    public byte[]? ImagePng { get; init; } // set on new units; loaded units fetch theirs on demand (HasImage)
    public bool HasImage { get; init; }
    public string Prompt { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
    public string Title => Source != "" ? Source : HasImage || ImagePng != null ? "Image" : "";

    public bool Matches(string query) =>
        Source.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Prompt.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Result.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    BitmapImage? _image;
    public BitmapImage? Image
    {
        get
        {
            if (_image != null) return _image;
            byte[]? png = ImagePng;
            if (png == null && HasImage)
                try { png = HistoryDb.LoadImage(Id); }
                catch (Exception e) when (e is SqliteException or IOException or System.Security.Cryptography.CryptographicException) { }
            if (png == null) return null;
            _image = new BitmapImage();
            _image.BeginInit();
            _image.CacheOption = BitmapCacheOption.OnLoad;
            _image.StreamSource = new MemoryStream(png);
            _image.EndInit();
            _image.Freeze();
            return _image;
        }
    }

    string _result = "";
    public string Result { get => _result; set { _result = value; Changed(nameof(Result)); } }
    bool _pinned;
    public bool Pinned { get => _pinned; set { _pinned = value; Changed(nameof(Pinned)); } }
    bool _expanded;
    public bool Expanded { get => _expanded; set { _expanded = value; Changed(nameof(Expanded)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

public partial class ResultWindow : Window
{
    // shortcut: the list shows at most 100 units (newest unpinned + pinned, or newest search matches); search reaches all of _all.
    // Add load-more-on-scroll if people need to browse further back without searching.
    const int MaxShown = 100;
    const int MaxImageSide = 2048; // downscale big screenshots before sending
    CancellationTokenSource? _cts;
    readonly List<ResultItem> _all = new();                          // every unit, oldest first (decrypted, without images)
    readonly ObservableCollection<ResultItem> _history = new();      // what the list shows
    readonly ObservableCollection<ResultItem> _pinned = new();
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ResultWindow()
    {
        InitializeComponent();
        History.ItemsSource = _history;
        PinnedList.ItemsSource = _pinned;
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Refresh(); };
        try
        {
            HistoryDb.Init();
            HistoryDb.Cleanup(App.Settings.HistoryDays);
            _all.AddRange(HistoryDb.Load());
        }
        catch (Exception e) when (e is SqliteException or IOException or System.Security.Cryptography.CryptographicException)
        {
            _all.Add(new ResultItem { Result = "Could not load history: " + e.Message });
        }
        foreach (var item in _all.Where(i => i.Pinned)) _pinned.Add(item);
        UpdateSidebar();
        Refresh();
    }

    // Rebuild the visible list from _all and the search box.
    void Refresh()
    {
        var query = Search.Text.Trim();
        IEnumerable<ResultItem> shown;
        if (query == "")
        {
            var recent = _all.Where(i => !i.Pinned).TakeLast(MaxShown).ToHashSet();
            shown = _all.Where(i => i.Pinned || recent.Contains(i));
        }
        else shown = _all.Where(i => i.Matches(query)).TakeLast(MaxShown);
        _history.Clear();
        foreach (var item in shown) _history.Add(item);
        EmptyTitle.Text = query == "" ? "No results yet" : "No matches";
        EmptyHint.Text = query == "" ? "Copy some text and press one of your hotkeys." : $"Nothing in your history contains “{query}”.";
        FocusLast();
    }

    void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { _searchDelay.Stop(); _searchDelay.Start(); }

    void ClearSearch()
    {
        if (Search.Text == "") return;
        Search.Text = "";
        _searchDelay.Stop();
        Refresh();
    }

    // Called after Settings are saved: apply the new retention to the database and the list.
    public void ApplyRetention()
    {
        int days = App.Settings.HistoryDays;
        if (days <= 0) return;
        try { HistoryDb.Cleanup(days); }
        catch (Exception e) when (e is SqliteException or IOException) { return; } // keep showing what is still on disk
        var cutoff = DateTime.Now.AddDays(-days);
        _all.RemoveAll(i => !i.Pinned && i.Id != 0 && i.Time < cutoff);
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND); // rounded corners on Win11
        App.ApplyCaptureSetting(this);
    }

    void UpdateSidebar() => Sidebar.Visibility = _pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Saving history must never break asking, so a failure is shown on the unit instead of thrown.
    static void Save(ResultItem item, Action<ResultItem> write)
    {
        try { write(item); }
        catch (Exception e) when (e is SqliteException or IOException) { item.Result += "\n\n(History not saved: " + e.Message + ")"; }
    }

    // Clipboard image as PNG, scaled so its longest side is at most MaxImageSide. Null if there is none.
    static byte[]? ClipboardPng()
    {
        BitmapSource? img;
        try { img = Clipboard.ContainsImage() ? Clipboard.GetImage() : null; }
        catch (COMException) { return null; }
        if (img == null) return null;
        double scale = Math.Min(1, (double)MaxImageSide / Math.Max(img.PixelWidth, img.PixelHeight));
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else { Show(); Activate(); FocusLast(); }
    }

    void FocusLast() { if (_history.Count > 0) ScrollTo(_history[^1]); }

    // Scroll so the top of the unit sits at the top of the view (after layout has run).
    void ScrollTo(ResultItem item) => Dispatcher.InvokeAsync(() =>
    {
        if (History.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement unit)
            Scroll.ScrollToVerticalOffset(unit.TranslatePoint(new Point(), History).Y);
    }, DispatcherPriority.Loaded);

    public async void Ask(HotkeyPrompt binding)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        string clip;
        try { clip = Clipboard.GetText(); }
        catch (COMException) { clip = ""; } // clipboard locked by another app

        // Text wins when both are present (Office etc. put a picture of copied text on the clipboard too).
        var png = string.IsNullOrWhiteSpace(clip) ? ClipboardPng() : null;
        var item = new ResultItem { Source = png == null ? clip : "", ImagePng = png, Prompt = binding.Prompt };
        ClearSearch(); // the new unit must be visible
        _all.Add(item);
        _history.Add(item);
        if (_history.Count(i => !i.Pinned) > MaxShown) _history.Remove(_history.First(i => !i.Pinned));
        Show();
        Activate();
        FocusLast();

        if (string.IsNullOrWhiteSpace(clip) && png == null) { item.Result = "Clipboard has no text or image."; return; }
        if (App.Settings.ApiKey == "") { item.Result = "Set your Nous Portal API key in Settings."; return; }
        if (binding.Model == "") { item.Result = $"Choose a model for {binding.Hotkey} in Settings."; return; }
        Save(item, i => HistoryDb.Insert(i, binding.Model));

        object content = png == null
            ? $"{binding.Prompt}\n\n<clipboard>\n{clip}\n</clipboard>"
            : new object[]
            {
                new { type = "text", text = binding.Prompt },
                new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(png) } },
            };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, App.Api + "/chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model = binding.Model,
                    stream = true,
                    messages = new[] { new { role = "user", content } },
                }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", App.Settings.ApiKey);
            using var res = await App.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                item.Result = $"Error {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync(cts.Token)}";
                return;
            }

            // OpenAI-style server-sent events: "data: {json}" lines, ending with "data: [DONE]".
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
            while (await reader.ReadLineAsync(cts.Token) is string line)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err)) { item.Result += "\n\nError: " + err; break; }
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
                    item.Result += text.GetString();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            item.Result += (item.Result == "" ? "" : "\n\n") + "Error: " + ex.Message;
        }
        if (item.Result == "") item.Result = cts.IsCancellationRequested ? "(cancelled)" : "(no response)";
        if (item.Id != 0) Save(item, HistoryDb.Update);
    }

    void OnDrag(object sender, MouseButtonEventArgs e) => DragMove();

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Search.Text != "") ClearSearch();
        else if (e.Key == Key.Escape) OnClose(this, e);
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { Search.Focus(); Search.SelectAll(); }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _history.LastOrDefault()?.Result is { Length: > 0 } r) SetClipboard(r);
    }

    void OnPin(object sender, RoutedEventArgs e)
    {
        var item = (ResultItem)((FrameworkElement)sender).DataContext;
        item.Pinned = !item.Pinned;
        if (item.Pinned) _pinned.Add(item); else _pinned.Remove(item);
        UpdateSidebar();
        if (item.Id != 0) Save(item, HistoryDb.Update);
    }

    void OnPinnedClick(object sender, RoutedEventArgs e)
    {
        var item = (ResultItem)((FrameworkElement)sender).DataContext;
        if (!_history.Contains(item)) ClearSearch(); // the search is hiding it
        ScrollTo(item);
    }

    void OnExpand(object sender, RoutedEventArgs e)
    {
        var item = (ResultItem)((FrameworkElement)sender).DataContext;
        item.Expanded = !item.Expanded;
    }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        var item = (ResultItem)button.DataContext;
        bool copied = (string)button.Tag != "Question" ? SetClipboard(item.Result)
            : item.Image != null ? SetClipboardImage(item.Image)
            : SetClipboard($"{item.Source}\n\n{item.Prompt}");
        if (!copied) return;
        // Brief checkmark as feedback, then back to the copy icon.
        button.Content = "";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) => { timer.Stop(); button.Content = ""; };
        timer.Start();
    }

    static bool SetClipboardImage(BitmapSource image)
    {
        try { Clipboard.SetImage(image); return true; }
        catch (COMException) { return false; } // clipboard locked by another app
    }

    static bool SetClipboard(string text)
    {
        if (text == "") return false;
        try { Clipboard.SetText(text); return true; }
        catch (COMException) { return false; } // clipboard locked by another app
    }

    void OnClose(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Hide();
    }
}
