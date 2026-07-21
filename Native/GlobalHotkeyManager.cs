using System.Runtime.InteropServices;
using ReCall.Models;

namespace ReCall.Native;

/// <summary>
/// Wraps RegisterHotKey/UnregisterHotKey + WM_HOTKEY for a single system-wide
/// shortcut bound to ClipboardPanelWindow's own hwnd. The panel's window is
/// never closed until the process quits (it's just Hide()/Show()n, same
/// lifetime rationale as WindowEffects.BlockMaximizeCommand's subject being
/// recreated per open vs this one being permanent), so subclassing its
/// WNDPROC once here is safe for the whole app lifetime -- this class owns
/// its hwnd's subclass outright rather than sharing WindowEffects' dictionary,
/// since nothing else subclasses the panel's hwnd. Ported from
/// WorldClockTray's Native/GlobalHotkeyManager.cs.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    // Arbitrary but fixed id -- only one hotkey is ever registered per
    // process, so a single constant is enough to identify it in WM_HOTKEY's
    // wParam.
    private const int HotkeyId = 0xB00C;

    private readonly IntPtr _hwnd;
    private readonly Action _onPressed;
    private NativeMethods.WndProc? _wndProc; // kept alive: GC'd delegate = crash
    private IntPtr _originalWndProc;
    private bool _subclassed;
    private bool _registered;

    public GlobalHotkeyManager(IntPtr hwnd, Action onPressed)
    {
        _hwnd = hwnd;
        _onPressed = onPressed;
    }

    /// <summary>True if the OS accepted the registration. False typically
    /// means another app already owns that exact combo -- the caller should
    /// surface that to the user rather than silently pretending it worked.</summary>
    public bool Register(uint modifiers, uint vk)
    {
        Unregister();
        EnsureSubclassed();
        _registered = NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    private void EnsureSubclassed()
    {
        if (_subclassed) return;

        _wndProc = (h, msg, wParam, lParam) =>
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                _onPressed();
                return IntPtr.Zero;
            }
            return NativeMethods.CallWindowProc(_originalWndProc, h, msg, wParam, lParam);
        };

        _originalWndProc = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProc));
        _subclassed = true;
    }

    /// <summary>Converts the user-facing modifier checkboxes into the
    /// RegisterHotKey MOD_* bitmask.</summary>
    public static uint ToModifiers(HotkeySettings h)
    {
        uint m = 0;
        if (h.Alt) m |= NativeMethods.MOD_ALT;
        if (h.Ctrl) m |= NativeMethods.MOD_CONTROL;
        if (h.Shift) m |= NativeMethods.MOD_SHIFT;
        if (h.Win) m |= NativeMethods.MOD_WIN;
        return m;
    }

    public void Dispose() => Unregister();
}
