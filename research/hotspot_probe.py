"""Read-only check of the RTX 50 hotspot register through PawnIO, independent of ReTweak's C# code.

  python hotspot_probe.py [seconds] [out.csv]      (run as administrator; PawnIO must be installed)

Loads PawnIO's signed NVIDIA module (src/ReTweak.Core/PawnIo/Nvidia.bin), calls
ioctl_read_thermal_registers at 10 Hz and logs the six raw DWORDs from BAR0 + 0xAD0A90
next to NVML's core temperature, load and power. Then reports: valid-bit coverage, how
often each channel changes, whether channel 5 is the maximum of sensors 1-4 (it is: the
hotspot), how channel 1 (what HWMonitor shows as "Hot Spot") compares, and how far the
hotspot sits above the core.
"""
import ctypes as C
import ctypes.wintypes as W
import os
import statistics
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
MODULE = os.path.join(HERE, "..", "src", "ReTweak.Core", "PawnIo", "Nvidia.bin")
DEVICE = r"\\?\GLOBALROOT\Device\PawnIO"
IOCTL_LOAD = (41394 << 16) | (0x821 << 2)
IOCTL_EXEC = (41394 << 16) | (0x841 << 2)
VALID = 1 << 30

k32 = C.WinDLL("kernel32", use_last_error=True)
k32.CreateFileW.restype = W.HANDLE
k32.CreateFileW.argtypes = [W.LPCWSTR, W.DWORD, W.DWORD, C.c_void_p, W.DWORD, W.DWORD, W.HANDLE]
k32.DeviceIoControl.argtypes = [W.HANDLE, W.DWORD, C.c_void_p, W.DWORD, C.c_void_p, W.DWORD, C.POINTER(W.DWORD), C.c_void_p]

h = k32.CreateFileW(DEVICE, 0xC0000000, 3, None, 3, 0, None)
if h in (None, W.HANDLE(-1).value):
    sys.exit(f"Can't open PawnIO (error {C.get_last_error()}): installed, and running as administrator?")
blob = open(MODULE, "rb").read()
n = W.DWORD()
if not k32.DeviceIoControl(h, IOCTL_LOAD, blob, len(blob), None, 0, C.byref(n), None):
    sys.exit(f"PawnIO refused the module (error {C.get_last_error()})")

# NVML: bus/device/function of GPU 0, plus core temperature, load and power.
nvml = C.WinDLL("nvml.dll")
nvml.nvmlInit_v2()
dev = C.c_void_p()
nvml.nvmlDeviceGetHandleByIndex_v2(0, C.byref(dev))


class Pci(C.Structure):
    _fields_ = [("legacy", C.c_char * 16), ("domain", C.c_uint), ("bus", C.c_uint), ("device", C.c_uint),
                ("id", C.c_uint), ("subsys", C.c_uint), ("busId", C.c_char * 32)]


class Util(C.Structure):
    _fields_ = [("gpu", C.c_uint), ("mem", C.c_uint)]


pci = Pci()
nvml.nvmlDeviceGetPciInfo_v3(dev, C.byref(pci))
function = int(pci.busId.decode().rsplit(".", 1)[1], 16)


def read_channels():
    req = (C.c_ubyte * (32 + 3 * 8))()
    name = b"ioctl_read_thermal_registers"
    C.memmove(req, name, len(name))
    C.memmove(C.addressof(req) + 32, (C.c_int64 * 3)(pci.bus, pci.device, function), 24)
    out = (C.c_int64 * 6)()
    if not k32.DeviceIoControl(h, IOCTL_EXEC, req, len(req), out, C.sizeof(out), C.byref(n), None):
        return None
    return [v & 0xFFFFFFFF for v in out]


seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 20
path = sys.argv[2] if len(sys.argv) > 2 else None
log = open(path, "w") if path else None
if log:
    log.write("t,core_c,load_pct,power_w," + ",".join(f"raw{i + 1}" for i in range(6)) + "," +
              ",".join(f"ch{i + 1}_c" for i in range(6)) + "\n")

rows, failures = [], 0
start = time.perf_counter()
while time.perf_counter() - start < seconds:
    raw = read_channels()
    if raw is None:
        failures += 1
        time.sleep(0.1)
        continue
    t, u, mw = C.c_uint(), Util(), C.c_uint()
    nvml.nvmlDeviceGetTemperature(dev, 0, C.byref(t))
    nvml.nvmlDeviceGetUtilizationRates(dev, C.byref(u))
    nvml.nvmlDeviceGetPowerUsage(dev, C.byref(mw))
    temps = [(r & 0xFFFF) / 256 if r & VALID else None for r in raw]
    rows.append((time.perf_counter() - start, t.value, u.gpu, mw.value / 1000, raw, temps))
    if log:
        log.write(f"{rows[-1][0]:.2f},{t.value},{u.gpu},{mw.value / 1000:.1f}," + ",".join(f"0x{r:08X}" for r in raw) + "," +
                  ",".join("" if c is None else f"{c:.2f}" for c in temps) + "\n")
    time.sleep(0.1)

print(f"GPU {pci.busId.decode()}  {len(rows)} reads in {seconds:.0f} s, {failures} failed")
for ch in range(6):
    vals = [r[5][ch] for r in rows]
    valid = [v for v in vals if v is not None]
    changes = [rows[i][0] for i in range(1, len(rows)) if rows[i][4][ch] != rows[i - 1][4][ch]]
    gaps = [b - a for a, b in zip(changes, changes[1:])]
    upd = f"changes every ~{statistics.median(gaps):.2f} s" if gaps else "never changed"
    rng = f"{min(valid):.2f}-{max(valid):.2f} °C" if valid else "no valid samples"
    flags = {f"0x{r[4][ch] & 0xFFFF0000:08X}" for r in rows}
    print(f"  ch{ch + 1}: valid {len(valid)}/{len(vals)}, {rng}, {upd}, upper bits {sorted(flags)}")

both = [r for r in rows if all(c is not None for c in r[5])]
eq = sum(1 for r in both if abs(r[5][4] - max(r[5][:4])) < 1e-6)
print(f"  ch5 == max(ch1..ch4) in {eq}/{len(both)} reads (ch5 = hotspot)")
low = [r[5][4] - r[5][0] for r in both]
if low:
    print(f"  ch1 (HWMonitor's 'Hot Spot') below ch5 by {min(low):.2f}-{max(low):.2f} °C, mean {statistics.mean(low):.2f}")
gap = [r[5][4] - r[1] for r in rows if r[5][4] is not None]
if gap:
    print(f"  hotspot - NVML core: median {statistics.median(gap):+.1f} °C, range {min(gap):+.1f} to {max(gap):+.1f}")
    print(f"  NVML load {min(r[2] for r in rows)}-{max(r[2] for r in rows)} %, power {min(r[3] for r in rows):.0f}-{max(r[3] for r in rows):.0f} W")
