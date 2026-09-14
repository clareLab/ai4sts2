import argparse
import glob
import json
import os
import shutil

import bosslab
import tune
import tuning
from harness import Harness

import metrics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--candidate", default=os.path.join(ROOT, "metrics", "value.candidate.json"))
    ap.add_argument("--instances", default="dev")
    ap.add_argument("--runs", default="")
    ap.add_argument("--boss", type=int, default=12)
    ap.add_argument("--elite", type=int, default=12)
    ap.add_argument("--monster", type=int, default=36)
    ap.add_argument("--salts", type=int, default=1)
    ap.add_argument("--max-turns", type=int, default=40)
    ap.add_argument("--loss-weight", type=float, default=50.0)
    ap.add_argument("--alpha", type=float, default=0.1)
    ap.add_argument("--block", type=int, default=8)
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args()
    with open(a.candidate, encoding="utf-8") as f:
        candidate = json.load(f)
    trained = set(candidate.get("trainedRuns", []))
    instances = [x for x in a.instances.split(",") if x]
    wb = Harness(instances[0], timeout=3600)
    ping = wb.call("ping")
    digest = wb.call("wb.value", {"path": os.path.abspath(a.candidate)})["hash"]
    paths = [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(ROOT, "metrics", "runs", "*-run-*.json"))
    )
    pool = [c for c in bosslab.collect(paths, set(tune.TYPES), 0, 0, trained) if len(c["party"]) == 1]
    cases = tune.sample_cases(pool, {"Boss": a.boss, "Elite": a.elite, "Monster": a.monster})
    pairs = [(tune.case_id(c), s) for c in cases for s in range(a.salts)]
    strata = {}
    for c in cases:
        for s in range(a.salts):
            strata.setdefault(tune.stratum(c), []).append((tune.case_id(c), s))
    fingerprint = f"promote-{ping.get('built')}"
    runner = tune.Runner(a, cases, tune.Cells(fingerprint), instances)
    for name in instances:
        client = Harness(name, timeout=3600)
        client.call("wb.value", {"path": os.path.abspath(a.candidate)})
    base_cfg = {**tuning.load(), "Value": "hand"}
    cand_cfg = {**tuning.load(), "Value": digest}
    print(f"{len(cases)} cases disjoint from {len(trained)} training runs; candidate {digest}")
    base, cand = runner.evaluate([base_cfg, cand_cfg], pairs)
    verdict = tune.screen(cand, base, a, 0.5, strata)
    d = tune.diffs_of(cand, base, a.loss_weight)
    mean = sum(d.values()) / max(1, len(d))
    wins = tune.wins_delta(cand, base, list(d))
    ratio = tune.nodes_ratio(cand, base)
    inferior = any(
        tune.p_signflip([-d[u] for u in keys if u in d]) < a.alpha
        or tune.wins_delta(cand, base, [u for u in keys if u in d]) < 0
        for keys in strata.values()
    )
    ok = not inferior and mean > -0.5
    print(
        f"mean {mean:+.2f} HP/case, wins {wins:+d}, nodes x{ratio:.2f}, p_better {tune.p_signflip(list(d.values())):.3f}, {'PASS' if ok else 'FAIL'}{' (quality)' if verdict else ''}"
    )
    summary = {
        "candidate": digest,
        "cases": len(cases),
        "mean": round(mean, 3),
        "wins": wins,
        "nodesRatio": round(ratio, 3),
        "pBetter": round(tune.p_signflip(list(d.values())), 4),
        "pass": ok,
        "promoted": bool(ok and a.write),
        "replay": False,
    }
    metrics.record("promote", summary, {"cases": cases}, {"game": ping.get("game"), "mod": ping.get("mod")})
    if ok and a.write:
        shutil.copyfile(a.candidate, os.path.join(ROOT, "metrics", "value.json"))
        print("promoted to metrics/value.json")


if __name__ == "__main__":
    main()
