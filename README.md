# PC Vitals

A lightweight Windows 11/10 system monitor that puts your PC's vitals **right on the taskbar**. CPU, GPU, memory
and network usage, temperatures and power draw are always in view. Hover any item for a detail card, or click it
for a dashboard of every sensor in your machine.

![Taskbar strip](docs/taskbar.png)

![Dashboard overview](docs/dashboard.png)

| CPU page | GPU page (light theme) |
|---|---|
| ![CPU page](docs/dashboard-cpu.png) | ![GPU page, light theme](docs/dashboard-gpu-light.png) |
| **Memory page (AMOLED theme)** | **Hover cards** |
| ![Memory page, AMOLED theme](docs/dashboard-memory-amoled.png) | ![Hover cards](docs/hover-cards.png) |

## Features

### On the taskbar
- **Labelled readouts** with mini history graphs, drawn next to the clock.
  - Default layout: **CPU** (usage, temperature, power), **GPU** (usage, temperature, power), **MEM**, **NET**.
  - Optional items: **PWR** (CPU + GPU draw), **iGPU** (the second GPU), **BOARD** (chipset/VRM temps), **SSD**.
  - Rename, reorder and toggle items; choose Small, Medium or Large; drag the strip sideways to move it.
- **Never covers your apps.** The strip measures the gap between the Start button / app icons and the tray, and
  if it doesn't fit it hides graphs, then shrinks, then hides trailing items. Settings tells you what it did.
- Follows the taskbar's light/dark theme, steps aside for fullscreen games and video, and supports auto-hiding
  taskbars (optionally as a floating bar).
- **Hover cards** with the full device name (e.g. *NVIDIA GeForce RTX 5080 · Discrete*), a graph and key details.
- **Optional tray icons** (label + number, or number only) for CPU/GPU temperature and usage, RAM and power.

### Dashboard
- **Device rail** with live thumbnails (CPU, each GPU, memory, each drive,
  network, motherboard, battery/PSU when present). Click a taskbar item to jump straight to its page.
- **Big history graph** for 1, 5 or 15 minutes, with temperature overlaid on its own axis and a hover crosshair.
- **Stat tiles** per device: clocks, power vs. limit, hot spot / memory junction, VRAM, fan state, drive wear and
  lifetime writes, DIMM temperatures, process/thread/handle counts and more.
- **Top processes** by CPU or memory (grouped by app), and a per-thread CPU load graph.
- **Every sensor** LibreHardwareMonitor can read, with min/max. Noisy ones (per-engine D3D load, power-connector
  pins, memory timings) stay hidden until you ask for them.
- **Overview page** with system information: Windows version, CPU, GPUs, memory modules, motherboard, drives.

### Settings
Sidebar navigation and setting cards, with a live preview of the taskbar strip as you edit.

| Taskbar | Appearance |
|---|---|
| ![Taskbar settings](docs/settings-taskbar.png) | ![Appearance settings](docs/settings-appearance.png) |

- **Themes**: System (follows Windows), Light, Dark, and AMOLED black.
- **Temperature unit**: °C or °F.
- **Alerts**: a Windows notification when CPU or GPU temperature, or memory use, stays above your limit.
- **Keyboard shortcut** to open the dashboard from anywhere (off by default).
- **Primary GPU** choice on multi-GPU systems, with discrete/integrated labels.
- **Start with Windows** without a UAC prompt at each sign-in (a scheduled task with highest privileges).

No network access, no telemetry. Settings live in `%APPDATA%\PCVitals\settings.json`.

## Requirements

- Windows 10 or 11, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (to run), or the .NET 8 SDK (to build)
- **Administrator rights** for CPU temperature/power, motherboard, chipset, fan, DIMM and drive sensors. The app
  asks once at startup; if you decline it runs in a limited mode (GPU, load, memory and network still work).
