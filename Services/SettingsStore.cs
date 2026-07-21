using Microsoft.Win32;
using ReCall.Models;
using Windows.System;

namespace ReCall.Services;

/// <summary>Persists app settings to HKCU\Software\LocalUtilities\ReCall,
/// same pattern as WorldClockTray's SettingsStore. Currently just the theme
/// mode -- add more Load/Save pairs here as the settings surface grows.</summary>
public sealed class SettingsStore
{
    private const string KeyPath = @"Software\LocalUtilities\ReCall";

    private static RegistryKey OpenKey(bool writable) =>
        writable
            ? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            : Registry.CurrentUser.OpenSubKey(KeyPath, writable: false) ?? Registry.CurrentUser.CreateSubKey(KeyPath);

    public ThemeMode LoadThemeMode()
    {
        using var key = OpenKey(false);
        return (string?)key.GetValue("theme_mode", "system") switch
        {
            var m when string.Equals(m, "light", StringComparison.OrdinalIgnoreCase) => ThemeMode.Light,
            var m when string.Equals(m, "dark", StringComparison.OrdinalIgnoreCase) => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    public void SaveThemeMode(ThemeMode mode)
    {
        using var key = OpenKey(true);
        key.SetValue("theme_mode", mode switch
        {
            ThemeMode.Light => "light",
            ThemeMode.Dark => "dark",
            _ => "system",
        });
    }

    public CornerStyle LoadCornerStyle()
    {
        using var key = OpenKey(false);
        return Enum.TryParse<CornerStyle>((string?)key.GetValue("theme_corner_style", "Round"), out var cs)
            ? cs : CornerStyle.Round;
    }

    public void SaveCornerStyle(CornerStyle style)
    {
        using var key = OpenKey(true);
        key.SetValue("theme_corner_style", style.ToString());
    }

    // --- global hotkey ---------------------------------------------------
    // Ported from WorldClockTray's SettingsStore.

    public HotkeySettings LoadHotkey()
    {
        using var key = OpenKey(false);
        return new HotkeySettings
        {
            Enabled = Convert.ToInt32(key.GetValue("hotkey_enabled", 0)) != 0,
            Ctrl = Convert.ToInt32(key.GetValue("hotkey_ctrl", 1)) != 0,
            Shift = Convert.ToInt32(key.GetValue("hotkey_shift", 1)) != 0,
            Alt = Convert.ToInt32(key.GetValue("hotkey_alt", 0)) != 0,
            Win = Convert.ToInt32(key.GetValue("hotkey_win", 0)) != 0,
            Key = Enum.TryParse<VirtualKey>((string?)key.GetValue("hotkey_key", "V"), out var vk) ? vk : VirtualKey.V,
        };
    }

    public void SaveHotkey(HotkeySettings hotkey)
    {
        using var key = OpenKey(true);
        key.SetValue("hotkey_enabled", hotkey.Enabled ? 1 : 0);
        key.SetValue("hotkey_ctrl", hotkey.Ctrl ? 1 : 0);
        key.SetValue("hotkey_shift", hotkey.Shift ? 1 : 0);
        key.SetValue("hotkey_alt", hotkey.Alt ? 1 : 0);
        key.SetValue("hotkey_win", hotkey.Win ? 1 : 0);
        key.SetValue("hotkey_key", hotkey.Key.ToString());
    }

    // --- about / updates ---------------------------------------------------

    public bool LoadCheckUpdatesOnStartup()
    {
        using var key = OpenKey(false);
        return Convert.ToInt32(key.GetValue("check_updates_on_startup", 1)) != 0;
    }

    public void SaveCheckUpdatesOnStartup(bool enabled)
    {
        using var key = OpenKey(true);
        key.SetValue("check_updates_on_startup", enabled ? 1 : 0);
    }

    // --- run at Windows startup ------------------------------------------------
    // Unlike the app's own settings above (stored under our private
    // LocalUtilities\ReCall key), this writes to the well-known
    // per-user "Run" key that Explorer reads at logon -- the standard
    // mechanism for an unpackaged (non-MSIX) Win32 app to launch at startup.

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ReCall";

    public bool IsStartOnStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var existing = key?.GetValue(RunValueName) as string;
        return existing is not null && existing.Trim('"') == ExePath();
    }

    public void SetStartOnStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
            key.SetValue(RunValueName, $"\"{ExePath()}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    // Quoted in the Run value above since the install path may contain
    // spaces (e.g. "C:\Users\me\Clipboard tray\...").
    private static string ExePath() =>
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
}
