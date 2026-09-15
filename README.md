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
- Small information strip above each selected window showing content name, size, zoom, position, bounds, monitor and clickability state.
- Move the active overlay cyclically between available monitors.
- Position, size and zoom are saved separately for each web content URL.
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

Press `Ctrl+Shift+Alt+O` to enter manipulation mode. In this mode the selected overlay is highlighted and the content remains visible even when it was hidden by normal application logic. The information strip above the window is also shown.

Press the same hotkey again to leave manipulation mode. The normal content visibility logic is restored.

`Ctrl+Shift+Alt+\` cycles the active overlay through all detected monitors. The overlay keeps its relative position where possible and is clamped to the target monitor's working area.

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

Per-content window state is stored under `%AppData%\WebOverlay\config\` and contains position, size and zoom information.

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

Publish the same self-contained single-file format used by CI:

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

The CI runs on pushes to `main`, pull requests targeting `main`, and manual dispatch. It uses a Windows runner and performs:

1. Dependency restore.
2. Release build.
3. Self-contained `win-x64` single-file publish.
4. Verification that `WebOverlay.exe` was produced.
5. Upload of the published executable as a workflow artifact for 14 days.

The CI also sets `CI=true`, so the local-only post-publish copy in the project file is not executed on GitHub runners.

## Data and locales

```text
%AppData%\WebOverlay\
├── config.json
├── debug.log
├── config\
│   └── <content-state>.txt
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

Localization files use simple `key=value` pairs. Additional languages can be added through the same format.

## License

This project is distributed under the [MIT License](LICENSE).
