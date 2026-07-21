using ReCall.Models;
using ReCall.Native;
using ReCall.Services;
using ReCall.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace ReCall;

public partial class App : Application
{
    private TrayIconManager? _trayIcon;
    private ClipboardMonitor? _monitor;
    private ClipboardPanelWindow? _panelWindow;
    private SettingsWindow? _settingsWindow;
    private SettingsStore _settingsStore = null!;
    private HistoryStore _historyStore = null!;
    private MouseHookWatcher _mouseWatcher = null!;
    private GlobalHotkeyManager _hotkeyManager = null!;
    private readonly SingleInstance _singleInstance;

    public App()
    {
        // Checked before anything else: if another ReCall process
        // already owns the mutex, ask it to show its panel (same effect as
        // clicking the tray icon) and exit immediately without touching
        // XAML, tray icons, or hotkeys, so this launch never creates a
        // second instance. Ported from Kronos's App.xaml.cs.
        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.RequestShowOnRunningInstance();
            Environment.Exit(0);
        }

        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _settingsStore = new SettingsStore();
        _historyStore = new HistoryStore();

        _panelWindow = new ClipboardPanelWindow(_settingsStore, _historyStore);

        // Click-away hiding: a global low-level mouse hook, same technique
        // WorldClockTray's ClockPanel uses -- more reliable than
        // Window.Activated/Deactivated for a tray-launched window that
        // doesn't always get real OS foreground activation.
        _mouseWatcher = new MouseHookWatcher(_panelWindow);
        _mouseWatcher.Install();

        _monitor = new ClipboardMonitor();
        _monitor.ClipboardChanged += OnClipboardChanged;

        var iconPath = TrayIconPathFor(_panelWindow.IsDarkMode);
        _trayIcon = new TrayIconManager(_panelWindow, iconPath, _panelWindow.IsDarkMode, OpenSettings, Quit,
            () => _settingsStore.IsStartOnStartupEnabled(), ToggleStartOnStartup);
        _panelWindow.EffectiveThemeChanged += OnEffectiveThemeChanged;

        // Same hwnd for the shortcut's whole lifetime -- the panel is never
        // Close()d/recreated, just Hide()/Show()n, so one GlobalHotkeyManager
        // for the process is enough. The hotkey toggles the panel exactly
        // like a tray-icon left-click would (open if hidden, close if open)
        // -- anchored at the tray icon's own position (TrayIconManager.
        // TrayIconAnchor) rather than the cursor, since a keyboard shortcut
        // has no click position of its own. Ported from WorldClockTray's
        // App.xaml.cs.
        _hotkeyManager = new GlobalHotkeyManager(_panelWindow.Hwnd, () => _panelWindow.Toggle(_trayIcon.TrayIconAnchor()));
        ApplyHotkeyLive(_settingsStore.LoadHotkey());

        // If a second ReCall.exe is launched later, treat it exactly
        // like a tray-icon left-click or the global hotkey: toggle the
        // panel open at the tray icon's position, instead of doing nothing
        // (that second process exits right after signaling -- see
        // SingleInstance and App()'s constructor).
        _singleInstance.WatchForShowRequests(_panelWindow.DispatcherQueue, () => _panelWindow.Toggle(_trayIcon.TrayIconAnchor()));

