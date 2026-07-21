using System.Threading;

namespace ReCall.Native;

/// <summary>
/// Ensures only one ReCall process runs at a time. A named Mutex is
/// how the second launch finds out a first instance already exists; a named
/// EventWaitHandle is then used to ask that first instance to show its
/// panel, the same way a tray-icon click or the global hotkey would (see
/// App.OnLaunched's WatchForShowRequests wiring). Both names are fixed
/// strings scoped to this session (no "Global\" prefix), which is enough
/// since a second instance can only ever be launched by the same user
/// session that's already running the first one. Ported from Kronos's
/// Native/SingleInstance.cs.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "ReCall-6A1D9E77-2F4A-4E1B-8C55-SingleInstance";
    private const string ShowEventName = "ReCall-6A1D9E77-2F4A-4E1B-8C55-ShowRequested";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;

    /// <summary>True if this process is the only one running -- the caller
    /// should proceed with normal startup. False means another instance
    /// already owns the mutex; the caller should signal it via
    /// RequestShowOnRunningInstance() and exit without creating any
    /// windows.</summary>
    public bool IsFirstInstance { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
        IsFirstInstance = createdNew;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    }

    /// <summary>Called by a second-launch process (IsFirstInstance == false)
    /// to ask the already-running instance to show its panel. The caller
    /// should exit immediately afterwards.</summary>
    public void RequestShowOnRunningInstance() => _showEvent.Set();

    /// <summary>Called by the first instance only. Blocks on a background
    /// thread waiting for later launches to call RequestShowOnRunningInstance(),
    /// then hops back onto dispatcherQueue to run onShowRequested -- mirrors
    /// how GlobalHotkeyManager's WM_HOTKEY callback and the tray icon's
    /// left-click both just call _panelWindow.Toggle(...) directly.</summary>
    public void WatchForShowRequests(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue, Action onShowRequested)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                _showEvent.WaitOne();
                dispatcherQueue.TryEnqueue(() => onShowRequested());
            }
        })
        { IsBackground = true };
        thread.Start();
    }

    /// <summary>No-op on a second instance: it never actually acquired
    /// ownership of the mutex (the OS grants initiallyOwned only when this
    /// call is the one that creates it), so there's nothing for it to
    /// release.</summary>
    public void Dispose()
    {
        if (IsFirstInstance)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
        _showEvent.Dispose();
    }
}
