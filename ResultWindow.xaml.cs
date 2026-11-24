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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    public string Model { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
    public string Title => Source != "" ? Source : HasImage || ImagePng != null ? "Image" : "";

    // One-line trimmed TextBlocks still lay out the whole string (measured ~110 ms per pinned card with full text vs ~10 ms
    // cut to 200 chars), so collapsed/sidebar displays bind these instead of the full text.
    const int PreviewChars = 200;
    static string Cut(string s) => s.Length <= PreviewChars ? s : s[..PreviewChars];
    public string TitlePreview => Cut(Title);
    public string ResultPreview => Cut(Result);

    // Custom title set by renaming a pinned item; "" = use the question.
    string _name = "";
    public string Name { get => _name; set { _name = value; Changed(nameof(Name)); Changed(nameof(DisplayName)); } }
    public string DisplayName => Name != "" ? Name : TitlePreview;
    bool _renaming;
    public bool Renaming { get => _renaming; set { _renaming = value; Changed(nameof(Renaming)); } }

    // The request streaming into this unit, if any (each unit runs on its own, so several can stream at once).
    public CancellationTokenSource? Cts { get; set; }

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Source.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Prompt.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Result.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    // Display copies sized to what is shown (px = 2x DIP to cover 200% scaling): a 128 px thumbnail while collapsed (64 DIP),
    // up to 800 px while expanded (400 DIP), freed on collapse. A full 2048x1152 decode is 9 MB per image
    // (measured +181 MB for 20 units); copying uses Decode() at full size.
    const int ThumbHeight = 128, ExpandedMaxHeight = 800;
    BitmapImage? _thumb, _large;
    public BitmapImage? Image => Expanded ? _large ??= Decode(ExpandedMaxHeight) : _thumb ??= Decode(ThumbHeight);

    public BitmapImage? Decode(int maxHeight = 0)
    {
        byte[]? png = ImagePng;
        if (png == null && HasImage)
            try { png = HistoryDb.LoadImage(Id); }
            catch (Exception e) when (e is SqliteException or IOException or System.Security.Cryptography.CryptographicException) { }
        if (png == null) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        if (maxHeight > 0 && BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelHeight > maxHeight)
            img.DecodePixelHeight = maxHeight; // never upscale small images
        img.EndInit();
        img.Freeze();
        return img;
    }

    string _result = "";
    public string Result { get => _result; set { _result = value; Changed(nameof(Result)); Changed(nameof(ResultPreview)); } }

    // Streaming: every Result change re-lays out the whole answer (measured ~28 ms per token on a 6k-char answer),
    // so tokens are buffered and published at most every 50 ms. Call Flush() before writing Result directly.
    StringBuilder? _pending;
    DispatcherTimer? _flushTimer;

    public void Append(string text)
    {
        (_pending ??= new StringBuilder(_result)).Append(text);
        _flushTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher.CurrentDispatcher);
        if (!_flushTimer.IsEnabled) _flushTimer.Start();
    }

    // Rerun: drop the old answer, including text still waiting in the buffer.
    public void Reset()
    {
        _flushTimer?.Stop();
        _pending = null;
        Result = "";
    }

    public void Flush()
    {
        _flushTimer?.Stop();
        if (_pending == null) return;
        var text = _pending.ToString();
        _pending = null;
        Result = text;
    }
    bool _pinned;
    public bool Pinned { get => _pinned; set { _pinned = value; Changed(nameof(Pinned)); } }
    bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            if (!value) _large = null; // free the big display copy
            Changed(nameof(Expanded));
            if (HasImage || ImagePng != null) Changed(nameof(Image));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

public partial class ResultWindow : Window
{
    // shortcut: the list shows at most 100 units (newest unpinned + pinned, or newest search matches); search reaches all of _all.
    // Add load-more-on-scroll if people need to browse further back without searching.
    const int MaxShown = 100;
    const int MaxImageSide = 2048; // downscale big screenshots before sending
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
        SetSidebarCollapsed(App.Settings.SidebarCollapsed);
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

    // Clipboard image (UI thread only), frozen so it can be encoded on another thread. Null if there is none.
    static BitmapSource? ClipboardImage()
    {
        BitmapSource? img;
        try { img = Clipboard.ContainsImage() ? Clipboard.GetImage() : null; }
        catch (COMException) { return null; }
        if (img is { CanFreeze: true }) img.Freeze();
        return img;
    }

