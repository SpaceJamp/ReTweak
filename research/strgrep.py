"""Grep ASCII and UTF-16LE strings in binaries. Usage: strgrep.py REGEX file..."""
import re, sys

pat = re.compile(sys.argv[1], re.I)
for path in sys.argv[2:]:
    d = open(path, "rb").read()
    hits = []
    for m in re.finditer(rb"[\x20-\x7e]{5,}", d):
        s = m.group().decode()
        if pat.search(s):
            hits.append((m.start(), "a", s))
    for m in re.finditer(rb"(?:[\x20-\x7e]\x00){5,}", d):
        s = m.group().decode("utf-16le")
        if pat.search(s):
            hits.append((m.start(), "w", s))
    if hits:
        print(f"## {path.split(chr(92))[-1]} ({len(hits)})")
        for off, k, s in sorted(hits)[:60]:
            print(f"  {off:08x} {k} {s[:200]}")
