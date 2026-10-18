"""Minimal PE parser: print exported names and imported DLLs. Stdlib only."""
import struct, sys

def rva_to_off(secs, rva):
    for va, vsz, raw, rsz in secs:
        if va <= rva < va + max(vsz, rsz):
            return rva - va + raw
    return None

def cstr(d, off):
    end = d.index(b"\0", off)
    return d[off:end].decode("latin-1")

def parse(path):
    d = open(path, "rb").read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    optsz = struct.unpack_from("<H", d, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", d, opt)[0]
    ddir = opt + (96 if magic == 0x10B else 112)
    exp_rva = struct.unpack_from("<I", d, ddir)[0]
    imp_rva = struct.unpack_from("<I", d, ddir + 8)[0]
    secs = []
    so = opt + optsz
    for i in range(nsec):
        vsz, va, rsz, raw = struct.unpack_from("<IIII", d, so + i * 40 + 8)
        secs.append((va, vsz, raw, rsz))
    exports, imports = [], []
    if exp_rva:
        eo = rva_to_off(secs, exp_rva)
        n_names, _, names_rva = struct.unpack_from("<III", d, eo + 24)[0], 0, struct.unpack_from("<I", d, eo + 32)[0]
        no = rva_to_off(secs, names_rva)
        for i in range(n_names):
            exports.append(cstr(d, rva_to_off(secs, struct.unpack_from("<I", d, no + 4 * i)[0])))
    if imp_rva:
        io = rva_to_off(secs, imp_rva)
        while True:
            name_rva = struct.unpack_from("<I", d, io + 12)[0]
            if not name_rva:
                break
            imports.append(cstr(d, rva_to_off(secs, name_rva)))
            io += 20
    return exports, imports

for p in sys.argv[1:]:
    try:
        ex, im = parse(p)
    except Exception as e:
        print(f"## {p}: error {e}"); continue
    print(f"## {p.split(chr(92))[-1]}  ({len(ex)} exports)")
    print("  imports:", ", ".join(im))
    for n in ex[:80]:
        print("   ", n)
    if len(ex) > 80:
        print(f"    ... +{len(ex) - 80} more")
