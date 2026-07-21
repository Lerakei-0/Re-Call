using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ReCall.Native;
using Windows.Graphics;
using Windows.Foundation;

namespace ReCall.UI;

/// <summary>
/// Tray icon via NativeNotifyIcon (raw Shell_NotifyIcon) rather than
/// H.NotifyIcon's TaskbarIcon.
///
/// The right-click menu is a real WinUI3 MenuFlyout, rebuilt fresh on every
/// right-click (see BuildMenu) rather than kept around long-lived, since
/// that's the simplest way to have "Start with Windows"'s checkmark always
/// match isStartupEnabled() without a separate refresh step. A MenuFlyout
/// needs a XamlRoot and an owner HWND to show from, which a bare tray icon
/// doesn't have on its own -- TrayMenuHost is a tiny invisible window that
/// exists purely to supply both.
///
/// This replaces an earlier TrackPopupMenuEx-based menu (a real Win32 HMENU)
/// that was used here after a first MenuFlyout attempt had unreliable item
/// clicks -- that attempt attached the flyout to the panel window itself, so
/// clicks depended on the panel's own XAML focus/activation state. Ported
/// from WorldClockTray's TrayIconManager, which uses TrayMenuHost's
/// dedicated always-activated owner window instead to sidestep exactly that
/// problem; test click reliability carefully after this port.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NativeNotifyIcon _trayIcon;
    private readonly ClipboardPanelWindow _panel;
    private readonly TrayMenuHost _menuHost;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly Func<bool> _isStartupEnabled;
    private readonly Action _toggleStartup;

    public TrayIconManager(ClipboardPanelWindow panel, string iconPath, bool darkMode, Action openSettings, Action quit,
        Func<bool> isStartupEnabled, Action toggleStartup)
    {
        _panel = panel;
        _openSettings = openSettings;
        _quit = quit;
        _isStartupEnabled = isStartupEnabled;
        _toggleStartup = toggleStartup;
        _menuHost = new TrayMenuHost();

        _trayIcon = new NativeNotifyIcon();
        _trayIcon.LeftClick += () => _panel.Toggle(TrayAnchor());
        _trayIcon.RightClick += ShowMenu;
        _trayIcon.SetTooltip("Re:Call");
        _trayIcon.SetIcon(NativeNotifyIcon.LoadIconFromFile(iconPath));
        _menuHost.SetTheme(darkMode);
    }

    private void ShowMenu()
    {
        // Same click-point anchoring TrackPopupMenuEx used before -- a
        // context menu should open where you right-clicked, not at some
        // fixed icon-center point (that's what TrayAnchor()'s
        // Shell_NotifyIconGetRect fallback is for instead).
        NativeMethods.GetCursorPos(out var cursorPos);
        _menuHost.MoveTo(cursorPos.X, cursorPos.Y);
        _menuHost.TakeForeground();

        var menu = BuildMenu();
        menu.Opened += (_, _) => AllowMenuToOverlapTaskbar(cursorPos);
        menu.ShowAt(_menuHost.AnchorElement, new FlyoutShowOptions
        {
            Position = new Point(0, 0),
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }

    /// <summary>WinUI positions a Placement-based MenuFlyout within the
    /// target monitor's *work area* -- the same GetMonitorInfo rcWork
    /// Windows itself uses to keep popups off the taskbar -- regardless of
    /// where the menu was actually invoked from. That's normally correct
    /// for in-app flyouts, but it also means a tray menu opened from inside
    /// the taskbar strip gets pushed clear of it, and there's no
    /// FlyoutShowOptions switch to turn that off (ShouldConstrainToRootBounds
    /// governs the app's own XamlRoot bounds, not this).
    ///
    /// Once the menu has actually opened, this finds its real top-level
    /// popup window and moves it back over the click point with a raw
    /// SetWindowPos, which isn't subject to that clamp. WinAppSDK's
    /// "windowed" popups use the class name "Xaml_WindowedPopupClass" --
    /// undocumented, so if a future Windows App SDK version renames it this
    /// silently no-ops (FindOwnPopupWindow returns Zero) rather than
    /// throwing. Ported from WorldClockTray's TrayIconManager.</summary>
    private void AllowMenuToOverlapTaskbar(NativeMethods.POINT cursorPos)
    {
        var popup = FindOwnPopupWindow();
        if (popup == IntPtr.Zero) return;

        NativeMethods.GetWindowRect(popup, out var rect);
        int height = rect.bottom - rect.top;

        // Same bottom-left-aligned-on-the-cursor anchor as the
        // BottomEdgeAlignedLeft placement above, just without the
        // work-area clamp.
        NativeMethods.SetWindowPos(popup, IntPtr.Zero, cursorPos.X, cursorPos.Y - height, 0, 0,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);
    }

    private static IntPtr FindOwnPopupWindow()
    {
        uint pid = NativeMethods.GetCurrentProcessId();
        IntPtr found = IntPtr.Zero;

        // EnumWindows walks top-to-bottom in Z-order, so the first matching
        // window is the one on top -- i.e. the menu that was just opened.
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(hWnd, className, className.Capacity);
            if (className.ToString() != "Xaml_WindowedPopupClass") return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != pid) return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();

        // E890 (View/eye) and E713 (Setting/gear) are the standard Segoe
        // Fluent Icons glyphs Windows itself uses for these actions --
        // same convention as WorldClockTray's tray menu.
        var showHide = new MenuFlyoutItem
        {
            Text = "Show",
            Icon = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE890" },
        };
        showHide.Click += (_, _) => _panel.Toggle(TrayAnchor());
        menu.Items.Add(showHide);

        var settings = new MenuFlyoutItem
        {
            Text = "Settings...",
            Icon = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE713" },
        };
        settings.Click += (_, _) => _openSettings();
        menu.Items.Add(settings);

        menu.Items.Add(new MenuFlyoutSeparator());

        // A ToggleMenuFlyoutItem draws its checkmark in its own dedicated
        // column, sized independently of the Icon column MenuFlyoutItem
        // uses for Show/Settings above, which throws the two groups of
        // item labels out of alignment. Using a plain MenuFlyoutItem with
        // a checkmark FontIcon (E73E, same glyph Windows itself uses for
        // "checked") instead puts "Start with Windows" through that same
        // Icon column, so its label lines up with the others. Trade-off:
        // this reads to Narrator as a regular button rather than a toggle,
        // so it doesn't announce "checked"/"unchecked" the way
        // ToggleMenuFlyoutItem would.
        var startOnBoot = new MenuFlyoutItem
        {
            Text = "Start with Windows",
            Icon = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = "\uE73E",
                Visibility = _isStartupEnabled() ? Visibility.Visible : Visibility.Collapsed,
            },
        };
        startOnBoot.Click += (_, _) => _toggleStartup();
        menu.Items.Add(startOnBoot);

        menu.Items.Add(new MenuFlyoutSeparator());

        var quit = new MenuFlyoutItem { Text = "Quit" };
        quit.Click += (_, _) => _quit();
        menu.Items.Add(quit);

        return menu;
    }

    /// <summary>Where the panel opens for a real tray-icon click: the
    /// icon's actual on-screen center via Shell_NotifyIconGetRect. Falls
    /// back to the cursor position on the rare chance the shell can't
    /// report the icon's rect.</summary>
    private PointInt32 TrayAnchor()
    {
        if (_trayIcon.TryGetIconRect(out var rect))
            return new PointInt32((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2);
        NativeMethods.GetCursorPos(out var cur);
        return new PointInt32(cur.X, cur.Y);
    }

    /// <summary>Public alias of TrayAnchor(), for the global hotkey to
    /// anchor the panel against -- unlike a real click, the hotkey has no
    /// position of its own to go on.</summary>
    public PointInt32 TrayIconAnchor() => TrayAnchor();

    /// <summary>Swaps the tray icon's image (e.g. to follow a light/dark
    /// theme change) and keeps the menu's own theme in sync with it.
    /// SetIcon takes care of destroying the previous HICON.</summary>
    public void UpdateIcon(string iconPath, bool darkMode)
    {
        _trayIcon.SetIcon(NativeNotifyIcon.LoadIconFromFile(iconPath));
        _menuHost.SetTheme(darkMode);
    }

    public void Dispose()
    {
        _trayIcon.Dispose();
    }
}
