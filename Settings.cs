using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Asker;

public class HotkeyPrompt
{
    public string Hotkey { get; set; } = "";
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
}

public class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Asker", "settings.json");

    // API key encrypted with Windows DPAPI (only this Windows user can decrypt it).
    public string ApiKeyProtected { get; set; } = "";

    public string ToggleHotkey { get; set; } = "Ctrl+Alt+H";

    // Hide Asker windows from screen sharing, screenshots and recording (Win10 2004+).
    public bool HideFromCapture { get; set; }

    // Delete unpinned history older than this many days; 0 = keep forever.
    public int HistoryDays { get; set; }

    public ObservableCollection<HotkeyPrompt> Bindings { get; set; } = new()
    {
        new() { Hotkey = "Ctrl+Alt+Q", Prompt = "Explain this clearly and concisely." },
        new() { Hotkey = "Ctrl+Alt+T", Prompt = "Translate this to English. Reply with the translation only." },
    };

    // Result window: pinned sidebar collapsed to a narrow strip.
    public bool SidebarCollapsed { get; set; }

    // Proxy for API calls; empty host = use the Windows system proxy. Host may include a scheme (http://, socks5://).
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string ProxyUser { get; set; } = "";
    public string ProxyPasswordProtected { get; set; } = "";

    [JsonIgnore]
    public string ApiKey { get => Unprotect(ApiKeyProtected); set => ApiKeyProtected = Protect(value); }

    [JsonIgnore]
    public string ProxyPassword { get => Unprotect(ProxyPasswordProtected); set => ProxyPasswordProtected = Protect(value); }

    static string Unprotect(string protectedText)
    {
        if (protectedText == "") return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return ""; }
    }

    static string Protect(string value) => value == "" ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
}

// The Run registry value is the single source of truth, so nothing extra is stored in settings.json.
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Asker") is string;
        }
        set
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Asker", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Asker", throwOnMissingValue: false);
        }
    }
}

public static class Hotkey
{
    // "Ctrl+Alt+Q" -> RegisterHotKey args. WPF ModifierKeys values equal the Win32 MOD_* flags.
    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = vk = 0;
        try
        {
            if (new KeyGestureConverter().ConvertFromInvariantString(text) is not KeyGesture g || g.Modifiers == ModifierKeys.None) return false;
            mods = (uint)g.Modifiers;
            vk = (uint)KeyInterop.VirtualKeyFromKey(g.Key);
            return true;
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException) { return false; }
    }
}
