"""Analyze a ReTweak load-test log.

  python analyze_load.py logs/loadtest.csv

Reads <name>.csv (connector frames), <name>.gpu.csv (driver stats, 1/s) and
<name>.txt (CLI output, for alerts). Prints frame rate, per-pin extremes, noise,
spread/imbalance distributions and how close each metric came to its thresholds.
"""
import csv
import statistics as st
import sys
from datetime import datetime
from pathlib import Path

# Must match AlertOptions defaults in AlertEngine.cs
PIN_WARN_A, PIN_CRIT_A = 7.6, 9.5
WARN_LOW_V, CRIT_LOW_V, WARN_HIGH_V = 11.64, 11.40, 12.60
WARN_SPREAD, CRIT_SPREAD = 0.040, 0.100
WARN_IMB, CRIT_IMB = 1.25, 1.70
LOAD_A = 10.0  # spread/imbalance only evaluated above this total current


def ts(s):
    return datetime.fromisoformat(s)


def pct(values, p):
    if not values:
        return float("nan")
    v = sorted(values)
    return v[min(len(v) - 1, int(round(p / 100 * (len(v) - 1))))]


def load_frames(path):
    frames = []
    with open(path, newline="") as f:
        for row in csv.DictReader(f):
            t = ts(row["time"])
            v = [float(row[f"v{i}"]) for i in range(1, 7)]
            a = [float(row[f"a{i}"]) for i in range(1, 7)]
            frames.append((t, v, a))
    return frames


def load_gpu(path):
    rows = []
    if not path.exists():
        return rows
    with open(path, newline="") as f:
        for row in csv.DictReader(f):
            if row["board_w"]:
                rows.append((ts(row["time"]), float(row["board_w"]), row))
    return rows


def margin(value, warn, crit, higher_is_bad=True):
    if higher_is_bad:
        state = "CRITICAL" if value >= crit else "warning" if value >= warn else "ok"
        return f"{state:8s} ({value / warn * 100:5.1f}% of warn)"
    state = "CRITICAL" if value < crit else "warning" if value < warn else "ok"
    return f"{state:8s} ({value - warn:+.3f} V vs warn)"


