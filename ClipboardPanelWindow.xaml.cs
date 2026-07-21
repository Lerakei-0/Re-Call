using ReCall.Models;
using ReCall.Native;
using ReCall.Services;
using ReCall.UI;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace ReCall;

/// <summary>
/// The sliding panel. Positioning/animation are ported from WorldClockTray's
/// ClockPanel: the panel docks against whichever screen edge the anchor
/// point (tray icon click / cursor) is actually closest to, instead of
/// always assuming the taskbar is at the bottom. Both the window itself
/// (native SetWindowPos+DwmFlush tween, see WindowAnimator) and its content
/// (a separate XAML slide+fade, see TriggerContentAnimation) enter from that
/// same resolved edge, so a taskbar on the right slides the panel in from
/// the right, matching WorldClockTray exactly.
/// </summary>
public sealed partial class ClipboardPanelWindow : Window
{
    private const int MaxItems = 25;
    private const double PanelWidthDip = 340;

    // Height is no longer fixed: the panel grows to fit its content (down to
    // this floor, so it never looks cramped when empty/near-empty) and up to
    // this fraction of the current monitor's work area, beyond which it holds
    // at that cap and the ListView's own built-in ScrollViewer takes over.
    // See UpdateWindowHeight.
    private const double MinPanelHeightDip = 220;
    private const double MaxPanelHeightFraction = 0.75;
    private const double ExtraFitHeightDip = 10;

    private const double EdgeMarginDip = 10;
    private const int SlideDurationMs = 280;
    private const double ContentSlideOffsetDip = 14;
    private const int ContentSlideDurationMs = 320;
    private const int ContentFadeDurationMs = 150;
    private const double ReopenDebounceSeconds = 0.25;
    // How long a content-driven height change (new item pasted, Clear All,
    // search narrowing the list, ...) takes to tween to its new size while
    // the panel is already open. Deliberately shorter than SlideDurationMs
    // -- this isn't the panel entering/leaving the screen, just settling to
    // a new height, so it should read as quick and responsive rather than
    // as its own entrance.
    private const int HeightResizeDurationMs = 200;

    private DateTime _lastCloseTimeUtc = DateTime.MinValue;

    public ObservableCollection<ClipboardItem> Items { get; } = new();

    /// <summary>What PinnedList binds to: the subset of Items where
    /// IsPinned is true, further narrowed by whatever the search box/
    /// content filter currently restrict to. Kept in sync by ApplyFilter,
    /// same as UnpinnedItems below -- together the two collections used to
    /// be one combined FilteredItems feeding a single ListView; splitting
    /// them is what lets PinnedList sit in its own sticky section (see the
    /// XAML) instead of scrolling away with the rest of the history, while
    /// still sharing all the same filter/search logic.</summary>
    public ObservableCollection<ClipboardItem> PinnedItems { get; } = new();

    /// <summary>What HistoryList (the scrollable list) binds to: the
    /// subset of Items where IsPinned is false, same filter/search
    /// treatment as PinnedItems. Kept as separate collections, rather than
    /// filtering Items in place, so pinning/trim/persistence logic
    /// elsewhere never has to know or care that a search is active --
    /// TogglePin just flips IsPinned and calls ApplyFilter, which moves
    /// the item from one collection to the other for free.</summary>
    public ObservableCollection<ClipboardItem> UnpinnedItems { get; } = new();

    public bool IsPanelVisible { get; private set; }

    /// <summary>Panel-level pin (distinct from ClipboardItem.IsPinned, which
    /// pins individual history entries to the top of the list). Toggled by
    /// PinPanelButton next to "Clear all" -- see OnPinPanelClicked. While
    /// true: MouseHookWatcher's click-away check skips SlideOut entirely
    /// (see the IsPanelPinned check there), and OnItemClicked's
    /// copy-then-close skips HidePanel so the panel stays open for
    /// copying/pasting several items in a row. "Always on top" itself needs
    /// no separate flag to enforce -- ApplyTopmostBelowTaskbar already runs
    /// on every SlideIn, and pinning just means SlideOut never fires to
    /// undo it while the panel is open.</summary>
    public bool IsPanelPinned { get; private set; }

    public IntPtr Hwnd => _hwnd;

    /// <summary>Bound (x:Bind function call, Mode=OneWay) to the "Nothing
    /// in your clipboard" label's Visibility in the XAML -- re-evaluates
    /// automatically on every PinnedItems/UnpinnedItems mutation since
    /// ObservableCollection raises PropertyChanged("Count") itself, no
    /// extra wiring needed. Driven by the two filtered collections rather
    /// than Items so the label also appears when a search matches
    /// nothing.</summary>
    public Visibility EmptyStateVisibility(int pinnedCount, int unpinnedCount) =>
        pinnedCount == 0 && unpinnedCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Paired with EmptyStateVisibility above (same x:Bind call
    /// site conditions, see the XAML): distinguishes an actually-empty
    /// history from a search that just didn't match anything.</summary>
    public string EmptyStateText(int pinnedCount, int unpinnedCount, int itemCount) =>
        itemCount == 0 ? "Nothing in your clipboard" : "No matching items";

    /// <summary>Bound to the divider Border between PinnedList and
    /// HistoryList: visible only once there's actually a pinned item above
    /// it AND something unpinned below to separate it from -- matches what
    /// the old per-row ShowPinnedSeparatorBelow used to guard against
    /// (never showing when there are no pinned items, or when every
    /// visible item is pinned), just evaluated once for the whole panel
    /// now that the two groups are separate ListViews instead of one.</summary>
    public Visibility PinnedDividerVisibility(int pinnedCount, int unpinnedCount) =>
        pinnedCount > 0 && unpinnedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>User-created folders, shown as chips in the sticky folders
    /// bar (see ClipboardPanelWindow.xaml, Grid.Row="3") until one is
    /// opened, at which point that bar is replaced by a back-arrow header
    /// for that folder's page instead (see EnterFolder/UpdateFolderBarVisibility).
    /// Loaded once at startup (LoadFolders/LoadHistoryAsync, called from the
    /// constructor) and mutated via CreateFolderViaDialogAsync/
    /// RenameFolderAsync/DeleteFolderAsync below -- each of those also calls
    /// PersistAll to persist the change.</summary>
    public ObservableCollection<ClipboardFolder> Folders { get; } = new();

    /// <summary>Which folder's page is currently being shown, or null while
    /// looking at the main clipboard history. Set by EnterFolder (folder
    /// chip click) and cleared by ExitFolder (back-arrow click) -- see both
    /// below. This is real in-place navigation, not a filter: PinnedList/
    /// HistoryList's underlying source (CurrentSource, right below) swaps
    /// over to this folder's own Items collection instead of Items being
    /// narrowed down to a subset.</summary>
    private ClipboardFolder? _currentFolder;

    /// <summary>Whichever collection PinnedItems/UnpinnedItems are
    /// currently being derived from by ApplyFilter -- Items (the main
    /// history) while _currentFolder is null, or that folder's own Items
    /// otherwise. Every mutation that used to hard-code Items (TogglePin,
    /// OnRemoveItemClicked, OnClearAllClicked, the pinned-list drag
    /// handler) now goes through this instead, so the same code works
    /// identically whether you're looking at the main list or a folder
    /// page -- no separate per-folder copies of that logic needed.</summary>
    private ObservableCollection<ClipboardItem> CurrentSource => _currentFolder?.Items ?? Items;

    public ThemeMode Theme { get; private set; }
    public CornerStyle CornerStyle { get; private set; }
    public bool IsDarkMode { get; private set; }

    /// <summary>Fires whenever the panel's *resolved* light/dark state
    /// changes -- covers manual theme picks (Settings) and, when
    /// Theme == System, live follow-up of Windows' own setting. Used by
    /// App to swap the tray icon between its white/black monochrome
    /// variants so it stays visible against either taskbar.</summary>
    public event Action<bool>? EffectiveThemeChanged;

    private readonly SettingsStore _settingsStore;
    private readonly HistoryStore _historyStore;

    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly double _scale;
    private string _lastEffectiveEdge = "right";

    // Anchor from the most recent SlideIn (or, before the panel has ever been
    // shown, the window's initial OS-assigned position) -- UpdateWindowHeight
    // needs this to know which monitor's work area to measure the 75% cap
    // against, and to re-dock the panel if a height change happens while it's
    // already open.
    private PointInt32 _lastAnchor;
    private bool _sizingInProgress;

    // ApplyFilter mutates PinnedItems/UnpinnedItems one item at a time
    // (inserts/removes/moves in a loop) -- e.g. restoring the whole saved
    // history at launch,
    // or a cleared search re-inserting everything that was filtered out.
    // Reacting to every single one of those synchronously would measure
    // Container mid-loop, before the ListView has actually realized the
    // containers for items just (re)inserted, undercounting the natural
    // height. This coalesces a whole burst into one resize, deferred via
    // DispatcherQueue so it runs after that burst -- and the layout pass it
    // causes -- has fully settled.
    private bool _heightUpdateQueued;

    // Window itself slides via WindowAnimator (native SetWindowPos, see
    // AnimateTo). Container gets its own slide+fade, kicked off a beat
    // before the window tween finishes (WindowAnimator's onNearComplete),
    // so content reads as arriving alongside the panel instead of trailing
    // it or arriving as one rigid block.
    private readonly TranslateTransform _contentTransform = new();
    private Storyboard? _contentStoryboard;
    private CancellationTokenSource? _animCts;

    // Height-resize tween (see AnimateHeightTo), a separate animation
    // channel from _animCts's open/close slide since both can be in flight
    // at different times but must never run concurrently against the same
    // hwnd -- AnimateTo cancels _resizeAnimCts on every open/close, and
    // UpdateWindowHeight only starts a resize tween while _slideInProgress
    // is false.
    private CancellationTokenSource? _resizeAnimCts;
    private bool _slideInProgress;

    // Auto-hide for HistoryList's built-in vertical scrollbar: found once
    // via the visual tree in OnHistoryListLoaded (ListView doesn't expose it
    // directly), then faded out after ScrollBarHideDelay of no scrolling and
    // faded back in immediately on the next scroll or on hovering it
    // directly. See ShowScrollBar/ScheduleScrollBarHide below.
    private static readonly TimeSpan ScrollBarHideDelay = TimeSpan.FromSeconds(2);
    private ScrollBar? _historyScrollBar;
    private DispatcherTimer? _scrollBarHideTimer;
    private Storyboard? _scrollBarFadeStoryboard;

    // Jump-to-top button: shown once HistoryList's ScrollViewer (same one
    // found in OnHistoryListLoaded, above) is scrolled down more than
    // ScrollToTopThreshold. Faded rather than just toggling Visibility, to
    // match ShowScrollBar/FadeScrollBarTo's existing polish.
    private const double ScrollToTopThreshold = 80;
    private ScrollViewer? _historyScrollViewer;
    private Storyboard? _scrollToTopFadeStoryboard;

    /// <summary>What PinnedItems/UnpinnedItems are currently restricted to
    /// beyond SearchBox's text query (which stays independently applied on top --
    /// see ApplyFilter): a content type, pinned-only, or no restriction at
    /// all. Never resets automatically; it's a standing filter, same as the
    /// search text, until the user picks a different option from
    /// FilterButton's menu.</summary>
    private enum ContentFilterMode { All, Text, Image, Pinned, Colors, Gradients, ColorsAndGradients }
    private ContentFilterMode _contentFilter = ContentFilterMode.All;

    public ClipboardPanelWindow(SettingsStore settingsStore, HistoryStore historyStore)
    {
        this.InitializeComponent();
        Container.RenderTransform = _contentTransform;

        // Items starts empty and is populated asynchronously (LoadHistory)
        // plus mutated throughout the panel's lifetime (new copies, pin,
        // remove, clear all). Subscribing here means every one of those
        // mutations re-runs the search filter, so PinnedItems/UnpinnedItems
        // -- what the two ListViews actually show -- never drift out of
        // sync with it.
        Items.CollectionChanged += (_, _) => ApplyFilter();

        // Whatever actually changes what's on screen -- new/removed items
        // in either list, or the search box narrowing/widening the visible
        // set -- should re-run the fit-to-content sizing. Scheduled rather
        // than called directly: see _heightUpdateQueued above.
        PinnedItems.CollectionChanged += (_, _) => ScheduleWindowHeightUpdate();
        UnpinnedItems.CollectionChanged += (_, _) => ScheduleWindowHeightUpdate();

        _settingsStore = settingsStore;
        _historyStore = historyStore;
        Theme = settingsStore.LoadThemeMode();
        CornerStyle = settingsStore.LoadCornerStyle();

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = global::Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _scale = WindowEffects.ScaleFactor(_hwnd);

        // Seeded from wherever the OS put the not-yet-shown window; refined
        // to the real click point on the first SlideIn. Good enough for
        // WorkAreaPx's DisplayArea.GetFromPoint(..., Nearest) in the
        // meantime, which is all the initial (empty-state) sizing needs.
        _lastAnchor = new PointInt32(_appWindow.Position.X, _appWindow.Position.Y);

        var widthPx = (int)Math.Round(PanelWidthDip * _scale);
        var heightPx = (int)Math.Round(MinPanelHeightDip * _scale);
        _appWindow.Resize(new SizeInt32(widthPx, heightPx));
        _appWindow.IsShownInSwitchers = false;

        // WS_CAPTION stays (matching WorldClockTray's ClockPanel): DWM only
        // auto-tints a window's border via DWMWA_USE_IMMERSIVE_DARK_MODE
        // (set in ApplyThemeCore below) when the window still has its
        // native caption/frame. Stripping the caption entirely via
        // SetBorderAndTitleBar(false, false) makes DWM fall back to the
        // borderless "sheet of glass" hairline instead, which does NOT
        // follow DWMWA_USE_IMMERSIVE_DARK_MODE and stays Windows' default
        // light-gray/white in dark mode regardless of DWMWA_BORDER_COLOR --
        // that's the "white border in dark theme" bug. ExtendsContentIntoTitleBar
        // + a collapsed title bar height gets the same fully-chromeless
        // look without giving up the native caption DWM needs to tint.
        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, true);
        }

