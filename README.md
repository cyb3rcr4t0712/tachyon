<div align="center">

# ⚡ Tachyon

**Faster-than-light Windows file search.**

Tachyon bypasses the Windows filesystem API entirely and reads the NTFS Master File Table directly via raw Win32 volume handles. It indexes millions of files in seconds and stays live using the USN Journal — no rescans, no polling the disk.

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue?style=flat-square&logo=windows)](https://github.com/cyb3rcr4t0712/tachyon)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-purple?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Admin Required](https://img.shields.io/badge/requires-Administrator-red?style=flat-square&logo=windows-terminal)](https://github.com/cyb3rcr4t0712/tachyon#setup--running)

</div>

---

## What it does

| | |
|---|---|
| **226,000 files/sec** | Raw MFT streaming via `FSCTL_ENUM_USN_DATA` |
| **< 5ms search** | In-memory filter with `ReaderWriterLockSlim` |
| **Instant updates** | USN Journal tail — no rescans after first boot |
| **~50ms reload** | Binary cache at `%LOCALAPPDATA%\Tachyon\index.bin` |

---

## Benchmarks

> Measured on a real machine with 4 NTFS volumes.

| Operation | Result | Notes |
| :--- | :--- | :--- |
| **Initial MFT Scan** | ~15.1s | 3,417,011 files across 4 drives at ~226,000 files/sec |
| **Cached Startup** | 50ms - 150ms | Deserializes `index.bin` with 1 MB read buffers |
| **Search Query** | < 5ms | In-memory `Contains()` with `OrdinalIgnoreCase` |
| **Live Update** | Instant | 500ms USN Journal poll, write-locked index patch |
| **UI Render** | 60 FPS | `VirtualizingStackPanel` with container recycling |

**Drive breakdown:**

| Drive | Files Indexed |
| :---: | ---: |
| `B:\` | 11,383 |
| `C:\` | 2,131,360 |
| `T:\` | 3,038 |
| `X:\` | 1,272,230 |
| **Total** | **3,417,011** |

### Why is it this fast?

Standard `Directory.GetFiles()` must recursively walk directory trees, issuing a separate I/O call per folder and resolving ACLs at every node. On a drive with millions of files this takes minutes.

Tachyon opens a raw volume handle (`\\.\C:`) and fires a single `FSCTL_ENUM_USN_DATA` ioctl. Windows streams every MFT record directly into a 256KB unmanaged buffer in one shot. Records are parsed with unsafe pointer arithmetic and `ReadOnlySpan<char>` to avoid heap pressure. All drives run in parallel via `Parallel.ForEach`.

The initial scan happens once. After that, the binary cache loads in milliseconds and the background USN watcher patches the in-memory index as files change — no rescans ever again.

---

## Features

- **WPF GUI** — dark theme, virtualized list, instant search-as-you-type
- **CLI tool** — scriptable, benchmarkable, runs headless
- **Context menu** — copy path, open in Explorer, compute SHA-256, VirusTotal lookup
- **Rescan button** — force a full rebuild and cache flush
- **Smart skip** — non-NTFS drives (FAT32, exFAT, ReFS, network) are skipped automatically
- **USN Journal hint** — if a drive's journal is off, the CLI prints the exact `fsutil` command to enable it

---

## Getting Started

### Requirements

- Windows 10 / 11 (64-bit)
- .NET 8 SDK or later
- NTFS-formatted drives
- **Administrator rights** (required to open raw volume handles)

### Run the GUI

```powershell
Start-Process -FilePath ".\MftSearchWpf\bin\Release\net8.0-windows\win-x64\publish\MftSearchWpf.exe" -Verb RunAs
```

### Run the CLI

Open an **Administrator PowerShell**, then:

```powershell
.\MftSearch\bin\Debug\net10.0\MftSearch.exe
```

Type a filename to search. Type `exit`, `quit`, or `q` to close.

### Build from Source

```cmd
# WPF GUI
dotnet build MftSearchWpf/MftSearchWpf.csproj -c Release

# CLI
dotnet build MftSearch/MftSearch.csproj -c Release
```

### Enable USN Journal on a drive

If a drive is skipped at startup, run this once as Administrator:

```powershell
fsutil usn createjournal m=33554432 a=8388608 D:
```

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                        Startup                          │
│   Load cache (index.bin)  ──or──  Full MFT scan         │
│             ↓                           ↓               │
│     IndexPersistenceService        MftEngine            │
│       BinaryReader / 1MB         FSCTL_ENUM_USN_DATA    │
│       buffer, v2 header          Parallel.ForEach       │
└───────────────────────┬─────────────────────────────────┘
                        │
                        ▼
            ┌───────────────────────┐
            │   In-Memory Index     │
            │  List<FileRecord>     │
            │  ReaderWriterLockSlim │
            └───────┬───────┬───────┘
                    │       │
          ┌─────────┘       └──────────┐
          ▼                            ▼
  UsnWatcherService             MainViewModel
  FSCTL_READ_USN_JOURNAL        CancellationTokenSource
  500ms poll / drive            200-result cap
  Write lock on changes         Search-as-you-type
          │
          ▼
  UI status bar update
  "Live index updated (+N changes)"
```

### Key components

| File | Responsibility |
| :--- | :--- |
| `MftEngine.cs` | Raw MFT enumeration, unsafe buffer parsing, FRN path resolution |
| `UsnWatcherService.cs` | Background USN Journal tail, incremental index patches |
| `IndexPersistenceService.cs` | Atomic binary cache read/write with version header |
| `MainViewModel.cs` | Search logic, locking, watcher lifecycle, command bindings |
| `MainWindow.xaml` | Dark-theme WPF UI, virtualized list, context menu |
| `Program.cs` | Standalone CLI, same MFT logic, interactive search loop |

---

## Project Layout

```
Tachyon/
├── MftSearchWpf/              # WPF GUI
│   ├── Models/FileRecord.cs
│   ├── Services/
│   │   ├── MftEngine.cs
│   │   ├── UsnWatcherService.cs
│   │   └── IndexPersistenceService.cs
│   ├── ViewModels/MainViewModel.cs
│   └── Views/MainWindow.xaml
│
└── MftSearch/                 # CLI tool
    └── Program.cs
```

---

## License

[MIT](LICENSE) © 2026 cyb3rcr4t0712
