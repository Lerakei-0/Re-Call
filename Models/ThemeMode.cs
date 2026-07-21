namespace ReCall.Models;

/// <summary>Ported from WorldClockTray's Models/Theme.cs.</summary>
public enum ThemeMode
{
    Dark,
    Light,

    /// <summary>Follow Windows' own Light/Dark setting instead of a fixed
    /// choice. Windows 11's "Custom" personalization mode lets the taskbar
    /// ("Windows mode") and app windows ("app mode") be set independently,
    /// so resolving this to an actual Dark/Light value depends on which
    /// surface is asking -- see Services.SystemThemeService.Resolve. Never
    /// used directly as a lookup key; always resolve first.</summary>
    System,
}
