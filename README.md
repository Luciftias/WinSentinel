<div align="center">

<img src="src/WinSentinel/Assets/logo.png" alt="WinSentinel" width="120" height="120" />

# WinSentinel

**Floating system monitor & optimizer for Windows 10 / 11**

A lightweight, always-on-top "accessibility balloon" style tool that lives in the system
tray, shows live CPU / RAM / GPU / disk / network, and gives you Task-Manager-grade control
over processes, memory working sets, CPU priority/affinity, Windows Efficiency Mode, I/O and
memory priorities, and startup programs — wrapped in a themed WPF dashboard.

`C#` · `.NET 10` · `WPF` · `MVVM` · `Win32 / NT / PDH P/Invoke` · zero third-party runtime dependencies

</div>

---

## 1. What's new in 2.0 (deep-research upgrade)

| Area | Added |
|------|-------|
| **Metrics** | Per-process **CPU %**, **disk I/O rate**, **GPU %** (PDH `GPU Engine`, busiest-engine convention), **Efficiency-Mode state**; system **disk throughput** (PDH `PhysicalDisk`), **network up/down** (`GetIfTable`, alias-deduplicated), **GPU total**, **battery/AC**, **CPU clock MHz** (`CallNtPowerInformation`) |
| **Process control** | **Efficiency Mode (EcoQoS)** toggle, **Suspend / Resume** (`NtSuspendProcess`), **End process tree**, **I/O priority**, **Memory priority** — all behind the protected-process guard rail |
| **Memory** | Working-set trim (single / all, honest about page-back) + advanced **standby-list purge** (`NtSetSystemInformation`), each with real trade-off text |
| **Startup** | Run + **RunOnce** + **Startup folders** (user & all-users), **Enable/Disable** via Explorer's `StartupApproved` keys (non-destructive), plus add/remove for HKCU |
| **UX** | **Dark / Light / System themes + 7 accent choices** (live swap), sidebar navigation (Task-Manager-style), 4 live gauges + auto-scaling sparklines, searchable **auto-refreshing process table** (paused at will), row-accurate diffing (selection survives refresh), context menus, keyboard shortcuts, upgraded balloon (opacity, remembered position, optional GPU/disk/net rows, right-click actions), richer tray menu |
| **Reliability** | Crash handlers + log file, settings persistence (`%AppData%\WinSentinel\settings.json`), resource-hog alerts with cooldown, fixed event-handler leak, background snapshots (UI never blocks), thread-safe tray updates, affinity-mask bug fix |
| **Platform** | Retargeted to **.NET 10 LTS** (.NET 8 reached end of support 2026-11-10) |

Every claim above is exercised by the in-repo smoke harness (`tools/`-style throwaway console
project) that runs the real service layer against live Windows APIs — GPU counters, disk
throughput, network deltas, per-process sampling, EcoQoS, suspend/resume, tree-kill, startup
registry reads and alerts were all verified on a real machine (Windows 10 22H2).

---

## 2. System Specification

| Property              | Value                                                              |
|-----------------------|--------------------------------------------------------------------|
| Target framework      | `net10.0-windows`                                                  |
| UI stack              | WPF (XAML) + WinForms `NotifyIcon` (tray only)                    |
| Architecture pattern  | MVVM (services → view models → views)                              |
| Language version      | C# `latest`                                                       |
| Min OS                | Windows 10 1809+ / Windows 11                                     |
| Privilege level       | `requireAdministrator` (declared in `app.manifest`)              |
| External NuGet deps   | none — registry, charts, gauges, PDH interop are all built in     |
| Platforms             | `AnyCPU`, `x64`                                                   |
| Output                | `WinSentinel.exe` (WinExe, no console window)                     |

---

## 3. Feature Map

```
WinSentinel
├── Floating Balloon         Always-on-top, frameless, translucent, draggable,
│                            position remembered. CPU + RAM + optional GPU/disk/net.
│                            Right-click → quick actions. Double-click → Dashboard.
├── System Tray              NotifyIcon tooltip (CPU/RAM/GPU), context menu:
│                            Open • Show Balloon • Trim • Purge standby •
│                            Alerts toggle • Settings • Exit.
├── Alerts                   Sustained CPU/RAM thresholds → tray notification,
│                            rate-limited, never changes system state.
└── Dashboard (sidebar nav)
    ├── Overview             4 live gauges (CPU/RAM/GPU + disk activity rates),
    │                        auto-scaling sparklines, network up/down graphs,
    │                        battery + uptime + clock detail, memory tuning card.
    ├── Processes            Searchable, auto-refreshing table: CPU %, memory,
    │                        disk rate, GPU %, threads, priority, ECO chip,
    │                        suspended state. Per-process actions:
    │                          • End Task / End Tree       — confirmations + guard rail
    │                          • Suspend / Resume         — NtSuspendProcess
    │                          • Efficiency Mode          — EcoQoS toggle
    │                          • Trim Working Set         — EmptyWorkingSet
    │                          • Priority                 — Idle … RealTime (guarded)
    │                          • CPU Affinity             — per-core dialog
    │                          • I/O priority             — Very low / Low / Normal
    │                          • Memory priority          — Very low … Normal
    │                          • Open file location / Copy details
    └── Startup              HKCU/HKLM Run + RunOnce + Startup folders, with
                             enable/disable (StartupApproved), add, remove (HKCU).
    └── Settings             Theme + accent, sampling cadences, balloon options,
                             alert thresholds, safety confirmations, about/reset.
```

