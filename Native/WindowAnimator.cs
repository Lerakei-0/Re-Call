using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace ReCall.Native;

/// <summary>
/// Position-tween engine ported from the WorldClockTray app. Moves the
/// window from a dedicated background thread and calls DwmFlush() after
/// every SetWindowPos -- DwmFlush blocks until the next vsync, which both
/// paces the loop to the display's actual refresh rate (via
/// GetRefreshIntervalMs, rather than assuming 60Hz) and guarantees each
/// frame's position change is picked up by the very next composited frame.
/// SetWindowPos itself is safe to call from a thread that doesn't own the
/// window -- Win32 marshals it through the owning thread's message queue
/// internally, which is exactly what lets this run off the UI thread at
/// ThreadPriority.Highest without contending with XAML.
/// </summary>
internal static class WindowAnimator
{
    /// <summary>Exponential ease-out. Starts fast and settles gently into
    /// the target -- used for opening.</summary>
    public static double ExponentialEaseOut(double t) =>
        t >= 1.0 ? 1.0 : 1 - System.Math.Pow(2, -10 * t);

    /// <summary>Circular ease-in. Starts slow and accelerates toward the
    /// target -- used for closing.</summary>
    public static double CircularEaseIn(double t)
    {
        var c = System.Math.Clamp(t, 0.0, 1.0);
        return 1 - System.Math.Sqrt(1 - c * c);
    }

    /// <summary>Per-frame interval matched to the monitor's real refresh
    /// rate (falls back to 16ms/~60Hz if it can't be determined), so the
    /// animation's step count -- and therefore its wall-clock duration --
    /// stays correct on 120Hz/144Hz displays instead of assuming 60Hz.</summary>
    public static int GetRefreshIntervalMs(IntPtr hwnd)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX)),
            };
            NativeMethods.GetMonitorInfo(monitor, ref monitorInfo);

            var devMode = new NativeMethods.DEVMODE
            {
                dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE)),
            };
            if (NativeMethods.EnumDisplaySettings(monitorInfo.szDevice, -1, ref devMode))
            {
                var refreshRate = devMode.dmDisplayFrequency;
                if (refreshRate > 0) return 1000 / refreshRate;
            }
        }
        catch
        {
            // best effort
        }
        return 16;
    }

    /// <summary>Slides <paramref name="hwnd"/> from <paramref name="start"/>
    /// to <paramref name="target"/> over <paramref name="durationMs"/>,
    /// stepping on a background thread synced via DwmFlush.
    ///
    /// <paramref name="nearCompleteFraction"/>: when the remaining distance
    /// drops to that fraction of the total (e.g. 0.1 = last 10%), <paramref
    /// name="onNearComplete"/> fires once, a beat before the window has
    /// actually settled -- used on open to make content feel like it keeps
    /// pace with the panel instead of trailing it. Pass 0 to disable.
    ///
    /// <paramref name="onComplete"/> fires once the tween finishes
    /// naturally -- NOT if <paramref name="cancellationToken"/> cancels it
    /// first (a cancelled tween is a silent stop, e.g. a new SlideIn
    /// interrupting a still-running SlideOut). Both callbacks are marshaled
    /// back onto <paramref name="dispatcherQueue"/> since the tween itself
    /// runs off-thread.</summary>
    public static void Slide(
        IntPtr hwnd,
        PointInt32 start,
        PointInt32 target,
        int durationMs,
        System.Func<double, double> easing,
        DispatcherQueue dispatcherQueue,
        double nearCompleteFraction = 0.0,
        System.Action? onNearComplete = null,
        System.Action? onComplete = null,
        CancellationToken cancellationToken = default)
    {
        var intervalMs = GetRefreshIntervalMs(hwnd);
        var steps = System.Math.Max(1, durationMs / intervalMs);
        var totalDistance = System.Math.Sqrt(
            System.Math.Pow(target.X - start.X, 2) + System.Math.Pow(target.Y - start.Y, 2));
        var triggerDistance = totalDistance * nearCompleteFraction;
        var nearTriggered = nearCompleteFraction <= 0;

        Task.Run(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;

            var currentStep = 0;
            while (true)
            {
                // A cancelled tween is a silent stop -- onComplete must NOT
                // fire here. Otherwise interrupting a SlideOut mid-flight
                // (e.g. a new SlideIn starting while the panel is still
                // closing) would still run the SlideOut's onFinished
                // (hiding the window) right after the panel was just told
                // to show again.
                if (cancellationToken.IsCancellationRequested) return;

                currentStep++;
                var t = (double)currentStep / steps;
                var eased = currentStep >= steps ? 1.0 : easing(t);

                var x = (int)System.Math.Round(start.X + (target.X - start.X) * eased);
                var y = (int)System.Math.Round(start.Y + (target.Y - start.Y) * eased);
                if (currentStep >= steps) { x = target.X; y = target.Y; }

                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                NativeMethods.DwmFlush();

                if (!nearTriggered)
                {
                    var remaining = System.Math.Sqrt(
                        System.Math.Pow(target.X - x, 2) + System.Math.Pow(target.Y - y, 2));
                    if (remaining <= triggerDistance)
                    {
                        nearTriggered = true;
                        if (onNearComplete != null)
                            dispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () => onNearComplete());
                    }
                }

                if (currentStep >= steps)
                {
                    if (onComplete != null)
                        dispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () => onComplete());
                    return;
                }
            }
        });
    }

    /// <summary>Tweens <paramref name="hwnd"/>'s position AND size together,
    /// from <paramref name="start"/> to <paramref name="target"/>, over
    /// <paramref name="durationMs"/> -- same background-thread
    /// SetWindowPos+DwmFlush stepping as Slide, just changing all four
    /// SetWindowPos values together each frame instead of only x/y. Used to
    /// grow/shrink the panel to fit its content (e.g. Clear All collapsing a
    /// tall history) instead of snapping to the new size in a single frame.
    /// onComplete/cancellation semantics are identical to Slide's -- see its
    /// doc comment. No onNearComplete hook here: unlike the open/close
    /// slide, no caller currently needs to kick off a secondary animation a
    /// beat before a resize lands.</summary>
    public static void ResizeAndMove(
        IntPtr hwnd,
        RectInt32 start,
        RectInt32 target,
        int durationMs,
        System.Func<double, double> easing,
        DispatcherQueue dispatcherQueue,
        System.Action? onComplete = null,
        CancellationToken cancellationToken = default)
    {
        var intervalMs = GetRefreshIntervalMs(hwnd);
        var steps = System.Math.Max(1, durationMs / intervalMs);

        Task.Run(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;

            var currentStep = 0;
            while (true)
            {
                // Silent stop on cancellation, same rationale as Slide: a
                // new resize (or an open/close slide) superseding this one
                // mid-flight must not also fire this one's onComplete.
                if (cancellationToken.IsCancellationRequested) return;

                currentStep++;
                var t = (double)currentStep / steps;
                var eased = currentStep >= steps ? 1.0 : easing(t);

                var x = (int)System.Math.Round(start.X + (target.X - start.X) * eased);
                var y = (int)System.Math.Round(start.Y + (target.Y - start.Y) * eased);
                var w = (int)System.Math.Round(start.Width + (target.Width - start.Width) * eased);
                var h = (int)System.Math.Round(start.Height + (target.Height - start.Height) * eased);
                if (currentStep >= steps) { x = target.X; y = target.Y; w = target.Width; h = target.Height; }

                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                NativeMethods.DwmFlush();

                if (currentStep >= steps)
                {
                    if (onComplete != null)
                        dispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () => onComplete());
                    return;
                }
            }
        });
    }
}
