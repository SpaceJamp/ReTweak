"""Standalone reader for ASUS Power Detector+ (12V-2x6 per-pin current).

Mirrors what GPU Tweak III's ExpanModule.dll does: read-only NvAPI_I2CReadEx
calls to the monitor chip on the card's I2C bus. Nothing is ever written.

  python pinread.py            one reading
  python pinread.py --watch    refresh every second
  python pinread.py --raw      also dump raw register bytes
"""
import ctypes as C
import sys
import time

I2C_ADDR = 0x56      # 8-bit form, as NVAPI expects (7-bit 0x2B)
I2C_PORT = 1
SPEED_100KHZ = 4     # NV_I2C_SPEED enum value used by ExpanModule

REG_ID = (0x1E, 0x1F)  # 16-bit id/version; GPU Tweak requires >= 0x500
REG_PINS = 0x80        # 24 bytes: 6 x (u16 mV, u16 mA), big-endian
REG_POWER = 0x98       # 4 bytes: two u16, multiplied by GPU Tweak

ID_INIT = 0x0150E828
ID_ENUM_GPUS = 0xE5AC921F
ID_GET_PCI_IDS = 0x2DDFB66E
ID_I2C_READ_EX = 0x4D7B0709


class NV_I2C_INFO_V3(C.Structure):
    _fields_ = [
        ("version", C.c_uint32),
        ("displayMask", C.c_uint32),
        ("bIsDDCPort", C.c_uint8),
        ("i2cDevAddress", C.c_uint8),
        ("pbI2cRegAddress", C.POINTER(C.c_uint8)),
        ("regAddrSize", C.c_uint32),
        ("pbData", C.POINTER(C.c_uint8)),
        ("cbSize", C.c_uint32),
        ("i2cSpeed", C.c_uint32),
        ("i2cSpeedKhz", C.c_uint32),
        ("portId", C.c_uint8),
        ("bIsPortIdSet", C.c_uint32),
    ]


class NvApi:
    def __init__(self):
        dll = C.WinDLL("nvapi64.dll" if C.sizeof(C.c_void_p) == 8 else "nvapi.dll")
        qi = dll.nvapi_QueryInterface
        qi.restype = C.c_void_p
        qi.argtypes = [C.c_uint32]

        def fn(fid, *args):
            p = qi(fid)
            if not p:
                raise OSError(f"NVAPI function 0x{fid:08X} not available")
            return C.CFUNCTYPE(C.c_int, *args)(p)

        fn(ID_INIT)() == 0 or self._fail("NvAPI_Initialize")
        self._enum = fn(ID_ENUM_GPUS, C.POINTER(C.c_void_p * 64), C.POINTER(C.c_uint32))
        self._pci = fn(ID_GET_PCI_IDS, C.c_void_p, *[C.POINTER(C.c_uint32)] * 4)
        self._read = fn(ID_I2C_READ_EX, C.c_void_p, C.POINTER(NV_I2C_INFO_V3), C.POINTER(C.c_uint32))

    @staticmethod
    def _fail(what, rc=None):
        raise OSError(f"{what} failed" + (f" ({rc})" if rc is not None else ""))

    def gpus(self):
        handles, n = (C.c_void_p * 64)(), C.c_uint32()
        rc = self._enum(C.byref(handles), C.byref(n))
        if rc:
            self._fail("NvAPI_EnumPhysicalGPUs", rc)
        out = []
        for h in handles[:n.value]:
            dev, sub, rev, ext = (C.c_uint32() for _ in range(4))
            self._pci(h, C.byref(dev), C.byref(sub), C.byref(rev), C.byref(ext))
            out.append((h, dev.value, sub.value))
        return out

    def read(self, gpu, reg, size):
        regb = (C.c_uint8 * 1)(reg)
        data = (C.c_uint8 * size)()
        info = NV_I2C_INFO_V3()
        info.version = C.sizeof(NV_I2C_INFO_V3) | (3 << 16)
        info.i2cDevAddress = I2C_ADDR
        info.pbI2cRegAddress = C.cast(regb, C.POINTER(C.c_uint8))
        info.regAddrSize = 1
        info.pbData = C.cast(data, C.POINTER(C.c_uint8))
        info.cbSize = size
        info.i2cSpeed = 0xFFFF
        info.i2cSpeedKhz = SPEED_100KHZ
        info.portId = I2C_PORT
        info.bIsPortIdSet = 1
        unknown = C.c_uint32(1)
        rc = self._read(gpu, C.byref(info), C.byref(unknown))
        if rc:
            self._fail(f"I2C read reg 0x{reg:02X}", rc)
        return bytes(data)


def be16(b, i):
    return b[i] << 8 | b[i + 1]


def sample(nv, gpu, raw=False):
    pins = nv.read(gpu, REG_PINS, 24)
    # 6 groups of (mV, mA). Same pin order as ExpanModule: pin 1 is the last group.
    groups = [(be16(pins, off) * 0.001, be16(pins, off + 2) * 0.001) for off in (20, 16, 12, 8, 4, 0)]
    pw = nv.read(gpu, REG_POWER, 4)
    w0, w1 = be16(pw, 0) * 0.001, be16(pw, 2) * 0.001
    lines, total_w, total_a = [], 0.0, 0.0
    for i, (v, a) in enumerate(groups):
        if a > 50:  # GPU Tweak treats >50 A as a bad read
            lines.append(f"  pin {i + 1}: {v:6.3f} V   bad current read")
            continue
        total_a += a
        total_w += v * a
        lines.append(f"  pin {i + 1}: {v:6.3f} V  {a:6.2f} A  {v * a:6.1f} W")
    lines.append(f"  total:            {total_a:6.2f} A  {total_w:6.1f} W")
    lines.append(f"  reg 0x98 words: {w0:.3f}, {w1:.3f}  (one is likely 12V rail volts)")
    if raw:
        lines.append(f"  raw 0x80: {pins.hex(' ')}")
        lines.append(f"  raw 0x98: {pw.hex(' ')}")
    return "\n".join(lines)


def main():
    nv = NvApi()
    raw, watch = "--raw" in sys.argv, "--watch" in sys.argv
    for h, dev, sub in nv.gpus():
        print(f"GPU dev=0x{dev:08X} subsys=0x{sub:08X}")
        try:
            lo, hi = (nv.read(h, r, 1)[0] for r in REG_ID)
        except OSError as e:
            print(f"  no Power Detector chip at 0x{I2C_ADDR:02X} ({e})")
            continue
        ver = hi << 8 | lo
        print(f"  chip id/version 0x{ver:04X}" + ("" if ver >= 0x500 else "  (below GPU Tweak's 0x500 minimum)"))
        while True:
            print(sample(nv, h, raw))
            if not watch:
                break
            time.sleep(1)
            print()


if __name__ == "__main__":
    main()
