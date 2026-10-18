"""x86 PE helper on capstone.

  dis.py FILE at OFFSET [COUNT]      disassemble from file offset (hex)
  dis.py FILE callers OFFSET         list `call rel32` sites targeting file offset
  dis.py FILE func OFFSET [COUNT]    walk back to function start (after CC/C3 padding), disassemble
"""
import struct, sys
from capstone import Cs, CS_ARCH_X86, CS_MODE_32


class PE:
    def __init__(self, path):
        self.d = open(path, "rb").read()
        pe = struct.unpack_from("<I", self.d, 0x3C)[0]
        nsec = struct.unpack_from("<H", self.d, pe + 6)[0]
        optsz = struct.unpack_from("<H", self.d, pe + 20)[0]
        self.base = struct.unpack_from("<I", self.d, pe + 24 + 28)[0]
        self.secs = []
        so = pe + 24 + optsz
        for i in range(nsec):
            name = self.d[so + i * 40:so + i * 40 + 8].rstrip(b"\0").decode()
            vsz, va, rsz, raw = struct.unpack_from("<IIII", self.d, so + i * 40 + 8)
            self.secs.append((name, va, vsz, raw, rsz))

    def off2va(self, off):
        for _, va, vsz, raw, rsz in self.secs:
            if raw <= off < raw + rsz:
                return self.base + va + off - raw

    def va2off(self, v):
        rva = v - self.base
        for _, va, vsz, raw, rsz in self.secs:
            if va <= rva < va + max(vsz, rsz):
                return rva - va + raw

    def text(self):
        return next(s for s in self.secs if s[0] == ".text")


def main():
    pe = PE(sys.argv[1])
    cmd, off = sys.argv[2], int(sys.argv[3], 16)
    count = int(sys.argv[4]) if len(sys.argv) > 4 else 60
    md = Cs(CS_ARCH_X86, CS_MODE_32)
    if cmd == "func":
        o = off
        while o > 0 and not (pe.d[o - 1] in (0xCC, 0xC3) and pe.d[o - 2] in (0xCC, 0xC3, 0x00)):
            o -= 1
        print(f"; function start ~ file {o:08x} va {pe.off2va(o):08x}")
        off, cmd = o, "at"
    if cmd == "at":
        va = pe.off2va(off)
        for n, ins in enumerate(md.disasm(pe.d[off:off + count * 8], va)):
            if n >= count:
                break
            print(f"{ins.address:08x} {pe.va2off(ins.address):06x}  {ins.bytes.hex():20s} {ins.mnemonic} {ins.op_str}")
    elif cmd == "floats":
        # sys.argv[3] is a VA: dump COUNT float32 values from there
        o = pe.va2off(off)
        for i in range(count):
            print(f"{off + 4 * i:08x}  {struct.unpack_from('<f', pe.d, o + 4 * i)[0]!r}")
    elif cmd == "xref":
        # sys.argv[3] is a VA here: find instructions in .text embedding it as imm32/disp32
        target = off
        _, va, vsz, raw, rsz = pe.text()
        pat = struct.pack("<I", target)
        o = pe.d.find(pat, raw, raw + rsz)
        while o != -1:
            print(f"ref at file {o:06x} va {pe.off2va(o):08x}")
            o = pe.d.find(pat, o + 1, raw + rsz)
    elif cmd == "callers":
        target = pe.off2va(off)
        _, va, vsz, raw, rsz = pe.text()
        for o in range(raw, raw + rsz - 5):
            if pe.d[o] == 0xE8:
                rel = struct.unpack_from("<i", pe.d, o + 1)[0]
                if (pe.off2va(o) + 5 + rel) & 0xFFFFFFFF == target:
                    print(f"call at file {o:06x} va {pe.off2va(o):08x}")


main()
