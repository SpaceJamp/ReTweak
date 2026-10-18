"""Read-only probe of every GPU temperature NVIDIA's driver will report.

  python thermal_probe.py
  python thermal_probe.py --watch out.csv [seconds]   log live sensors every 0.5 s

1. NvAPI_GPU_GetThermalSettings (documented): per-target sensors (GPU, memory, board...)
2. NvAPI_GPU_ThermalGetSensors (undocumented, used by HWiNFO/LibreHardwareMonitor):
   a bitmask of up to 32 sensors in 1/256 °C. On Ampere/Ada, index 1 is the hotspot
   and index 9 the memory junction. Probes which mask the driver accepts.
3. NVML nvmlDeviceGetTemperature for the core, for comparison.
"""
import ctypes as C

nvapi = C.WinDLL("nvapi64.dll")
qi = nvapi.nvapi_QueryInterface
qi.restype = C.c_void_p
qi.argtypes = [C.c_uint32]


def fn(fid, *args):
    p = qi(fid)
    return C.CFUNCTYPE(C.c_int, *args)(p) if p else None


init = fn(0x0150E828)
enum_gpus = fn(0xE5AC921F, C.POINTER(C.c_void_p * 64), C.POINTER(C.c_uint32))
assert init and init() == 0, "NvAPI_Initialize failed"
handles, n = (C.c_void_p * 64)(), C.c_uint32()
assert enum_gpus(C.byref(handles), C.byref(n)) == 0
gpu = handles[0]

# --- 1. documented thermal settings ------------------------------------------
class Sensor(C.Structure):
    _fields_ = [("controller", C.c_int), ("defaultMinTemp", C.c_int), ("defaultMaxTemp", C.c_int),
                ("currentTemp", C.c_int), ("target", C.c_int)]

class ThermalSettingsV2(C.Structure):
    _fields_ = [("version", C.c_uint32), ("count", C.c_uint32), ("sensor", Sensor * 3)]

TARGETS = {0: "none", 1: "GPU", 2: "memory", 4: "power supply", 8: "board",
           9: "VCD board", 10: "VCD inlet", 11: "VCD outlet", 15: "all", -1: "unknown"}
CONTROLLERS = {0: "none", 1: "GPU internal", 2: "ADM1032", 3: "MAX6649", 4: "MAX1617", 5: "LM99",
               6: "LM89", 7: "LM64", 8: "ADT7473", 9: "SBMAX6649", 10: "VBIOS evt", 11: "OS", -1: "unknown"}

get_thermal = fn(0xE3640A56, C.c_void_p, C.c_int, C.POINTER(ThermalSettingsV2))
ts = ThermalSettingsV2(version=C.sizeof(ThermalSettingsV2) | (2 << 16))
rc = get_thermal(gpu, 15, C.byref(ts))  # NVAPI_THERMAL_TARGET_ALL
print(f"1. NvAPI_GPU_GetThermalSettings: rc={rc}, {ts.count} sensor(s)")
for i in range(min(ts.count, 3)):
    s = ts.sensor[i]
    print(f"   [{i}] target={TARGETS.get(s.target, s.target)}  controller={CONTROLLERS.get(s.controller, s.controller)}  "
          f"current={s.currentTemp} C  range {s.defaultMinTemp}..{s.defaultMaxTemp} C")

# --- 2. undocumented sensor mask ---------------------------------------------
class ThermalEx(C.Structure):
    _fields_ = [("version", C.c_uint32), ("mask", C.c_uint32), ("reserved", C.c_int * 8), ("temp", C.c_int * 32)]

sensors = fn(0x65FE3AAD, C.c_void_p, C.POINTER(ThermalEx))
print("\n2. NvAPI_GPU_ThermalGetSensors (undocumented):", "not available" if sensors is None else "")
if sensors:
    accepted = None
    for bits in range(32, 0, -1):  # find the widest mask the driver accepts
        t = ThermalEx(version=C.sizeof(ThermalEx) | (2 << 16), mask=(1 << bits) - 1)
        if sensors(gpu, C.byref(t)) == 0:
            accepted = (bits, t)
            break
    if accepted is None:
        print("   driver rejected every mask")
    else:
        bits, t = accepted
        print(f"   accepted mask 0x{(1 << bits) - 1:X} ({bits} sensors)")
        names = {0: "GPU core", 1: "hotspot (Ampere/Ada)", 9: "memory junction (Ampere/Ada)"}
        for i in range(bits):
            raw = t.temp[i]
            print(f"   [{i:2}] {raw / 256:7.2f} C  raw={raw:#x}  {names.get(i, '')}")

# --- 3. NVML ------------------------------------------------------------------
nvml = C.WinDLL("nvml.dll")
nvml.nvmlInit_v2()
dev = C.c_void_p()
nvml.nvmlDeviceGetHandleByIndex_v2(0, C.byref(dev))
temp = C.c_uint()
rc = nvml.nvmlDeviceGetTemperature(dev, 0, C.byref(temp))
print(f"\n3. NVML core temperature: {temp.value} C (rc={rc})")

# --- watch mode: tell hotspot from memory by how each slot behaves under load ---
import sys
import time
from datetime import datetime

if "--watch" in sys.argv and sensors and accepted:
    i = sys.argv.index("--watch")
    path = sys.argv[i + 1] if len(sys.argv) > i + 1 else "thermal.csv"
    seconds = float(sys.argv[i + 2]) if len(sys.argv) > i + 2 else 600
    bits = accepted[0]
    live = [k for k in range(bits) if accepted[1].temp[k] != 0xFF00]  # skip "no sensor" slots
    clk_core, clk_mem, power = C.c_uint(), C.c_uint(), C.c_uint()

    class Util(C.Structure):
        _fields_ = [("gpu", C.c_uint), ("memory", C.c_uint)]
    util = Util()

    print(f"\nLogging slots {live} to {path} for {seconds:.0f} s. Ctrl+C to stop.")
    with open(path, "w") as f:
        f.write("time,nvml_core_c," + ",".join(f"slot{k}_c" for k in live) +
                ",core_mhz,mem_mhz,power_w,gpu_util,mem_util\n")
        end = time.time() + seconds
        try:
            while time.time() < end:
                t = ThermalEx(version=C.sizeof(ThermalEx) | (2 << 16), mask=(1 << bits) - 1)
                if sensors(gpu, C.byref(t)) != 0:
                    time.sleep(0.5)
                    continue
                nvml.nvmlDeviceGetTemperature(dev, 0, C.byref(temp))
                nvml.nvmlDeviceGetClockInfo(dev, 0, C.byref(clk_core))
                nvml.nvmlDeviceGetClockInfo(dev, 2, C.byref(clk_mem))
                nvml.nvmlDeviceGetPowerUsage(dev, C.byref(power))
                nvml.nvmlDeviceGetUtilizationRates(dev, C.byref(util))
                vals = [t.temp[k] / 256 for k in live]
                f.write(f"{datetime.now().isoformat()},{temp.value}," + ",".join(f"{v:.2f}" for v in vals) +
                        f",{clk_core.value},{clk_mem.value},{power.value / 1000:.1f},{util.gpu},{util.memory}\n")
                f.flush()
                time.sleep(0.5)
        except KeyboardInterrupt:
            pass
    print("Stopped.")

nvml.nvmlShutdown()
