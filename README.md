# ReTweak

A lightweight successor to ASUS GPU Tweak III for NVIDIA cards, focused first on
**monitoring the 12V-2x6 power connector** and warning early when something is
going wrong.

- GPU stats on any NVIDIA card through NVML (temperature, clocks, load, board power, fans, VRAM)
- **Per-pin voltage and current** on ASUS cards with Power Detector+ (e.g. ROG Astral), read directly from the card's monitor chip; see [docs/power-detector.md](docs/power-detector.md)
- **Temperatures**: precise core (0.01 °C) and memory on RTX 50; on ASUS cards, memory comes from the
  board sensors GPU Tweak III uses, plus 8 board sensors (VRM, core, memory and more)
- Early-warning alerts: pin over-current, low/high voltage, pin-to-pin voltage spread, current imbalance, fast voltage drops or current ramps, frozen sensor, high core or memory temperature
- Native Windows notifications for alerts
- Runs in the notification area: closing the window keeps monitoring; the tray icon shows the worst active alert
- Optional start with Windows (`--minimized`, tray only), CSV logging and adjustable thresholds in Settings
  (stored in `%LocalAppData%\ReTweak\settings.json`)

Read-only: ReTweak never writes to the GPU.

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
