using System.Runtime.InteropServices;
using ReCall.Native;
using Windows.Graphics;

namespace ReCall.UI;

/// <summary>
/// System-wide low-level mouse hook used to detect "click away" from the open
/// panel and to anchor the panel at the exact point a tray-icon click
/// happened. Background/tray processes are frequently denied real foreground
/// activation on Windows, so watching raw mouse-down position is the
/// standard flyout/tray-app technique. Ported from WorldClockTray's
/// UI/MouseHookWatcher.cs -- replaces the previous Window.Activated
/// (Deactivated) based hiding, which only fires when this process itself
/// loses real OS activation and can miss/lag clicks the way a tray app's
/// windows sometimes do.
/// </summary>
public sealed class MouseHookWatcher
{
    private readonly ClipboardPanelWindow _panel;
    private IntPtr _hookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelMouseProc _proc; // keep alive: GC'd delegate = crash

    public PointInt32? LastClickPos { get; private set; }

    public MouseHookWatcher(ClipboardPanelWindow panel)
    {
        _panel = panel;
        _proc = HookCallback;
    }

    public void Install()
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _proc,
            NativeMethods.GetModuleHandle(curModule.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"[mouse_hook] SetWindowsHookEx FAILED, GetLastError={err}");
        }
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == 0 && (wParam == NativeMethods.WM_LBUTTONDOWN || wParam == NativeMethods.WM_RBUTTONDOWN))
        {
            NativeMethods.GetCursorPos(out var cursor);
            LastClickPos = new PointInt32(cursor.X, cursor.Y);

            if (_panel.IsPanelVisible)
            {
                try
                {
                    var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    PixelRect panelRect = _panel.CurrentScreenRectPx();
                    if (!panelRect.Contains(hookStruct.pt.X, hookStruct.pt.Y) && !_panel.IsPanelPinned)
                        _panel.SlideOut();
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[mouse_hook] error: {e}");
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }
}

/// <summary>Minimal physical-pixel rect + Contains(). Ported from
/// WorldClockTray's UI/ClockPanel.cs.</summary>
public readonly struct PixelRect
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public PixelRect(int x, int y, int width, int height)
    {
        X = x; Y = y; Width = width; Height = height;
    }

    public bool Contains(int px, int py) =>
        px >= X && px <= X + Width && py >= Y && py <= Y + Height;
}