---

## 4. Project Structure

```
WinSentinel/
├── WinSentinel.sln
├── README.md
├── .gitignore
└── src/
    └── WinSentinel/
        ├── WinSentinel.csproj
        ├── app.manifest                 requireAdministrator + PerMonitorV2 DPI
        ├── App.xaml / App.xaml.cs        Bootstrap: settings → theme → services → tray/balloon
        ├── Assets/                       app.ico, tray.ico, logo.png
        ├── Themes/
        │   ├── Shared.xaml               Fonts + all control styles (theme-agnostic)
        │   ├── Dark.xaml                 Dark colour tokens
        │   └── Light.xaml                Light colour tokens (WCAG-AA contrast)
        ├── Models/
        │   ├── MetricSample.cs           Immutable system snapshot
        │   ├── ProcessInfo.cs            Process snapshot (CPU/disk/GPU/eco aware)
        │   ├── StartupItem.cs            Startup entry (+ StartupApproved state)
        │   └── AppSettings.cs            Persisted user settings
        ├── Services/
        │   ├── NativeMethods.cs          All P/Invoke (kernel32 / psapi / ntdll / powrprof / iphlpapi)
        │   ├── PdhHelper.cs              PDH wrapper: GPU Engine + PhysicalDisk counters
        │   ├── SystemMonitorService.cs   CPU/RAM/disk/net/GPU/battery/clock sampling
        │   ├── ProcessService.cs         Snapshot + all guarded mutations
        │   ├── ProcessSampler.cs         Delta engine for per-process rates
        │   ├── MemoryOptimizer.cs        Trim all / single, standby purge
        │   ├── StartupManager.cs         Run/RunOnce/folders + enable/disable/add/remove
        │   ├── AlertService.cs           Sustained CPU/RAM alert detector
        │   ├── SettingsService.cs        JSON settings (debounced save, sanitised load)
        │   ├── ProtectedProcesses.cs     OS-critical denylist (service-layer enforced)
        │   └── Logger.cs                 Crash/diagnostic log
        ├── ViewModels/
        │   ├── ViewModelBase.cs          INotifyPropertyChanged base
        │   ├── RelayCommand.cs           ICommand relay
        │   ├── ProcessRow.cs             In-place-updated table row VM
        │   ├── BalloonViewModel.cs       Balloon data + position/opacity
        │   └── DashboardViewModel.cs     Gauges, table, startup, settings surface
        ├── Controls/
        │   ├── CircularGauge.cs          Owner-drawn arc gauge (no deps)
        │   └── Sparkline.cs              Owner-drawn real-time line graph
        ├── Converters/Converters.cs      Protected→brush, bool→visibility, startup state
        ├── Helpers/
        │   ├── ThemeManager.cs           Runtime theme/accent swapping + system theme watch
        │   └── TrayIconManager.cs        NotifyIcon owner (only WinForms file)
        └── Views/
            ├── BalloonWindow.xaml(.cs)   The floating balloon
            ├── DashboardWindow.xaml(.cs) Sidebar dashboard (Overview/Processes/Startup/Settings)
            └── AffinityWindow.xaml(.cs)  Per-core affinity dialog
```

---

## 5. How It Works (Technical Notes)

### 5.1 CPU — `GetSystemTimes` deltas
`busy% = (kernelΔ + userΔ − idleΔ) / (kernelΔ + userΔ) × 100` (kernel time already includes
idle). No `PerformanceCounter` pitfalls (first-read 0, counter-DB corruption, locale issues).

### 5.2 Memory — `GlobalMemoryStatusEx`
Physical load/used/available straight from `MEMORYSTATUSEX`, matching Task Manager.

### 5.3 Per-process metrics
* **CPU %** — `Process.TotalProcessorTime` deltas, normalised to total capacity
  (`Δcpu / (Δt × logicalCores)`), the Task Manager convention.
* **Disk rate** — `GetProcessIoCounters` deltas (documented caveat: aggregate file+network+device I/O).
* **GPU %** — PDH `\GPU Engine(*)\Utilization Percentage`, instance names parsed
  (`pid_…_engtype_…`), per-process value = **busiest engine** (summing engines can exceed 100%),
  system value = busiest engine type summed across adapters.
* First observation of a process yields `—` (no delta yet); PID reuse is guarded.

### 5.4 System disk & network
* Disk — PDH `\PhysicalDisk(_Total)\Disk Read/Write Bytes/sec` (English counter names via
  `PdhAddEnglishCounterW`, so localised Windows works).
