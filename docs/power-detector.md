# ASUS Power Detector+ (12V-2x6 per-pin readout)

How ASUS cards such as the ROG Astral report per-pin voltage and current on the
12V-2x6 / 12VHPWR connector, recovered from GPU Tweak III and verified on real
hardware. ReTweak implements this in `src/ReTweak.Core/AsusPowerDetector.cs`.

Reference setup: GPU Tweak III 2.1.9.5, ROG Astral RTX 5080
(PCI `10DE:2C02`, subsystem `1043:89DE`), NVIDIA driver 617.42.

## Where GPU Tweak III keeps it

GPU Tweak III installs to `C:\Program Files (x86)\ASUS\GPUTweakIII` and is native
C++ (mostly 32-bit), with a SOUI-based UI.

| Piece | Role |
|---|---|
| `GPU Tweak III.exe` | UI (`power_detector_root_wnd`, labels "CARD 12V-2X6", "MB 12V-2X6", "ROG EQ 12V-2X6"). Calls `Get12Voltage`, `GetPowerDetection`, `SetPowerDetection` |
| `TweakInterface.dll` | Exports `GetPowerDetection` / `SetPowerDetection` for the UI |
| `ExpanModule.dll` | Class `CPowerDetect`; exports `Expan_Api_GetPowerStatus`, `Expan_Api_Get_Sensor_RealTime_Data`, `Expan_Api_Check_MTPD_Support`, ... This is where the sensor is read |

"MB 12V-2X6" and "ROG EQ 12V-2X6" readings come from the motherboard side
(AsIO / ASUS kernel drivers), not from the card, and aren't covered here.

## Transport

`ExpanModule.dll` loads `nvapi.dll` at runtime and resolves functions through
`nvapi_QueryInterface`. It uses only these IDs:

| ID | Function |
|---|---|
| `0x0150E828` | `NvAPI_Initialize` |
| `0xE5AC921F` | `NvAPI_EnumPhysicalGPUs` |
| `0x1BE0B8E5` | `NvAPI_GPU_GetBusId` |
| `0x4D7B0709` | `NvAPI_I2CReadEx` |

**There is no I2C write.** The whole readout is register reads, which is why a
standalone reader is low-risk.

The wrapper (`ExpanModule.dll` VA `0x10001410`) fills a global `NV_I2C_INFO_V3`:

| Field | Value |
|---|---|
| `version` | `sizeof(NV_I2C_INFO_V3) \| (3 << 16)` (`0x3002C` on x86) |
| `i2cDevAddress` | `0x56` (8-bit form; 7-bit `0x2B`) |
| `regAddrSize` | 1 |
| `i2cSpeed` | `0xFFFF` (deprecated field) |
| `i2cSpeedKhz` | 4 (`NVAPI_I2C_SPEED_100KHZ`) |
| `portId` / `bIsPortIdSet` | 1 / 1 |
| 3rd argument | pointer to a `NvU32` set to 1 |

GPU Tweak III only probes on ASUS boards; ReTweak does the same (subsystem
vendor `0x1043`), because address `0x56` means nothing on other vendors' cards.

## Registers

| Reg | Len | Contents | ExpanModule code |
|---|---|---|---|
| `0x1E` / `0x1F` | 1 + 1 | Chip id/version, low/high byte. Must be `>= 0x500` (this card: `0x0513`) | `10006da0` |
| `0x4C`..`0x4F` | 1 each | Presence probes before the detector object is created | `10004630` |
| `0x80` | 24 | Six groups of (u16 BE millivolts, u16 BE milliamps). **Pin 1 is the last group** | `10003e30` |
| `0x98` | 4 | Two u16 BE ×0.001 (seen 12.000 / 0.780). GPU Tweak multiplies them (×2 ×1000). Meaning not confirmed | `10003e30` |
| `0x60` | 16 | **Eight board temperatures**, s16 BE in tenths of a °C (`0x0164` = 35.6 °C). See "Temperature sensors" | `10004160` |
| `0xC0` | 12 | Three u32 BE counters, probably usage/"mileage" time | `10003c40` |

GPU Tweak III treats a current above 50 A as a bad read. ReTweak also rejects
voltages outside 6–15 V, since an all-zero or all-`0xFF` frame is a bus glitch.

## Measured behaviour (RTX 5080 Astral, 2026-10-17)

| Property | Value |
|---|---|
| I2C read latency (24 bytes) | ~4 ms median, ~5 ms p95, no failures in 300 back-to-back reads |
| **New-data rate** | **one frame every ~600 ms (1.7/s)**, the same at idle and under load |
| Resolution | 8 mV voltage steps; ~20 mA current steps |
| Noise at steady load | ±25 mA current, ±2 mV voltage |
| Accuracy check | connector 382 W vs driver board power 403 W; the ~19 W median gap is PCIe slot power |
| Healthy cable at 400 W | 4.9–5.3 A per pin, max 5.58 A; imbalance ×1.04; pin spread 16 mV; lowest pin 11.872 V (104 mV droop) |

