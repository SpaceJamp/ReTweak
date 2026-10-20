"""Read-only probe of NVAPI 0x0BC8163D (thermal sensor descriptions, as used by HWMonitor 1.68)."""
import ctypes as C, struct

nvapi = C.WinDLL("nvapi64.dll")
qi = nvapi.nvapi_QueryInterface
qi.restype = C.c_void_p
qi.argtypes = [C.c_uint32]
def fn(fid, *args):
    p = qi(fid)
    return C.CFUNCTYPE(C.c_int, *args)(p) if p else None

assert fn(0x0150E828)() == 0
handles, n = (C.c_void_p * 64)(), C.c_uint32()
fn(0xE5AC921F, C.c_void_p, C.c_void_p)(C.byref(handles), C.byref(n))
gpu = handles[0]

info_fn = fn(0x0BC8163D, C.c_void_p, C.c_void_p)
print("info fn:", "present" if info_fn else "MISSING")
buf = (C.c_ubyte * 0xAB0)()
struct.pack_into("<I", buf, 0, 0xAB0 | (2 << 16))
rc = info_fn(gpu, buf)
print("rc", rc)
b = bytes(buf)
mask = struct.unpack_from("<I", b, 4)[0]
print(f"mask {mask:#010x}  header {b[:0x28].hex(' ')}")

class ThermalEx(C.Structure):
    _fields_ = [("version", C.c_uint32), ("mask", C.c_uint32), ("reserved", C.c_int * 8), ("temp", C.c_int * 32)]
sensors = fn(0x65FE3AAD, C.c_void_p, C.POINTER(ThermalEx))
t = ThermalEx(version=C.sizeof(ThermalEx) | (2 << 16), mask=mask)
rc2 = sensors(gpu, C.byref(t))
print("ThermalGetSensors with that mask: rc", rc2)

for i in range(32):
    if not mask >> i & 1: continue
    e = b[0x28 + i * 0x54:0x28 + (i + 1) * 0x54]
    words = struct.unpack_from("<21I", e)
    print(f"[{i:2}] temp {t.temp[i] / 256:7.2f} C  raw {t.temp[i]:#07x}  fields: " + " ".join(f"{w:x}" for w in words[:8]))
