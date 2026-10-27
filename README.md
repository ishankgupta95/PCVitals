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
  - Optional items: **PWR** (CPU + GPU draw), **iGPU** (the second GPU), **BOARD** (chipset/VRM temps), **SSD**,
    **BAT** (charge, charging/discharging watts, time left; switched on automatically on laptops).
  - Rename, reorder and toggle items; choose Small, Medium or Large; drag the strip sideways to move it.
  - Optionally shown on every display's taskbar.
- **Never covers your apps.** The strip measures the gap between the Start button / app icons and the tray, and
  if it doesn't fit it hides graphs, then shrinks, then hides trailing items. Settings tells you what it did.
- Follows the taskbar's light/dark theme, steps aside for fullscreen games and video, and supports auto-hiding
  taskbars (optionally as a floating bar).
- **Hover cards** with the full device name (e.g. *NVIDIA GeForce RTX 5080 · Discrete*), a graph and key details.
- **Any sensor on the taskbar**: pin any reading (a fan, a drive, the GPU hot spot, a voltage) with Settings →
  Taskbar → Add sensor…, or from the dashboard. Each shows its value with its min and max, a mini graph, and a hover
  card with the session average. A sensor whose device is unplugged stays in place and shows `--`.
- **Optional tray icons** (label + number, or number only) for CPU/GPU temperature and usage, RAM and power.

### Dashboard
- **Device rail** with live thumbnails (CPU, each GPU, memory, each drive,
  network, motherboard, battery/PSU when present). Click a taskbar item to jump straight to its page.
- **Big history graph** for 1, 5 or 15 minutes, 1 or 24 hours (one-minute averages), or 7 or 30 days (saved
  history, with the minimum-to-maximum range shaded behind the average), with temperature overlaid on its own
  axis and a hover crosshair that shows the date and time.
- **Saved history**: one-minute averages, minimums and maximums of the headline readings and your favourite and
  pinned sensors are kept on disk for 30 days by default (7, 30 or 90), so the graphs continue across restarts.
  About 1–2 MB a day; days older than a week are folded into 5-minute periods. Export any period as CSV, at
  1-minute, 5-minute or hourly resolution. Turn it off or delete it in Settings → General.
- **Stat tiles** per device: clocks, power vs. limit, hot spot / memory junction, VRAM, fan state, drive wear and
  lifetime writes, DIMM temperatures, battery wear and capacity, process/thread/handle counts and more.
- **Drive health and space**: each drive shows its letters (C:, D:) and free space per volume, and a
  Good / Caution / Bad health verdict from its SMART data (spare blocks, wear, reallocated, pending and unreadable
  sectors, integrity errors), with the reasons in plain words. Free space per volume can be pinned, graphed and
  alerted on like any sensor, and "drive health" is an alert preset.
- **Wi-Fi and latency**: the Network page shows the Wi-Fi network, signal and link speed (optionally the signal on
  the NET item), and, if you turn it on, ping latency, jitter and loss to your router or a host you choose.
- **Top processes** by CPU, memory or GPU (grouped by app, with a per-app GPU column), and a per-thread CPU load
  graph.
- **Keyboard and screen reader friendly**: Tab / Shift+Tab move through everything clickable, Enter activates,
  and every control has a name for screen readers (in Settings too).
- **Every sensor** LibreHardwareMonitor can read, with min, session average and max. Noisy ones (per-engine D3D
  load, power-connector pins, memory timings) stay hidden until you ask for them.
- **Sensor options**: click or right-click any sensor (or press the menu key) to favourite it, rename it (the new
  name is used everywhere), hide it, show it on the taskbar, or reset its min, max and average.
- **Overview page** with your favourite sensors and system information: Windows version, CPU, GPUs, memory
  modules, motherboard, drives.

### Settings
Sidebar navigation and setting cards, with a live preview of the taskbar strip as you edit.

| Taskbar | Appearance |
|---|---|
| ![Taskbar settings](docs/settings-taskbar.png) | ![Appearance settings](docs/settings-appearance.png) |

- **Themes**: System (follows Windows), Light, Dark, and AMOLED black.
- **Temperature unit**: °C or °F.
- **Alerts on any sensor**: when a reading stays above or below your limit for a while (CPU or GPU temperature,
  memory use, or any sensor: "fan below 300 RPM", "SSD above 60 °C"), show a notification (click it to open that
  device in the dashboard), play a sound, and/or run a program of your choice. Programs run as you, without
  administrator rights, unless you tick "as administrator". Repeats at most once per interval you choose while the
  reading stays there; the last 100 alerts are listed in Settings → Alerts. Add one from Settings or from a
  sensor's menu in the dashboard.
- **Keyboard shortcut** to open the dashboard from anywhere (off by default).
- **Primary GPU** choice on multi-GPU systems, with discrete/integrated labels.
- **Start with Windows** without a UAC prompt at each sign-in (a scheduled task with highest privileges).

No network access unless you turn on ping (Settings → General; off by default, and it pings your router unless
you name another host). No telemetry. Settings live in `%APPDATA%\PCVitals\settings.json`, saved history in
`%LOCALAPPDATA%\PCVitals\history` (one file per day; it never leaves your PC).

## Requirements

- Windows 10 or 11, x64. On Windows on ARM (Snapdragon) it runs under x64 emulation in the limited mode
  only: the PawnIO driver and LibreHardwareMonitor's hardware access are x64-only, so CPU temperature/power,
  motherboard and fan sensors aren't available.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (to run), or the .NET 8 SDK (to build)
- **Administrator rights** for CPU temperature/power, motherboard, chipset, fan, DIMM and drive sensors. The app
  asks once at startup; if you decline it runs in a limited mode (GPU, load, memory and network still work).
