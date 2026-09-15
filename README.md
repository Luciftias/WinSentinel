<div align="center">

<img src="src/WinSentinel/Assets/logo.png" alt="WinSentinel" width="120" height="120" />

# WinSentinel

**Floating system monitor & optimizer for Windows 10 / 11**

A lightweight, always-on-top "accessibility balloon" style tool that lives in the system
tray, shows live CPU and RAM, and gives you Task-Manager-grade control over processes,
memory working sets, CPU priority/affinity, and startup programs — wrapped in a clean
dark WPF interface.

`C#` · `.NET 8` · `WPF` · `MVVM` · `Win32 P/Invoke`

</div>

---

## 1. System Specification

| Property              | Value                                                              |
|-----------------------|--------------------------------------------------------------------|
| Target framework      | `net8.0-windows`                                                   |
| UI stack              | WPF (XAML) + WinForms `NotifyIcon` (tray only)                    |
| Architecture pattern  | MVVM (services → view models → views)                             |
| Language version      | C# `latest`                                                       |
| Min OS                | Windows 10 (1809+) / Windows 11                                   |
| Privilege level       | `requireAdministrator` (declared in `app.manifest`)              |
| External NuGet deps   | none — registry, charts and gauges are all built in              |
| Platforms             | `AnyCPU`, `x64`                                                   |
| Output                | `WinSentinel.exe` (WinExe, no console window)                    |

---

## 2. Feature Map

```
WinSentinel
├── Floating Balloon         Always-on-top, frameless, translucent, draggable.
│                            Live CPU% + RAM%. Double-click → Dashboard.
├── System Tray              NotifyIcon with hover tooltip ("CPU x%  RAM y%"),
│                            context menu (Open / Toggle Balloon / Trim / Exit),
│                            double-click → Dashboard.
└── Dashboard
    ├── Overview             Circular gauges + real-time sparklines for CPU & RAM,
    │                        memory detail (used / total / free), one-click Trim.
    ├── Processes            Live table sorted by memory. Per-process:
    │                          • End Task (Kill)        — confirmation + guard rail
    │                          • Trim Working Set       — EmptyWorkingSet
    │                          • Set Priority           — Idle … RealTime (guarded)
    │                          • Set CPU Affinity       — per-core checkbox dialog
    └── Startup              HKCU + HKLM Run entries. Add / Remove (HKCU only),
                             with confirmation. HKLM shown read-only for visibility.
```

---

## 3. Project Structure

```
WinSentinel/
├── WinSentinel.sln
├── README.md
├── .gitignore
└── src/
    └── WinSentinel/
        ├── WinSentinel.csproj
        ├── app.manifest                 requireAdministrator + PerMonitorV2 DPI
        ├── App.xaml / App.xaml.cs        Bootstrap: monitor + tray + balloon
        ├── Assets/
        │   ├── app.ico                   Application/window icon (embedded)
        │   ├── tray.ico                  Tray glyph (copied to output)
        │   └── logo.png                  In-app + README logo (WPF resource)
        ├── Models/
        │   ├── MetricSample.cs           Immutable CPU/RAM snapshot
        │   └── ProcessInfo.cs            UI-friendly process row
        ├── Services/
        │   ├── NativeMethods.cs          All P/Invoke (kernel32 / psapi)
        │   ├── SystemMonitorService.cs   GetSystemTimes + GlobalMemoryStatusEx
        │   ├── MemoryOptimizer.cs        EmptyWorkingSet (single + all)
        │   ├── ProcessService.cs         List / Kill / Priority / Affinity / Trim
        │   ├── ProtectedProcesses.cs     Critical-process denylist (guard rail)
        │   └── StartupManager.cs         HKCU/HKLM Run read, HKCU write/remove
        ├── ViewModels/
        │   ├── ViewModelBase.cs          INotifyPropertyChanged base
        │   ├── RelayCommand.cs           ICommand relay
        │   ├── BalloonViewModel.cs       Live CPU/RAM for the balloon
        │   └── DashboardViewModel.cs     Gauges, history, processes, startup
        ├── Controls/
        │   ├── CircularGauge.cs          Owner-drawn arc gauge (no deps)
        │   └── Sparkline.cs              Owner-drawn real-time line graph
        ├── Converters/
        │   └── Converters.cs             Protected-row → grey brush
        ├── Helpers/
        │   └── TrayIconManager.cs        NotifyIcon owner (only WinForms file)
        └── Views/
            ├── BalloonWindow.xaml(.cs)   The floating balloon
            ├── DashboardWindow.xaml(.cs) Tabbed main window
            └── AffinityWindow.xaml(.cs)  Per-core affinity dialog
```

---

## 4. How It Works (Technical Notes)

### 4.1 CPU sampling — `GetSystemTimes`, not PerformanceCounter
`SystemMonitorService` computes CPU load from the deltas of idle / kernel / user times
returned by `kernel32!GetSystemTimes`. Note that on Windows the **kernel time already
includes idle time**, so:

```
busy% = (kernelΔ + userΔ - idleΔ) / (kernelΔ + userΔ) * 100
```

