using Windows.System;

namespace ReCall.Models;

/// <summary>User-configurable global shortcut that toggles the panel
/// (opens it if hidden, closes it if open -- same Toggle() the tray icon's
/// left-click already uses) from anywhere in Windows, regardless of which
/// app has focus. Persisted/applied via Services.SettingsStore and
/// Native.GlobalHotkeyManager respectively. Ported from WorldClockTray's
/// HotkeySettings.</summary>
public sealed class HotkeySettings
{
    public bool Enabled { get; set; }
    public bool Ctrl { get; set; } = true;
    public bool Shift { get; set; } = true;
    public bool Alt { get; set; }
    public bool Win { get; set; }
    public VirtualKey Key { get; set; } = VirtualKey.V;

    /// <summary>VirtualKey.None is the sentinel for "no key captured yet" --
    /// distinct from Enabled so a half-configured hotkey (enabled but never
    /// actually recorded) never gets registered with a garbage vk code.</summary>
    public bool HasKey => Key != VirtualKey.None;

    public HotkeySettings Clone() => (HotkeySettings)MemberwiseClone();

    public string DisplayText()
    {
        if (!HasKey) return "Not set";

        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(VirtualKey key) => key switch
    {
        VirtualKey.Number0 => "0",
        VirtualKey.Number1 => "1",
        VirtualKey.Number2 => "2",
        VirtualKey.Number3 => "3",
        VirtualKey.Number4 => "4",
        VirtualKey.Number5 => "5",
        VirtualKey.Number6 => "6",
        VirtualKey.Number7 => "7",
        VirtualKey.Number8 => "8",
        VirtualKey.Number9 => "9",
        VirtualKey.Space => "Space",
        VirtualKey.Escape => "Esc",
        VirtualKey.Enter => "Enter",
        VirtualKey.Tab => "Tab",
        _ => key.ToString(),
    };
}