- The **[PawnIO](https://pawnio.eu/) driver** for CPU and motherboard sensors. LibreHardwareMonitor uses it in
  place of the old WinRing0 driver, which Windows Defender now blocks. If you already use a recent
  LibreHardwareMonitor, FanControl or similar tool, it is probably installed. PC Vitals checks for it: if it's
  missing you get a one-time notification, a banner in the dashboard and its status in Settings → About, each
  with a link to install it. Restart PC Vitals afterwards.

### "Windows protected your PC"

The released exe isn't code-signed (signing certificates cost money), so Windows SmartScreen may warn about an
unrecognised app the first time you run it. Click **More info → Run anyway**. If you'd rather not, build it
yourself from source with the commands below; it's the same code.

## Build and run

```bash
git clone https://github.com/<you>/PCVitals.git
cd PCVitals
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
publish\PCVitals.exe
```

For a single exe that doesn't need the .NET runtime installed (much larger), use `--self-contained true`.

Run the unit tests (taskbar fit rules, settings migration, GPU detection, formatting):

```bash
dotnet test tests/PCVitals.Tests
```

### Using it

| Action | Result |
|---|---|
| Hover a taskbar item | Detail card for that component |
| Click a taskbar item | Open the dashboard on that item's page (click the same item again to minimise it) |
| Click the tray icon | Open the dashboard, or bring it to the front |
| Close the dashboard window | It hides; PC Vitals keeps running in the tray (exit from the tray menu) |
| Drag the taskbar strip sideways | Move it (Settings → Taskbar → Reset puts it back) |
| Right-click the strip or tray icon | Menu: dashboard, settings, refresh rate, start with Windows, exit |
| Ctrl+↑ / Ctrl+↓ in the dashboard | Previous / next device |
| Tab / Shift+Tab, Enter in the dashboard | Move between and activate buttons, devices and tiles |
| Click or right-click a sensor in the dashboard | Favourite, rename, hide, show on the taskbar, reset min/max |

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
PCVitals.exe --bench-history hist.txt       # write, compact and load a synthetic month of saved history
PCVitals.exe --alert-selftest alert.txt     # run one alert rule on live readings, including its "run a program" action
PCVitals.exe --taskbar-report tb.txt         # taskbar geometry and how wide the strip would be
PCVitals.exe --preview docs                  # render the README screenshots
PCVitals.exe --write-icon PCVitals.ico      # regenerate the app icon from the logo drawing
PCVitals.exe --replace                       # replace a running instance with this build
PCVitals.exe --no-elevate                    # skip the admin prompt (limited mode)
```

Unexpected errors are written to `%APPDATA%\PCVitals\error.log`. The same log notes, once, if a sensor poll fails
or one of the memory/CPU optimisations can't be applied (for example after a LibreHardwareMonitor update), so a
quiet loss of efficiency doesn't go unnoticed. The library version is pinned for that reason.

Found a bug? Please [open an issue](../../issues/new/choose) and attach the sensor report from Settings → About.

## Project layout

| File | What it does |
|---|---|
| `Core/HardwareService.cs` | Owns LibreHardwareMonitor, polls on a background thread, builds snapshots and summaries |
| `Core/Readings.cs` / `Core/SensorCatalog.cs` | Readings with stable sensor ids, saved sensor references, and sensor lookup and search |
| `Core/HistoryStore.cs` | Recent and one-minute history, by sensor id with aliases for the headline series |
| `Core/HistoryArchive.cs` / `Core/HistoryRecorder.cs` | Saved history: day files, series map, loading, clean-up, and the background writer |
| `Core/HistoryCsv.cs` / `Core/HeadlineSeries.cs` | CSV export of saved history, and names for the headline series |
| `Core/Alerts.cs` | Alert rules, the engine that decides when they go off, and the alert history |
| `Core/Drives.cs` / `Core/Network.cs` | Drive volumes and health rules; Wi-Fi status and the opt-in ping |
| `Core/NvmlFeed.cs` | Helper process that streams NVML readings while the dashboard is open |
| `ProcessStats.cs` / `GpuProcessUsage.cs` | Top processes by CPU, memory and GPU |
| `Vitals.cs` | Turns a snapshot into display items (labels, values, detail rows) shared by every view |
| `TaskbarWidget.cs` / `StripFit.cs` | The per-pixel-alpha layered window drawn on the taskbar, and its fit rules |
| `tests/PCVitals.Tests` | Unit tests (xUnit) |
| `HoverCard.cs` | Detail card shown when hovering a taskbar item |
| `DashboardForm.cs` / `DashboardView.cs` | The dashboard window (owner-drawn) |
| `SettingsForm.cs` / `SettingsControls.cs` | Settings window and its toggle, card, dropdown and preview controls |
| `SensorDialogs.cs` | The sensor picker and the rename prompt |
| `HistoryDialogs.cs` | The history export dialog |
| `AlertEditor.cs` / `AlertActions.cs` | The alert rule editor; notification text, sounds, and starting programs without admin rights |
| `TrayContext.cs` | App lifetime, tray icons, menu, alerts and wiring |
| `AppSettings.cs` | Settings persistence and the start-with-Windows task |
| `Theme.cs` | Light, dark and AMOLED palettes, and value formatting |
| `Hotkeys.cs`, `Native.cs`, `Gfx.cs`, `TrayIconRenderer.cs` | Global shortcut, Win32 interop, drawing helpers, tray icons |

## License

MIT, see [LICENSE](LICENSE). LibreHardwareMonitor is used under its own MPL-2.0 licence.

## Credits

- Sensor access by [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
  (MPL-2.0), using the [PawnIO](https://pawnio.eu/) driver.
