# Re:Call

A clipboard history manager that lives in the Windows system tray. Click the
tray icon to bring up a small panel with your recent copies — text and
images — and click any item to copy it back to the clipboard.

## Features

- **Clipboard history** — automatically captures everything you copy, text
  or images, no manual saving needed.
- **Tray-anchored panel** — click the tray icon to slide out a compact
  history panel near the cursor; click away to dismiss it.
- **Pinning** — pin frequently-used items to keep them at the top,
  separate from your general history.
- **Folders** — organize items into named collections; file any item into
  one or more folders and filter the list down to a folder with a click.
- **Search & filters** — search history by content, or filter by type
  (text, image, colors, gradients).
- **Persistent history** — history and folders survive app restarts,
  stored locally on your machine.
- **Global hotkey** — open the panel from anywhere with a configurable
  keyboard shortcut.
- **Personalization** — light, dark, or system-matched theme.
- **Update checker** — checks GitHub Releases for new versions from the
  Settings → About tab.

## Minimum requirements

- Windows 10, version 1809 (build 17763) or later — Windows 11 recommended
- **Windows App Runtime 2.x** installed (only needed if you're not using a
  self-contained build; self-contained publishes bundle it and need nothing
  extra)

## Building from source

Requires:
- Visual Studio 2022 (17.14+) with the **Windows application development**
  workload, with **Windows App SDK C# Templates** checked
- **.NET 10 SDK** (installed automatically with the workload above, or from
  https://dotnet.microsoft.com)

This project targets **Windows App SDK 2.2.0**. If you hit runtime errors
after installing, make sure a matching **Windows App Runtime 2.x** is on
your machine (Microsoft Store, or the installer from the Windows App SDK
downloads page) — a 1.x runtime won't satisfy a 2.2.0-built app.

```bash
dotnet restore
```

Then open `ReCall.csproj` in Visual Studio and build/run in **x64** or
**ARM64** configuration (WinUI 3 doesn't support AnyCPU).

To publish a self-contained executable from the command line:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true
```

The resulting exe lands in
`bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish\`.

On first run, look for the icon in the system tray (you may need to expand
the "hidden icons" chevron) — click it to open the history panel, then copy
something to see it appear.

## License

TBD
