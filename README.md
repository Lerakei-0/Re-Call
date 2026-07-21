# Re:Call

A WinUI 3 app that sits in the Windows 11 system tray. Clicking the tray icon
opens a small borderless panel showing your recent clipboard history (text
and images). Clicking a history item copies it back to the clipboard.

## How it works

- **Tray icon** — WinUI 3 has no built-in tray API, so this uses a raw
  `Shell_NotifyIcon` wrapper (`Native/NativeNotifyIcon.cs`, ported from the
  WorldClockTray app) instead of a NuGet package: a hidden message-only
  window receives the icon's click callbacks, and right-click shows a real
  Win32 `HMENU` via `TrackPopupMenuEx` (see `UI/TrayIconManager.cs`) rather
  than a WinUI `MenuFlyout` — the native menu gets Windows 11's own
  rounded/Mica context-menu chrome for free, and its clicks fire reliably
  since `TrackPopupMenuEx` runs its own modal loop instead of depending on
  this window's XAML focus/activation state.
- **Clipboard monitoring** — `ClipboardMonitor.cs` creates a hidden native
  window and calls `AddClipboardFormatListener`, so it gets notified (via
  `WM_CLIPBOARDUPDATE`) whenever *any* app changes the clipboard — no
  polling needed.
- **Reading clipboard content** — uses the WinRT
  `Windows.ApplicationModel.DataTransfer.Clipboard` API to pull out text or a
  bitmap, whichever is present.
- **Panel window** — `ClipboardPanelWindow` is a normal `Window`, resized
  small, borderless, and hidden until the tray icon is clicked. It's
  positioned near the cursor (there's no direct WinRT API for "tray icon
  screen position", so we use the cursor position at click time, which is
  right next to the icon).
