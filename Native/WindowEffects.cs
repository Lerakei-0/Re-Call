using ReCall.Models;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using WinRT;

namespace ReCall.Native;

/// <summary>
/// WinUI3 exposes Acrylic as a first-class Window.SystemBackdrop property
/// (DesktopAcrylicBackdrop) -- no manual DWMWA_SYSTEMBACKDROP_TYPE plumbing
/// needed. The one thing that simple property API doesn't give us: it wires
/// the backdrop up to the window's *real* OS activation state, so it renders
/// as a flat, washed-out "inactive" look until the window is genuinely
/// focused. For a flyout-style panel that's about to be shown via a slide
/// animation (before it has necessarily been activated), that reads as a
/// wrong-looking flash. Driving DesktopAcrylicController directly and
/// pinning its SystemBackdropConfiguration.IsInputActive to true permanently
/// avoids that.
/// </summary>
internal static class WindowEffects
{
    /// <summary>Per-window Acrylic state. The controller/configuration have
    /// to be kept alive for the window's lifetime (they're not owned by
    /// anything else), hence this dictionary.</summary>
    private static readonly Dictionary<Window, (DesktopAcrylicController Controller, SystemBackdropConfiguration Config)> _acrylicByWindow = new();

    /// <summary>Real Acrylic backdrop, pinned to always render as "active"
    /// regardless of window focus. <paramref name="dark"/>, when given, seeds
    /// SystemBackdropConfiguration.Theme directly instead of reading
    /// root.ActualTheme -- for a window that hasn't been Activate()d yet
    /// (e.g. the panel, hidden until first opened), ActualTheme can still be
    /// stuck at its pre-RequestedTheme default at attach time, which shows
    /// up as the backdrop briefly rendering in the wrong light/dark mode.</summary>
    public static void ApplyAcrylicBackdrop(Window window, bool? dark = null, bool thin = false)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            // Older Windows builds: fall back to the simple property API,
            // which still gives Acrylic (just with focus-tracking dimming).
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }

        if (_acrylicByWindow.TryGetValue(window, out var existing))
        {
            // Already set up for this window -- just make sure it's
            // attached, and re-seed the theme if the caller now knows it.
            existing.Controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
            if (dark is { } d) existing.Config.Theme = ToBackdropTheme(d ? ElementTheme.Dark : ElementTheme.Light);
            return;
        }

        var config = new SystemBackdropConfiguration
        {
            IsInputActive = true, // pinned: never dim for lack of focus
        };

        var controller = new DesktopAcrylicController
        {
            Kind = thin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base,
        };
        controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(config);

        _acrylicByWindow[window] = (controller, config);

        // A manually-driven SystemBackdropConfiguration doesn't inherit the
        // window's Dark/Light state on its own the way the built-in
        // Window.SystemBackdrop property does -- wire it up explicitly.
        config.Theme = dark is { } initialDark
            ? ToBackdropTheme(initialDark ? ElementTheme.Dark : ElementTheme.Light)
            : (window.Content is FrameworkElement seedRoot ? ToBackdropTheme(seedRoot.ActualTheme) : SystemBackdropTheme.Default);

        if (window.Content is FrameworkElement root)
            root.ActualThemeChanged += (sender, _) => config.Theme = ToBackdropTheme(((FrameworkElement)sender).ActualTheme);

        window.Closed += (_, _) =>
        {
            if (_acrylicByWindow.Remove(window, out var state))
                state.Controller.Dispose();
        };
    }

    private static SystemBackdropTheme ToBackdropTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        ElementTheme.Light => SystemBackdropTheme.Light,
        _ => SystemBackdropTheme.Default,
    };

    /// <summary>Per-window Mica state. A mutable class (rather than the
    /// tuple ApplyAcrylicBackdrop above uses) on purpose: ReattachMicaBackdrop
    /// (below) needs to swap in a brand-new Controller/Config for a window
    /// that's already been set up, without re-subscribing (and thereby
    /// duplicating) the ActualThemeChanged/Closed handlers wired up in
    /// ApplyMicaBackdrop, which close over this same object rather than a
    /// specific controller/config pair.</summary>
    private sealed class MicaState
    {
        public MicaController Controller = null!;
        public SystemBackdropConfiguration Config = null!;
    }

    /// <summary>Per-window Mica state, tracked the same way as
    /// <see cref="_acrylicByWindow"/> and for the same reason: driving
    /// MicaController directly (rather than the Window.SystemBackdrop
    /// convenience property) and pinning IsInputActive permanently avoids
    /// the flat, washed-out "inactive" look Mica would otherwise show until
    /// the window is genuinely OS-focused.</summary>
    private static readonly Dictionary<Window, MicaState> _micaByWindow = new();

    /// <summary>Real Mica backdrop (SettingsWindow's look, ported from
    /// WorldClockTray), pinned to always render as "active" regardless of
    /// window focus, same as ApplyAcrylicBackdrop.</summary>
    public static void ApplyMicaBackdrop(Window window)
    {
        if (!MicaController.IsSupported())
        {
            if (DesktopAcrylicController.IsSupported())
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }

        if (_micaByWindow.ContainsKey(window))
        {
            // Already set up for this window -- ReattachMicaBackdrop is the
            // path for refreshing it (e.g. after the window was hidden),
            // since simply re-adding the existing controller as a target
            // isn't sufficient there. Nothing to do here.
            return;
        }

        var state = new MicaState();
        _micaByWindow[window] = state;
        CreateAndAttachMicaController(window, state);

        // A manually-driven SystemBackdropConfiguration doesn't inherit the
        // window's Dark/Light state on its own the way the built-in
        // Window.SystemBackdrop property does -- wire it up explicitly, both
        // now and on every future theme change. Reads state.Config (not a
        // captured config instance) so this stays correct across
        // ReattachMicaBackdrop swapping the config out from under it.
        if (window.Content is FrameworkElement root)
        {
            state.Config.Theme = ToBackdropTheme(root.ActualTheme);
            root.ActualThemeChanged += (sender, _) => state.Config.Theme = ToBackdropTheme(((FrameworkElement)sender).ActualTheme);
        }

        window.Activated += (_, _) =>
        {
            if (!_micaByWindow.TryGetValue(window, out var current)) return;
            try
            {
                current.Config.IsInputActive = true;
                current.Controller.SetSystemBackdropConfiguration(current.Config);
            }
            catch
            {
                // best effort -- see CreateAndAttachMicaController's remarks
                // for why this pin can silently fail to stick.
            }
        };

        window.Closed += (_, _) =>
        {
            if (_micaByWindow.Remove(window, out var removed))
                removed.Controller.Dispose();
        };
    }

    /// <summary>Creates a fresh MicaController + SystemBackdropConfiguration,
    /// attaches the controller to <paramref name="window"/>, and stores both
    /// on <paramref name="state"/> (overwriting whatever was there, if
    /// anything -- callers that need the old controller disposed first, i.e.
    /// ReattachMicaBackdrop, do that before calling this). Wrapped in
    /// try/catch: AddSystemBackdropTarget is documented to be able to fail
    /// (E_ACCESSDENIED) if the window's composition target isn't fully
    /// valid yet, which is a real risk here since ReattachMicaBackdrop can
    /// run very soon after AppWindow.Show() brings a previously-hidden
    /// window back.</summary>
    private static void CreateAndAttachMicaController(Window window, MicaState state)
    {
        try
        {
            var config = new SystemBackdropConfiguration
            {
                IsInputActive = true, // pinned: never dim for lack of focus
            };

            var controller = new MicaController { Kind = MicaKind.Base };
            controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
            controller.SetSystemBackdropConfiguration(config);

            if (window.Content is FrameworkElement root)
                config.Theme = ToBackdropTheme(root.ActualTheme);

            state.Controller = controller;
            state.Config = config;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowEffects] Mica attach failed: {ex}");
        }
    }

    /// <summary>Refreshes Mica for a window that's being shown again after
    /// having been hidden (AppWindow.Hide(), not a real Close()) --
    /// SettingsWindow's reuse-the-same-instance pattern being the motivating
    /// case. A window's SystemBackdropController target goes dead once the
    /// underlying AppWindow has been hidden; re-adding the *same* controller
    /// instance as a target (what a plain re-call to ApplyMicaBackdrop would
    /// do, since the window is already in _micaByWindow) does not revive it
    /// -- only building a brand-new MicaController does. Disposes the old
    /// (dead) controller and swaps in a new one via
    /// CreateAndAttachMicaController, reusing the same MicaState object (and
    /// therefore the ActualThemeChanged/Closed handlers already wired up to
    /// it in ApplyMicaBackdrop) rather than re-subscribing those. If the
    /// window was never set up via ApplyMicaBackdrop in the first place,
    /// falls back to doing that instead.</summary>
    public static void ReattachMicaBackdrop(Window window)
    {
        if (!MicaController.IsSupported())
        {
            ApplyMicaBackdrop(window);
            return;
        }

        if (!_micaByWindow.TryGetValue(window, out var state))
        {
            ApplyMicaBackdrop(window);
            return;
        }

        try
        {
            state.Controller.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowEffects] Disposing stale Mica controller failed: {ex}");
        }

        CreateAndAttachMicaController(window, state);
    }

    /// <summary>Keeps each window's fallback-clear timer (see
    /// ApplyMicaBackdropWithFallback) alive until it fires. The timer used
    /// to live only as a local variable there -- nothing but its own Tick
    /// closure held a reference to it once the method returned, and a
    /// DispatcherQueueTimer is not guaranteed to root itself just because
    /// it's running. A GC landing between Start() and the 80ms mark could
    /// collect it before it ever ticked, silently and with no exception --
    /// leaving the flat fallback color on screen permanently, which reads
    /// as an intermittent "Mica never shows up" bug. Rooting it here until
    /// Tick fires (or the window closes first) removes that race.</summary>
    private static readonly Dictionary<Window, Microsoft.UI.Dispatching.DispatcherQueueTimer> _fallbackTimers = new();

    private static SolidColorBrush FallbackBrush(bool dark) => new(dark
        ? Windows.UI.Color.FromArgb(255, 32, 32, 34)
        : Windows.UI.Color.FromArgb(255, 255, 255, 255));

    /// <summary>For windows that get Activate()d immediately after
    /// construction (e.g. SettingsWindow, unlike the panel which stays
    /// hidden for a warm-up beat first): paints rootPanel with a flat,
    /// theme-matched fallback color immediately, attaches the real Mica
    /// backdrop, then clears the fallback a beat later once Mica has had a
    /// chance to actually paint. MicaController attaches asynchronously --
    /// there's no synchronous "Mica has rendered its first frame" signal to
    /// key off, so a short one-shot timer stands in for that wait. Without
    /// this, a freshly-created window shows DWM's raw default (white)
    /// surface for a frame or two before Mica catches up -- the "white
    /// flash before dark mode loads" symptom.</summary>
    public static void ApplyMicaBackdropWithFallback(Window window, Panel rootPanel, bool dark)
    {
        rootPanel.Background = FallbackBrush(dark);
        ApplyMicaBackdrop(window);

        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(80);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            rootPanel.Background = null;
            _fallbackTimers.Remove(window);
        };

        // Root it for the window's lifetime in case it closes before the
        // timer fires (e.g. an instant double-click close) -- otherwise
        // this dictionary entry would be the last thing keeping the timer
        // (and transitively the window) alive forever.
        window.Closed += (_, _) => _fallbackTimers.Remove(window);

        _fallbackTimers[window] = timer;
        timer.Start();
    }

    /// <summary>Same fallback-cover trick as ApplyMicaBackdropWithFallback,
    /// but for a window that's being *reshown* after AppWindow.Hide()
    /// rather than set up for the first time -- SettingsWindow.ShowAndActivate
    /// is the motivating case. ReattachMicaBackdrop replaces the dead
    /// controller with a fresh one, which is just as asynchronous as the
    /// very first attach, so without a cover here every reopen has the same
    /// "white flash before Mica catches up" gap the first-open fallback was
    /// built to avoid -- it just went unnoticed while the window was being
    /// destroyed and rebuilt on every close (which always ran the first-open
    /// path fresh). Paints the fallback synchronously so it's already in
    /// place before the caller's Show() makes the window visible again;
    /// ReattachMicaBackdrop itself is posted via TryEnqueue for the same
    /// composition-target-not-valid-yet reason ShowAndActivate's caller
    /// already posts it for.</summary>
    public static void ReattachMicaBackdropWithFallback(Window window, Panel rootPanel, bool dark)
    {
        rootPanel.Background = FallbackBrush(dark);

        window.DispatcherQueue.TryEnqueue(() =>
        {
            ReattachMicaBackdrop(window);

            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(80);
            timer.IsRepeating = false;
            timer.Tick += (t, _) =>
            {
                t.Stop();
                rootPanel.Background = null;
                _fallbackTimers.Remove(window);
            };
            _fallbackTimers[window] = timer;
            timer.Start();
        });
    }

    /// <summary>Generates true per-pixel grain directly into a WriteableBitmap
    /// at exactly (pixelWidth, pixelHeight) -- no source asset, no stretching,
    /// so there's no resampling to blow individual texels up into visible
    /// blobs the way a stretched static PNG does. Call with the panel's
    /// actual current pixel size (DIP size * ScaleFactor) any time that size
    /// changes; a size baked in at the wrong DPI/dimensions is exactly the
    /// "texture too big" symptom the first version had.
    ///
    /// supersample: each output texel is the average of a (supersample x
    /// supersample) block of independent random samples rather than a single
    /// one. One random value per physical pixel is already the finest grain
    /// the display can show individually, so "smaller-looking" grain from
    /// here on isn't about shrinking a texel further -- it's about breaking
    /// up the coincidental bigger blobs that pure per-pixel randomness
    /// produces whenever two or three neighboring pixels happen to roll
    /// similar bright values. Averaging several independent samples per
    /// texel makes that kind of accidental clustering statistically rarer,
    /// which is what actually reads as finer/smaller grain -- and as a
    /// side effect it also pulls every texel's alpha toward the middle of
    /// its range instead of occasionally hitting the top of it, so the
    /// whole overlay reads as more uniformly see-through too.
    ///
    /// baseAlpha/alphaRange: alpha ends up in the range [baseAlpha,
    /// baseAlpha + alphaRange), out of 255, *before* the supersample
    /// averaging above narrows that further. Real Acrylic's grain is extremely subtle --
    /// keep this low (roughly 1-2% opacity per pixel) or it reads as visible
    /// static rather than the faint texture in the reference screenshots.
    ///
    /// WriteableBitmap's PixelBuffer is BGRA8 *premultiplied* alpha -- each
    /// color channel has to already be scaled by alpha/255, not written at
    /// full brightness alongside a small alpha value. Writing straight-alpha
    /// values here (full-brightness grayscale next to a near-zero alpha byte)
    /// is invalid premultiplied data and is exactly what produced the
    /// blown-out, fully-opaque static instead of faint grain.</summary>
    public static void RegenerateNoise(Image target, int pixelWidth, int pixelHeight, byte baseAlpha = 3, byte alphaRange = 5, int supersample = 2)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return;

        var sw = pixelWidth * supersample;
        var sh = pixelHeight * supersample;
        var samplesPerTexel = supersample * supersample;

        // Two independent random bytes per super-sample: one for luminance,
        // one for alpha. Filled in one vectorized call rather than per-pixel
        // Random.Next() calls, same reasoning as the single-sample version
        // this replaces.
        var raw = new byte[sw * sh * 2];
        Random.Shared.NextBytes(raw);

        var bitmap = new WriteableBitmap(pixelWidth, pixelHeight);
        var buffer = new byte[pixelWidth * pixelHeight * 4]; // BGRA8, premultiplied

        var bufIndex = 0;
        for (var y = 0; y < pixelHeight; y++)
        {
            var rawRowBase = y * supersample * sw;
            for (var x = 0; x < pixelWidth; x++)
            {
                var lumSum = 0;
                var alphaSum = 0;
                var rawColBase = rawRowBase + x * supersample;
                for (var sy = 0; sy < supersample; sy++)
                {
                    var rowIndex = (rawColBase + sy * sw) * 2;
                    for (var sx = 0; sx < supersample; sx++)
                    {
                        var idx = rowIndex + sx * 2;
                        lumSum += raw[idx];
                        alphaSum += raw[idx + 1];
                    }
                }

                var luminance = (byte)(lumSum / samplesPerTexel);
                var alpha = (byte)(baseAlpha + (alphaSum / samplesPerTexel) % alphaRange);
                var premultiplied = (byte)(luminance * alpha / 255); // premultiplied channel value is <= alpha

                buffer[bufIndex] = premultiplied;
                buffer[bufIndex + 1] = premultiplied;
                buffer[bufIndex + 2] = premultiplied;
                buffer[bufIndex + 3] = alpha;
                bufIndex += 4;
            }
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(buffer, 0, buffer.Length);

        bitmap.Invalidate();
        target.Source = bitmap;
    }

    /// <summary>The "sheet of glass" trick: extending a negative frame into
    /// the client area gives a borderless-looking window DWM's own soft drop
    /// shadow.</summary>
    public static void ApplyShadow(IntPtr hwnd)
    {
        try
        {
            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Keeps the panel above every ordinary app window, the same
    /// way Windows' own flyouts (Action Center, the calendar/clock popup)
    /// behave -- but slotted directly *behind* the taskbar in the topmost
    /// band, rather than in front of it like HWND_TOPMOST alone would give.
    /// The taskbar is itself a topmost window, so "topmost" isn't a single
    /// flag with one winner: it's an ordered list, and where you land in
    /// that list is decided by hWndInsertAfter. Two calls are needed --
    /// SetWindowPos only special-cases HWND_TOPMOST for *toggling* the
    /// WS_EX_TOPMOST bit; inserting relative to an arbitrary HWND (the
    /// taskbar's) repositions within the topmost band but won't set that bit
    /// on a window that doesn't have it yet. So: first call marks the panel
    /// topmost (front of the band), second slots it back behind the taskbar
    /// specifically. Safe to call every time the panel is about to show.</summary>
    public static void ApplyTopmostBelowTaskbar(IntPtr hwnd)
    {
        try
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(hwnd, taskbar, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        }
        catch
        {
            // best effort -- worst case the panel ends up plain HWND_TOPMOST
            // (in front of the taskbar) instead of tucked behind it.
        }
    }

    /// <summary>Hides the window from the taskbar and Alt+Tab entirely
    /// (WS_EX_TOOLWINDOW), matching a proper flyout panel rather than a
    /// normal app window.</summary>
    public static void HideFromTaskbar(IntPtr hwnd)
    {
        try
        {
            var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            ex = (ex | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>DIP-per-pixel scale for a given hwnd (96 DPI == 1.0).
    /// AppWindow works in physical pixels; XAML content is measured in DIPs,
    /// so every position/size crossing that boundary needs this.</summary>
    public static double ScaleFactor(IntPtr hwnd) => NativeMethods.GetDpiForWindow(hwnd) / 96.0;

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE: tints the native titlebar
    /// (and Mica/Acrylic tone) to match dark or light mode. Ported from
    /// WorldClockTray's WindowEffects.SetDarkMode.</summary>
    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        try
        {
            var value = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Best-effort DWMWA_WINDOW_CORNER_PREFERENCE. No-op on
    /// pre-Windows-11 builds.</summary>
    public static void ApplyCornerStyle(IntPtr hwnd, CornerStyle style)
    {
        try
        {
            var pref = (int)style;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Strips the little app-icon square from a window's native
    /// title bar.</summary>
    public static void RemoveTitleBarIcon(IntPtr hwnd)
    {
        try
        {
            var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_DLGMODALFRAME);

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);

            NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_SMALL, IntPtr.Zero);
            NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_BIG, IntPtr.Zero);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Strips WS_MAXIMIZEBOX, which classically both hides the
    /// maximize button and blocks double-click-to-maximize. Under
    /// ExtendsContentIntoTitleBar this is only best-effort though -- the
    /// extended title bar's own hit-testing is documented to sometimes
    /// ignore it (see BlockMaximizeCommand for the actual reliable fix).</summary>
    public static void RemoveMaximizeButton(IntPtr hwnd)
    {
        try
        {
            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style & ~NativeMethods.WS_MAXIMIZEBOX);

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Per-hwnd subclass state: the managed WndProc delegate (kept
    /// alive here so the GC doesn't collect it out from under the native
    /// callback pointer) and the original WNDPROC to forward everything
    /// else to.</summary>
    private static readonly Dictionary<IntPtr, (NativeMethods.WndProc Proc, IntPtr Original)> _subclassed = new();

    /// <summary>Both OverlappedPresenter.IsMaximizable = false and stripping
    /// WS_MAXIMIZEBOX (RemoveMaximizeButton, above) are documented as
    /// unreliable once ExtendsContentIntoTitleBar is set -- the extended
    /// title bar's own hit-testing (button click AND double-click) bypasses
    /// both checks in current Windows App SDK builds. The one thing that
    /// isn't affected: every maximize trigger -- button, double-click,
    /// Win+Up, the system menu -- funnels through the same
    /// WM_SYSCOMMAND/SC_MAXIMIZE message before Windows acts on it.
    /// Subclassing the window's WNDPROC to swallow that message blocks all
    /// of them uniformly, one level below wherever the WinAppSDK-side bug
    /// lives.</summary>
    public static void BlockMaximizeCommand(IntPtr hwnd)
    {
        if (_subclassed.ContainsKey(hwnd)) return; // already subclassed

        NativeMethods.WndProc proc = (h, msg, wParam, lParam) =>
        {
            if (msg == NativeMethods.WM_SYSCOMMAND && ((long)wParam & NativeMethods.SC_MASK) == NativeMethods.SC_MAXIMIZE)
                return IntPtr.Zero; // swallowed: no maximize, regardless of what triggered it

            return NativeMethods.CallWindowProc(_subclassed[h].Original, h, msg, wParam, lParam);
        };

        var original = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(proc));

        _subclassed[hwnd] = (proc, original);
    }

    /// <summary>Clamps how small the window can be dragged/snapped, since
    /// there's no OverlappedPresenter-level API for this (unlike
    /// IsResizable/IsMaximizable, which are real properties) -- min-size
    /// has to be enforced by answering WM_GETMINMAXINFO directly, the same
    /// message DefWindowProc itself consults for edge-drag, maximize, and
    /// Aero-snap alike, so handling it here covers all three uniformly.
    /// Shares BlockMaximizeCommand's _subclassed dictionary/pattern above,
    /// but is otherwise independent of it -- a window only needs this one
    /// if it's resizable at all.</summary>
    public static void EnforceMinimumSize(IntPtr hwnd, int minWidthPx, int minHeightPx)
    {
        if (_subclassed.ContainsKey(hwnd)) return; // already subclassed

        NativeMethods.WndProc proc = (h, msg, wParam, lParam) =>
        {
            if (msg == NativeMethods.WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.X = minWidthPx;
                mmi.ptMinTrackSize.Y = minHeightPx;
                Marshal.StructureToPtr(mmi, lParam, false);
                return IntPtr.Zero;
            }

            return NativeMethods.CallWindowProc(_subclassed[h].Original, h, msg, wParam, lParam);
        };

        var original = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(proc));

        _subclassed[hwnd] = (proc, original);
    }

    /// <summary>SetDarkMode (DWMWA_USE_IMMERSIVE_DARK_MODE) only tints the
    /// native non-client frame. Once ExtendsContentIntoTitleBar is set (as
    /// SettingsWindow does, to draw its own in-XAML title bar), the
    /// min/maximize/close buttons are no longer part of that native frame --
    /// they're drawn in-process via AppWindow.TitleBar, and DWM has no say
    /// over their colors. Worse, AppWindow.TitleBar's button colors are just
    /// static properties: whatever they're set to is what paints, and
    /// nothing pushes them to follow root.ActualTheme on its own. Seed the
    /// colors once up front, then keep them in sync by reacting to
    /// ActualThemeChanged directly, the same event Mica/Acrylic already key
    /// off of in this file.</summary>
    public static void SyncTitleBarButtonColors(Window window)
    {
        if (window.Content is not FrameworkElement root) return;

        ApplyTitleBarButtonColors(window, root.ActualTheme);
        root.ActualThemeChanged += (sender, _) =>
            ApplyTitleBarButtonColors(window, ((FrameworkElement)sender).ActualTheme);
    }

    private static void ApplyTitleBarButtonColors(Window window, ElementTheme theme)
    {
        var titleBar = window.AppWindow.TitleBar;
        var isDark = theme == ElementTheme.Dark;

        var foreground = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Windows.UI.Color.FromArgb(150, 255, 255, 255)
            : Windows.UI.Color.FromArgb(150, 0, 0, 0);

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Windows.UI.Color.FromArgb(30, 255, 255, 255)
            : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Windows.UI.Color.FromArgb(50, 255, 255, 255)
            : Windows.UI.Color.FromArgb(50, 0, 0, 0);
    }
}
