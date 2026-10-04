# Tachyon

**Tachyon** is a fast Windows file search utility for NTFS drives. It bypasses standard filesystem APIs by directly reading the NTFS Master File Table (MFT) via raw Win32 volume handles, and stays current by tailing the USN (Update Sequence Number) Journal in the background — no disk rescans required.

> **Requires Administrator privileges.** Raw NTFS volume handles cannot be opened without elevated rights.

---

## Features

* Indexes **3.4 million files across 4 drives in ~15 seconds**
* In-memory search with **< 5ms query response**
* Live index updates via **USN Journal polling** — no rescans after startup
* **Persistent cache** (`index.bin`) for instant reloads on next launch
* WPF GUI with dark theme, virtualized list, and context menu actions
* Lightweight CLI for scripting and benchmarking

---

## Benchmarks

Measured on a real machine with 4 NTFS volumes (`B:\`, `C:\`, `T:\`, `X:\`).

| Operation | Measured | Notes |
| :--- | :--- | :--- |
| **Initial MFT Scan** | ~15.1s (3.4M files) | ~226,000 files/sec via raw volume handle |
| **Cached Startup Load** | ~50ms - 150ms | Reads `%LOCALAPPDATA%\Tachyon\index.bin` |
| **Search Query Response** | < 5ms | In-memory `Contains()` filter with read lock |
| **USN Journal Delta** | Instant | Background thread, 500ms poll interval |
| **UI List Rendering** | 60 FPS | WPF `VirtualizingStackPanel` with recycling |

**Drive breakdown from benchmark run:**

| Drive | Files Indexed |
| :--- | :--- |
| `B:\` | 11,383 |
| `C:\` | 2,131,360 |
| `T:\` | 3,038 |
| `X:\` | 1,272,230 |
| **Total** | **3,417,011** |

### Why is it fast?

Standard C# `Directory.GetFiles()` recursively walks folder trees, issuing thousands of individual I/O calls and resolving permissions at every node. Tachyon skips all of that by opening a raw volume handle and issuing a single `FSCTL_ENUM_USN_DATA` ioctl, which streams every file record directly from the MFT into a memory buffer.

The ~15 second initial scan is a one-time cost. Every subsequent launch reads the serialized cache in milliseconds, and the background USN watcher keeps it current without any further disk scans.

---

## Executables

### GUI (WPF)

| Build | Path |
| :--- | :--- |
| Standalone (self-contained) | `MftSearchWpf\bin\Release\net8.0-windows\win-x64\publish\MftSearchWpf.exe` |
| Framework-dependent | `MftSearchWpf\bin\Release\net8.0-windows\win-x64\MftSearchWpf.exe` |
| Debug | `MftSearchWpf\bin\Debug\net8.0-windows\MftSearchWpf.exe` |

```powershell
Start-Process -FilePath ".\MftSearchWpf\bin\Release\net8.0-windows\win-x64\publish\MftSearchWpf.exe" -Verb RunAs
```

### CLI

```powershell
Start-Process -FilePath ".\MftSearch\bin\Debug\net10.0\MftSearch.exe" -Verb RunAs
```

Or open an **Administrator PowerShell** and run directly (recommended for benchmarking):

```powershell
.\MftSearch\bin\Debug\net10.0\MftSearch.exe
```

---

## Setup & Running

### Prerequisites

* Windows 10 / 11 (64-bit)
* .NET 8 SDK or later
* NTFS-formatted drives
* Administrator rights

### USN Journal Requirement

Tachyon requires the NTFS USN Journal to be active on each drive it indexes. It is enabled by default on most system drives. If a drive is skipped with a `[SKIPPED] USN Journal is disabled` message, enable it with:

```powershell
# Replace D: with your drive letter
fsutil usn createjournal m=33554432 a=8388608 D:
```

### Build from Source

```cmd
# WPF GUI
dotnet build MftSearchWpf/MftSearchWpf.csproj -c Release

# CLI
dotnet build MftSearch/MftSearch.csproj -c Release
```

---

## Architecture

### 1. MFT Indexing (`MftEngine.cs` / `Program.cs`)

Opens raw NTFS volume handles via `kernel32.dll!CreateFile` and streams all file records using `FSCTL_ENUM_USN_DATA`. Records are parsed from unmanaged memory buffers using unsafe pointer arithmetic and `ReadOnlySpan<char>` to minimize heap allocations. Drives are processed in parallel via `Parallel.ForEach`. Parent FRNs (File Reference Numbers) are resolved into full paths in a second pass.

### 2. Live Change Tracking (`UsnWatcherService.cs`)

A `LongRunning` background task polls `FSCTL_READ_USN_JOURNAL` every 500ms per drive. Only the following events are captured:

* `USN_REASON_FILE_CREATE`
* `USN_REASON_FILE_DELETE`
* `USN_REASON_RENAME_NEW_NAME`
* `USN_REASON_RENAME_OLD_NAME`

Each drive maintains its own `NextUsn` cursor so only incremental deltas are fetched. Journal truncation (`ERROR_MORE_DATA` / `ERROR_JOURNAL_ENTRY_DELETED`) triggers a full rescan flag.

### 3. Cache (`IndexPersistenceService.cs`)

Serializes the full file list to `%LOCALAPPDATA%\Tachyon\index.bin` using `BinaryWriter` with 1 MB buffers. Writes go to a `.tmp` file first, then atomically replace the target via `File.Move(..., overwrite: true)`. Format is validated on load via a version header (`FormatVersion = 2`).

### 4. Concurrency (`MainViewModel.cs`)

Search operations acquire a `ReaderWriterLockSlim` read lock. USN background updates acquire write locks. Rapid keystrokes cancel in-flight searches via a `CancellationTokenSource`. Results are capped at 200 items to keep UI throughput consistent.

### 5. UI (`MainWindow.xaml`)

Dark theme (`#1e1e1e`), `VirtualizingStackPanel` with container recycling, and an `InverseBooleanConverter` to disable inputs during index rebuilds. Context menu actions: copy path, open in Explorer, compute SHA-256, and look up the hash on VirusTotal.

---

## Project Layout

```
Tachyon/
├── README.md
├── LICENSE
├── create_shortcut.ps1
│
├── MftSearchWpf/              # WPF GUI application
│   ├── App.xaml / App.xaml.cs
│   ├── Models/
│   │   └── FileRecord.cs
│   ├── Services/
│   │   ├── MftEngine.cs
│   │   ├── UsnWatcherService.cs
│   │   └── IndexPersistenceService.cs
│   ├── ViewModels/
│   │   ├── MainViewModel.cs
│   │   ├── ViewModelBase.cs
│   │   └── RelayCommand.cs
│   └── Views/
│       └── MainWindow.xaml
│
└── MftSearch/                 # Standalone CLI tool
    └── Program.cs
```

---

## License

[MIT License](LICENSE)