- **App theme** — Settings > App theme offers Dark / Light / System, ported
  from WorldClockTray. `Services/SystemThemeService.cs` reads Windows 11's
  "Custom" personalization mode, which lets the taskbar ("Default Windows
  mode") and ordinary app windows ("Default app mode") be Light/Dark
  independently. The panel (which opens beside the taskbar) resolves System
  against the taskbar's mode; the Settings window (an ordinary app window)
  resolves it against the apps mode — matching how Windows itself would
  theme those two surfaces. Picking System keeps following Windows live if
  you flip it later, no restart needed. Stored in
  `HKCU\Software\LocalUtilities\ReCall` (`Services/SettingsStore.cs`).
- **Click-away hiding** — a global low-level mouse hook
  (`UI/MouseHookWatcher.cs`, ported from WorldClockTray) closes the panel on
  any click outside its bounds, replacing the earlier `Window.Activated`/
  `Deactivated`-based hiding, which can miss or lag clicks for a tray-
  launched window that doesn't always get real OS foreground activation.
- **History persistence** — `Services/HistoryStore.cs` saves clipboard
  history to `%LocalAppData%\ReCall`, so it survives app restarts.
  Text items and metadata live in a small `history.json` manifest; each
  image item's raw bytes are written once to its own file under an
  `Images` subfolder (named by the item's Id) and referenced from the
  manifest by filename, so a save doesn't rewrite multi-megabyte image
  bytes to disk every time the list changes. `ClipboardPanelWindow` calls
  `SaveHistory()` after every add/remove/clear/trim and restores everything
  via `LoadHistoryAsync()` on startup.
- **Folders** — a sticky chip bar (`ClipboardPanelWindow.xaml`, `Grid.Row="3"`,
  just above the search footer) lets you organize items into named
  collections. Right-click any history/pinned row to add it to an existing
  folder (submenu, greyed out until at least one folder exists) or "Add to
  new folder…"; an item can belong to any number of folders at once
  (`ClipboardItem.FolderIds`). Right-click a folder chip to Rename it or
  Delete it — deleting a folder permanently removes every item filed into
  it (not just the folder tag) after a confirmation dialog. Clicking a chip
  filters both lists down to that folder's items; clicking it again clears
  the filter. Folders are modeled by `Models/ClipboardFolder.cs` and
  persisted separately from clipboard history in
  `%LocalAppData%\ReCall\folders.json`
  (`HistoryStore.LoadFolders`/`SaveFolders`).

## Setup (Windows, Visual Studio 2022)

> **Note:** this project targets **Windows App SDK 2.2.0** (the current stable
> line, released after the 1.x series) and **.NET 10**. Make sure the
> **.NET 10 SDK** is installed (Visual Studio 2022 17.14+ installs it via the
> workload, or grab it from https://dotnet.microsoft.com). If you hit runtime
> errors after upgrading an existing install, make sure the matching
> **Windows App Runtime 2.x** is installed on your machine too (Microsoft
> Store or the installer from the Windows App SDK downloads page) — a 1.5
> runtime won't satisfy a 2.2.0-built app, and vice versa.

1. Install the **Windows App SDK** workload in Visual Studio 2022
   (via *Tools → Get Tools and Features → .NET Desktop Development*, then
   make sure "Windows App SDK C# Templates" is checked).
2. Copy this folder anywhere, open `ReCall.csproj` in Visual Studio
   (or run `dotnet restore` then open it).
3. Build/run in **x64** or **ARM64** configuration (WinUI 3 doesn't support
   AnyCPU) — when publishing from the command line, pass `-p:Platform=x64`
   explicitly, e.g.:
   ```
   dotnet publish -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true
   ```
   The resulting self-contained exe lands in
   `bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish\`.
4. On first run you'll see the icon appear in the system tray (you may need
   to check the "hidden icons" chevron the first time — right-click the
   taskbar → Taskbar settings → drag it up if you want it always visible).
5. Click the icon → the history panel appears. Copy some text or an image
   elsewhere on your PC and it'll show up at the top of the list.

## Slide animation, acrylic backdrop & tray menu

Ported from the WorldClockTray app's approach:

- **`Native/WindowAnimator.cs`** — tweens the panel's position from a
  background thread, calling `SetWindowPos` + `DwmFlush()` every step so
  motion is synced to the monitor's actual refresh rate instead of a UI-thread
  timer. Opening uses an exponential ease-out, closing a circular ease-in.
  `ClipboardPanelWindow.SlideIn`/`SlideOut` drive it.
- **Edge-aware docking** — `ClipboardPanelWindow.ResolveEdge` picks whichever
  screen edge (left/right/top/bottom) the anchor point is actually closest
  to, the same as WorldClockTray's `ClockPanel`. The panel docks against
  that edge and slides in from just off that side — so with the taskbar on
  the right, it slides in from the right, not always from the bottom. The
  content (`Container` in the XAML) does its own short slide+fade along the
  matching axis (X for left/right, Y for top/bottom) a beat before the
  window settles.
- **`Native/WindowEffects.cs`** — `ApplyAcrylicBackdrop` drives a real
  `DesktopAcrylicController` directly (rather than the simple
  `Window.SystemBackdrop` property) and pins it to always render as "active",
  so the acrylic doesn't dim while the panel is sliding in before it's been
  focused. Also handles keeping the panel topmost-but-below-the-taskbar and
  hiding it from the taskbar/Alt+Tab.
- **Tray context menu** — right-clicking the tray icon shows a real Win32
  popup menu (`Native/NativeNotifyIcon.cs` + `UI/TrayIconManager.cs`, see
  above) with **Show / Hide**, **Settings...**, and **Quit**. Left-click
  still toggles the history panel.
- **`UI/SettingsWindow.xaml`** — opened from the tray menu's "Settings..."
  item. Deliberately left blank for now (just a title) — build out real
  settings UI here later. It reuses the same acrylic backdrop helper.

## Known limitations / things to extend

- **Image round-trip copy**: clicking a *text* item correctly re-copies it.
  Clicking an *image* item currently does not re-copy the image bytes back
  to the clipboard — `BitmapImage` doesn't retain the original encoded
  bytes needed for `DataPackage.SetBitmap`. To fix: when `AddImage` first
  captures the clipboard image, also stash the raw
  `RandomAccessStreamReference` (or re-encode the decoded pixels) alongside
  the `ClipboardItem`, and use that on click instead of the `BitmapImage`.
- **Startup**: to have it launch automatically with Windows, add a shortcut
  to the built `.exe` in the Startup folder, or register a Run key.
- **App icon**: `Assets/tray-icon.ico` is a placeholder generated for this
  project — swap in your own `.ico`.
- **Single instance**: there's no single-instance guard yet, so running the
  exe twice will create two tray icons. Add a named `Mutex` check in
  `Program.cs` if that matters to you.
