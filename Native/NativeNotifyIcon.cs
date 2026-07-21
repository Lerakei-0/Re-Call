using System.Runtime.InteropServices;
using System.Threading;

namespace ReCall.Native;

/// <summary>
/// Minimal Shell_NotifyIcon-based tray icon, ported from WorldClockTray's
/// NativeNotifyIcon. Replaces H.NotifyIcon's TaskbarIcon: in this unpackaged
/// WinUI3 app, H.NotifyIcon's MenuFlyout-based context menu was unreliable
/// (clicks on "Settings..."/"Quit" didn't register) because the flyout isn't
/// owned by a real foreground window the way a native TrackPopupMenuEx menu
/// is. This class depends on nothing but user32.dll/shell32.dll P/Invokes
/// (see NativeMethods.cs).
///
/// Runs classic-mode (no NIM_SETVERSION / NOTIFYICON_VERSION_4) --
/// Shell_NotifyIcon's callback message delivers the raw mouse message
/// (WM_LBUTTONUP/WM_RBUTTONUP) directly in lParam, which is all this app
/// needs (left-click toggles the panel, right-click shows the native popup
/// menu -- see TrayIconManager).
/// </summary>
internal sealed class NativeNotifyIcon : IDisposable
{
    private static int _classCounter;

    private readonly string _className;
    private readonly NativeMethods.WndProc _wndProc; // kept alive: native code holds a raw pointer to this delegate
    private readonly IntPtr _hInstance;
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarRestartMsg;
    private const uint Uid = 1;
    private const uint TrayCallbackMsg = NativeMethods.WM_USER + 1;
    private static readonly IntPtr TaskbarRestartTimerId = new(1);
    private const int MaxRestartRetries = 5;

    private bool _added;
    private string _tooltip = string.Empty;
    private IntPtr _hIcon;
    private bool _disposed;
    private int _restartRetriesLeft;

    public event Action? LeftClick;
    public event Action? RightClick;

    public NativeNotifyIcon()
    {
        // Window classes are process-global, so give this one a unique name.
        _className = $"ReCall_TrayIcon_{Environment.ProcessId}_{Interlocked.Increment(ref _classCounter)}";
        _wndProc = WndProc;
        _hInstance = NativeMethods.GetModuleHandle(null);

        var wcex = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
        };
        NativeMethods.RegisterClassEx(ref wcex);

        // A hidden top-level window (NOT a message-only/HWND_MESSAGE
        // window): never shown, never painted, just somewhere for
        // Shell_NotifyIcon's callback message to land. It rides the same
        // message pump every other window on this thread already uses
        // (including WinUI3's own), so nothing extra needs to pump it.
        //
        // This must be a real top-level window -- message-only windows are
        // explicitly documented as not receiving broadcast messages, and
        // explorer.exe announces its restart via exactly such a broadcast
        // (see _taskbarRestartMsg below). HWND_MESSAGE compiles fine and
        // the window still works for the Shell_NotifyIcon callback, but it
        // silently never gets that broadcast, so the icon fails to come
        // back after explorer crashes/restarts. WS_EX_TOOLWINDOW keeps
        // this top-level window out of the taskbar and Alt+Tab.
        _hwnd = NativeMethods.CreateWindowEx(NativeMethods.WS_EX_TOOLWINDOW, _className, null, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        // explorer.exe can restart (crash, "Restart Windows Explorer" in
        // Task Manager) -- every tray icon it was showing disappears with
        // it. The shell broadcasts this registered message afterwards so
        // anyone who had an icon up knows to re-add it.
        _taskbarRestartMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayCallbackMsg)
        {
            switch ((uint)lParam.ToInt64())
            {
                case NativeMethods.WM_LBUTTONUP:
                    LeftClick?.Invoke();
                    break;
                case NativeMethods.WM_RBUTTONUP:
                    RightClick?.Invoke();
                    break;
            }
            return IntPtr.Zero;
        }

        if (msg == _taskbarRestartMsg)
        {
            _added = false; // the shell dropped it; AddOrModify below re-adds rather than modifies
            _restartRetriesLeft = MaxRestartRetries;
            AddOrModify();
            // Right after explorer restarts, its notification-area host is
            // sometimes not fully up yet, so this first NIM_ADD can fail
            // silently. Poll a few times with a short delay rather than
            // giving up after one attempt.
            if (!_added)
                NativeMethods.SetTimer(_hwnd, TaskbarRestartTimerId, 500, IntPtr.Zero);
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_TIMER && wParam == TaskbarRestartTimerId)
        {
            if (!_added && _restartRetriesLeft-- > 0)
                AddOrModify();

            if (_added || _restartRetriesLeft <= 0)
                NativeMethods.KillTimer(_hwnd, TaskbarRestartTimerId);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Loads a .ico file straight into an HICON -- no
    /// System.Drawing dependency needed.</summary>
    public static IntPtr LoadIconFromFile(string path) =>
        NativeMethods.LoadImage(IntPtr.Zero, path, NativeMethods.IMAGE_ICON, 0, 0,
            NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);

    /// <summary>Sets the tray icon's image. Takes ownership of
    /// <paramref name="hIcon"/> and destroys whichever handle it's replacing
    /// so repeated calls don't leak GDI icon handles.</summary>
    public void SetIcon(IntPtr hIcon)
    {
        var previous = _hIcon;
        _hIcon = hIcon;
        AddOrModify();
        if (previous != IntPtr.Zero)
            NativeMethods.DestroyIcon(previous);
    }

    public void SetTooltip(string text)
    {
        _tooltip = text;
        if (_added) AddOrModify();
    }

    /// <summary>Real screen rect of the icon right now, via
    /// Shell_NotifyIconGetRect -- works for icons hidden in the overflow
    /// flyout too. Returns false if the shell can't report it.</summary>
    public bool TryGetIconRect(out NativeMethods.RECT rect)
    {
        var id = new NativeMethods.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = Uid,
        };
        return NativeMethods.Shell_NotifyIconGetRect(ref id, out rect) == 0; // S_OK
    }

    private void AddOrModify()
    {
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = Uid,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = TrayCallbackMsg,
            hIcon = _hIcon,
            szTip = _tooltip,
        };

        if (NativeMethods.Shell_NotifyIcon(_added ? NativeMethods.NIM_MODIFY : NativeMethods.NIM_ADD, ref data))
            _added = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        NativeMethods.KillTimer(_hwnd, TaskbarRestartTimerId);

        if (_added)
        {
            var data = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = Uid,
            };
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        NativeMethods.DestroyWindow(_hwnd);
        NativeMethods.UnregisterClass(_className, _hInstance);
    }
}