        WindowEffects.HideFromTaskbar(_hwnd);
        WindowEffects.ApplyShadow(_hwnd);
        WindowEffects.ApplyCornerStyle(_hwnd, CornerStyle);

        // Must run before ApplyAcrylicBackdrop and its return value passed
        // straight in below: the panel is never Activate()d until the first
        // SlideIn, so root.ActualTheme hasn't caught up with RequestedTheme
        // yet at this point -- reading it here would seed the backdrop for
        // the (light) default and only self-correct once ActualThemeChanged
        // eventually fires, which is exactly the "panel opens in light mode"
        // symptom. Passing the already-known dark/light value directly
        // sidesteps that lag entirely.
        var isDark = ApplyThemeCore(Theme);

        // Backdrop is applied last: earlier style flips above can trigger a
        // frame invalidation that would otherwise drop the composition
        // target before it's had a chance to render.
        //
        // thin: false (DesktopAcrylicKind.Base) -- more tint/opacity than
        // Thin, less desktop bleed-through. Thin was tried first (matching
        // the Notification-Center-style transparency of a transient flyout)
        // but read as too busy/washed-out against colorful wallpapers, so
        // this panel now uses the same stronger, more opaque Base kind as
        // permanent app surfaces.
        // WriteableBitmap's pixel data maps 1:1 to DIPs when shown through an
        // Image (Stretch="None"), not to physical pixels -- so a bitmap
        // deliberately sized in physical pixels (widthPx/heightPx above)
        // still renders that many DIPs on screen, inflating every grain cell
        // by _scale. Countering it with a 1/_scale render-transform is what
        // actually gets one generated pixel per physical screen pixel --
        // the finest grain the display can show, rather than grain that's
        // artificially chunky at any DPI above 100%.
        NoiseOverlay.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        NoiseOverlay.RenderTransform = new ScaleTransform { ScaleX = 1.0 / _scale, ScaleY = 1.0 / _scale };

        WindowEffects.ApplyAcrylicBackdrop(this, isDark, thin: false);
        WindowEffects.RegenerateNoise(NoiseOverlay, widthPx, heightPx);

        // Warm-up: one real Show/Activate cycle, far off-screen, before the
        // panel is ever meant to be visible. The comment above (on
        // ApplyThemeCore) already notes the panel isn't normally Activate()d
        // until the first SlideIn -- but WinUI's virtualizing ListView only
        // finishes wiring up its container generator once the window has
        // actually been shown/composited for real at least once. Without
        // this, UpdateWindowHeight's Measure call on the very first SlideIn
        // undercounts the natural height (containers for the restored
        // history aren't realized yet), even though every SlideIn after
        // that -- once the window really has been shown once -- measures
        // correctly. Doing it here, synchronously and far off-screen, means
        // it's invisible: shown and hidden again within the same tick,
        // long before the tray icon is ever clicked.
        var warmUpPos = new PointInt32(_appWindow.Position.X - 30000, _appWindow.Position.Y - 30000);
        _appWindow.Move(warmUpPos);
        _appWindow.Show();
        this.Activate();
        Container.UpdateLayout();
        _appWindow.Hide();

        // Keep following Windows' own light/dark setting live while
        // Theme == System, same as WorldClockTray's ClockPanel.
        SystemThemeService.Changed += OnSystemThemeChanged;

        // Click-away hiding is owned by MouseHookWatcher (installed in
        // App.OnLaunched), matching WorldClockTray's ClockPanel -- a global
        // low-level mouse hook rather than Window.Activated/Deactivated,
        // which can miss or lag clicks for a tray-icon-launched window that
        // never gets real OS foreground activation.

        // Synchronous and cheap (just names/ids, no image bytes) -- unlike
        // LoadHistoryAsync below, there's no need to defer this off the
        // constructor. Loaded before history so folder chips already exist
        // (and the folders bar can size itself) before the panel is ever
        // shown; each folder's actual item content streams in afterwards,
        // alongside the main history, in LoadHistoryAsync.
        foreach (var record in _historyStore.LoadFolders())
        {
            var folder = new ClipboardFolder
            {
                Id = record.Id,
                Name = record.Name,
                Color = string.IsNullOrEmpty(record.Color) ? ClipboardFolder.DefaultColor : record.Color,
            };
            SubscribeFolderItems(folder);
            Folders.Add(folder);
        }
        UpdateFolderBarVisibility();
        Folders.CollectionChanged += (_, _) => UpdateFolderBarVisibility();