    // PNG scaled so its longest side is at most MaxImageSide. Measured ~250 ms for a 4K screenshot, so Ask runs it off the UI thread.
    static byte[] EncodePng(BitmapSource img)
    {
        // Screenshot tools put 32-bit bitmaps on the clipboard with the 4th byte 0; WPF reads that as fully transparent,
        // which made the thumbnail invisible and sent the model a blank image. Such images are opaque, so drop the alpha.
        if ((img.Format == PixelFormats.Bgra32 || img.Format == PixelFormats.Pbgra32) && AllTransparent(img))
            img = new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0);
        double scale = Math.Min(1, (double)MaxImageSide / Math.Max(img.PixelWidth, img.PixelHeight));
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static bool AllTransparent(BitmapSource img)
    {
        var px = new byte[img.PixelWidth * img.PixelHeight * 4];
        img.CopyPixels(px, img.PixelWidth * 4, 0);
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) return false;
        return true;
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else { Show(); Activate(); FocusLast(); }
    }

    void FocusLast() { if (_history.Count > 0) ScrollTo(_history[^1]); }

    // Scroll so the top of the unit sits at the top of the view (after layout has run). The list is virtualized,
    // so the unit may not exist yet: bring it into view first, then align it once it has been built.
    void ScrollTo(ResultItem item) => Dispatcher.InvokeAsync(() =>
    {
        int index = _history.IndexOf(item);
        if (index < 0 || History.Template?.FindName("PART_Scroll", History) is not System.Windows.Controls.ScrollViewer scroll) return;
        _panel ??= FindChild<System.Windows.Controls.VirtualizingStackPanel>(History);
        if (_panel == null) return; // not shown yet; Toggle/Ask call this again after Show
        _panel.BringIndexIntoViewPublic(index);
        Dispatcher.InvokeAsync(() =>
        {
            if (History.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement unit)
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + unit.TranslatePoint(new Point(), scroll).Y);
        }, DispatcherPriority.Loaded);
    }, DispatcherPriority.Loaded);

    System.Windows.Controls.VirtualizingStackPanel? _panel;

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found || (found = FindChild<T>(child)!) != null) return found;
        }
        return null;
    }

    public async void Ask(HotkeyPrompt binding)
    {
        string clip;
        try { clip = Clipboard.GetText(); }
        catch (COMException) { clip = ""; } // clipboard locked by another app

        // Text wins when both are present (Office etc. put a picture of copied text on the clipboard too).
        var shot = string.IsNullOrWhiteSpace(clip) ? ClipboardImage() : null;
        byte[]? png = null;
        if (shot != null)
        {
            Show(); // appear right away instead of after the encode
            Activate();
            png = shot.IsFrozen ? await Task.Run(() => EncodePng(shot)) : EncodePng(shot);
        }
        var item = new ResultItem { Source = png == null ? clip : "", ImagePng = png, Prompt = binding.Prompt, Model = binding.Model };
        ClearSearch(); // the new unit must be visible
        _all.Add(item);
        _history.Add(item);
        if (_history.Count(i => !i.Pinned) > MaxShown) _history.Remove(_history.First(i => !i.Pinned));
        Show();
        Activate();
        FocusLast();

        if (string.IsNullOrWhiteSpace(clip) && png == null) { item.Result = "Clipboard has no text or image."; return; }
        await Run(item, png);
    }

    // Streams an answer into the unit. Other units keep streaming; only this unit's previous request is replaced.
    async Task Run(ResultItem item, byte[]? png)
    {
        if (App.Settings.ApiKey == "") { item.Result = "Set your Nous Portal API key in Settings."; return; }
        if (item.Model == "") { item.Result = "Choose a model for this hotkey in Settings."; return; }
        if (item.Id == 0) Save(item, i => HistoryDb.Insert(i, i.Model));

        item.Cts?.Cancel();
        var cts = item.Cts = new CancellationTokenSource();

        object content = png == null
            ? $"{item.Prompt}\n\n<clipboard>\n{item.Source}\n</clipboard>"
            : new object[]
            {
                new { type = "text", text = item.Prompt },
                new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(png) } },
            };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, App.Api + "/chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model = item.Model,
                    stream = true,
                    messages = new[] { new { role = "user", content } },
                }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", App.Settings.ApiKey);
            using var res = await App.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode)
                item.Result = $"Error {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync(cts.Token)}";
            else
            {
                // OpenAI-style server-sent events: "data: {json}" lines, ending with "data: [DONE]".
                using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
                while (await reader.ReadLineAsync(cts.Token) is string line && !cts.IsCancellationRequested)
                {
                    if (!line.StartsWith("data:")) continue;
                    var data = line[5..].Trim();
                    if (data == "[DONE]") break;
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err)) { item.Flush(); item.Result += "\n\nError: " + err; break; }
                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                        && choices[0].TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
                        item.Append(text.GetString()!);
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            item.Flush();
            item.Result += (item.Result == "" ? "" : "\n\n") + "Error: " + ex.Message;
        }
        if (item.Cts != cts) return; // rerun or deleted meanwhile: the newer state owns the unit
        item.Cts = null;
        item.Flush();
        if (item.Result == "") item.Result = cts.IsCancellationRequested ? "(cancelled)" : "(no response)";
        if (item.Id != 0) Save(item, HistoryDb.Update);
    }

    async void OnRerun(object sender, RoutedEventArgs e)
    {
        var item = (ResultItem)((FrameworkElement)sender).DataContext;
        byte[]? png = item.ImagePng;
        if (png == null && item.HasImage)
            try { png = HistoryDb.LoadImage(item.Id); }
            catch (Exception ex) when (ex is SqliteException or IOException or System.Security.Cryptography.CryptographicException) { }
        if (item.Source == "" && png == null) return; // nothing was asked (e.g. "Clipboard has no text or image.")
        item.Cts?.Cancel();
        item.Cts = null;
        item.Reset();
        await Run(item, png);
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        var item = (ResultItem)((FrameworkElement)sender).DataContext;
        item.Cts?.Cancel();
        item.Cts = null;
        _all.Remove(item);
        _history.Remove(item);
        if (_pinned.Remove(item)) UpdateSidebar();
        if (item.Id != 0)
            try { HistoryDb.Delete(item.Id); }
            catch (Exception ex) when (ex is SqliteException or IOException) { } // gone from the list; the row comes back next launch
    }

    // Pinned list: double-click a card to rename it; Enter or clicking away saves, Esc cancels, empty restores the question.
    void OnRenameStart(object sender, MouseButtonEventArgs e)
    {
        var button = (FrameworkElement)sender;
        var item = (ResultItem)button.DataContext;
        var box = ((System.Windows.Controls.Panel)button.Parent).Children.OfType<System.Windows.Controls.TextBox>().First();
        box.Text = item.Name != "" ? item.Name : item.TitlePreview;
        item.Renaming = true;
        Dispatcher.InvokeAsync(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Loaded);
        e.Handled = true;
    }

    void OnRenameKey(object sender, KeyEventArgs e)
    {
        var box = (System.Windows.Controls.TextBox)sender;
        if (e.Key == Key.Enter) { CommitRename(box); e.Handled = true; }
        else if (e.Key == Key.Escape) { ((ResultItem)box.DataContext).Renaming = false; e.Handled = true; }
    }

    void OnRenameLostFocus(object sender, RoutedEventArgs e) => CommitRename((System.Windows.Controls.TextBox)sender);

    void CommitRename(System.Windows.Controls.TextBox box)
    {
        var item = (ResultItem)box.DataContext;
        if (!item.Renaming) return; // Esc already cancelled, or Enter already saved
        item.Renaming = false;
        var name = box.Text.Trim();
        item.Name = name == item.TitlePreview ? "" : name;
        if (item.Id != 0) Save(item, HistoryDb.Update);
    }

    void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        SetSidebarCollapsed(!App.Settings.SidebarCollapsed);
        try { App.Settings.Save(); } catch (IOException) { } // only a remembered layout preference
    }

    void SetSidebarCollapsed(bool collapsed)
    {
        App.Settings.SidebarCollapsed = collapsed;
        Sidebar.Width = collapsed ? 48 : 200;
        PinnedScroll.Visibility = SidebarTitle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarToggle.Content = collapsed ? "" : ""; // chevron right / left
        SidebarToggle.ToolTip = collapsed ? "Show pinned" : "Hide pinned";
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggle, (string)SidebarToggle.ToolTip);
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

    void OnImageClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // don't start a window drag from the image
        if (e.ClickCount != 2) return;
        var item = (ResultItem)((FrameworkElement)sender).DataContext;
        if (item.Decode() is { } full) new ImageViewer(full, this).Show(); // full resolution, not the thumbnail
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
            : item.Decode() is { } full ? SetClipboardImage(full) // full resolution, not the display copy
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
        foreach (var item in _all) item.Cts?.Cancel();
        Hide();
    }
}
