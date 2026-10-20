"""Static-analysis helper for a 64-bit PE (reads bytes only, never runs the file).

  dis64.py FILE va OFF              file offset -> VA
  dis64.py FILE xref VA             RIP-relative references to VA (disp32 assumed to end the instruction,
                                   or be followed by an imm8/imm32)
  dis64.py FILE dis VA [COUNT]
  dis64.py FILE back VA [BYTES]
  dis64.py FILE imm HEX             32-bit constant anywhere in .text
  dis64.py FILE calls VA            direct call rel32 sites targeting VA
"""
import struct, sys
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

d = open(sys.argv[1], "rb").read()
pe = struct.unpack_from("<I", d, 0x3C)[0]
nsec = struct.unpack_from("<H", d, pe + 6)[0]
optsz = struct.unpack_from("<H", d, pe + 20)[0]
base = struct.unpack_from("<Q", d, pe + 24 + 24)[0]
secs = []
for i in range(nsec):
    so = pe + 24 + optsz + i * 40
    name = d[so:so + 8].rstrip(b"\0").decode()
    vsz, va, rsz, raw = struct.unpack_from("<IIII", d, so + 8)
    secs.append((name, va, vsz, raw, rsz))
text = next(s for s in secs if s[0] == ".text")

def off2va(o):
    for _, va, vsz, raw, rsz in secs:
        if raw <= o < raw + rsz: return base + va + o - raw
def va2off(v):
    r = v - base
    for _, va, vsz, raw, rsz in secs:
        if va <= r < va + max(vsz, rsz): return r - va + raw

md = Cs(CS_ARCH_X86, CS_MODE_64)
def dis(v, n):
    o = va2off(v)
    for i, ins in enumerate(md.disasm(d[o:o + n * 12], v)):
        if i >= n: break
        print(f"{ins.address:010x}  {ins.mnemonic:6s} {ins.op_str}")

cmd, arg = sys.argv[2], int(sys.argv[3], 16)
n = int(sys.argv[4]) if len(sys.argv) > 4 else 40
_, tva, tvsz, traw, trsz = text
if cmd == "va": print(hex(off2va(arg)))
elif cmd == "imm":
    pat = struct.pack("<I", arg)
    o = d.find(pat, traw, traw + trsz)
    while o != -1:
        print(f"imm at va {off2va(o):010x}")
        o = d.find(pat, o + 1, traw + trsz)
elif cmd == "xref":
    tbase = base + tva - traw
    for o in range(traw, traw + trsz - 4):
        disp = struct.unpack_from("<i", d, o)[0]
        end = tbase + o + 4
        for extra in (0, 1, 4):
            if end + extra + disp == arg:
                print(f"ref near va {end - 4:010x} (+{extra})")
elif cmd == "calls":
    tbase = base + tva - traw
    for o in range(traw, traw + trsz - 5):
        if d[o] == 0xE8 and tbase + o + 5 + struct.unpack_from("<i", d, o + 1)[0] == arg:
            print(f"call at va {tbase + o:010x}")
elif cmd == "dis": dis(arg, n)
elif cmd == "back": dis(arg - n, n)