Polling faster than ~600 ms returns duplicate frames. ReTweak polls every 100 ms
so each new frame is picked up within 100 ms, drops duplicates, and raises a
"readings frozen" alert if nothing changes for 10 s.

## Temperature sensors (RTX 5080 Astral, 2026-10-18)

Three sources, all read-only:

| Source | What | Notes |
|---|---|---|
| NVML `nvmlDeviceGetTemperature` | GPU core, whole °C | Documented; the only sensor NVML's thermal API lists on this card |
| NVAPI `NvAPI_GPU_ThermalGetSensors` (`0x65FE3AAD`, undocumented) | 19-slot mask, 1/256 °C; `0xFF00` = no sensor | **Slot 1** = core (precise), **slot 2** = memory chip sensor, whole °C. Older architectures reportedly use slot 1 for hotspot and slot 9 for memory junction, so ReTweak only enables this on Blackwell (NVML arch 10) |
| ASUS chip register `0x60` | Eight board sensors, 0.1 °C | Order: VRM 1, Core, **Memory 1**, **Memory 2**, VRM 2, then three more. GPU Tweak III's memory temperature matches Memory 1/2 (~35 °C at idle) |

The memory chip sensor (slot 2) reads about 10 °C above the board memory sensors at
idle (44 vs 35 °C). A load test showed it lags the core and stays warm after load
stops, which is how memory, not a hotspot, behaves. ReTweak uses the board sensors
on ASUS cards (to match GPU Tweak III) and the chip sensor elsewhere.

No hotspot reading is available through NVML or the NVAPI calls above on RTX 50.
`research/thermal_probe.py` dumps the NVAPI slots and can log them under load;
`research/asus_regs.py` dumps ASUS chip registers.

## Hotspot on RTX 50 (2026-10-19)

NVIDIA's driver APIs stopped reporting a hotspot on Blackwell. HWMonitor 1.65.1+ shows one
anyway; disassembling HWMonitor 1.68 (x64, static only) shows how it finds temperatures:

| HWMonitor sensor | Source |
|---|---|
| GPU, Hot Spot, Memory Junction (ids 1-3) | NVAPI `0x0BC8163D` (undocumented, v2, 0xAB0 bytes) describes the 32 `ThermalGetSensors` slots: entry *i* at `0x28 + i*0x54`, word 0 = class, word 1 = **type** (0 core, 1 hotspot, 3 memory). HWMonitor picks the slot by type instead of by index |
| `[VRM #1]` ... `[TMP #8]` (ids 20-28) | The ASUS chip's eight board sensors at register `0x60` (address `0x2B`, the same reads ReTweak does), only on ASUS subsystems `89DE`, `89E3`, `89EA`, `89EC`, `8A61` |
| **Hot Spot** (id 29), Channel #2-#4 (ids 30-32) | **GPU register BAR0 + `0xAD0A90`** (`+0x94`, `+0x98`, `+0x9C`) read through HWMonitor's kernel driver. Bit 30 = valid, low 16 bits = 1/256 °C |

On the RTX 5080 Astral (driver 617.42) the type-1 NVAPI slot is empty (`0xFF00`), so only the
register path gives a hotspot. It reads about 4 °C above the core at light load
(HWMonitor: hotspot 38.0 / core 34.0; ReTweak: 40.6-41.9 / 37.0-38.0).

ReTweak reads the register with PawnIO's signed NVIDIA module (`ioctl_read_thermal_registers`,
GB202-GB207, BAR0 `0xAD0A90`, 24 bytes, read-only); see `src/ReTweak.Core/PawnIo/README.md`.
That needs PawnIO installed and ReTweak running as administrator.

## Research tools

In `research/` (Python, stdlib only except `dis32.py`, which needs `capstone`):

- `pe_exports.py FILE...`: list a PE's exports and imports
- `strgrep.py REGEX FILE...`: ASCII and UTF-16 string search
- `nvapi_ids.py FILE`: find embedded NVAPI QueryInterface IDs
- `dis32.py FILE at|func|callers|xref|floats OFFSET`: x86 disassembly helpers
- `dis64.py FILE va|xref|dis|back|imm|calls ADDR`: x64 disassembly helpers (RIP-relative xrefs)
- `thermal_info_probe.py`: dump NVAPI `0x0BC8163D` slot descriptions next to `ThermalGetSensors` readings
- `pinread.py [--watch] [--raw]`: the original standalone Python reader
- `asus_regs.py [REG LEN]... [--watch]`: dump ASUS chip registers with signed/BE interpretations
- `thermal_probe.py [--watch out.csv secs]`: list NVAPI/NVML temperature sensors, optionally log them