* Network — `GetIfTable` octet counters. Windows exposes the same physical traffic through
  multiple alias rows (`\DEVICE\TCPIP_{GUID}` …); identical deltas are collapsed so each flow
  is counted once. Loopback is skipped; 32-bit counter wrap is handled.

### 5.5 Efficiency Mode (EcoQoS) — with an honest caveat
`SetProcessInformation(ProcessPowerThrottling, ControlMask=StateMask=EXECUTION_SPEED)`.
`GetProcessInformation` for the same class is **not implemented on Windows 10** (returns
`ERROR_INVALID_PARAMETER`); WinSentinel therefore stops probing after a few failures and
tracks the state it set itself, so the ECO chip is accurate for changes made here on Win10
and authoritative (queried) on Windows 11.

### 5.6 Suspend / Resume
`NtSuspendProcess` / `NtResumeProcess` — the same calls behind Resource Monitor / Process
Explorer. Documented as debugger-class operations, so they sit behind the guard rail and a
confirmation-free but clearly-labelled UI action.

### 5.7 I/O & memory priority
I/O — `NtSetInformationProcess(ProcessIoPriority=33, IO_PRIORITY_HINT)`: Very low / Low /
Normal only (High/Critical are reserved for the system). Memory —
`SetProcessInformation(ProcessMemoryPriority)`: lower-priority pages are trimmed first.

### 5.8 Memory trim & standby purge — stated honestly
`EmptyWorkingSet` pages a working set out; Windows pages it back in on demand. Standby purge
(`NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)`) is admin-only
and rebuilds from disk afterwards. The UI says exactly that — no "RAM cleaner" theatre.

### 5.9 Guard rails
`ProtectedProcesses` (kernel pseudo-processes, session infrastructure, Defender, `dwm`,
`explorer`, WinSentinel itself, …) is re-checked **inside every service mutation**, so the
guard holds even if the UI is bypassed. RealTime priority gets an extra confirmation.
MemoryOptimizer refuses to trim protected processes.

### 5.10 Threading & performance
Sampling runs on a timer thread; snapshots run on `Task.Run` (UI never blocks); process rows
are diffed by PID (no clear-and-refill, selection/scroll survive); the table refreshes on a
configurable cadence and only while the dashboard is open. The tray tooltip is marshalled and
coalesced. A single-instance mutex keeps duplicates out; crash handlers log to
`%AppData%\WinSentinel\winsentinel.log`.

---

## 6. Build & Run

> Build on Windows 10/11 with the **.NET 10 SDK** (or Visual Studio 2022 17.12+ with the
> *.NET desktop development* workload).

```bat
:: restore + build
dotnet build WinSentinel.sln -c Release

:: run (accept the UAC prompt — elevation is required to read every process)
dotnet run --project src\WinSentinel\WinSentinel.csproj -c Release
```

Self-contained single EXE (no .NET install needed on the target):

```bat
dotnet publish src\WinSentinel\WinSentinel.csproj -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `src\WinSentinel\bin\Release\net10.0-windows\win-x64\publish\WinSentinel.exe`

---

## 7. Usage

1. Launch `WinSentinel.exe` → accept UAC.
2. Drag the floating balloon anywhere (**position is remembered**); double-click it for the
   dashboard; right-click for quick actions.
3. **Overview** — gauges, sparklines, battery/system info, Trim / Purge actions.
4. **Processes** — search (Ctrl+F), watch CPU/disk/GPU per process; select a row and use the
   toolbar or the right-click menu. Shortcuts: `F5` refresh, `Del` end task (not while typing),
   `Alt+E` efficiency mode.
5. **Startup** — review every auto-start source; enable/disable per-user entries or remove them.
6. **Settings** — theme/accent, cadences, balloon options, alert thresholds, confirmations.
7. Tray → **Exit** quits; closing windows only hides them.

---

## 8. Safety & Scope

- Never terminates, suspends or re-prioritises OS-critical processes — enforced in the service layer.
- Memory trims and standby purges are presented as tuning aids, with their trade-offs in the UI.
- Startup edits are confined to per-user locations and are fully reversible (disable ≠ delete).
- All destructive actions require explicit confirmation (configurable).
- Every failure path lands in a log rather than a crash.

This is a defensive, user-facing utility for monitoring and tuning **your own machine**.

---

## 9. Extending

- **Temperatures/fans** — add `LibreHardwareMonitorLib` (MPL-2.0) behind an interface; note it
  installs a kernel driver and needs admin (intentionally not bundled).
- **Per-process network** — an ETW `Microsoft-Windows-Kernel-Network` session (admin) is the
  documented path; the polling `GetPerTcpConnectionEStats` option needs per-connection opt-in.
- **History persistence** — `MetricSample` is immutable; serialize to CSV/SQLite from
  `SystemMonitorService.SampleUpdated`.
- **Theming** — add a colours-only `Themes/*.xaml` and register it in `ThemeManager`.

---

<div align="center">
<sub>WinSentinel 2.0 · C# / .NET 10 / WPF · MVVM · zero third-party runtime dependencies</sub>
</div>
