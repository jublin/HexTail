# HexTail — native Avalonia desktop

Date: 2026-08-03  
Status: Implemented

## Overview

HexTail is a native .NET 10 desktop application built with Avalonia 12.1.1. It tails plaintext and logfmt files, supports multiple files and searches, highlights matches, and shows an optional inline context pane. The UI is rendered by Avalonia controls and XAML; it does not host a browser, WebView, JavaScript runtime, or HTTP server.

## Goals

- Tail multiple files simultaneously, each in its own file tab.
- Parse plaintext and logfmt lines without changing the raw log display.
- Support literal and regular-expression searches with case sensitivity and per-search colors.
- Highlight active searches and global labels in the All view.
- Show a scrollable full-file context view; selecting a main-view line jumps
  the context view to that line.
- Open files from the native file picker, OS drag-and-drop, and command-line paths.
- Restore open files, searches, settings, window geometry, and pane size on restart.

## Non-goals

- Browser/PWA delivery, embedded web content, or a local HTTP server.
- Native installers, signing, auto-update, tray integration, or platform-specific menus.
- A third-party control suite or an MVVM framework.
- Writing log files or collecting remote logs.

## Architecture

- **Avalonia window** — `Window`/XAML code-behind owns the single visible workspace and dispatcher timer.
- **Application state** — `AppState` owns file tabs, settings, searches, and persistence; it publishes a simple `Changed` event.
- **Tailer layer** — one background `FileTailer` per open path watches appends, truncation, rotation, and missing-file recovery through an event channel.
- **Domain layer** — `FileBuffer`, `Line`, parser, and search types are UI-independent and enforce the line cap and result rebasing.
- **Persistence** — `JsonFileAppPersistence` writes `AppConfig` atomically under the OS application-data directory.

## Controls

The workspace uses documented Avalonia controls:

- `SplitView` for the settings pane.
- `TabControl`/`TabItem` for file and search views.
- `TextBox`, `ComboBox`, `CheckBox`, `ColorPicker`, and `Button` for input.
- Virtualized `ListBox`/`VirtualizingStackPanel` for log and context rows.
- `GridSplitter` for the context pane.
- `StorageProvider` and `DragDrop` for native file operations.

Log rows are `TextBlock` instances containing `Run` inlines, so matching ranges are drawn without HTML markup.

## Data flow

1. The desktop lifetime creates `MainWindow`, `TailerService`, `JsonFileAppPersistence`, and `AppState`.
2. Startup paths and restored paths are opened through `AppState.OpenFileAsync`.
3. Each tailer reads complete lines and pushes immutable events to the channel.
4. The Avalonia dispatcher drains events, parses/appends lines, updates searches, and refreshes visible virtualized lists.
5. Selecting a row records its buffer index; the context view selects and
   scrolls to the matching line while remaining independently scrollable.
6. Closing the window saves state and disposes every tailer.

## Search and filtering

- Empty queries are ignored.
- Invalid regular expressions stay in the search form and show an inline error.
- Search result indices are buffer-relative and are rebased when old lines roll out.
- Global exclusions hide matching rows from every visible log/context list but do not remove them from the buffer.
- Global labels produce additional case-insensitive highlight ranges in both
  the main and context views.

## Limits and errors

- The default buffer cap is 100,000 lines per file; `AppSettings.MaxLines` controls it.
- Missing files are represented by error tabs and recover when the path reappears.
- Truncation and rotation clear the affected buffer before new lines arrive.
- Malformed or inaccessible session JSON is treated as a first launch; the last good file remains intact during atomic saves.