        // Silent, best-effort -- just populates UpdateChecker.LastResult so
        // it's already there (instead of the About tab having to trigger its
        // own check) if/when Settings > About happens to be opened later
        // this session. No popup/toast on its own; nothing to fail loudly
        // over if it errors (offline, GitHub down, etc). Ported from
        // Kronos's App.xaml.cs.
        if (_settingsStore.LoadCheckUpdatesOnStartup())
            _ = UpdateChecker.CheckAsync();
    }

    // White glyph for dark mode (visible against a dark taskbar), black
    // glyph for light mode -- same monochrome-follows-theme convention
    // Windows' own built-in tray icons use.
    private static string TrayIconPathFor(bool isDarkMode) =>
        AppContext.BaseDirectory + (isDarkMode ? "Assets\\tray-icon-white.ico" : "Assets\\tray-icon-black.ico");

    private void OnEffectiveThemeChanged(bool isDarkMode)
    {
        _trayIcon?.UpdateIcon(TrayIconPathFor(isDarkMode), isDarkMode);
    }

    private void ToggleStartOnStartup()
    {
        _settingsStore.SetStartOnStartup(!_settingsStore.IsStartOnStartupEnabled());
    }

    private void ApplyAndPersistTheme(ThemeMode mode)
    {
        _panelWindow?.ApplyAndPersistTheme(mode);
    }

    private void ApplyAndPersistCornerStyle(CornerStyle style)
    {
        _panelWindow?.ApplyAndPersistCornerStyle(style);
    }

    /// <summary>Registers/unregisters the OS-level shortcut to match the
    /// given settings, without touching the registry -- used both at
    /// startup (settings already saved, nothing to persist again) and from
    /// ApplyAndPersistHotkey below. Ported from WorldClockTray's App.xaml.cs.</summary>
    private bool ApplyHotkeyLive(HotkeySettings hotkey)
    {
        _hotkeyManager.Unregister();
        if (!hotkey.Enabled || !hotkey.HasKey) return true;
        return _hotkeyManager.Register(GlobalHotkeyManager.ToModifiers(hotkey), (uint)hotkey.Key);
    }

    /// <summary>Called live from Settings > Shortcuts. Returns whether the
    /// OS actually accepted the registration (false usually means another
    /// app already owns that exact combo), so Settings can surface that
    /// instead of silently pretending the shortcut works.</summary>
    private bool ApplyAndPersistHotkey(HotkeySettings hotkey)
    {
        var ok = ApplyHotkeyLive(hotkey);
        _settingsStore.SaveHotkey(hotkey);
        return ok;
    }

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settingsStore, _panelWindow!.Theme, _panelWindow.CornerStyle,
                _settingsStore.LoadHotkey(), ApplyAndPersistTheme, ApplyAndPersistCornerStyle, ApplyAndPersistHotkey);
            // SettingsWindow hides instantly on close (no white flash --
            // nothing on screen for DWM to animate), then actually destroys
            // itself half a second later while still hidden, so this fires
            // periodically during normal use, not just at app shutdown --
            // ShowAndActivate cancels that deferred destroy if Settings is
            // reopened first. See SettingsWindow.SetupWindow.
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        // ShowAndActivate undoes the Hide() from a previous close -- plain
        // Activate() alone doesn't.
        _settingsWindow.ShowAndActivate();
    }

    private void Quit()
    {
        _mouseWatcher.Uninstall();
        _hotkeyManager.Dispose();
        _monitor?.Dispose();
        _trayIcon?.Dispose();
        _settingsWindow?.ForceClose();
        _singleInstance.Dispose();
        Environment.Exit(0);
    }

    private async void OnClipboardChanged()
    {
        // Clipboard can throw if another app holds it briefly; ignore and wait for the next change.
        try
        {
            var view = Clipboard.GetContent();

            if (view.Contains(StandardDataFormats.Bitmap))
            {
                var imageRef = await view.GetBitmapAsync();
                using var stream = await imageRef.OpenReadAsync();

                // Grab the raw bytes first (needed later to re-copy the image
                // to the clipboard on click), then rewind and hand the same
                // stream to BitmapImage for display.
                var bytes = new byte[stream.Size];
                using (var reader = new Windows.Storage.Streams.DataReader(stream))
                {
                    await reader.LoadAsync((uint)stream.Size);
                    reader.ReadBytes(bytes);
                    reader.DetachStream(); // otherwise Dispose() below closes `stream` too
                }
                stream.Seek(0);

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                _panelWindow?.DispatcherQueue.TryEnqueue(() => _panelWindow.AddImage(bitmap, bytes));
            }
            else if (view.Contains(StandardDataFormats.Text))
            {
                var text = await view.GetTextAsync();
                _panelWindow?.DispatcherQueue.TryEnqueue(() => _panelWindow.AddText(text));
            }
        }
        catch
        {
            // ignore transient clipboard access failures
        }
    }
}
