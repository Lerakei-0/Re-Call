using ReCall.Models;
using ReCall.Native;
using ReCall.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace ReCall.UI;

/// <summary>
/// Settings window, opened from the tray icon's right-click menu. Themed to
/// match WorldClockTray's SettingsWindow: a real Mica backdrop, a custom
/// extended title bar, and CommunityToolkit's SettingsCard for each row
/// instead of loose XAML controls dropped straight into a StackPanel. Now
/// has a NavigationView (PaneDisplayMode="Top") + Appearance/Shortcuts
/// *Host panels, the same structure WorldClockTray's SettingsWindow uses,
/// added to make room for the Shortcuts tab below.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly Action<ThemeMode> _onApplyTheme;
    private readonly Action<CornerStyle> _onApplyCornerStyle;
    private readonly Func<HotkeySettings, bool> _onApplyHotkey;
    private IntPtr _hwnd;
    private AppWindow _appWindow = null!;
    private double _scale = 1.0;
    private bool _suppressThemeEvents;

    /// <summary>Set only by <see cref="ForceClose"/> (App.Quit's path).
    /// Everything else that would close this window -- the native titlebar
    /// X, Alt+F4 -- goes through AppWindow.Closing instead, which cancels
    /// and Hides rather than letting the real close happen. See SetupWindow
    /// for why.</summary>
    private bool _allowRealClose;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _deferredCloseTimer;

    /// <summary>True once the AppWindow.Closing handler has actually hidden
    /// this window at least once. ReattachMicaBackdrop exists to revive a
    /// MicaController whose composition target went dead from
    /// AppWindow.Hide() -- on a window that has never been hidden (e.g. the
    /// very first ShowAndActivate right after SetupWindow's first-time
    /// ApplyMicaBackdropWithFallback), the controller is still perfectly
    /// alive and reattaching just disposes a brand-new controller and
    /// replaces it with another one for no reason. Set true the moment
    /// _appWindow.Hide() actually runs, never reset back to false.</summary>
    private bool _hasBeenHiddenAtLeastOnce;

    private RadioButton _darkRadio = null!, _lightRadio = null!, _systemRadio = null!;
    private ComboBox _cornerStyleCombo = null!;

    // Shortcuts tab controls, ported from WorldClockTray's SettingsWindow.
    private ToggleSwitch _hotkeyEnabledToggle = null!;
    private Button _hotkeyRecordButton = null!;
    private TextBlock _hotkeyStatus = null!;
    private bool _recordingHotkey;
    private HotkeySettings _currentHotkey = new();

    // About tab controls, ported from Kronos's SettingsWindow.
    private TextBlock _aboutStatus = null!;
    private Button _checkNowButton = null!;

    public SettingsWindow(SettingsStore settingsStore, ThemeMode initialTheme, CornerStyle initialCornerStyle,
        HotkeySettings initialHotkey, Action<ThemeMode> onApplyTheme, Action<CornerStyle> onApplyCornerStyle,
        Func<HotkeySettings, bool> onApplyHotkey)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _onApplyTheme = onApplyTheme;
        _onApplyCornerStyle = onApplyCornerStyle;
        _onApplyHotkey = onApplyHotkey;
        _currentHotkey = initialHotkey.Clone();

        Title = "Re:Call Settings";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SetupWindow(initialTheme, initialCornerStyle);

        AppearanceHost.Content = BuildAppearanceSection(initialTheme, initialCornerStyle);
        ShortcutsHost.Content = BuildShortcutsTab();
        AboutHost.Content = BuildAboutTab();

        // Keep following Windows' own light/dark setting live while the
        // stored mode is System, same as the panel.
        SystemThemeService.Changed += OnSystemThemeChanged;
        Closed += (_, _) => SystemThemeService.Changed -= OnSystemThemeChanged;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        AppearanceHost.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsHost.Visibility = tag == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        AboutHost.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetupWindow(ThemeMode theme, CornerStyle cornerStyle)
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = global::Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        _scale = WindowEffects.ScaleFactor(_hwnd);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // Resizable + maximizable now, to match Kronos's SettingsWindow --
            // a min-size floor only means something if the window can
            // actually be resized in the first place.
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }
        // Widened from 560 -- EnforceMinimumSize below sets a 660px floor
        // (same rule Kronos's SettingsWindow uses), so the window can't
        // open narrower than what it would immediately clamp back up to.
        appWindow.Resize(new SizeInt32((int)(680 * _scale), (int)(460 * _scale)));

        // This is an ordinary app window (not the taskbar-adjacent panel),
        // so ThemeMode.System resolves against Windows' "Default app mode"
        // -- taskbarSurface: false -- matching WorldClockTray's
        // SettingsWindow vs ClockPanel split.
        var effectiveMode = SystemThemeService.Resolve(theme, taskbarSurface: false);

        WindowEffects.ApplyShadow(_hwnd);
        WindowEffects.ApplyCornerStyle(_hwnd, cornerStyle);
        WindowEffects.SetDarkMode(_hwnd, effectiveMode == ThemeMode.Dark);
        WindowEffects.RemoveTitleBarIcon(_hwnd);

        // No OverlappedPresenter-level API for a minimum size (that's
        // IsResizable/IsMaximizable above, which are real properties --
        // this isn't), so it's enforced natively instead. Nav's
        // PaneDisplayMode="Auto" collapses the 220px pane down to a 48px
        // icon-only rail once the window narrows past
        // CompactModeThresholdWidth="640" (set in XAML), but that alone
        // isn't a floor by itself: Card() forces every SettingsCard row to
        // stay horizontal (never stack label above content) regardless of
        // width, so past a certain point the header column itself gets
        // squeezed and labels start wrapping one character per line. 660 is
        // about where that starts happening -- same floor Kronos's
        // SettingsWindow uses, for the same reason.
        //
        // Maximize is intentionally allowed here (IsResizable/IsMaximizable
        // above are the supported way to do that) -- no
        // RemoveMaximizeButton/BlockMaximizeCommand calls needed anymore.
        WindowEffects.EnforceMinimumSize(_hwnd, (int)(660 * _scale), (int)(380 * _scale));

        // Must run before ApplyMicaBackdropWithFallback: it reads
        // root.ActualTheme at attach time to seed the backdrop's
        // SystemBackdropConfiguration.Theme, and RootGrid's fallback color
        // below needs to match the mode picked here too.
        if (Content is FrameworkElement root)
            root.RequestedTheme = effectiveMode == ThemeMode.Dark ? ElementTheme.Dark : ElementTheme.Light;

        // App.SyncTheme only sets RequestedTheme on the XAML root, which
        // every ThemeResource-based control reacts to automatically. The
        // in-process min/maximize/close buttons (drawn via AppWindow.TitleBar
        // because of ExtendsContentIntoTitleBar above) don't listen to that
        // on their own -- see WindowEffects.SyncTitleBarButtonColors for why.
        WindowEffects.SyncTitleBarButtonColors(this);

        // Covers the cold-start gap between this brand-new window becoming
        // visible and Mica actually painting its first frame. SettingsWindow
        // has no existing slide-animation suspend/resume mechanism to rely
        // on the way the panel does, since it's recreated fresh every time
        // it's reopened and shown (Activate()d) immediately.
        WindowEffects.ApplyMicaBackdropWithFallback(this, RootGrid, effectiveMode == ThemeMode.Dark);

        // Hide instantly on close (no DWM close animation for a Hide(),
        // hence no white flash), then let the *real* close/destroy happen
        // half a second later, while the window is already invisible.
        // Ported from Kronos's SettingsWindow: that gets an actual close
        // animation for free (whatever the OS would normally do) instead of
        // no animation at all, and it means AppWindow.Closing's real-close
        // branch below (which disposes the MicaController -- see
        // ApplyMicaBackdrop's window.Closed handler) actually fires
        // periodically instead of only at app shutdown, so resources aren't
        // held onto indefinitely just because Settings was opened once.
        //
        // ScheduleDeferredRealClose is cancelled in ShowAndActivate if the
        // user reopens Settings before the delay elapses -- reopening
        // reuses this same instance rather than racing its own teardown.
        _appWindow.Closing += (_, args) =>
        {
            if (_allowRealClose)
            {
                // Real close is actually happening (App.Quit's ForceClose) --
                // paint a flat background first so DWM's close animation
                // doesn't try to animate a live Mica surface out.
                var isDark = RootGrid.ActualTheme == ElementTheme.Dark;
                RootGrid.Background = new SolidColorBrush(isDark
                    ? Windows.UI.Color.FromArgb(255, 32, 32, 34)
                    : Windows.UI.Color.FromArgb(255, 255, 255, 255));
                return;
            }

            args.Cancel = true;
            _appWindow.Hide();
            _hasBeenHiddenAtLeastOnce = true;
            ScheduleDeferredRealClose();
        };
    }

    private FrameworkElement BuildAppearanceSection(ThemeMode theme, CornerStyle cornerStyle)
    {
        var root = new StackPanel();

        root.Children.Add(SectionHeader("Personalization"));

        var radios = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _darkRadio = new RadioButton { Content = "Dark", GroupName = "ThemeMode" };
        _lightRadio = new RadioButton { Content = "Light", GroupName = "ThemeMode" };
        _systemRadio = new RadioButton { Content = "System", GroupName = "ThemeMode" };

        _darkRadio.IsChecked = theme == ThemeMode.Dark;
        _lightRadio.IsChecked = theme == ThemeMode.Light;
        _systemRadio.IsChecked = theme == ThemeMode.System;

        radios.Children.Add(_darkRadio);
        radios.Children.Add(_lightRadio);
        radios.Children.Add(_systemRadio);
        root.Children.Add(Card("Theme", radios));

        root.Children.Add(new TextBlock
        {
            Text = "\"System\" follows Windows' own Light/Dark setting -- the panel " +
                   "matches the taskbar, this window matches your apps, same as Windows itself.",
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 8, 4, 0),
        });

        _cornerStyleCombo = new ComboBox { Width = 170 };
        _cornerStyleCombo.Items.Add("Not rounded");
        _cornerStyleCombo.Items.Add("Round");
        _cornerStyleCombo.Items.Add("Round (small)");
        _cornerStyleCombo.SelectedIndex = cornerStyle switch
        {
            CornerStyle.NotRounded => 0,
            CornerStyle.RoundSmall => 2,
            _ => 1,
        };
        root.Children.Add(Card("Corner style", _cornerStyleCombo));

        _darkRadio.Checked += (_, _) => OnThemeRadioChanged(ThemeMode.Dark);
        _lightRadio.Checked += (_, _) => OnThemeRadioChanged(ThemeMode.Light);
        _systemRadio.Checked += (_, _) => OnThemeRadioChanged(ThemeMode.System);
        _cornerStyleCombo.SelectionChanged += (_, _) => OnCornerStyleChanged();

        return root;
    }

    // --- Shortcuts tab -----------------------------------------------------------
    // Ported from WorldClockTray's SettingsWindow.xaml.cs.

    private FrameworkElement BuildShortcutsTab()
    {
        var root = new StackPanel();

        root.Children.Add(SectionHeader("Global hotkey"));
        root.Children.Add(new TextBlock
        {
            Text = "Toggle the panel open/closed from anywhere in Windows, even when another " +
                   "app has focus. The same shortcut both shows and hides it.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(4, 0, 0, 12),
        });

        _hotkeyEnabledToggle = new ToggleSwitch { OnContent = "", OffContent = "", IsOn = _currentHotkey.Enabled };
        root.Children.Add(Card("Enable global hotkey", _hotkeyEnabledToggle));

        _hotkeyRecordButton = new Button { Content = _currentHotkey.DisplayText(), Width = 220, HorizontalContentAlignment = HorizontalAlignment.Center };
        _hotkeyRecordButton.Click += OnHotkeyRecordClicked;
        _hotkeyRecordButton.KeyDown += OnHotkeyRecordKeyDown;
        _hotkeyRecordButton.LostFocus += (_, _) => { if (_recordingHotkey) StopRecordingHotkey(); };
        root.Children.Add(Card("Shortcut", _hotkeyRecordButton));

        var hint = new TextBlock
        {
            Text = "Click the button, then press a key combo including at least one modifier " +
                   "(Ctrl, Shift, Alt, or Win). Esc cancels.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            Margin = new Thickness(4, 4, 0, 0),
            FontSize = 12,
        };
        root.Children.Add(hint);

        _hotkeyStatus = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_hotkeyStatus);

        _hotkeyEnabledToggle.Toggled += (_, _) => OnHotkeyChanged();

        return root;
    }

    private void OnHotkeyRecordClicked(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        _hotkeyRecordButton.Content = "Press a key combo\u2026";
        _hotkeyRecordButton.Focus(FocusState.Programmatic);
    }

    private void StopRecordingHotkey()
    {
        _recordingHotkey = false;
        _hotkeyRecordButton.Content = _currentHotkey.DisplayText();
    }

    /// <summary>Reads live modifier key state via InputKeyboardSource rather
    /// than KeyRoutedEventArgs.KeyStatus (which only reports the single key
    /// that triggered this event, not which modifiers are concurrently
    /// held) -- the same approach a hotkey-recorder textbox needs regardless
    /// of framework, since "Ctrl+Shift+V" only exists as three keys held at
    /// once, not as a single event.</summary>
    private void OnHotkeyRecordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;

        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            return; // a bare modifier isn't a complete combo yet -- keep waiting

        if (key == VirtualKey.Escape)
        {
            StopRecordingHotkey();
            return;
        }

        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        var win = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(CoreVirtualKeyStates.Down)
            || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(CoreVirtualKeyStates.Down);

        if (!ctrl && !shift && !alt && !win)
        {
            _hotkeyStatus.Text = "Include at least one modifier key (Ctrl, Shift, Alt, or Win).";
            return;
        }

        _currentHotkey = new HotkeySettings
        {
            Enabled = _hotkeyEnabledToggle.IsOn,
            Ctrl = ctrl,
            Shift = shift,
            Alt = alt,
            Win = win,
            Key = key,
        };
        StopRecordingHotkey();
        OnHotkeyChanged();
    }

    private void OnHotkeyChanged()
    {
        _currentHotkey.Enabled = _hotkeyEnabledToggle.IsOn;
        _hotkeyRecordButton.Content = _currentHotkey.DisplayText();

        if (_currentHotkey.Enabled && !_currentHotkey.HasKey)
        {
            _hotkeyStatus.Text = "Record a shortcut above to enable it.";
            return;
        }

        var ok = _onApplyHotkey(_currentHotkey);
        _hotkeyStatus.Text = !_currentHotkey.Enabled
            ? "Global hotkey disabled."
            : ok
                ? $"Global hotkey set to {_currentHotkey.DisplayText()}."
                : $"Couldn't register {_currentHotkey.DisplayText()}; it may already be used by another app.";
    }

    // --- About tab -----------------------------------------------------------
    // Ported from Kronos's SettingsWindow.xaml.cs.

    private FrameworkElement BuildAboutTab()
    {
        var root = new StackPanel();

        root.Children.Add(SectionHeader("About"));

        var githubGlyph = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry),
                "M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0 0 16 8c0-4.42-3.58-8-8-8z"),
        };

        var githubButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Viewbox
                    {
                        Width = 14,
                        Height = 14,
                        Stretch = Stretch.Uniform,
                        Child = githubGlyph,
                    },
                    new TextBlock { Text = "Lerakei-0", VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        // Shape.Fill isn't an inherited DP the way TextBlock.Foreground is, and a
        // static Application.Current.Resources[...] lookup ignores the active
        // theme, so bind directly to the button's own (theme-correct) Foreground
        // instead -- this tracks light/dark switches the same way the label does.
        githubGlyph.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.FillProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = githubButton,
            Path = new PropertyPath("Foreground"),
        });
        githubButton.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://github.com/Lerakei-0/Re-Call"));
        root.Children.Add(Card("Re:Call", $"Version {UpdateChecker.CurrentVersion}", githubButton));

        var checkOnStartupToggle = new ToggleSwitch
        {
            OnContent = "",
            OffContent = "",
            IsOn = _settingsStore.LoadCheckUpdatesOnStartup(),
        };
        checkOnStartupToggle.Toggled += (_, _) =>
            _settingsStore.SaveCheckUpdatesOnStartup(checkOnStartupToggle.IsOn);
        root.Children.Add(Card("Check for updates on startup",
            "Automatically check for new versions when the app starts", checkOnStartupToggle));

        _checkNowButton = new Button { Content = "Check Now" };
        _checkNowButton.Click += (_, _) => _ = OnCheckForUpdatesAsync();
        root.Children.Add(Card("Check for updates", "Click to check for new versions", _checkNowButton));

        _aboutStatus = new TextBlock { Margin = new Thickness(4, 8, 0, 0), Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_aboutStatus);

        // If a startup check already ran silently this session (see
        // App.OnLaunched), show that result immediately instead of leaving
        // the tab blank until the button is clicked.
        if (UpdateChecker.LastResult is { } startupResult)
            ShowUpdateResult(startupResult);

        return root;
    }

    private async System.Threading.Tasks.Task OnCheckForUpdatesAsync()
    {
        _checkNowButton.IsEnabled = false;
        _aboutStatus.Text = "Checking for updates\u2026";
        try
        {
            var result = await UpdateChecker.CheckAsync();
            ShowUpdateResult(result);
        }
        finally
        {
            _checkNowButton.IsEnabled = true;
        }
    }

    private void ShowUpdateResult(UpdateChecker.Result result)
    {
        _aboutStatus.Text = result switch
        {
            { CheckSucceeded: false } => $"Couldn't check for updates: {result.Error}",
            { UpdateAvailable: true } => $"Version {result.LatestVersion} is available (you have {UpdateChecker.CurrentVersion}).",
            _ => $"You're on the latest version ({UpdateChecker.CurrentVersion}).",
        };
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["SettingsSectionHeaderStyle"],
        Margin = new Thickness(4, 0, 0, 8),
    };

    private static SettingsCard Card(string header, UIElement content) => Card(header, null, content);

    private static SettingsCard Card(string header, string? description, UIElement? content)
    {
        var card = new SettingsCard
        {
            Header = header,
            // SettingsCard.Description is typed as non-nullable `object` even
            // though the control explicitly supports (and expects) null to
            // collapse the description row -- the package just isn't
            // nullable-annotated here, so this isn't an actual bug.
            Description = description!,
            Content = content,
            Margin = new Thickness(0, 0, 0, 2),
        };

        // SettingsCard auto-wraps Content below Header once the two don't
        // fit side-by-side, and reserves a taller combined area for that
        // stacked state. This row is meant to always stay compact/single-line,
        // so drop the wrap thresholds to 0 to force horizontal layout
        // regardless of window width.
        card.Resources["SettingsCardWrapThreshold"] = 0.0;
        card.Resources["SettingsCardWrapNoIconThreshold"] = 0.0;

        return card;
    }

    private void OnThemeRadioChanged(ThemeMode mode)
    {
        if (_suppressThemeEvents) return;
        ApplyThemeCore(mode);
        _onApplyTheme(mode); // App applies+persists to the panel's own store
    }

    private void OnCornerStyleChanged()
    {
        var style = _cornerStyleCombo.SelectedIndex switch
        {
            0 => CornerStyle.NotRounded,
            2 => CornerStyle.RoundSmall,
            _ => CornerStyle.Round,
        };
        WindowEffects.ApplyCornerStyle(_hwnd, style);
        _onApplyCornerStyle(style); // App applies+persists to the panel's own store
    }

    private void OnSystemThemeChanged()
    {
        if (_systemRadio is null || _systemRadio.IsChecked != true) return;
        DispatcherQueue.TryEnqueue(() => ApplyThemeCore(ThemeMode.System));
    }

    /// <summary>Re-themes this window only (RequestedTheme + native frame
    /// tint); does not touch the panel or persist anything -- callers that
    /// need that call ApplyAndPersistTheme through the App-level callback.
    /// Mica and the title bar button colors both follow root.RequestedTheme
    /// automatically via the ActualThemeChanged subscriptions wired up in
    /// SetupWindow, so nothing else needs to run here on every change.</summary>
    private void ApplyThemeCore(ThemeMode mode)
    {
        var effectiveMode = SystemThemeService.Resolve(mode, taskbarSurface: false);
        if (Content is FrameworkElement root)
            root.RequestedTheme = effectiveMode == ThemeMode.Dark ? ElementTheme.Dark : ElementTheme.Light;
        WindowEffects.SetDarkMode(_hwnd, effectiveMode == ThemeMode.Dark);
    }

    /// <summary>Called by App after the theme changes elsewhere (currently
    /// nothing else changes it, but this keeps the radios in sync if that
    /// ever grows, e.g. a future tray-menu quick toggle).</summary>
    public void RefreshTheme(ThemeMode mode)
    {
        _suppressThemeEvents = true;
        _darkRadio.IsChecked = mode == ThemeMode.Dark;
        _lightRadio.IsChecked = mode == ThemeMode.Light;
        _systemRadio.IsChecked = mode == ThemeMode.System;
        _suppressThemeEvents = false;
        ApplyThemeCore(mode);
    }

    /// <summary>Brings a hidden SettingsWindow back, the same two-call
    /// sequence Kronos's SettingsWindow uses (_appWindow.Show() then
    /// Activate()) -- Activate() alone doesn't undo AppWindow.Hide().
    /// App.OpenSettings calls this on every open now that the window is
    /// kept alive rather than recreated (see SetupWindow's AppWindow.Closing
    /// handler). Also plays PlayOpenAnimation -- see its remarks for why
    /// that's a content-level animation rather than an OS-level one.</summary>
    public void ShowAndActivate()
    {
        // A reopen within the half-second window means the previous close
        // never actually needs to happen -- this is the same instance
        // coming back, not a race with its own teardown.
        _deferredCloseTimer?.Stop();

        // AppWindow.Hide() (the Closing handler's cancel-and-hide path)
        // leaves the previously-attached MicaController's composition
        // target in a dead state -- re-adding the *same* controller as a
        // target isn't enough to revive it; the controller itself has to
        // be thrown away and replaced, which is just as asynchronous as
        // the very first attach. Only do this if the window has genuinely
        // been hidden before -- the very first ShowAndActivate after
        // construction has a perfectly live controller from SetupWindow's
        // ApplyMicaBackdropWithFallback call, so reattaching there would be
        // pure churn: disposing a controller that was never dead and
        // replacing it with a fresh one for no benefit.
        //
        // Painting the fallback cover *before* Show() (rather than only
        // after, inside the reattach) is what actually avoids the white
        // flash -- otherwise the window becomes visible via Show() with
        // nothing covering the async gap until Mica's replacement
        // controller renders its first frame. See
        // WindowEffects.ReattachMicaBackdropWithFallback.
        if (_hasBeenHiddenAtLeastOnce)
        {
            var isDark = RootGrid.ActualTheme == ElementTheme.Dark;
            WindowEffects.ReattachMicaBackdropWithFallback(this, RootGrid, isDark);
        }

        _appWindow.Show();
        PlayOpenAnimation();
        Activate();
    }

    /// <summary>Stands in for a real window-open animation, which isn't
    /// safely available here: AppWindow.Show() has no animation hook of its
    /// own (unlike SW_RESTORE from minimized, which rides the taskbar's own
    /// zoom), and the one Win32 API that *can* force one -- AnimateWindow()
    /// -- predates DirectComposition/Mica-backed windows. It works by
    /// grabbing a static bitmap of the window and blending/sliding that,
    /// which doesn't understand a live, continuously-updating Mica surface
    /// underneath.
    ///
    /// This sidesteps that: only the XAML content on top of Mica animates,
    /// so Mica itself keeps rendering normally throughout and the HWND
    /// itself is never touched. Ported from Kronos's SettingsWindow, same
    /// BackEase overshoot on a ScaleTransform.</summary>
    private void PlayOpenAnimation()
    {
        var scaleTransform = (ScaleTransform)Nav.RenderTransform;
        scaleTransform.ScaleX = 0.96;
        scaleTransform.ScaleY = 0.96;
        Nav.Opacity = 0;

        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };

        var scaleXAnim = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(scaleXAnim, scaleTransform);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");

        var scaleYAnim = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(scaleYAnim, scaleTransform);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");

        var fadeAnim = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(160) };
        Storyboard.SetTarget(fadeAnim, Nav);
        Storyboard.SetTargetProperty(fadeAnim, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(scaleXAnim);
        storyboard.Children.Add(scaleYAnim);
        storyboard.Children.Add(fadeAnim);
        storyboard.Begin();
    }

    /// <summary>The one path that's allowed to actually destroy this
    /// window's HWND -- either app shutdown (App.Quit) or, ordinarily, the
    /// deferred timer scheduled from AppWindow.Closing half a second after
    /// the window was hidden. Idempotent: both of those can reach this
    /// (e.g. the app quits while a deferred close is still pending), and
    /// Close()-ing an already-closing window isn't something to repeat.</summary>
    public void ForceClose()
    {
        if (_allowRealClose) return;
        _allowRealClose = true;
        _deferredCloseTimer?.Stop();
        Close();
    }

    /// <summary>Half a second after the window is hidden, lets the real
    /// close/destroy proceed -- see the AppWindow.Closing comment in
    /// SetupWindow for why that's safe (nothing on screen to glitch) and
    /// worth doing at all (frees the MicaController instead of holding it
    /// for the rest of the app's run). Cancelled by ShowAndActivate if the
    /// window is reopened before this fires.</summary>
    private void ScheduleDeferredRealClose()
    {
        _deferredCloseTimer?.Stop();
        _deferredCloseTimer = DispatcherQueue.CreateTimer();
        _deferredCloseTimer.Interval = TimeSpan.FromMilliseconds(500);
        _deferredCloseTimer.IsRepeating = false;
        _deferredCloseTimer.Tick += (t, _) =>
        {
            t.Stop();
            ForceClose();
        };
        _deferredCloseTimer.Start();
    }
}
