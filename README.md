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

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish