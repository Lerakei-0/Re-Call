using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using ReCall.Native;
using Windows.Graphics;
using WinRT.Interop;

namespace ReCall.UI;

/// <summary>
/// A WinUI3 MenuFlyout is what actually gets Windows 11's Fluent menu look
/// (rounded corners, Mica/acrylic, the real open/close animation) -- unlike
/// TrackPopupMenuEx's classic HMENU popup (the previous approach here),
/// which renders the same flat Windows-10-era menu no matter which OS it's
/// shown on. But a MenuFlyout still needs two things a bare tray icon
/// doesn't have: a XamlRoot to attach to, and an owner HWND for the
/// flyout's underlying windowed Popup (WinAppSDK popups are "windowed" by
/// default on desktop -- their own top-level HWND, so they aren't clipped
/// to the owner's bounds; this is exactly what lets a 1x1 owner host a
/// normal-sized menu anywhere on screen).
///
/// This window exists purely to be that owner: 1x1, no border/titlebar, off
/// the taskbar and Alt+Tab, and moved to wherever the menu should appear
/// right before each ShowAt call (see MoveTo). It's genuinely invisible via
/// WS_EX_LAYERED + zero alpha rather than AppWindow.Hide()/Visibility --
/// a window that's actually been hidden can't host a live popup, so this
/// stays "shown" (Activate()'d once, never hidden again) and is made
/// invisible at the pixel level instead.
///
/// This is a dedicated always-activated owner, separate from the panel
/// window -- unlike the earlier MenuFlyout attempt here (see
/// TrayIconManager's history), whose menu was attached to the panel
/// itself and depended on the panel's own XAML focus/activation state,
/// which is why its item clicks didn't fire reliably. Ported from
/// WorldClockTray's TrayMenuHost.
/// </summary>
internal sealed class TrayMenuHost
{
    /// <summary>The flyout's placement target. A real, in-tree
    /// FrameworkElement is required (a bare Window has no XamlRoot of its
    /// own until its Content does); this is the whole content of the host
    /// window, so its top-left corner is exactly the window's client-area
    /// origin -- which MoveTo pins to the desired screen point. Its
    /// RequestedTheme (see SetTheme) is also what the flyout's own
    /// MenuFlyoutPresenter inherits, since the flyout's popup content is
    /// attached under this same XamlRoot.</summary>
    public FrameworkElement AnchorElement { get; }

    public IntPtr Hwnd => _hwnd;

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;

    public TrayMenuHost()
    {
        _window = new Window();
        AnchorElement = new Grid();
        _window.Content = AnchorElement;

        _hwnd = WindowNative.GetWindowHandle(_window);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _appWindow.Resize(new SizeInt32(1, 1));
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            // Deliberately NOT IsAlwaysOnTop: the flyout's own windowed
            // popup already floats above everything else while it's open
            // on its own, and keeping this owner permanently topmost
            // fights the normal activation handoff (this window loses
            // foreground, the popup notices, closes itself) that
            // light-dismiss depends on -- with it set, clicking elsewhere
            // left the menu stuck open.
        }

        var ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 0, NativeMethods.LWA_ALPHA);

        // Never call Hide() on this window afterward -- see class remarks.
        _window.Activate();

        PrimeMouseInputMode();
    }

    /// <summary>WinUI sizes MenuFlyoutItem rows (32px for mouse, 40px for
    /// touch) off the last real input event Windows has seen anywhere in
    /// the session, and defaults to the larger touch sizing until one has
    /// happened at least once -- deliberately, per the WinUI team, with no
    /// API to override it (see microsoft/microsoft-ui-xaml#7374). For a
    /// tray app, the user's first right-click can easily be the very first
    /// mouse event the whole session has seen, which is why only the very
    /// first menu ever shows oversized and every one after is correct.
    ///
    /// Nudging the real cursor by a pixel and back, once, right here at
    /// startup "spends" that one-time touch default silently instead of on
    /// the user's actual first right-click.</summary>
    private static void PrimeMouseInputMode()
    {
        var move = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new NativeMethods.MOUSEINPUT { dx = 1, dwFlags = NativeMethods.MOUSEEVENTF_MOVE },
        };
        var moveBack = move;
        moveBack.mi.dx = -1;

        NativeMethods.SendInput(2, [move, moveBack], Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>Repositions the (invisible) owner window so the flyout's
    /// placement target -- and therefore the menu itself, via
    /// FlyoutShowOptions.Position in TrayIconManager -- lands at this
    /// screen point.</summary>
    public void MoveTo(int screenX, int screenY) => _appWindow.Move(new PointInt32(screenX, screenY));

    /// <summary>Matches the app's actual light/dark/system theme --
    /// without this, the menu always renders in whichever theme the
    /// system default happens to be, since this window's own XamlRoot has
    /// no other reason to know about the app's theme setting at all.</summary>
    public void SetTheme(bool dark) =>
        AnchorElement.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

    /// <summary>Classic tray-icon gotcha (see Raymond Chen's "menus for
    /// notification icons don't work correctly"): a background/tray
    /// process's window isn't normally allowed to steal foreground, but
    /// Windows grants a brief exception when this is called synchronously
    /// from within the same message handling that reported the click (as
    /// ShowMenu does). Without foreground here, the flyout's light-dismiss
    /// never reliably notices a click outside it -- same underlying cause
    /// as needing this for the old TrackPopupMenuEx popup.</summary>
    public void TakeForeground()
    {
        _window.Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }
}