This avoids the classic `PerformanceCounter` pitfalls — the "first read returns 0",
counter database corruption (`0x800007D5`), and broken behaviour on non-English locales.

### 4.2 Memory — `GlobalMemoryStatusEx`
Physical-memory load, total, and available come straight from `MEMORYSTATUSEX`, matching
the percentage Task Manager reports. No counters, no allocation.

### 4.3 Memory trim — `EmptyWorkingSet`
`MemoryOptimizer` calls `psapi!EmptyWorkingSet(hProcess)` (equivalent to
`SetProcessWorkingSetSizeEx(h, -1, -1, 0)`). This pages a process's resident set out so the
"in use" figure drops. **It is a measurement / tuning aid, not a permanent accelerator** —
Windows pages memory back in on demand. The UI states this honestly rather than promising
miracle "RAM cleaning".

### 4.4 Guard rails (safety)
`ProtectedProcesses` is a case-insensitive denylist of OS-critical processes
(`System`, `csrss`, `wininit`, `services`, `lsass`, `smss`, `winlogon`, `svchost`, `dwm`,
`explorer`, WinSentinel itself, …). `Kill`, `SetPriority`, `SetAffinity`, and trim **all
re-check this list at the service layer**, so the guard holds even if the UI is bypassed.
The UI additionally requires confirmation for End Task, real-time priority, and startup
removal.

### 4.5 Startup management
`StartupManager` reads both `HKCU\…\Run` and `HKLM\…\Run` for full visibility, but only
**writes/removes HKCU** to stay least-privilege and per-user reversible. Removing an entry
does not uninstall the program — it just stops it auto-starting.

### 4.6 Tray + balloon
The tray icon (`Helpers/TrayIconManager.cs`) is the only place WinForms is used
(`NotifyIcon`). WinForms implicit usings are removed in the `.csproj` so `System.Windows`
types never clash with `System.Windows.Forms`. The balloon (`Views/BalloonWindow.xaml`) is
`WindowStyle=None` + `AllowsTransparency=True` + `Topmost=True`, dragged with `DragMove()`.

---

## 5. Build & Run

> The project ships as source. Build it on a Windows 10/11 machine with the .NET 8 SDK.

### 5.1 Prerequisites
- Windows 10 (1809+) or Windows 11
- One of:
  - **Visual Studio 2022** (17.8+) with the *.NET desktop development* workload, **or**
  - **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`) for command-line builds

### 5.2 Visual Studio
```
1. Open  WinSentinel.sln
2. Set configuration to Release | x64  (or Any CPU)
3. Build → Build Solution           (Ctrl+Shift+B)
4. Press F5 to run  (accept the UAC elevation prompt)
```

### 5.3 Command line (dotnet CLI)
```bat
:: from the repository root

:: restore + build
dotnet build WinSentinel.sln -c Release

:: run (elevated console recommended so the manifest can elevate cleanly)
dotnet run --project src\WinSentinel\WinSentinel.csproj -c Release
```

### 5.4 Publish a self-contained single EXE (no .NET install needed on the target)
```bat
dotnet publish src\WinSentinel\WinSentinel.csproj -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true

:: output:
:: src\WinSentinel\bin\Release\net8.0-windows\win-x64\publish\WinSentinel.exe
```

Framework-dependent (smaller, needs .NET 8 Desktop Runtime on the target):
```bat
dotnet publish src\WinSentinel\WinSentinel.csproj -c Release -r win-x64 ^
    --self-contained false -p:PublishSingleFile=true
```

---

## 6. Usage

1. Launch `WinSentinel.exe` → accept the UAC prompt (needed to read every process).
2. A small **floating balloon** appears top-left showing live CPU / RAM. Drag it anywhere.
3. **Double-click** the balloon, or **double-click the tray icon**, to open the dashboard.
4. **Overview** tab: watch the gauges + graphs, hit *Trim Memory Now* to reclaim working sets.
5. **Processes** tab: select a row, then End Task / Trim / set Priority / set CPU Affinity.
6. **Startup** tab: review auto-start entries; add or remove your own (HKCU) entries.
7. Right-click the tray icon → **Exit** to quit (closing windows keeps it running in tray).

---

## 7. Safety & Scope

- WinSentinel never terminates or re-prioritises OS-critical processes — they are on a
  hard denylist enforced in the service layer.
- Memory trimming is presented honestly as a tuning aid, not a permanent RAM cleaner.
- Startup edits are confined to the per-user `HKCU` hive and are fully reversible.
- All destructive actions require explicit confirmation.

This is a defensive, user-facing utility for monitoring and tuning **your own machine**.

---

## 8. Extending

- **Per-process CPU%** — sample `Process.TotalProcessorTime` deltas in `ProcessService`.
- **Disk / network counters** — add fields to `MetricSample` and read the relevant
  `PerformanceCounter`s (add the `System.Diagnostics.PerformanceCounter` NuGet package).
- **Theming** — all colours live as resources at the top of `App.xaml`.
- **Auto-start WinSentinel itself** — add its own path via the Startup tab.

---

<div align="center">
<sub>WinSentinel · built with C# / .NET 8 / WPF · MVVM · zero third-party runtime dependencies</sub>
</div>
