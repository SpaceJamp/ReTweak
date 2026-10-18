"""Read-only dump of ASUS Power Detector chip registers (I2C 0x56, port 1) for decoding.

  python asus_regs.py [reg_hex len] ...      default: 0x60 16
  python asus_regs.py --watch 60 16          repeat every second

Prints raw bytes plus 8-bit signed, 16-bit BE signed and 16-bit /256 interpretations,
so a value can be matched against a known reading (e.g. GPU Tweak's memory temperature).
Only NvAPI_I2CReadEx is used; nothing is written.
"""
import ctypes as C
import struct
import sys
import time

nvapi = C.WinDLL("nvapi64.dll")
qi = nvapi.nvapi_QueryInterface
qi.restype = C.c_void_p
qi.argtypes = [C.c_uint32]


def fn(fid, *args):
    return C.CFUNCTYPE(C.c_int, *args)(qi(fid))


class I2CInfoV3(C.Structure):
    _fields_ = [("version", C.c_uint32), ("displayMask", C.c_uint32), ("bIsDDCPort", C.c_uint8),
                ("i2cDevAddress", C.c_uint8), ("pbI2cRegAddress", C.POINTER(C.c_uint8)), ("regAddrSize", C.c_uint32),
                ("pbData", C.POINTER(C.c_uint8)), ("cbSize", C.c_uint32), ("i2cSpeed", C.c_uint32),
                ("i2cSpeedKhz", C.c_uint32), ("portId", C.c_uint8), ("bIsPortIdSet", C.c_uint32)]


assert fn(0x0150E828)() == 0
enum = fn(0xE5AC921F, C.POINTER(C.c_void_p * 64), C.POINTER(C.c_uint32))
read = fn(0x4D7B0709, C.c_void_p, C.POINTER(I2CInfoV3), C.POINTER(C.c_uint32))
handles, n = (C.c_void_p * 64)(), C.c_uint32()
enum(C.byref(handles), C.byref(n))
gpu = handles[0]


def i2c_read(reg, size):
    regb = (C.c_uint8 * 1)(reg)
    data = (C.c_uint8 * size)()
    info = I2CInfoV3(version=C.sizeof(I2CInfoV3) | (3 << 16), i2cDevAddress=0x56,
                     pbI2cRegAddress=C.cast(regb, C.POINTER(C.c_uint8)), regAddrSize=1,
                     pbData=C.cast(data, C.POINTER(C.c_uint8)), cbSize=size, i2cSpeed=0xFFFF,
                     i2cSpeedKhz=4, portId=1, bIsPortIdSet=1)
    unk = C.c_uint32(1)
    rc = read(gpu, C.byref(info), C.byref(unk))
    return rc, bytes(data)


args = [a for a in sys.argv[1:] if a != "--watch"]
pairs = [(int(args[i], 16), int(args[i + 1])) for i in range(0, len(args) - 1, 2)] or [(0x60, 16)]
while True:
    for reg, size in pairs:
        rc, b = i2c_read(reg, size)
        if rc:
            print(f"reg 0x{reg:02X}: read failed ({rc})")
            continue
        s8 = struct.unpack(f"{size}b", b)
        s16 = struct.unpack(f">{size // 2}h", b[: size // 2 * 2])
        print(f"reg 0x{reg:02X} raw: {b.hex(' ')}")
        print(f"   s8   : {list(s8)}")
        print(f"   s16BE: {list(s16)}")
        print(f"   /256 : {[round(v / 256, 2) for v in s16]}")
    if "--watch" not in sys.argv:
        break
    time.sleep(1)
    print()