def main():
    base = Path(sys.argv[1] if len(sys.argv) > 1 else "logs/loadtest.csv")
    frames = load_frames(base)
    gpu = load_gpu(base.with_suffix(".gpu.csv"))
    if len(frames) < 3:
        sys.exit(f"Only {len(frames)} frames in {base}")

    dur = (frames[-1][0] - frames[0][0]).total_seconds()
    totals = [sum(a) for _, _, a in frames]
    loaded = [i for i, t in enumerate(totals) if t >= LOAD_A]
    idle = [i for i, t in enumerate(totals) if t < 8]  # desktop use on a 5080 can sit at ~6 A

    print(f"== {base.name}: {len(frames)} frames over {dur / 60:.1f} min, "
          f"{len(loaded)} under load (>= {LOAD_A:.0f} A), {len(idle)} idle")

    # Frame cadence: how often the chip delivered *new* values
    gaps = [(frames[i][0] - frames[i - 1][0]).total_seconds() for i in range(1, len(frames))]
    def cadence(idx, label):
        g = [gaps[i - 1] for i in idx if i > 0]
        if g:
            print(f"   {label:6s} frame gap: median {st.median(g) * 1000:4.0f} ms  p95 {pct(g, 95) * 1000:4.0f} ms  "
                  f"max {max(g):5.2f} s  -> {1 / st.median(g):.1f} frames/s")
    print("\n-- Frame rate (alerts debounce on frames, so this sets alert latency)")
    cadence(range(1, len(frames)), "all")
    cadence(loaded, "load")
    cadence(idle, "idle")

    print("\n-- Per pin (whole run)")
    print("   pin   max A   mean A@load   min V    max V")
    for p in range(6):
        amps = [a[p] for _, _, a in frames]
        volts = [v[p] for _, v, _ in frames]
        mean_load = st.mean(frames[i][2][p] for i in loaded) if loaded else float("nan")
        print(f"   {p + 1}   {max(amps):6.2f}   {mean_load:9.2f}    {min(volts):7.3f}  {max(volts):7.3f}")

    peak_w = max(sum(v[p] * a[p] for p in range(6)) for _, v, a in frames)
    print(f"   peak connector power: {peak_w:.0f} W, peak total current {max(totals):.1f} A")

    if loaded:
        spreads = [max(frames[i][1]) - min(frames[i][1]) for i in loaded]
        imbs = []
        for i in loaded:
            a = frames[i][2]
            avg = sum(a) / 6
            imbs.append(max(a) / avg if avg > 0.5 else 1.0)
        print("\n-- Under load")
        print(f"   voltage spread mV: p50 {pct(spreads, 50) * 1000:.0f}  p99 {pct(spreads, 99) * 1000:.0f}  max {max(spreads) * 1000:.0f}")
        print(f"   current imbalance: p50 x{pct(imbs, 50):.2f}  p99 x{pct(imbs, 99):.2f}  max x{max(imbs):.2f}")
        idle_v = st.median(min(frames[i][1]) for i in idle) if idle else float("nan")
        load_v = st.median(min(frames[i][1]) for i in loaded)
        print(f"   lowest-pin voltage: idle median {idle_v:.3f} V, load median {load_v:.3f} V "
              f"(droop {(idle_v - load_v) * 1000:.0f} mV)")

        # Noise: frame-to-frame change per pin while load is steady (total within 5% of previous)
        deltas_a, deltas_v = [], []
        for i in loaded:
            if i > 0 and i - 1 in loaded and abs(totals[i] - totals[i - 1]) < 0.05 * totals[i]:
                deltas_a += [frames[i][2][p] - frames[i - 1][2][p] for p in range(6)]
                deltas_v += [frames[i][1][p] - frames[i - 1][1][p] for p in range(6)]
        if len(deltas_a) > 10:
            print(f"   steady-load noise: current sd {st.pstdev(deltas_a) / 2 ** 0.5 * 1000:.0f} mA, "
                  f"voltage sd {st.pstdev(deltas_v) / 2 ** 0.5 * 1000:.1f} mV per reading")

        print("\n-- Threshold margins (worst moment)")
        max_a = max(max(a) for _, _, a in frames)
        min_v = min(min(v) for _, v, _ in frames)
        print(f"   pin current   {max_a:6.2f} A   {margin(max_a, PIN_WARN_A, PIN_CRIT_A)}")
        print(f"   pin voltage   {min_v:6.3f} V   {margin(min_v, WARN_LOW_V, CRIT_LOW_V, higher_is_bad=False)}")
        print(f"   spread        {max(spreads) * 1000:6.0f} mV  {margin(max(spreads), WARN_SPREAD, CRIT_SPREAD)}")
        # Imbalance is a ratio where 1.0 is perfect, so compare the excess over 1.0
        imb = max(imbs)
        state = "CRITICAL" if imb >= CRIT_IMB else "warning" if imb >= WARN_IMB else "ok"
        print(f"   imbalance     x{imb:5.2f}    {state:8s} ({(imb - 1) / (WARN_IMB - 1) * 100:5.1f}% of warn excess)")

    if gpu:
        # Match each driver sample to the nearest connector frame
        diffs = []
        j = 0
        for t, board_w, _ in gpu:
            while j + 1 < len(frames) and frames[j + 1][0] <= t:
                j += 1
            _, v, a = frames[j]
            conn_w = sum(v[p] * a[p] for p in range(6))
            if board_w > 100:
                diffs.append(board_w - conn_w)
        print("\n-- Connector vs driver board power (difference ~ PCIe slot power + measurement error)")
        if diffs:
            print(f"   board - connector: median {st.median(diffs):.0f} W, p5 {pct(diffs, 5):.0f} W, p95 {pct(diffs, 95):.0f} W "
                  f"(over {len(diffs)} loaded seconds)")
        peak = max(gpu, key=lambda r: r[1])
        print(f"   driver peak board power {peak[1]:.0f} W of {peak[2]['limit_w']} W limit, "
              f"max temp {max(int(r[2]['temp_c'] or 0) for r in gpu)} C")

    txt = base.with_suffix(".txt")
    if txt.exists():
        lines = [l.strip() for l in txt.read_text(errors="replace").splitlines()
                 if any(k in l for k in ("Warning:", "Critical:", "sampler error"))]
        print(f"\n-- Alerts raised during the run: {len(lines)}")
        for l in lines[:20]:
            print("   " + l)


if __name__ == "__main__":
    main()