        // Fire-and-forget: repopulates Items (and every folder's Items)
        // from disk once decoded. Runs after the window is otherwise fully
        // constructed and hidden, so there's nothing for the user to see
        // mid-load; items simply appear in the (still-closed) panel a
        // moment after startup.
        _ = LoadHistoryAsync();
    }

    /// <summary>Hooks a folder's Items so that, while its page happens to
    /// be the one currently on screen, any mutation (move/add-copy in,
    /// remove, pin) re-runs ApplyFilter the same way Items.CollectionChanged
    /// does for the main history -- see the constructor's Items
    /// subscription. Called once per folder, whether it's restored at
    /// startup (above) or created fresh (CreateFolderViaDialogAsync).</summary>
    private void SubscribeFolderItems(ClipboardFolder folder)
    {
        folder.Items.CollectionChanged += (_, _) =>
        {
            if (_currentFolder == folder) ApplyFilter();
        };
    }

    /// <summary>Restores persisted history (and every folder's own items)
    /// from HistoryStore, decoding each image item's bytes back into a
    /// BitmapImage for display. Best-effort per item -- a record whose
    /// image file went missing is just skipped rather than aborting the
    /// whole load.</summary>
    private async Task LoadHistoryAsync()
    {
        foreach (var record in _historyStore.Load())
        {
            var item = await RecordToItemAsync(record);
            if (item is not null) Items.Add(item);
        }

        foreach (var folderRecord in _historyStore.LoadFolders())
        {
            var folder = Folders.FirstOrDefault(f => f.Id == folderRecord.Id);
            if (folder is null) continue; // shouldn't happen -- same file the constructor already read

            foreach (var itemRecord in folderRecord.Items)
            {
                var item = await RecordToItemAsync(itemRecord);
                if (item is not null) folder.Items.Add(item);
            }
        }
    }

    /// <summary>Turns one manifest row into a live ClipboardItem -- shared
    /// by both the main history and every folder's items above, since a
    /// folder item is a full, independent ClipboardItem in its own right
    /// (see Models/ClipboardFolder.cs), stored with the exact same Record
    /// shape. Returns null for a text record with no text, or an image
    /// record whose backing file has gone missing.</summary>
    private async Task<ClipboardItem?> RecordToItemAsync(HistoryStore.Record record)
    {
        if (record.Type == nameof(ClipboardItemType.Text) && record.Text is not null)
        {
            return new ClipboardItem
            {
                Id = record.Id,
                Type = ClipboardItemType.Text,
                Text = record.Text,
                Timestamp = record.Timestamp,
                IsPinned = record.IsPinned,
            };
        }

        if (record.Type == nameof(ClipboardItemType.Image) && record.ImageFile is not null)
        {
            var bytes = _historyStore.ReadImageBytes(record.ImageFile);
            if (bytes is null) return null; // file went missing -- skip, don't break the rest of the load

            var bitmap = await BytesToBitmapImageAsync(bytes);
            return new ClipboardItem
            {
                Id = record.Id,
                Type = ClipboardItemType.Image,
                Image = bitmap,
                ImageBytes = bytes,
                ImageFile = record.ImageFile,
                Timestamp = record.Timestamp,
                IsPinned = record.IsPinned,
            };
        }

        return null;
    }

    /// <summary>Decodes raw bytes into a displayable BitmapImage. Shared by
    /// history restore; the click-to-copy path in OnItemClicked below needs
    /// the same bytes as a stream but hands them straight to the clipboard
    /// instead of a BitmapImage, so it doesn't go through this.</summary>
    private static async Task<BitmapImage> BytesToBitmapImageAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    /// <summary>Snapshots Items and every folder's own Items into
    /// HistoryStore's manifest format and saves both together. Called
    /// after every mutation anywhere in the app -- new/removed/pinned
    /// items, moving/copying an item into a folder, and folder create/
    /// rename/delete -- since a folder's contents can now change independently
    /// of the main history and vice versa, and HistoryStore.SaveAll needs
    /// both at once to correctly sweep orphaned images (see its own
    /// comment). Manifest writes are cheap either way -- just metadata;
    /// image bytes are already on disk by the time an image item exists in
    /// either list, written once in AddImage/AddCopyToFolder.</summary>
    private void PersistAll()
    {
        var historyRecords = Items.Select(ItemToRecord).ToList();
        var folderRecords = Folders.Select(f => new HistoryStore.FolderRecord
        {
            Id = f.Id,
            Name = f.Name,
            Color = f.Color,
            Items = f.Items.Select(ItemToRecord).ToList(),
        }).ToList();

        _historyStore.SaveAll(historyRecords, folderRecords);
    }

    private static HistoryStore.Record ItemToRecord(ClipboardItem item) => new()
    {
        Id = item.Id,
        Type = item.Type.ToString(),
        Text = item.Text,
        ImageFile = item.ImageFile,
        Timestamp = item.Timestamp,
        IsPinned = item.IsPinned,
    };

    private void OnSystemThemeChanged()
    {
        if (Theme != ThemeMode.System) return;
        DispatcherQueue.TryEnqueue(() => ApplyTheme(Theme));
    }

    /// <summary>Re-themes the panel and persists the choice. Call this from
    /// Settings when the user picks a new mode.</summary>
    public void ApplyAndPersistTheme(ThemeMode mode)
    {
        ApplyTheme(mode);
        _settingsStore.SaveThemeMode(mode);
    }

    /// <summary>Re-applies the panel's corner rounding and persists the
    /// choice. Call this from Settings when the user picks a new style.</summary>
    public void ApplyAndPersistCornerStyle(CornerStyle style)
    {
        CornerStyle = style;
        WindowEffects.ApplyCornerStyle(_hwnd, style);
        _settingsStore.SaveCornerStyle(style);
    }

    /// <summary>Re-themes the panel without persisting -- used for the
    /// initial load and for live system-theme-change follow-up while
    /// Theme == System (nothing new to persist in that case).</summary>
    public void ApplyTheme(ThemeMode mode)
    {
        Theme = mode;
        var isDark = ApplyThemeCore(mode);
        WindowEffects.ApplyAcrylicBackdrop(this, isDark, thin: false); // re-attach; cheap no-op if already attached
    }

    /// <summary>Sets ElementTheme (every built-in WinUI3 ThemeResource reacts
    /// to this automatically -- the ListView, buttons, and text in the panel
    /// all pick up the matching light/dark colors with no hand-set brushes
    /// needed) and tints the native frame to match. The panel opens beside
    /// the taskbar, so ThemeMode.System resolves against Windows' "Default
    /// Windows mode" (taskbarSurface: true), not "Default app mode" --
    /// matching WorldClockTray's ClockPanel. Returns the resolved dark/light
    /// bool so callers can hand it straight to the Acrylic backdrop (see
    /// the comment on ApplyAcrylicBackdrop's call sites below).</summary>
    private bool ApplyThemeCore(ThemeMode mode)
    {
        var effectiveMode = SystemThemeService.Resolve(mode, taskbarSurface: true);
        var isDark = effectiveMode == ThemeMode.Dark;
        if (Content is FrameworkElement root)
            root.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        WindowEffects.SetDarkMode(_hwnd, isDark);
        IsDarkMode = isDark;
        EffectiveThemeChanged?.Invoke(isDark);
        return isDark;
    }

    /// <summary>Panel bounds in physical screen pixels, for the mouse hook's
    /// click-away hit test (hookstruct.pt is physical). Ported from
    /// WorldClockTray's ClockPanel.CurrentScreenRectPx.</summary>
    public PixelRect CurrentScreenRectPx()
    {
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        return new PixelRect(pos.X, pos.Y, size.Width, size.Height);
    }

    public void AddText(string text)
    {
        // New entries land just below the pinned block, not always at
        // index 0 -- otherwise every fresh copy would push pinned items
        // down instead of leaving them at the very top.
        var insertAt = Items.TakeWhile(i => i.IsPinned).Count();

        // Skip consecutive duplicates among the non-pinned items (common
        // when an app re-writes the same text) -- a pinned item sitting
        // at the top shouldn't block this check for what's actually the
        // most recent copy.
        if (Items.Count > insertAt && Items[insertAt].Type == ClipboardItemType.Text && Items[insertAt].Text == text)
            return;

        Items.Insert(insertAt, new ClipboardItem { Type = ClipboardItemType.Text, Text = text });
        Trim();
        PersistAll();
    }

    public void AddImage(BitmapImage image, byte[] imageBytes)
    {
        var item = new ClipboardItem { Type = ClipboardItemType.Image, Image = image, ImageBytes = imageBytes };
        // Write bytes to disk once, up front, so the item is fully
        // persistable the moment it lands in Items -- PersistAll below
        // just references this filename rather than rewriting the bytes.
        item.ImageFile = _historyStore.SaveImageBytes(item.Id, imageBytes);

        var insertAt = Items.TakeWhile(i => i.IsPinned).Count();
        Items.Insert(insertAt, item);
        Trim();
        PersistAll();
    }

    private void Trim()
    {
        // Only trims from the bottom while the last item isn't pinned --
        // pinned items live at the top by construction, so this only ever
        // reaches one in the pathological case of more than MaxItems items
        // being pinned, in which case the list is just allowed to grow
        // past the cap rather than silently deleting a pin.
        while (Items.Count > MaxItems && !Items[^1].IsPinned)
            Items.RemoveAt(Items.Count - 1);
    }

    // --- fit-to-content height -------------------------------------------------

    /// <summary>Coalesces a burst of PinnedItems/UnpinnedItems mutations
    /// (see _heightUpdateQueued) into a single UpdateWindowHeight call, deferred
    /// with low-priority DispatcherQueue.TryEnqueue so it lands after the
    /// current call stack -- and the layout pass that stack's mutations
    /// trigger -- has fully finished, instead of measuring mid-loop against
    /// containers the ListView hasn't realized yet.</summary>
    private void ScheduleWindowHeightUpdate()
    {
        if (_heightUpdateQueued) return;
        _heightUpdateQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _heightUpdateQueued = false;
            UpdateWindowHeight();
        });
    }

    /// <summary>Grows/shrinks the panel's height to fit whatever's currently
    /// in PinnedItems + UnpinnedItems: tall enough to show it all with no
    /// scrollbar, down to MinPanelHeightDip so an empty/near-empty panel
    /// doesn't look cramped, and up to MaxPanelHeightFraction of the
    /// current monitor's work area -- past that the window holds at the
    /// cap and HistoryList's own built-in ScrollViewer takes over (its row,
    /// in the nested Grid inside Row 2, is a "*" row, so once the window
    /// stops growing, the leftover space it's arranged into is smaller than
    /// its content and it scrolls automatically; PinnedList is capped by
    /// its own MaxHeight instead, see the XAML).</summary>
    private void UpdateWindowHeight()
    {
        // Re-entrancy guard: Resize below triggers a layout pass, which
        // could in principle bounce back through a SizeChanged-style path;
        // this keeps a single call to this method atomic.
        if (_sizingInProgress) return;
        _sizingInProgress = true;
        try
        {
            // Force any pending layout (new/removed containers from the
            // PinnedItems/UnpinnedItems mutation that led here) to actually
            // happen synchronously first -- otherwise the Measure call
            // below can run against containers the ListViews haven't
            // realized yet and undercount the natural height (the "opens
            // too small" and "doesn't regrow when the search is cleared"
            // symptoms).
            Container.UpdateLayout();

            // Measuring with an infinite available height reports the size
            // the content actually wants. HistoryList's row (in the nested
            // Grid inside Row 2) is a "*" row, but during Measure (as
            // opposed to Arrange) a star row is just handed the same
            // available remainder as an Auto row would be, so it reports
            // its true full extent here. Items is capped at MaxItems (25)
            // total across both lists, so realizing every item to compute
            // that extent is cheap.
            var availableSize = new Windows.Foundation.Size(PanelWidthDip, double.PositiveInfinity);
            Container.Measure(availableSize);
            var naturalHeightDip = Container.DesiredSize.Height;

            var wa = WorkAreaPx(_lastAnchor);
            var maxHeightDip = (wa.Height * MaxPanelHeightFraction) / _scale;

            // +ExtraFitHeightDip: a small cushion on top of the exact fit,
            // since the exact-fit height sometimes clips the last visible
            // pixels of a row right at the panel edge.
            var targetHeightDip = Math.Clamp(naturalHeightDip, MinPanelHeightDip, Math.Max(MinPanelHeightDip, maxHeightDip)) + ExtraFitHeightDip;
            var newHeightPx = (int)Math.Round(targetHeightDip * _scale);

            if (newHeightPx == _appWindow.Size.Height) return;

            // Regenerate now, ahead of both branches below: width never
            // changes (only height does), and both the animated and instant
            // paths end at the same newHeightPx, so there's no need to
            // duplicate this per-branch or wait for the animation to finish.
            // A one-frame mismatch between the overlay's size and the
            // window's mid-animation size is imperceptible at this opacity;
            // regenerating every intermediate frame of a resize tween isn't
            // worth the extra allocation.
            WindowEffects.RegenerateNoise(NoiseOverlay, _appWindow.Size.Width, newHeightPx);

            // While the panel is already open and settled (not hidden, and
            // not mid open/close slide), tween the height change instead of
            // snapping -- e.g. Clear All collapsing a tall history down to
            // the empty-state floor used to jump in a single frame. Both the
            // resize and the repositioning that keeps it docked to the same
            // edge/point happen together, frame by frame, via
            // WindowAnimator.ResizeAndMove (same background-thread
            // SetWindowPos+DwmFlush stepping the open/close slide uses).
            //
            // Falls back to the original instant path in every other case:
            // panel not visible yet (the pre-show call from SlideIn, where
            // there's nothing on screen to animate), or a slide already in
            // flight (animating both at once would fight over the hwnd).
            if (IsPanelVisible && !_slideInProgress)
            {
                AnimateHeightTo(newHeightPx);
                return;
            }

            _appWindow.Resize(new SizeInt32(_appWindow.Size.Width, newHeightPx));

            // Keep the panel anchored to the same point/edge it opened from
            // now that its height has changed -- e.g. a bottom-right dock
            // grows upward instead of drifting off the anchor. Only matters
            // while actually on screen; SlideIn positions explicitly right
            // after calling this, before the panel is shown.
            if (IsPanelVisible)
                _appWindow.Move(TargetPosPx(_lastAnchor));
        }
        finally
        {
            _sizingInProgress = false;
        }
    }

    /// <summary>Tweens the panel from its current on-screen rect to the same
    /// width at <paramref name="newHeightPx"/>, repositioned to stay docked
    /// against _lastEffectiveEdge -- the animated counterpart to the plain
    /// Resize+Move path in UpdateWindowHeight above. Cancels any resize
    /// tween already in flight first, since a second content change (e.g.
    /// two rapid pastes) landing before the first tween settles must replace
    /// it outright rather than race it.</summary>
    private void AnimateHeightTo(int newHeightPx)
    {
        _resizeAnimCts?.Cancel();
        _resizeAnimCts = new CancellationTokenSource();

        var currentPos = _appWindow.Position;
        var currentSize = _appWindow.Size;
        var targetPos = TargetPosPxForSize(_lastAnchor, currentSize.Width, newHeightPx);

        WindowAnimator.ResizeAndMove(
            _hwnd,
            new RectInt32(currentPos.X, currentPos.Y, currentSize.Width, currentSize.Height),
            new RectInt32(targetPos.X, targetPos.Y, currentSize.Width, newHeightPx),
            HeightResizeDurationMs,
            WindowAnimator.ExponentialEaseOut,
            DispatcherQueue,
            cancellationToken: _resizeAnimCts.Token);
    }

    // --- positioning ---------------------------------------------------------

    private RectInt32 WorkAreaPx(PointInt32 anchor)
    {
        var area = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Nearest);
        return area.WorkArea;
    }

    /// <summary>Whichever screen edge the anchor is closest to.</summary>
    private static string ResolveEdge(PointInt32 anchor, RectInt32 wa)
    {
        var left = anchor.X - wa.X;
        var right = (wa.X + wa.Width) - anchor.X;
        var top = anchor.Y - wa.Y;
        var bottom = (wa.Y + wa.Height) - anchor.Y;

        var min = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        if (min == left) return "left";
        if (min == right) return "right";
        if (min == top) return "top";
        return "bottom";
    }

    /// <summary>Final docked position (physical px), content centered on the
    /// click point along the cross-axis.</summary>
    private PointInt32 TargetPosPx(PointInt32 anchor) =>
        TargetPosPxForSize(anchor, _appWindow.Size.Width, _appWindow.Size.Height);

    /// <summary>Same as TargetPosPx, but for an explicit width/height rather
    /// than _appWindow's current (live) size -- lets AnimateHeightTo work
    /// out where the panel will end up docked for a height it hasn't been
    /// resized to yet, so the resize and reposition tween together instead
    /// of the position being computed against the pre-resize size.</summary>
    private PointInt32 TargetPosPxForSize(PointInt32 anchor, int width, int height)
    {
        var wa = WorkAreaPx(anchor);
        var edge = ResolveEdge(anchor, wa);
        _lastEffectiveEdge = edge;

        var edgeMargin = (int)Math.Round(EdgeMarginDip * _scale);
        var contentWidth = width;
        var contentHeight = height;

        int x, y;
        if (edge is "left" or "right")
        {
            y = anchor.Y - contentHeight / 2;
            y = Math.Max(wa.Y + edgeMargin, Math.Min(y, wa.Y + wa.Height - contentHeight - edgeMargin));
            x = edge == "left" ? wa.X + edgeMargin : wa.X + wa.Width - contentWidth - edgeMargin;
        }
        else
        {
            x = anchor.X - contentWidth / 2;
            x = Math.Max(wa.X + edgeMargin, Math.Min(x, wa.X + wa.Width - contentWidth - edgeMargin));
            y = edge == "top" ? wa.Y + edgeMargin : wa.Y + wa.Height - contentHeight - edgeMargin;
        }

        return new PointInt32(x, y);
    }

    /// <summary>Off-screen start position matching the resolved edge.</summary>
    private PointInt32 HiddenPosPx(PointInt32 target)
    {
        var contentWidth = _appWindow.Size.Width;
        var contentHeight = _appWindow.Size.Height;
        var offset = (int)Math.Round(30 * _scale);
        return _lastEffectiveEdge switch
        {
            "left" => new PointInt32(target.X - contentWidth - offset, target.Y),
            "right" => new PointInt32(target.X + contentWidth + offset, target.Y),
            "top" => new PointInt32(target.X, target.Y - contentHeight - offset),
            _ => new PointInt32(target.X, target.Y + contentHeight + offset), // "bottom"
        };
    }

    // --- show/hide with slide --------------------------------------------------

    public void ShowNearPoint(int x, int y) => SlideIn(new PointInt32(x, y));

    public void SlideIn(PointInt32 anchor)
    {
        // Refresh before positioning: a different monitor (different work
        // area, different 75% cap) or content that changed while the panel
        // was closed both need to be accounted for before TargetPosPx runs.
        _lastAnchor = anchor;
        UpdateWindowHeight();

        var target = TargetPosPx(anchor);
        var hidden = HiddenPosPx(target);

        PrepareContentEntrance();

        _appWindow.Move(hidden);
        _appWindow.Show();
        WindowEffects.ApplyTopmostBelowTaskbar(_hwnd);
        this.Activate();

        // Activate() alone isn't reliable here -- this is a tray-launched
        // window that never gets real OS foreground activation the normal
        // way (see the click-away/MouseHookWatcher comment further down,
        // which exists for the same underlying reason). That's fine for the panel's own visuals/topmost-ness, but
        // SearchBox.Focus() below needs the window to genuinely hold OS
        // keyboard focus, not just look active -- otherwise it silently
        // no-ops on anything but the very first open (right after process
        // startup, when Windows is briefly lenient about foreground
        // switches). A raw SetForegroundWindow forces it every time,
        // matching the same workaround TrayIconManager's right-click menu
        // already relies on.
        NativeMethods.SetForegroundWindow(_hwnd);

        // So the user can just start typing to search the moment the panel
        // is open, without clicking into the box first. Must come after
        // Activate() -- focus can't land on an element in a window that
        // isn't the foreground window yet.
        SearchBox.Focus(FocusState.Programmatic);

        AnimateTo(target, isOpening: true, onNearComplete: TriggerContentAnimation);
        IsPanelVisible = true;
    }

    public void HidePanel() => SlideOut();

    public void SlideOut()
    {
        // Reset the search on close (Windows 11's own flyouts do the same
        // with their search boxes) so the panel never reopens silently
        // pre-filtered from whatever was typed last time.
        SearchBox.Text = string.Empty;

        StopContentStoryboard();
        var current = new PointInt32(_appWindow.Position.X, _appWindow.Position.Y);
        var hidden = HiddenPosPx(current);
        AnimateTo(hidden, isOpening: false, onFinished: () => _appWindow.Hide());
        IsPanelVisible = false;
        _lastCloseTimeUtc = DateTime.UtcNow;
    }

    public void Toggle(PointInt32 anchor)
    {
        // If we just auto-closed (e.g. a click-away fired right as the tray
        // icon was clicked again), don't immediately reopen -- otherwise the
        // click-away's SlideOut and this Toggle's SlideIn both fire for the
        // same physical click, and the panel never actually stays closed.
        if (!IsPanelVisible && (DateTime.UtcNow - _lastCloseTimeUtc).TotalSeconds < ReopenDebounceSeconds)
            return;

        if (IsPanelVisible) SlideOut();
        else SlideIn(anchor);
    }

    /// <summary>Sets Container's starting offset/opacity for the entrance,
    /// matching the direction the window itself is about to slide from
    /// (_lastEffectiveEdge, set by TargetPosPx just before this is called).</summary>
    private void PrepareContentEntrance()
    {
        StopContentStoryboard();

        var offset = ContentSlideOffsetDip * _scale;
        double x = 0, y = 0;
        switch (_lastEffectiveEdge)
        {
            case "left": x = -offset; break;
            case "right": x = offset; break;
            case "top": y = -offset; break;
            default: y = offset; break; // "bottom"
        }

        _contentTransform.X = x;
        _contentTransform.Y = y;
        Container.Opacity = 0;
    }

    /// <summary>Fires a beat before the window tween lands so Container's
    /// own slide+fade is still finishing as the window settles, instead of
    /// both stopping in the same frame.</summary>
    private void TriggerContentAnimation()
    {
        var axis = _lastEffectiveEdge is "left" or "right" ? "X" : "Y";
        var from = axis == "X" ? _contentTransform.X : _contentTransform.Y;

        var sb = new Storyboard();

        var slideAnim = new DoubleAnimation
        {
            From = from,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(ContentSlideDurationMs)),
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slideAnim, _contentTransform);
        Storyboard.SetTargetProperty(slideAnim, axis);

        var fadeAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(ContentFadeDurationMs))
        };
        Storyboard.SetTarget(fadeAnim, Container);
        Storyboard.SetTargetProperty(fadeAnim, "Opacity");

        sb.Children.Add(slideAnim);
        sb.Children.Add(fadeAnim);
        _contentStoryboard = sb;
        sb.Begin();
    }

    private void StopContentStoryboard()
    {
        if (_contentStoryboard != null)
        {
            try { _contentStoryboard.Stop(); } catch { /* best effort */ }
            _contentStoryboard = null;
        }
        Container.Opacity = 1;
        _contentTransform.X = 0;
        _contentTransform.Y = 0;
    }

    /// <summary>Position tween driven by WindowAnimator (background-thread
    /// SetWindowPos + DwmFlush) -- the live Acrylic backdrop stays attached
    /// throughout instead of needing to be suspended for a flat fallback
    /// fill, since the DwmFlush-synced native stepping doesn't jank.
    ///
    /// isOpening selects two distinct curves: exponential ease-out for
    /// opening (starts fast, settles in), circular ease-in for closing
    /// (starts slow, accelerates away) -- and, on open only, fires
    /// onNearComplete a bit early so the panel feels responsive rather than
    /// trailing its own animation.</summary>
    private void AnimateTo(PointInt32 target, bool isOpening, Action? onFinished = null, Action? onNearComplete = null)
    {
        _animCts?.Cancel();
        _animCts = new CancellationTokenSource();

        // A resize tween mid-flight would fight this slide over the same
        // hwnd's SetWindowPos calls -- stop it outright rather than let the
        // two race frame by frame.
        _resizeAnimCts?.Cancel();
        _slideInProgress = true;

        var start = _appWindow.Position;
        var easing = isOpening
            ? (Func<double, double>)WindowAnimator.ExponentialEaseOut
            : WindowAnimator.CircularEaseIn;

        WindowAnimator.Slide(
            _hwnd, start, target, SlideDurationMs, easing, DispatcherQueue,
            nearCompleteFraction: isOpening ? 0.1 : 0.0,
            onNearComplete: onNearComplete,
            onComplete: () =>
            {
                _slideInProgress = false;
                onFinished?.Invoke();
            },
            cancellationToken: _animCts.Token);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>Runs every time FilterButton's menu opens -- puts a
    /// checkmark icon next to whichever option matches _contentFilter and
    /// clears it from the other two, rather than tracking checked state
    /// separately from the field itself.</summary>
    private void OnFilterFlyoutOpening(object sender, object e)
    {
        SetFilterChecked(FilterAllItem, _contentFilter == ContentFilterMode.All);
        SetFilterChecked(FilterTextItem, _contentFilter == ContentFilterMode.Text);
        SetFilterChecked(FilterImageItem, _contentFilter == ContentFilterMode.Image);
        SetFilterChecked(FilterPinnedItem, _contentFilter == ContentFilterMode.Pinned);
        SetFilterChecked(FilterColorsItem, _contentFilter == ContentFilterMode.Colors);
        SetFilterChecked(FilterGradientsItem, _contentFilter == ContentFilterMode.Gradients);
        SetFilterChecked(FilterColorsAndGradientsItem, _contentFilter == ContentFilterMode.ColorsAndGradients);
    }

    private static void SetFilterChecked(MenuFlyoutItem item, bool isChecked) =>
        item.Icon = isChecked ? new FontIcon { Glyph = "\uE73E", FontSize = 12 } : null;

    /// <summary>Handles every filter MenuFlyoutItem (its Tag identifies
    /// which) -- switches _contentFilter, re-runs ApplyFilter, and tints
    /// the funnel glyph with the accent color while any restriction other
    /// than "All" is active, so there's a visible cue even after the menu
    /// closes.</summary>
    private void OnFilterModeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag }) return;

        _contentFilter = tag switch
        {
            "Text" => ContentFilterMode.Text,
            "Image" => ContentFilterMode.Image,
            "Pinned" => ContentFilterMode.Pinned,
            "Colors" => ContentFilterMode.Colors,
            "Gradients" => ContentFilterMode.Gradients,
            "ColorsAndGradients" => ContentFilterMode.ColorsAndGradients,
            _ => ContentFilterMode.All
        };

        if (_contentFilter == ContentFilterMode.All)
            FilterGlyph.ClearValue(FontIcon.ForegroundProperty);
        else if (Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var accentBrush))
            FilterGlyph.Foreground = (Brush)accentBrush;

        ApplyFilter();
    }

    /// <summary>Recomputes PinnedItems and UnpinnedItems from Items + the
    /// search box's current text + FilterButton's current content-type
    /// restriction (_contentFilter). Text items match the query when the
    /// search text (case-insensitive) appears anywhere in their content;
    /// images have nothing to search, so a non-empty query excludes them
    /// regardless of _contentFilter. Matches are then split by IsPinned
    /// and each half is synced into its own collection (see SyncCollection)
    /// -- diffing against each collection's existing contents rather than
    /// clearing and rebuilding, so neither ListView flickers or drops
    /// scroll position on every keystroke, and a pin/unpin toggle reads as
    /// the row cleanly leaving one list and landing in the other instead of
    /// both lists doing a full refresh.</summary>
    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;

        // Sourced from whichever page is currently on screen -- the main
        // history, or (while a folder is open) that folder's own
        // independent Items -- rather than always Items. See CurrentSource.
        IEnumerable<ClipboardItem> matches = CurrentSource;

        if (_contentFilter == ContentFilterMode.Text)
            matches = matches.Where(item => item.Type == ClipboardItemType.Text && !item.IsColorValue && !item.IsGradientValue);
        else if (_contentFilter == ContentFilterMode.Image)
            matches = matches.Where(item => item.Type == ClipboardItemType.Image);
        else if (_contentFilter == ContentFilterMode.Pinned)
            matches = matches.Where(item => item.IsPinned);
        else if (_contentFilter == ContentFilterMode.Colors)
            matches = matches.Where(item => item.IsColorValue);
        else if (_contentFilter == ContentFilterMode.Gradients)
            matches = matches.Where(item => item.IsGradientValue);
        else if (_contentFilter == ContentFilterMode.ColorsAndGradients)
            matches = matches.Where(item => item.IsColorValue || item.IsGradientValue);

        if (query.Length > 0)
        {
            matches = matches.Where(item =>
                item.Type == ClipboardItemType.Text &&
                item.Text != null &&
                item.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var matchList = matches.ToList();

        // Items already keeps pinned entries contiguous at the top (see
        // IsPinned/TogglePin/AddText/AddImage), so both halves come out of
        // this split still in their existing relative order -- pinned
        // newest-pinned/most-recently-moved first, unpinned newest-first.
        var pinnedTarget = matchList.Where(item => item.IsPinned).ToList();
        var unpinnedTarget = matchList.Where(item => !item.IsPinned).ToList();

        SyncCollection(PinnedItems, pinnedTarget);
        SyncCollection(UnpinnedItems, unpinnedTarget);
    }

    /// <summary>Diffs collection against target -- moving/inserting/
    /// removing individual entries -- instead of clearing and rebuilding
    /// it outright, so the ListView bound to it doesn't flicker or lose
    /// scroll position, and only the entries that actually changed play an
    /// add/remove/move animation. Shared by both PinnedItems and
    /// UnpinnedItems in ApplyFilter above.</summary>
    private static void SyncCollection(ObservableCollection<ClipboardItem> collection, List<ClipboardItem> target)
    {
        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(collection[i]))
                collection.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < collection.Count && collection[i] == target[i])
                continue;

            var existingIndex = collection.IndexOf(target[i]);
            if (existingIndex >= 0)
                collection.Move(existingIndex, i);
            else
                collection.Insert(i, target[i]);
        }
    }

    private void OnClearAllClicked(object sender, RoutedEventArgs e)
    {
        // Acts on whichever page is currently on screen -- the main
        // history, or a folder's own items while one is open (see
        // CurrentSource). Pinned items are exempt, same as they're exempt
        // from auto-trim -- pinning is meant to protect an item from
        // exactly this kind of bulk removal, so Clear All only touches the
        // unpinned ones.
        var source = CurrentSource;
        for (var i = source.Count - 1; i >= 0; i--)
        {
            if (!source[i].IsPinned)
                source.RemoveAt(i);
        }
        PersistAll();
    }

    /// <summary>Toggles the panel-level pin (see IsPanelPinned). Glyph and
    /// tooltip are updated imperatively here rather than via x:Bind, same
    /// pattern as FilterButton's checked-state elsewhere in this file --
    /// there's no bindable view-model backing the window itself, just this
    /// one piece of UI-only state. E718/E77A are the same Pin/UnPin glyph
    /// pair ClipboardItem.PinGlyph uses for per-item pinning, so the two
    /// pin affordances in the panel read as the same visual language.</summary>
    private void OnPinPanelClicked(object sender, RoutedEventArgs e)
    {
        IsPanelPinned = !IsPanelPinned;
        PinPanelButton.Content = IsPanelPinned ? "\uE77A" : "\uE718";
        ToolTipService.SetToolTip(PinPanelButton, IsPanelPinned ? "Unpin panel" : "Keep panel open");

        // Re-assert immediately rather than waiting for the next SlideIn --
        // the panel is already open when this is clicked, and pinning is
        // exactly the moment the user most wants "stays on top" to take
        // effect right away.
        if (IsPanelPinned)
            WindowEffects.ApplyTopmostBelowTaskbar(_hwnd);
    }

    private async void OnRemoveItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ClipboardItem item }) return;

        if (item.IsPinned)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete pinned item?",
                Content = "This item is pinned. Are you sure you want to delete it?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
                RequestedTheme = IsDarkMode ? ElementTheme.Dark : ElementTheme.Light,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return; // user cancelled -- leave the item in place
        }

        CurrentSource.Remove(item);
        PersistAll();
    }

    private void OnItemPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            // Both buttons are always Visibility="Visible" (see XAML) so the
            // button-stack column never collapses to zero height -- only
            // Opacity/IsHitTestVisible change here, which don't affect
            // layout, so the row height stays constant whether or not
            // you're hovering it.
            if (grid.FindName("RemoveButton") is Button removeButton)
            {
                removeButton.Opacity = 1;
                removeButton.IsHitTestVisible = true;
            }

            // Always reveal the pin button on hover -- including for
            // unpinned rows, so the user can pin them. OnItemPointerExited
            // below reverts this to IsPinned's data-bound baseline (via
            // PinOpacity) rather than unconditionally hiding it, so a
            // pinned row's pin button stays visible even after the pointer
            // leaves.
            if (grid.FindName("PinButton") is Button pinButton)
            {
                pinButton.Opacity = 1;
                pinButton.IsHitTestVisible = true;
            }

            if (grid.FindName("RowHighlight") is Border highlight)
                highlight.Opacity = 1;
        }
    }

    private void OnItemPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Grid { Tag: ClipboardItem item } grid)
        {
            if (grid.FindName("RemoveButton") is Button removeButton)
            {
                removeButton.Opacity = 0;
                removeButton.IsHitTestVisible = false;
            }

            if (grid.FindName("PinButton") is Button pinButton)
            {
                pinButton.Opacity = item.IsPinned ? 1 : 0;
                pinButton.IsHitTestVisible = item.IsPinned;
            }

            if (grid.FindName("RowHighlight") is Border highlight)
                highlight.Opacity = 0;
        }
    }

    /// <summary>Fires once PinnedList's built-in CanReorderItems/AllowDrop
    /// drag settles — args.DropResult is Move for a completed reorder,
    /// or None/Copy for a canceled drag (e.g. dropped outside the list),
    /// which is why this bails out early rather than persisting a no-op
    /// order. By this point the ListView has already run the Move
    /// directly against PinnedItems (it's an IList), so PinnedItems
    /// itself needs no further work here — only Items, the source of
    /// truth ApplyFilter rebuilds PinnedItems/UnpinnedItems from on every
    /// future change (e.g. the very next clipboard capture), which would
    /// otherwise snap PinnedItems right back to its old order next time
    /// ApplyFilter runs. Re-derives Items' full order as the new pinned
    /// order (still contiguous at the top, the same invariant TogglePin
    /// maintains) followed by the unpinned items in their existing order,
    /// and diffs Items onto that with the same SyncCollection helper
    /// ApplyFilter itself uses.</summary>
    private void OnPinnedListDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != DataPackageOperation.Move) return;

        var source = CurrentSource;
        var target = PinnedItems.Concat(source.Where(i => !i.IsPinned)).ToList();
        SyncCollection(source, target);
        PersistAll();
    }

    /// <summary>Runs once, the first time HistoryList's template is
    /// realized. Walks the visual tree for the ListView's built-in vertical
    /// ScrollBar and ScrollViewer -- neither is exposed as a named element
    /// in the XAML, so this is the only way to reach them without fully
    /// retemplating the ListView just to add auto-hide.</summary>
    private void OnHistoryListLoaded(object sender, RoutedEventArgs e)
    {
        if (_historyScrollBar != null) return; // already wired up

        var scrollViewer = FindDescendant<ScrollViewer>(HistoryList);
        if (scrollViewer != null)
        {
            _historyScrollViewer = scrollViewer;
            scrollViewer.ViewChanged += (_, _) =>
            {
                ShowScrollBar();
                UpdateScrollToTopButtonVisibility();
            };
        }

        _historyScrollBar = FindDescendant<ScrollBar>(HistoryList,
            sb => sb.Orientation == Orientation.Vertical);
        if (_historyScrollBar == null) return;

        // Hovering the bar itself always counts as "recently used" --
        // otherwise it could start fading out from under the cursor while
        // the user is lining up a drag on the thumb.
        _historyScrollBar.PointerEntered += (_, _) => ShowScrollBar();
        _historyScrollBar.PointerExited += (_, _) => ScheduleScrollBarHide();

        _scrollBarHideTimer = new DispatcherTimer { Interval = ScrollBarHideDelay };
        _scrollBarHideTimer.Tick += (_, _) =>
        {
            _scrollBarHideTimer!.Stop();
            FadeScrollBarTo(0, 400);
        };

        ScheduleScrollBarHide();
    }

    /// <summary>Fades the scrollbar to fully visible (if it isn't already)
    /// and restarts the 3-second countdown to hide it again. Called on
    /// every scroll (wheel, drag, keyboard) and on hovering the bar.</summary>
    private void ShowScrollBar()
    {
        FadeScrollBarTo(1, 150);
        ScheduleScrollBarHide();
    }

    private void ScheduleScrollBarHide()
    {
        if (_scrollBarHideTimer == null) return;
        _scrollBarHideTimer.Stop();
        _scrollBarHideTimer.Start();
    }

    private void FadeScrollBarTo(double target, int durationMs)
    {
        if (_historyScrollBar == null) return;
        if (_historyScrollBar.Opacity == target) return;

        _scrollBarFadeStoryboard?.Stop();

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, _historyScrollBar);
        Storyboard.SetTargetProperty(animation, "Opacity");

        _scrollBarFadeStoryboard = new Storyboard();
        _scrollBarFadeStoryboard.Children.Add(animation);
        _scrollBarFadeStoryboard.Begin();
    }

    /// <summary>Fades ScrollToTopButton in once the list is scrolled down
    /// past ScrollToTopThreshold, and back out when it's scrolled back up
    /// past it -- called from HistoryList's ScrollViewer.ViewChanged.</summary>
    private void UpdateScrollToTopButtonVisibility()
    {
        if (_historyScrollViewer == null) return;
        var shouldShow = _historyScrollViewer.VerticalOffset > ScrollToTopThreshold;
        FadeScrollToTopButtonTo(shouldShow ? 1 : 0, 150);
    }

    private void FadeScrollToTopButtonTo(double target, int durationMs)
    {
        if (ScrollToTopButton.Opacity == target) return;

        // Hit-testable only while visible/animating in, so it can't eat
        // clicks meant for whatever's underneath while fully faded out.
        ScrollToTopButton.IsHitTestVisible = target > 0;

        _scrollToTopFadeStoryboard?.Stop();

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, ScrollToTopButton);
        Storyboard.SetTargetProperty(animation, "Opacity");

        _scrollToTopFadeStoryboard = new Storyboard();
        _scrollToTopFadeStoryboard.Children.Add(animation);
        _scrollToTopFadeStoryboard.Begin();
    }

    /// <summary>ChangeView's own animation (disableAnimation defaults to
    /// false) handles the smooth scroll -- no need to drive it manually.</summary>
    private void OnScrollToTopClicked(object sender, RoutedEventArgs e) =>
        _historyScrollViewer?.ChangeView(null, 0, null);

    /// <summary>Depth-first search for the first descendant of type T
    /// (optionally matching predicate) in root's visual tree.</summary>
    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (predicate == null || predicate(match)))
                return match;

            var nested = FindDescendant(child, predicate);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private void OnPinItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardItem item })
        {
            TogglePin(item);
        }
    }

    /// <summary>Flips IsPinned and moves the item to preserve the list's
    /// invariant that every pinned item sits contiguously above every
    /// unpinned one: a newly pinned item jumps straight to index 0 (the
    /// very top, above any other already-pinned items); an unpinned item
    /// drops back into the unpinned section in chronological order (newest
    /// first, matching AddText/AddImage's insertion order) rather than
    /// always landing at the very top of that section -- so an item pinned
    /// out of the middle of the history returns to roughly where it'd be if
    /// it had never been pinned. AddText/AddImage insert fresh clipboard
    /// entries below the pinned block (not at index 0) so new copies never
    /// push a pin down, and Trim/OnClearAllClicked both leave pinned items
    /// alone.</summary>
    private void TogglePin(ClipboardItem item)
    {
        var source = CurrentSource;
        var currentIndex = source.IndexOf(item);
        if (currentIndex < 0) return;

        item.IsPinned = !item.IsPinned;

        if (item.IsPinned)
        {
            if (currentIndex != 0)
                source.Move(currentIndex, 0);
        }
        else
        {
            // Note: TakeWhile(i => i.IsPinned).Count() would stop as soon as
            // it reached this item (now unpinned), so unless this item
            // happened to be the very last one still pinned, that count
            // equaled currentIndex and the Move below was skipped entirely
            // -- leaving the item stuck inside the pinned block. Removing it
            // first avoids that trap, and lets the search below scan only
            // the remaining, still-correctly-ordered items.
            source.RemoveAt(currentIndex);

            var pinnedCount = source.Count(i => i.IsPinned);
            var insertAt = pinnedCount;
            for (var i = pinnedCount; i < source.Count; i++)
            {
                if (source[i].Timestamp <= item.Timestamp)
                    break;
                insertAt = i + 1;
            }

            source.Insert(insertAt, item);
        }

        // Move/RemoveAt+Insert already trigger ApplyFilter via the
        // CollectionChanged subscription in the constructor, but only when
        // they actually fire -- pinning an item that's already at index 0
        // skips the Move entirely, which would otherwise leave
        // PinnedItems/UnpinnedItems stale while filtered to Pinned. This
        // call is also what actually relocates the item between the two
        // ListViews: flipping IsPinned above changes which half of
        // ApplyFilter's pinned/unpinned split it falls into, and
        // ApplyFilter's diff-based sync (SyncCollection) removes it from
        // whichever collection it's leaving and inserts it into the
        // other -- reading as the row cleanly moving from one list to the
        // other rather than a full refresh of both.
        ApplyFilter();

        PersistAll();
    }

    private async void OnItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardItem item })
        {
            var package = new DataPackage();

            if (item.Type == ClipboardItemType.Text && item.Text is not null)
            {
                package.SetText(item.Text);
                Clipboard.SetContent(package);
            }
            else if (item.Type == ClipboardItemType.Image && item.ImageBytes is not null)
            {
                // Rebuild an in-memory stream from the bytes captured when this
                // item was first added, and hand that to the clipboard -- the
                // original BitmapImage is display-only and can't be re-copied
                // directly.
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(item.ImageBytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);

                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(package);
            }

            // Pinned: stay open so the user can copy/paste several items in
            // a row instead of having to reopen the panel after each click.
            if (!IsPanelPinned)
                HidePanel();
        }
    }

    /// <summary>Handles a click on one of the swatch's right-click "copy
    /// as" menu items (Copy as HEX/RGB/RGBA/HSL/HSLA -- see the
    /// MenuFlyout on the color swatch Border in ClipboardPanelWindow.xaml).
    /// The target item travels on the MenuFlyoutItem's own Tag (x:Bind
    /// within the DataTemplate) and the requested format on its
    /// CommandParameter, so a single handler covers all five items.</summary>
    private void OnCopyColorAsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ClipboardItem item, CommandParameter: string format })
            return;

        var converted = item.ConvertColorTo(format);
        if (converted is null) return;

        var package = new DataPackage();
        package.SetText(converted);
        Clipboard.SetContent(package);

        // Same "stay open while pinned" behavior as the regular
        // click-to-copy path in OnItemClicked above.
        if (!IsPanelPinned)
            HidePanel();
    }

    /// <summary>Right-click (or press-and-hold / Menu key -- ContextRequested
    /// covers all three) on a history/pinned row: builds the folder actions
    /// menu fresh every time rather than declaring it in XAML, since its
    /// contents depend on live state (Folders, and whether a folder page is
    /// currently open) that a static MenuFlyout can't express. Same
    /// technique used for the folder chips' own context menu below.
    ///
    /// From the main list, offers two distinct actions per spec: "Move to
    /// folder" (MoveItemToFolder -- the item leaves Items and becomes that
    /// folder's item) and "Add to folder" (AddCopyToFolder -- Items keeps
    /// the original, the folder gets an independent clone). From inside a
    /// folder page, there's nothing to move/copy *into* (the row is
    /// already filed), so this just offers "Remove from folder" instead --
    /// functionally the same as the row's own X button, but also reachable
    /// from the right-click menu for consistency with the main list.</summary>
    private void OnItemContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ClipboardItem item } element) return;
        args.Handled = true;

        var menu = new MenuFlyout();

        if (_currentFolder is null)
        {
            var moveSub = new MenuFlyoutSubItem
            {
                Text = "Move to folder",
                // Greyed out (rather than omitted) when there are no
                // folders yet, per spec -- it's still discoverable, just
                // not usable until "Move to new folder…" below creates the
                // first one.
                IsEnabled = Folders.Count > 0,
            };
            foreach (var folder in Folders)
            {
                var capturedFolder = folder;
                var folderItem = new MenuFlyoutItem { Text = folder.Name };
                folderItem.Click += (_, _) => MoveItemToFolder(item, capturedFolder);
                moveSub.Items.Add(folderItem);
            }
            menu.Items.Add(moveSub);

            var moveNewItem = new MenuFlyoutItem { Text = "Move to new folder…" };
            moveNewItem.Click += async (_, _) => await MoveItemToNewFolderAsync(item);
            menu.Items.Add(moveNewItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var addSub = new MenuFlyoutSubItem
            {
                Text = "Add to folder",
                IsEnabled = Folders.Count > 0,
            };
            foreach (var folder in Folders)
            {
                var capturedFolder = folder;
                var folderItem = new MenuFlyoutItem { Text = folder.Name };
                folderItem.Click += (_, _) => AddCopyToFolder(item, capturedFolder);
                addSub.Items.Add(folderItem);
            }
            menu.Items.Add(addSub);

            var addNewItem = new MenuFlyoutItem { Text = "Add copy to new folder…" };
            addNewItem.Click += async (_, _) => await AddCopyToNewFolderAsync(item);
            menu.Items.Add(addNewItem);
        }
        else
        {
            var removeItem = new MenuFlyoutItem { Text = "Remove from folder" };
            removeItem.Click += (_, _) =>
            {
                CurrentSource.Remove(item);
                PersistAll();
            };
            menu.Items.Add(removeItem);
        }

        ShowFlyoutAtPointer(menu, element, args);
    }

    /// <summary>"Move to folder": the item leaves the main list entirely
    /// (Items.Remove) and becomes this folder's own item (folder.Items,
    /// same object reference, inserted at the top) -- not a copy, not a
    /// tag. Only ever called from the main list (see OnItemContextRequested
    /// above), so there's no ambiguity about which list it's leaving.</summary>
    private void MoveItemToFolder(ClipboardItem item, ClipboardFolder folder)
    {
        Items.Remove(item);
        folder.Items.Insert(0, item);
        PersistAll();
    }

    /// <summary>"Add to folder": the original stays exactly where it is in
    /// the main list; the folder gets an independent clone (see
    /// ClipboardItem.Clone) inserted at the top of its own Items. An image
    /// clone needs its own persisted file (SaveImageBytes, keyed by the
    /// clone's own fresh Id) rather than sharing the original's ImageFile --
    /// PersistAll's cleanup sweep would otherwise have no way to tell the
    /// two apart and could delete the file out from under one of them the
    /// next time the other is removed.</summary>
    private void AddCopyToFolder(ClipboardItem item, ClipboardFolder folder)
    {
        var copy = item.Clone();
        if (copy.Type == ClipboardItemType.Image && copy.ImageBytes is not null)
            copy.ImageFile = _historyStore.SaveImageBytes(copy.Id, copy.ImageBytes);

        folder.Items.Insert(0, copy);
        PersistAll();
    }

    /// <summary>"Move to new folder…": prompts for a name, creates the
    /// folder, and moves the item into it in one step.</summary>
    private async Task MoveItemToNewFolderAsync(ClipboardItem item)
    {
        var folder = await CreateFolderViaDialogAsync();
        if (folder is null) return; // user cancelled or left the name blank

        MoveItemToFolder(item, folder);
    }

    /// <summary>"Add copy to new folder…": prompts for a name, creates the
    /// folder, and files an independent copy into it in one step.</summary>
    private async Task AddCopyToNewFolderAsync(ClipboardItem item)
    {
        var folder = await CreateFolderViaDialogAsync();
        if (folder is null) return; // user cancelled or left the name blank

        AddCopyToFolder(item, folder);
    }

    /// <summary>Shared "name this folder" prompt, used both by "Add to new
    /// folder…" above and by anywhere else that might want to create an
    /// empty folder in the future. Returns null if the user cancelled or
    /// submitted a blank/whitespace-only name.</summary>
    private async Task<ClipboardFolder?> CreateFolderViaDialogAsync()
    {
        var textBox = new TextBox { PlaceholderText = "Folder name" };

        var selectedColor = ClipboardFolder.DefaultColor;
        var colorPicker = BuildColorSwatchPicker(selectedColor, hex => selectedColor = hex);

        var dialog = new ContentDialog
        {
            Title = "New folder",
            Content = new StackPanel { Spacing = 12, Children = { textBox, colorPicker } },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = IsDarkMode ? ElementTheme.Dark : ElementTheme.Light,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;

        var name = textBox.Text.Trim();
        if (name.Length == 0) return null;

        var folder = new ClipboardFolder { Name = name, Color = selectedColor };
        SubscribeFolderItems(folder);
        Folders.Add(folder);
        PersistAll();
        return folder;
    }

    /// <summary>Builds a row of tappable color swatches for the New/Edit
    /// folder dialogs (see ClipboardFolder.Palette) -- hand-built with
    /// Ellipse dots rather than the full WinUI ColorPicker control, since a
    /// folder tint is always one of these eight presets, never an
    /// arbitrary RGB value. There's no bound "selected color" property to
    /// read back afterwards; instead onSelected fires the tapped swatch's
    /// hex string immediately (and moves the selection ring to it), so
    /// callers just close over a local variable in onSelected and read
    /// that same variable back once the dialog closes.</summary>
    private static StackPanel BuildColorSwatchPicker(string selectedColor, Action<string> onSelected)
    {
        var ringBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var swatchButtons = new List<Button>();

        foreach (var hex in ClipboardFolder.Palette)
        {
            var dot = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(ClipboardFolder.ParseColor(hex)),
            };
            var button = new Button
            {
                Content = dot,
                Tag = hex,
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = ringBrush,
                BorderThickness = new Thickness(string.Equals(hex, selectedColor, StringComparison.OrdinalIgnoreCase) ? 2 : 0),
            };
            button.Click += (_, _) =>
            {
                foreach (var b in swatchButtons) b.BorderThickness = new Thickness(0);
                button.BorderThickness = new Thickness(2);
                onSelected(hex);
            };
            swatchButtons.Add(button);
            panel.Children.Add(button);
        }

        return panel;
    }

    /// <summary>Click on a folder chip in the folders bar: navigates into
    /// that folder's own page in-place (see EnterFolder) -- the folders bar
    /// itself is replaced by a back-arrow header for as long as the folder
    /// page is open (see UpdateFolderBarVisibility), rather than the chip
    /// toggling a filter on the still-visible main list.</summary>
    private void OnFolderChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ClipboardFolder folder }) return;
        EnterFolder(folder);
    }

    /// <summary>Navigates into a folder's page: swaps CurrentSource over to
    /// this folder's own Items (via _currentFolder), clears any main-list
    /// search text so it doesn't carry over and hide items that would
    /// otherwise be visible here, and re-runs ApplyFilter so PinnedList/
    /// HistoryList immediately reflect the folder's contents.</summary>
    private void EnterFolder(ClipboardFolder folder)
    {
        _currentFolder = folder;
        FolderHeaderTitle.Text = folder.Name;
        FolderHeaderIcon.Foreground = folder.Brush;
        UpdateFolderBarVisibility();
        SearchBox.Text = string.Empty;
        ApplyFilter();
    }

    /// <summary>Back-arrow click (FolderHeaderBorder, see the XAML):
    /// returns to the main clipboard history in-place, same treatment as
    /// EnterFolder above but in reverse.</summary>
    private void OnFolderBackClicked(object sender, RoutedEventArgs e)
    {
        _currentFolder = null;
        UpdateFolderBarVisibility();
        SearchBox.Text = string.Empty;
        ApplyFilter();
    }

    /// <summary>Shows exactly one of the folders bar (chips) or the folder
    /// header (back arrow + name) at a time in the Grid.Row="3" slot they
    /// share -- the header while a folder page is open, the chip bar
    /// otherwise (and only then if at least one folder actually exists).
    /// Called on navigating in/out of a folder and whenever Folders itself
    /// changes (a folder being created or deleted can flip the chip bar's
    /// own visibility even while not navigating).</summary>
    private void UpdateFolderBarVisibility()
    {
        if (_currentFolder is not null)
        {
            FolderHeaderBorder.Visibility = Visibility.Visible;
            FoldersBarBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            FolderHeaderBorder.Visibility = Visibility.Collapsed;
            FoldersBarBorder.Visibility = Folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Right-click (or press-and-hold / Menu key) on a folder
    /// chip: Rename or Delete, built fresh per click same as
    /// OnItemContextRequested above (nothing dynamic needed here, but it
    /// keeps both context menus in this file built the same way).</summary>
    private void OnFolderChipContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ClipboardFolder folder } element) return;
        args.Handled = true;

        var menu = new MenuFlyout();

        var renameItem = new MenuFlyoutItem { Text = "Edit" };
        renameItem.Click += async (_, _) => await RenameFolderAsync(folder);
        menu.Items.Add(renameItem);

        var deleteItem = new MenuFlyoutItem { Text = "Delete" };
        deleteItem.Click += async (_, _) => await DeleteFolderAsync(folder);
        menu.Items.Add(deleteItem);

        ShowFlyoutAtPointer(menu, element, args);
    }

    /// <summary>Renames a folder and/or re-tints its glyph in place -- no
    /// confirmation needed (unlike Delete), since neither change is
    /// destructive and either can just be redone if the user changes
    /// their mind.</summary>
    private async Task RenameFolderAsync(ClipboardFolder folder)
    {
        var textBox = new TextBox { Text = folder.Name };
        textBox.SelectAll();

        var selectedColor = folder.Color;
        var colorPicker = BuildColorSwatchPicker(selectedColor, hex => selectedColor = hex);

        var dialog = new ContentDialog
        {
            Title = "Edit folder",
            Content = new StackPanel { Spacing = 12, Children = { textBox, colorPicker } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = IsDarkMode ? ElementTheme.Dark : ElementTheme.Light,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var newName = textBox.Text.Trim();
        if (newName.Length == 0) return;

        folder.Name = newName;
        folder.Color = selectedColor;
        if (_currentFolder == folder)
        {
            FolderHeaderTitle.Text = newName;
            FolderHeaderIcon.Foreground = folder.Brush;
        }
        PersistAll();
    }

    /// <summary>Deletes a folder after confirming -- per spec, deleting a
    /// folder permanently removes every item in it (it owns them outright
    /// now, not just a tag on a main-list item), so the confirmation
    /// dialog says exactly that rather than a generic "are you sure".</summary>
    private async Task DeleteFolderAsync(ClipboardFolder folder)
    {
        var itemCount = folder.Items.Count;

        var dialog = new ContentDialog
        {
            Title = $"Delete \"{folder.Name}\"?",
            Content = itemCount > 0
                ? $"This will permanently delete this folder and all {itemCount} item{(itemCount == 1 ? "" : "s")} in it. This can't be undone."
                : "This folder is empty. Delete it?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = IsDarkMode ? ElementTheme.Dark : ElementTheme.Light,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        // Navigate back out first if this folder's page happened to be the
        // one open -- there's nothing left to show once it's deleted.
        if (_currentFolder == folder)
            OnFolderBackClicked(this, new RoutedEventArgs());

        Folders.Remove(folder);
        PersistAll();

        // Folders.Remove already fires the CollectionChanged subscribed in
        // the constructor, which calls this -- but harmless to call again
        // explicitly in case that ever changes.
        UpdateFolderBarVisibility();
    }

    /// <summary>Shared ShowAt for both context menus above: opens at the
    /// actual right-click/press point when ContextRequestedEventArgs can
    /// supply one (mouse/touch), falling back to the default
    /// placement-relative-to-element position for a keyboard-invoked
    /// (Menu key) request, which has no meaningful pointer position.</summary>
    private static void ShowFlyoutAtPointer(MenuFlyout menu, FrameworkElement element, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(element, out var point))
            menu.ShowAt(element, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(element);
    }
}
