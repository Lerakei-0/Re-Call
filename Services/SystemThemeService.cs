using Microsoft.Win32;
using Windows.UI.ViewManagement;
using ReCall.Models;

namespace ReCall.Services;

/// <summary>Reads Windows 11's "Custom" personalization mode (Settings >
/// Personalization > Colors > "Choose your mode"), which lets the taskbar/
/// Start/Action Center ("Default Windows mode") and the title bar/content of
/// ordinary app windows ("Default app mode") be set to Light/Dark
/// independently of one another. WinUI3/WinRT has no typed API for either
/// flag -- UISettings.GetColorValue only exposes the derived accent-family
/// colors, not the mode itself -- so both are read the same two DWORDs
/// Explorer itself keys off internally.
///
/// Used only when Theme.Mode == ThemeMode.System: ClipboardPanelWindow (the
/// sliding panel, which opens beside the taskbar) resolves against
/// SystemMode, and SettingsWindow (an ordinary app window) resolves against
/// AppsMode -- matching how Windows itself would theme those two surfaces.
/// Ported from WorldClockTray's Services/SystemThemeService.cs.</summary>
public static class SystemThemeService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Settings > Personalization > Colors > "Default app mode".</summary>
    public static ThemeMode AppsMode => ReadIsLight("AppsUseLightTheme") ? ThemeMode.Light : ThemeMode.Dark;

    /// <summary>Settings > Personalization > Colors > "Default Windows
    /// mode" -- what actually drives the taskbar, Start, and Action
    /// Center.</summary>
    public static ThemeMode SystemMode => ReadIsLight("SystemUsesLightTheme") ? ThemeMode.Light : ThemeMode.Dark;

    /// <summary>Resolves a stored Theme.Mode to a concrete Dark/Light value
    /// for one specific surface. Mode values other than System pass
    /// straight through unchanged.</summary>
    public static ThemeMode Resolve(ThemeMode mode, bool taskbarSurface) =>
        mode == ThemeMode.System ? (taskbarSurface ? SystemMode : AppsMode) : mode;

    /// <summary>Raised on the same background thread UISettings itself uses
    /// whenever any system color value changes -- toggling either light/
    /// dark flag recomputes those derived values same as an accent-color
    /// change does, so this fires for both.</summary>
    public static event Action? Changed;

    private static readonly UISettings UiSettings = new();

    static SystemThemeService()
    {
        UiSettings.ColorValuesChanged += (_, _) => Changed?.Invoke();
    }

    private static bool ReadIsLight(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue(valueName) is int v) return v != 0;
        }
        catch
        {
            // Best-effort only -- if the key/value is ever missing or
            // unreadable, fall through to the default below rather than
            // throwing out of a theme-resolution path.
        }
        return true; // Windows' own historical default for an unset value
    }
}
