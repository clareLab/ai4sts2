import argparse
import math
import statistics
from collections import defaultdict

import metrics


def wilson(k, n, z=1.96):
    if n == 0:
        return (0.0, 0.0)
    p = k / n
    d = 1 + z * z / n
    centre = (p + z * z / (2 * n)) / d
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, centre - half), min(1.0, centre + half))


def act_of(floors):
    if floors >= 47:
        return 3
    if floors >= 32:
        return 2
    if floors >= 17:
        return 1
    return 0


def gather(tag=None, character=None, players=None, since=None):
    groups = defaultdict(list)
    for row in metrics.load_index():
        if row.get("kind") != "run":
            continue
        if tag is not None and row.get("tag") != tag:
            continue
        if character is not None and row.get("character") != character:
            continue
        if players is not None and row.get("players", 1) != players:
            continue
        if since is not None and row.get("ts", "") < since:
            continue
        key = (row.get("tag") or "-", row.get("character"), row.get("players", 1))
        for case in row.get("cases", []):
            groups[key].append({**case, "commit": (row.get("build") or {}).get("commit")})
    return groups


def summarize(cases):
    valid = [c for c in cases if c.get("outcome") != "error"]
    n = len(valid)
    wins = sum(c.get("outcome") == "act-cleared" for c in valid)
    lo, hi = wilson(wins, n)
    acts = [act_of(c.get("floors", 0)) for c in valid]
    reached = {a: sum(x >= a for x in acts) for a in (1, 2, 3)}
    walls = [c.get("wallSeconds", 0) for c in valid if c.get("wallSeconds")]
    return {
        "n": n,
        "errors": len(cases) - n,
        "wins": wins,
        "rate": wins / n if n else 0.0,
        "lo": lo,
        "hi": hi,
        "act1": reached[1] / n if n else 0.0,
        "act2": reached[2] / n if n else 0.0,
        "act3": reached[3] / n if n else 0.0,
        "medianWall": statistics.median(walls) if walls else 0.0,
        "commits": sorted({c.get("commit") for c in valid if c.get("commit")}),
    }


def report(tag=None, character=None, players=None, since=None):
    groups = gather(tag, character, players, since)
    if not groups:
        print("no run records match")
        return {}
    print(
        f"{'tag':<14} {'character':<12} {'p':>1} {'n':>4} {'win':>4} {'rate':>6} {'95% CI':<15} {'act1':>5} {'act2':>5} {'act3':>5} {'median s':>9}  commits"
    )
    out = {}
    for key in sorted(groups):
        s = summarize(groups[key])
        out["/".join(str(k) for k in key)] = s
        ci = f"{s['lo'] * 100:.0f}-{s['hi'] * 100:.0f}%"
        print(
            f"{key[0]:<14} {key[1]:<12} {key[2]:>1} {s['n']:>4} {s['wins']:>4} {s['rate'] * 100:>5.1f}% {ci:<15} {s['act1'] * 100:>4.0f}% {s['act2'] * 100:>4.0f}% {s['act3'] * 100:>4.0f}% {s['medianWall']:>9.0f}  {','.join(s['commits'][-3:])}"
        )
    return out


def perf(tag=None, character=None, players=None, since=None):
    by_type = defaultdict(list)
    nodes = []
    for row in metrics.load_index():
        if row.get("kind") != "run":
            continue
        if tag is not None and row.get("tag") != tag:
            continue
        if character is not None and row.get("character") != character:
            continue
        if players is not None and row.get("players", 1) != players:
            continue
        if since is not None and row.get("ts", "") < since:
            continue
        run = metrics.load_run(row["id"])
        if run is None:
            continue
        for case in (run.get("detail") or {}).get("cases", []):
            for fl in case.get("floorsDetail") or []:
                by_type[fl.get("type") or fl.get("room")].append(fl.get("wallSeconds", 0))
                if "combat" in fl and fl["combat"].get("turns"):
                    nodes.append(fl["combat"]["nodes"] / fl["combat"]["turns"])
    if not by_type:
        print("no floors")
        return
    print(f"{'room':<10} {'n':>5} {'median s':>9} {'p90 s':>7} {'max s':>7} {'total s':>8}")
    for kind, walls in sorted(by_type.items(), key=lambda kv: -sum(kv[1])):
        walls.sort()
        print(
            f"{kind:<10} {len(walls):>5} {statistics.median(walls):>9.1f} {walls[int(len(walls) * 0.9) - 1 if len(walls) > 1 else 0]:>7.1f} {walls[-1]:>7.1f} {sum(walls):>8.0f}"
        )
    if nodes:
        nodes.sort()
        print(f"nodes/turn median {statistics.median(nodes):.0f} p90 {nodes[int(len(nodes) * 0.9) - 1]:.0f}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tag")
    ap.add_argument("--character")
    ap.add_argument("--players", type=int)
    ap.add_argument("--since")
    ap.add_argument("--perf", action="store_true")
    a = ap.parse_args()
    character = a.character.upper() if a.character else None
    report(a.tag, character, a.players, a.since)
    if a.perf:
        perf(a.tag, character, a.players, a.since)


if __name__ == "__main__":
    main()
