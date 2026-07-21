namespace ReCall.Models;

/// <summary>
/// Corner style for the panel/settings window. WinUI3's DWM corner rounding
/// (DWMWA_WINDOW_CORNER_PREFERENCE, applied in Native/WindowEffects.cs) only
/// offers a few fixed styles, so this is a choice of style rather than a
/// pixel radius. Ported from WorldClockTray's Models/Theme.cs.
/// </summary>
public enum CornerStyle
{
    NotRounded = 1,  // DWMWCP_DONOTROUND
    Round = 2,       // DWMWCP_ROUND
    RoundSmall = 3,  // DWMWCP_ROUNDSMALL
}
