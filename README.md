# WebOverlay

**Прозрачное оверлей-окно с веб-контентом | Transparent overlay window with web content**

[![.NET](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 📋 Table of Contents / Оглавление

- [🇬🇧 English](#-english)
  - [Description](#description)
  - [Features](#features)
  - [Installation & Running](#installation--running)
  - [Global Hotkeys (Controls)](#global-hotkeys-controls)
  - [Configuration & Customization](#configuration--customization)
  - [File Structure](#file-structure)
  - [Requirements](#requirements)
  - [Build from Source](#build-from-source)
  - [License](#license)
- [🇷🇺 Русский](#-русский)
  - [Описание](#описание)
  - [Возможности](#возможности)
  - [Установка и запуск](#установка-и-запуск)
  - [Горячие клавиши (управление)](#горячие-клавиши-управление)
  - [Настройка и кастомизация](#настройка-и-кастомизация)
  - [Структура файлов](#структура-файлов)
  - [Требования](#требования)
  - [Сборка из исходников](#сборка-из-исходников)
  - [Лицензия](#лицензия)

---

## 🇬🇧 English

### Description

**WebOverlay** is a lightweight Windows application that creates a transparent, borderless window always on top of other windows. It loads a web page (or a local HTML file) and allows you to interact with it while keeping the background transparent. The window can be locked (click‑through mode) or unlocked to interact with the page, moved, resized, and zoomed – all via global hotkeys.

The application remembers the position, size and zoom level for each URL separately. It supports multiple languages (English, Russian, French, German, Spanish, Chinese, Japanese, Arabic) and can be easily extended with your own localization files.

---

### Features

- ✅ **Fully transparent background** – integrates seamlessly with any desktop or game.
- ✅ **Always on top** – stays above all other windows.
- ✅ **Lock/unlock mode** – when locked, all clicks pass through; when unlocked, you can interact with the web page.
- ✅ **Keyboard control** – move, resize, zoom, hide/show, toggle clickability, and toggle lock using global hotkeys.
- ✅ **Clickability toggle** – `Ctrl+Shift+Alt+U` enables/disables mouse interaction with the web page (state saved in config).
- ✅ **Separate state per URL** – position, size and zoom are saved independently for each address.
- ✅ **Single‑instance** – only one instance runs; subsequent launches with a new URL reload the content in the existing window.
- ✅ **Multi‑language** – choose your language at first launch; easily add custom locales.
- ✅ **Clickable config link** – in the language selection dialog, click the config path to open it (or the folder if it doesn't exist yet).
- ✅ **Portable** – published as a single executable (self‑contained) – no .NET Runtime required.
- ✅ **Command‑line support** – pass a URL as an argument to load it directly.

---

### Installation & Running

#### 1. Publish the application (one‑time)

Open a terminal in the project folder and run:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

The executable `WebOverlay.exe` will be placed in the `publish` folder.

#### 2. Add to PATH (optional)

To run `weboverlay.exe` from any directory, add the `publish` folder (or wherever you copied the .exe) to your system `PATH` environment variable.

#### 3. Launch

- **Without arguments** – opens a built‑in help page explaining all features and hotkeys.

    weboverlay.exe

- **With a URL** – loads the specified web page.

    weboverlay.exe https://example.com

or a local file:

    weboverlay.exe file:///C:/path/to/page.html

> **Note:** On the first run, you will be prompted to choose your language. The selection is saved in `%AppData%\WebOverlay\config.json`. You can click the config path in the dialog to open the file (or the folder if it doesn't exist yet).

---

### Global Hotkeys (Controls)

All hotkeys work globally – even when the window is not focused.

| Key Combination                              | Action                                      |
|----------------------------------------------|---------------------------------------------|
| `Ctrl+Shift+Alt+O`                           | **Lock / Unlock** the window                |
| `Ctrl+Shift+Alt+J`                           | Move window **left** (5px)                  |
| `Ctrl+Shift+Alt+I`                           | Move window **up** (5px)                    |
| `Ctrl+Shift+Alt+K`                           | Move window **down** (5px)                  |
| `Ctrl+Shift+Alt+L`                           | Move window **right** (5px)                 |
| `Ctrl+Shift+Alt+[` (open bracket)            | **Decrease width** (10px)                   |
| `Ctrl+Shift+Alt+]` (close bracket)           | **Increase width** (10px)                   |
| `Ctrl+Shift+Alt+;` (semicolon)               | **Decrease height** (10px)                  |
| `Ctrl+Shift+Alt+'` (apostrophe)              | **Increase height** (10px)                  |
| `Ctrl+Shift+Alt+P`                           | **Hide / Show** the window                  |
| `Ctrl+Shift+Alt+U`                           | **Toggle clickability** (enable/disable mouse interaction) |
| `Ctrl+Shift+Alt++` (plus)                    | **Zoom in** (0.1 step, range 0.3–3.0)       |
| `Ctrl+Shift+Alt+-` (minus)                   | **Zoom out** (0.1 step, range 0.3–3.0)      |
| `Esc`                                        | **Close** the application                   |

> The actual key bindings can be remapped in `config.json` (see below).

---

### Configuration & Customization

All settings are stored in `%AppData%\WebOverlay\`.

#### `config.json`

Contains language selection and key bindings. Example:

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
      "ResizeWidthDecrease": "Ctrl+Shift+Alt+OemOpenBrackets",
      "ResizeWidthIncrease": "Ctrl+Shift+Alt+OemCloseBrackets",
      "ResizeHeightDecrease": "Ctrl+Shift+Alt+OemSemicolon",
      "ResizeHeightIncrease": "Ctrl+Shift+Alt+OemQuotes",
      "ResizeStep": 10
    }

You can edit this file to change the language, toggle clickability, or redefine any hotkey.

#### Locales

Localization files are stored in `%AppData%\WebOverlay\locales\*.txt`.  
Each file uses `key=value` pairs. To add a new language, create a `<lang>.txt` file (e.g., `it.txt`) and set `"Language": "it"` in `config.json`.

---

### File Structure

    %AppData%\WebOverlay\
    ├── config.json                # main configuration
    ├── debug.log                  # debug log (if any errors)
    ├── config\                    # per‑URL state files
    │   └── <url_hash>.txt         # position, size, zoom
    └── locales\                   # localization files
        ├── en.txt
        ├── ru.txt
        ├── fr.txt
        ├── de.txt
        ├── es.txt
        ├── zh.txt
        ├── ja.txt
        └── ar.txt

---

### Requirements

- Windows 10 / 11 (64‑bit)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) – usually already installed on Windows 11.

---

### Build from Source

    git clone https://github.com/yourusername/WebOverlay.git
    cd WebOverlay
    dotnet build -c Release

---

### License

This project is distributed under the [MIT License](LICENSE).

---

## 🇷🇺 Русский

### Описание

**WebOverlay** – это лёгкое Windows-приложение, создающее прозрачное окно без рамок, которое всегда находится поверх всех окон. Оно загружает веб-страницу (или локальный HTML-файл) и позволяет взаимодействовать с ней, сохраняя прозрачный фон. Окно можно заблокировать (режим «сквозного клика») или разблокировать для взаимодействия со страницей, перемещать, изменять размер, масштабировать и включать/выключать кликабельность – всё через глобальные горячие клавиши.

Приложение запоминает позицию, размер и масштаб отдельно для каждого URL. Поддерживается несколько языков (английский, русский, французский, немецкий, испанский, китайский, японский, арабский), и вы можете легко добавить свои локализации.

---

### Возможности

- ✅ **Полностью прозрачный фон** – гармонично вписывается в рабочий стол или игру.
- ✅ **Всегда поверх всех окон** – не перекрывается другими приложениями.
- ✅ **Режимы блокировки/разблокировки** – в заблокированном состоянии клики проходят сквозь; в разблокированном – можно взаимодействовать со страницей.
- ✅ **Управление с клавиатуры** – перемещение, изменение размера, масштабирование, скрытие/показ, переключение кликабельности и режима глобальными хоткеями.
- ✅ **Переключение кликабельности** – `Ctrl+Shift+Alt+U` включает/выключает возможность взаимодействия мышью (состояние сохраняется в конфиге).
- ✅ **Раздельное сохранение состояния для каждого URL** – позиция, размер и масштаб сохраняются независимо для каждого адреса.
- ✅ **Один экземпляр** – при повторном запуске с новым URL содержимое перезагружается в существующем окне.
- ✅ **Многоязычность** – выбор языка при первом запуске; легко добавить свои локализации.
- ✅ **Кликабельная ссылка на конфиг** – в диалоге выбора языка можно кликнуть на путь к конфигу, чтобы открыть его (или папку, если файла ещё нет).
- ✅ **Переносимость** – публикуется как один EXE-файл (самодостаточный) – не требует установленного .NET Runtime.
- ✅ **Поддержка командной строки** – передайте URL как аргумент для прямой загрузки.

---

### Установка и запуск

#### 1. Публикация приложения (один раз)

Откройте терминал в папке проекта и выполните:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

Исполняемый файл `WebOverlay.exe` появится в папке `publish`.

#### 2. Добавление в PATH (по желанию)

Чтобы запускать `weboverlay.exe` из любой папки, добавьте папку `publish` (или туда, куда вы скопировали .exe) в системную переменную `PATH`.

#### 3. Запуск

- **Без аргументов** – открывается встроенная справочная страница со всеми функциями и комбинациями клавиш.

    weboverlay.exe

- **С URL** – загружается указанная веб-страница.

    weboverlay.exe https://example.com

или локальный файл:

    weboverlay.exe file:///C:/path/to/page.html

> **Примечание:** При первом запуске будет предложено выбрать язык. Выбор сохраняется в `%AppData%\WebOverlay\config.json`. В диалоге можно кликнуть на путь к конфигу, чтобы открыть файл (или папку, если файл ещё не создан).

---

### Горячие клавиши (управление)

Все комбинации работают глобально – даже когда окно не в фокусе.

| Комбинация клавиш                            | Действие                                      |
|----------------------------------------------|-----------------------------------------------|
| `Ctrl+Shift+Alt+O`                           | **Заблокировать / Разблокировать** окно       |
| `Ctrl+Shift+Alt+J`                           | Переместить окно **влево** (5px)              |
| `Ctrl+Shift+Alt+I`                           | Переместить окно **вверх** (5px)              |
| `Ctrl+Shift+Alt+K`                           | Переместить окно **вниз** (5px)               |
| `Ctrl+Shift+Alt+L`                           | Переместить окно **вправо** (5px)             |
| `Ctrl+Shift+Alt+[`  (открывающая скобка)     | **Уменьшить ширину** (10px)                   |
| `Ctrl+Shift+Alt+]`  (закрывающая скобка)     | **Увеличить ширину** (10px)                   |
| `Ctrl+Shift+Alt+;`  (точка с запятой)        | **Уменьшить высоту** (10px)                   |
| `Ctrl+Shift+Alt+'`  (апостроф)               | **Увеличить высоту** (10px)                   |
| `Ctrl+Shift+Alt+P`                           | **Скрыть / Показать** окно                    |
| `Ctrl+Shift+Alt+U`                           | **Включить / Выключить кликабельность** (взаимодействие мышью) |
| `Ctrl+Shift+Alt++`  (плюс)                   | **Увеличить масштаб** (шаг 0.1, диапазон 0.3–3.0) |
| `Ctrl+Shift+Alt+-`  (минус)                  | **Уменьшить масштаб** (шаг 0.1, диапазон 0.3–3.0) |
| `Esc`                                        | **Закрыть** приложение                        |

> Реальные привязки клавиш можно изменить в `config.json` (см. ниже).

---

### Настройка и кастомизация

Все настройки хранятся в `%AppData%\WebOverlay\`.

#### `config.json`

Содержит выбор языка, состояние кликабельности и привязки клавиш. Пример:

    {
      "Language": "ru",
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
      "ResizeWidthDecrease": "Ctrl+Shift+Alt+OemOpenBrackets",
      "ResizeWidthIncrease": "Ctrl+Shift+Alt+OemCloseBrackets",
      "ResizeHeightDecrease": "Ctrl+Shift+Alt+OemSemicolon",
      "ResizeHeightIncrease": "Ctrl+Shift+Alt+OemQuotes",
      "ResizeStep": 10
    }

Вы можете редактировать этот файл, чтобы изменить язык, состояние кликабельности или переназначить любую комбинацию клавиш.

#### Локализация

Файлы локализации хранятся в `%AppData%\WebOverlay\locales\*.txt`.  
Каждый файл содержит пары `ключ=значение`. Чтобы добавить новый язык, создайте файл `<lang>.txt` (например, `it.txt`) и установите `"Language": "it"` в `config.json`.

---

### Структура файлов

    %AppData%\WebOverlay\
    ├── config.json                # основной конфиг
    ├── debug.log                  # лог отладки (если есть ошибки)
    ├── config\                    # файлы состояния для каждого URL
    │   └── <url_hash>.txt         # позиция, размер, масштаб
    └── locales\                   # файлы локализаций
        ├── en.txt
        ├── ru.txt
        ├── fr.txt
        ├── de.txt
        ├── es.txt
        ├── zh.txt
        ├── ja.txt
        └── ar.txt

---

### Требования

- Windows 10 / 11 (64‑bit)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) – обычно уже установлен в Windows 11.

---

### Сборка из исходников

    git clone https://github.com/yourusername/WebOverlay.git
    cd WebOverlay
    dotnet build -c Release

---

### Лицензия

Проект распространяется под лицензией [MIT](LICENSE).

---

**Enjoy! / Приятного использования!** 🚀