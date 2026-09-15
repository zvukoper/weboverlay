# WebOverlay

[![.NET](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/)
[![CI](https://github.com/zvukoper/weboverlay/actions/workflows/ci.yml/badge.svg)](https://github.com/zvukoper/weboverlay/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**WebOverlay** — a lightweight Windows overlay for web content, designed for use over desktop applications and games such as Euro Truck Simulator 2.

## Features

- Transparent, borderless, always-on-top overlay window.
- WebView2-based web content, including local HTML files.
- Real mouse interaction with buttons, dialogs and other interactive web content when clickability is enabled.
- Click-through mode when interaction is disabled.
- Global hotkeys, including manipulation mode that works even when the overlay is not focused.
- Multiple overlay windows with active/inactive selection borders.
- Manipulation mode forces web content to remain visible and restores its previous visibility state when manipulation mode ends.
- Information strip above each selected window showing content name, size, zoom, position, bounds, monitor and clickability state.
- Move the active overlay cyclically between available monitors.
- Continuous 1-pixel movement while holding the configured I/J/K/L movement shortcuts in manipulation mode.
- Window state is stored independently for each web content URL and monitor.
- Manipulation mode calls `debugShow()` in the loaded web content when the active window enters manipulation mode.
- Single-instance operation with `append` support for opening additional windows.
- Built-in multilingual support: English, Russian, French, German, Spanish, Chinese, Japanese and Arabic.
- Self-contained single-file Windows publication.

## Hotkeys

All listed controls are global unless noted otherwise. Bindings can be changed in `%AppData%\WebOverlay\config.json`.

| Combination | Action |
|---|---|
| `Ctrl+Shift+Alt+O` | Enter/leave manipulation mode |
| `Ctrl+Shift+Alt+PageUp` | Select previous overlay window |
| `Ctrl+Shift+Alt+PageDown` | Select next overlay window |
| `Ctrl+Shift+Alt+J` | Move active window left |
| `Ctrl+Shift+Alt+I` | Move active window up |
| `Ctrl+Shift+Alt+K` | Move active window down |
| `Ctrl+Shift+Alt+L` | Move active window right |
| `Ctrl+Shift+Alt+[` | Decrease width |
| `Ctrl+Shift+Alt+]` | Increase width |
| `Ctrl+Shift+Alt+;` | Decrease height |
| `Ctrl+Shift+Alt+'` | Increase height |
| `Ctrl+Shift+Alt+P` | Hide/show web content |
| `Ctrl+Shift+Alt+U` | Toggle mouse clickability |
| `Ctrl+Shift+Alt+\` | Move active window to the next monitor |
| `Ctrl+Shift+Alt++` | Zoom in |
| `Ctrl+Shift+Alt+-` | Zoom out |
| `Esc` | Close the overlay application |

## Manipulation mode

Press `Ctrl+Shift+Alt+O` to enter manipulation mode. The selected overlay is highlighted with a yellow border, other overlays with a blue border, and the information strip is shown above each overlay.

While manipulation mode is active, holding the configured movement shortcuts continuously moves the active window by **1 pixel per timer tick**. The movement no longer depends on repeated `KeyDown` messages from WebView2.

When the active overlay is on a monitor that already has saved state for the current web content, that monitor's saved position, size and zoom are restored. The first time the content is used on a monitor without saved state, a new monitor-specific state file is created from the current/default state.

The active web page receives a `debugShow()` call when manipulation mode becomes active (and briefly retried after navigation/activation so pages that define the function later still receive it).

## Running

Without arguments, WebOverlay opens its built-in help page:

```text
WebOverlay.exe
```

Open a web page directly:

```text
WebOverlay.exe https://example.com
```

Open a local HTML file:

```text
WebOverlay.exe file:///C:/path/to/page.html
```

Add another overlay without replacing the current one:

```text
WebOverlay.exe append https://example.org
```

Only one WebOverlay process runs at a time. A subsequent launch sends its command to the existing instance.

## Configuration

Configuration is stored in:

```text
%AppData%\WebOverlay\config.json
```

Example:

```json
{
  "Language": "en",
  "Clickable": true,
  "ToggleLock": "Ctrl+Shift+Alt+O",
  "MoveLeft": "Ctrl+Shift+Alt+J",
  "MoveRight": "Ctrl+Shift+Alt+L",
  "MoveUp": "Ctrl+Shift+Alt+I",
  "MoveDown": "Ctrl+Shift+Alt+K",
  "ZoomIn": "Ctrl+Shift+Alt+OemPlus",
  "ZoomOut": "Ctrl+Shift+Alt+OemMinus",
  "ToggleHide": "Ctrl+Shift+Alt+P",
  "ToggleClickable": "Ctrl+Shift+Alt+U",
  "MoveMonitor": "Ctrl+Shift+Alt+Oem5",
  "ResizeWidthDecrease": "Ctrl+Shift+Alt+OemOpenBrackets",
  "ResizeWidthIncrease": "Ctrl+Shift+Alt+OemCloseBrackets",
  "ResizeHeightDecrease": "Ctrl+Shift+Alt+OemSemicolon",
  "ResizeHeightIncrease": "Ctrl+Shift+Alt+OemQuotes",
  "ResizeStep": 10
}
```

Per-content and per-monitor window state is stored under `%AppData%\WebOverlay\config\`.

## Build from source

Requirements:

- Windows 10/11 x64.
- .NET 10 SDK.
- Microsoft Edge WebView2 Runtime.

Build:

```powershell
dotnet restore WebOverlay.csproj
dotnet build WebOverlay.csproj -c Release
```

Publish the self-contained single-file version:

```powershell
dotnet publish WebOverlay.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -o ./publish
```

The resulting executable is `publish\WebOverlay.exe`.

## Continuous Integration

GitHub Actions workflow: `.github/workflows/ci.yml`.

The CI runs on pushes to `main`, pull requests targeting `main`, and manual dispatch. It uses a Windows runner and performs dependency restore, Release build, self-contained `win-x64` publish, verification of `WebOverlay.exe`, and artifact upload.

The workflow also sets `CI=true`, so the local-only post-publish copy step is not executed on GitHub runners.

## Data and locales

```text
%AppData%\WebOverlay\
├── config.json
├── debug.log
├── config\
│   └── <content>__monitor_<monitor>.txt
└── locales\
    ├── en.txt
    ├── ru.txt
    ├── fr.txt
    ├── de.txt
    ├── es.txt
    ├── zh.txt
    ├── ja.txt
    └── ar.txt
```

Locale files are plain `key=value` text files. The first run asks for a language and saves the choice in `config.json`.

## License

This project is distributed under the [MIT License](LICENSE).