- The **[PawnIO](https://pawnio.eu/) driver** for CPU and motherboard sensors. LibreHardwareMonitor uses it in
  place of the old WinRing0 driver, which Windows Defender now blocks. If you already use a recent
  LibreHardwareMonitor, FanControl or similar tool, it is probably installed.

## Build and run

```bash
git clone https://github.com/<you>/PCVitals.git
cd PCVitals
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
publish\PCVitals.exe
```

For a single exe that doesn't need the .NET runtime installed (much larger), use `--self-contained true`.

### Using it

| Action | Result |
|---|---|
| Hover a taskbar item | Detail card for that component |
| Click a taskbar item | Open the dashboard on that item's page (click again to close) |
| Click the tray icon | Open or close the dashboard |
| Drag the taskbar strip sideways | Move it (Settings → Taskbar → Reset puts it back) |
| Right-click the strip or tray icon | Menu: dashboard, settings, refresh rate, start with Windows, exit |
| Ctrl+↑ / Ctrl+↓ in the dashboard | Previous / next device |

On Windows 11, new tray icons start in the overflow (`^`) menu. The taskbar strip doesn't need this, but you
can drag the PC Vitals icon onto the taskbar if you like.

## Performance

PC Vitals is meant to sit in the background all day, so it is built for a small footprint. Measured on a
Ryzen 7 9800X3D + RTX 5080 + 3 NVMe drives, 1-second refresh, running as administrator:

| | |
|---|---|
| CPU, steady state | **~1.7–2.7 % of one core** (≈0.1–0.2 % of a 16-thread machine, including the UI) |
| Private memory (Task Manager "Memory") | **~35–47 MB** as admin, ~23 MB without admin |
| Sensor poll per refresh | **~15–19 ms** |
| Process start → UI | **~80 ms** (ReadyToRun); first full reading ~1.2 s |

How it stays light:

- **NVIDIA's NVML library is kept out of the app.** Initialising NVML commits about 20 MB that it never gives
  back, and its PCIe throughput query blocks for about 45 ms per poll. The taskbar only needs board power from
  it, so PC Vitals computes that from NVAPI's power percentage (already polled) times a per-card
  watts-per-percent ratio. The ratio is learned once by a short-lived helper process and is accurate to about
  0.2 W on average. While the dashboard is open, a helper process streams PCIe throughput and exact power; its
  memory goes away when the dashboard closes.
- **Garbage doesn't pile up.** .NET sizes its youngest-generation budget from the CPU's L3 cache; on a 96 MB-L3
  X3D chip that meant 40+ MB of garbage between collections. A 64 MB heap hard limit (the live heap is ~3–11 MB)
  caps it at about 5 MB. Drive discovery's one-off ~40 MB of allocations are decommitted right after it finishes.
- **Per-engine Direct3D counters** (about 20 per GPU) are only read while the dashboard is open. The headline
  GPU load comes from NVAPI/ADL.
- **Demand-driven polling**: drive SMART data, DIMM SPD (over SMBus), the motherboard sensor chip and the
  non-primary GPU are read every 3–30 s unless something on screen needs them every second.
- **Deferred discovery**: drives, USB/HID controllers, PSUs and batteries are probed after the first reading,
  so the taskbar fills in sooner.
- **Draw only on change**: the strip and tray icons re-render only when what they show changes.
- **Cheap process list**: top processes come from one `NtQuerySystemInformation` call per refresh (no
  per-process handles), and only while a page that shows them is open.
- **Startup and footprint**: ReadyToRun (no JIT warm-up), invariant globalization (no ICU), non-concurrent
  workstation GC with `ConserveMemory`, and the working set is trimmed after startup and whenever the dashboard
  or settings close.

## Diagnostics

```bash
PCVitals.exe --dump sensors.txt              # every sensor and its value (also: Settings → About → Save sensor report)
PCVitals.exe --bench bench.txt               # startup, per-device poll cost, CPU and memory
PCVitals.exe --memprofile mem.txt            # private memory per library / sensor group
PCVitals.exe --memprofile mem.txt pipeline   # the same for the app's actual lean path
PCVitals.exe --taskbar-report tb.txt         # taskbar geometry and how wide the strip would be
PCVitals.exe --preview docs                  # render the README screenshots
PCVitals.exe --replace                       # replace a running instance with this build
PCVitals.exe --no-elevate                    # skip the admin prompt (limited mode)
```

Unexpected errors are written to `%APPDATA%\PCVitals\error.log`.

## Project layout

| File | What it does |
|---|---|
| `HardwareService.cs` | Owns LibreHardwareMonitor, polls on a background thread, builds snapshots, summaries and history |
| `NvmlFeed.cs` | Helper process that streams NVML readings while the dashboard is open |
| `ProcessStats.cs` | Top processes by CPU and memory |
| `Vitals.cs` | Turns a snapshot into display items (labels, values, detail rows) shared by every view |
| `TaskbarWidget.cs` | The per-pixel-alpha layered window drawn on the taskbar, and its auto-fit |
| `HoverCard.cs` | Detail card shown when hovering a taskbar item |
| `DashboardForm.cs` / `DashboardView.cs` | The dashboard window (owner-drawn) |
| `SettingsForm.cs` / `SettingsControls.cs` | Settings window and its toggle, card, dropdown and preview controls |
| `TrayContext.cs` | App lifetime, tray icons, menu, alerts and wiring |
| `AppSettings.cs` | Settings persistence and the start-with-Windows task |
| `Theme.cs` | Light, dark and AMOLED palettes, and value formatting |
| `Hotkeys.cs`, `Native.cs`, `Gfx.cs`, `TrayIconRenderer.cs` | Global shortcut, Win32 interop, drawing helpers, tray icons |

## License

MIT, see [LICENSE](LICENSE). LibreHardwareMonitor is used under its own MPL-2.0 licence.

## Credits

- Sensor access by [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
  (MPL-2.0), using the [PawnIO](https://pawnio.eu/) driver.
