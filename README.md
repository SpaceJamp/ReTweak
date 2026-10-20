# ReTweak

<img src="docs/icon.png" alt="ReTweak icon" width="96" align="right">

A lightweight successor to ASUS GPU Tweak III for NVIDIA cards, focused first on
**monitoring the 12V-2x6 power connector** and warning early when something is
going wrong.

- GPU stats on any NVIDIA card through NVML (temperature, clocks, load, board power, fans, VRAM)
- **Per-pin voltage and current** on ASUS cards with Power Detector+ (e.g. ROG Astral), read directly from the card's monitor chip; see [docs/power-detector.md](docs/power-detector.md)
- **GPU hotspot on RTX 50**, optional: needs the PawnIO driver (see [below](#gpu-hotspot-and-pawnio))
- **Temperatures**: precise core (0.01 °C) and memory on RTX 50; on ASUS cards, memory comes from the
  board sensors GPU Tweak III uses, plus 8 board sensors (VRM, core, memory and more)
- **Early-warning alerts**: pin over-current, low/high voltage, pin-to-pin voltage spread, current imbalance, fast voltage drops or current ramps, frozen sensor, high core, memory or hotspot temperature
- **Fan control**: **Auto** is ReTweak's own controller. It works out what you're doing (idle, desktop,
  video, gaming, rendering/AI/encoding) from GPU, video-engine and memory load, how power swings and the
  foreground app, and holds a target per activity (60-70 °C hotspot by default) under a **75 °C ceiling**.
  Every sensor votes against its own limit (hotspot and its 4 sensors, core, memory or each memory chip,
  ASUS VRMs) and the one closest to its limit sets the speed; a rising load is projected a few seconds
  ahead, and it learns how much airflow your card needs at each power level. With PawnIO it sees the
  hotspot and per-chip memory temperatures too. Also: Driver (the card's own curve) with a safety net, a fixed speed, or a 5-point curve (drag the points
  on the dashboard, or pick Quiet / Balanced / Performance). The curve can follow the core, hotspot,
  memory or the hottest of them; speed changes are ramped and the temperature averaged so the fans don't
  step on short spikes; optional 0 RPM below a temperature you choose. Full speed at a core, hotspot or
  memory failsafe in every mode that writes. The Auto safety net leaves the driver in charge and only steps in
  when the hotspot or memory gets too hot, or the hotspot runs far ahead of the core (poor cooler contact)
- Detailed **diagnostic log** and a one-click diagnostics bundle (see [below](#diagnostic-log))
- Native Windows notifications for alerts
- Runs in the notification area: closing the window keeps monitoring; the tray icon shows the worst active alert
- Optional start with Windows as administrator (`--minimized`, tray only; see below), CSV logging and adjustable thresholds in Settings
  (stored in `%LocalAppData%\ReTweak\settings.json`)

Monitoring is read-only. The one thing ReTweak writes to the GPU is fan speed: always in Auto,
Fixed and Curve, and in Driver mode only when the safety net steps in (hotspot or memory too hot;
you can turn it off in Settings). All of these need ReTweak to run as administrator:

- It uses NVML's documented fan calls and needs administrator rights (the dashboard offers
  *Restart as administrator*). Start with Windows starts it elevated (see below).
- Switching to Driver, exiting, losing the temperature reading for 3 s or a failed write hands
  the fans back to the driver's own curve. At the failsafe temperature (88 °C by default)
  the fans go to 100%.
- If ReTweak is killed outright (Task Manager, crash), the last manual speed stays until the next
  reboot or until you run `ReTweak.Cli --fan auto`.
- Close GPU Tweak III and other fan tools first. ReTweak warns when another program keeps
  changing the fan speed.

## GPU hotspot and PawnIO

RTX 50 drivers no longer report the GPU hotspot to applications. The value is still in the GPU's
temperature registers, but Windows only lets kernel drivers read those, so every tool that shows it
(HWMonitor, HWiNFO, LibreHardwareMonitor) uses a driver. ReTweak uses [PawnIO](https://pawnio.eu/):

- **Optional.** Without PawnIO, the hotspot tile says what's missing and everything else works:
  connector monitoring, temperatures, alerts.
- **Open source and signed.** The official build only loads signed modules. ReTweak loads one:
  PawnIO's own NVIDIA module (LGPL-2.1, bundled unmodified in `src/ReTweak.Core/PawnIo`), which can only
  read a 24-byte block of temperature registers (`0xAD0A90`) on GB202-GB207 GPUs.
- **Needs administrator rights.** Install PawnIO from [pawnio.eu](https://pawnio.eu/) and run ReTweak
  as administrator. Unelevated, the tile says so.

How the register was found: [docs/power-detector.md](docs/power-detector.md#hotspot-on-rtx-50-2026-10-19).

## Diagnostic log

For tracking down problems, Settings → Diagnostics turns on a detailed log (or start the app or
CLI with `--debug` for one run at full detail). Files are JSON lines in
`%LocalAppData%\ReTweak\diagnostics` (10 MB each, last 10 kept), written from a background thread:

| Level | Adds |
|---|---|
| Events | startup environment (driver, GPU, PawnIO, admin, other fan tools running), settings changes, alerts raised and cleared, fan mode changes, errors and crashes with stack traces |
| Detailed | every sensor snapshot (all temperatures, clocks, load, power, fans), every connector frame, every fan-controller decision with its internal terms, driver-call timing (count, mean, p95, max, failures per NVML / NVAPI / I2C / PawnIO call every 10 s, plus any slow or failed call) |
| Everything | raw hotspot register reads at 10 Hz |

**Save diagnostics to Desktop** zips the logs, settings, the latest connector CSVs and an
environment summary, ready to attach to an issue.

## Layout

| Path | What |
|---|---|
| `src/ReTweak.Core` | NVML/NVAPI interop, ASUS sensor, background sampler, alert engine, CSV logger |
| `src/ReTweak.App` | WinUI 3 dashboard (Windows App SDK, unpackaged) |
| `src/ReTweak.Cli` | Console readout, live alerts, benchmark, simulations, log replay |
| `tools/analyze_load.py` | Analyze a logged load test |
| `research/` | Reverse-engineering scripts used to recover the sensor protocol |
| `docs/` | Protocol and measurement notes |

## Build and run

Requires the .NET 10 SDK and an NVIDIA driver.

```bash
dotnet build
dotnet run --project src/ReTweak.App
```

## CLI

```bash
dotnet run --project src/ReTweak.Cli                      # snapshot of every GPU
dotnet run --project src/ReTweak.Cli -- --alerts --log logs/run.csv --duration 900
dotnet run --project src/ReTweak.Cli -- --replay logs/run.csv   # re-check a log against current alert rules
dotnet run --project src/ReTweak.Cli -- --simulate              # scripted failure scenarios (no hardware)
dotnet run --project src/ReTweak.Cli -- --bench                 # sensor latency and noise
dotnet run --project src/ReTweak.Cli -- --fans                  # fan count, range, speeds
dotnet run --project src/ReTweak.Cli -- --fan smart             # Auto controller (curve, or 60 = fixed 60%), admin; Ctrl+C restores
dotnet run --project src/ReTweak.Cli -- --fan auto              # hand fans back to the driver
python tools/analyze_load.py logs/run.csv
```

## Alert defaults

Tuned against a 400 W load test on an RTX 5080 Astral (see the docs). The sensor
publishes new data about every 600 ms, which sets the alert latency.

| Condition | Warning | Critical | Latency |
|---|---|---|---|
| Pin current | 7.6 A | 9.5 A (pin rating) | ~1.8 s / ~1.2 s |
| Pin voltage | < 11.64 V or > 12.60 V | < 11.40 V | ~1.8 s / ~1.2 s |
| Pin-to-pin voltage spread (≥ 10 A load) | 40 mV | 100 mV | |
| Current imbalance (≥ 10 A load) | ×1.25 | ×1.70 | |
| Voltage falling / current rising | projected to hit critical within 5 s | | ~1.8 s after the trend starts |
| Sensor frozen or not responding | 10 s without new data / 10 failed reads | | |
| GPU core temperature | 83 °C | 88 °C | ~3 s / ~2 s |
| Memory temperature | 90 °C | 100 °C | ~3 s / ~2 s |
| GPU hotspot (RTX 50, PawnIO) | 95 °C | 105 °C | ~3 s / ~2 s |

The hotspot thresholds are placeholders until load-tested on this card.

## Start with Windows

Settings → *Start with Windows* registers a Task Scheduler task named `ReTweak` that runs
`ReTweak.exe --minimized` at logon for your account with highest privileges, so ReTweak starts
elevated (needed for the hotspot) without a UAC prompt each time. It runs on battery too.
Creating or removing the task needs administrator rights once: if ReTweak isn't running as
administrator, Windows asks when you save. Check it with `schtasks /Query /TN ReTweak`.

Older versions used the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key, which can't
start an elevated app. ReTweak moves such an entry to the task the next time it runs as administrator,
or when you save Settings. If you move ReTweak to another folder, open Settings and save to point the
task at the new location.

## License

Copyright (C) 2026 Yeti

ReTweak is free software: you can redistribute it and/or modify it under the
terms of the GNU General Public License as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later
version. It is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; see [LICENSE](LICENSE) for the full terms.

ReTweak is an independent project and is not affiliated with or endorsed by
ASUS or NVIDIA. "GPU Tweak", "ROG" and "Astral" are trademarks of ASUSTeK
Computer Inc.; NVIDIA, NVAPI and NVML are trademarks of NVIDIA Corporation.
