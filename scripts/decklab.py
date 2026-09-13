import argparse
import glob
import itertools
import json
import os
import time

from harness import Harness, HarnessError
from pair import parse_config, split_tuning

import metrics


def collect(paths, max_cases, characters):
    cases = []
    for path in paths:
        with open(path, encoding="utf-8") as f:
            run = json.load(f)
        if run.get("kind") != "run" or (characters and run.get("character") not in characters):
            continue
        for case in (run.get("detail") or run).get("cases", []):
            for fl in case.get("floorsDetail") or []:
                ev = fl.get("evaluation")
                party = fl.get("party")
                if not ev or not party or "combat" not in fl or not fl["combat"].get("won"):
                    continue
                offered = [o["label"] for o in ev["options"] if o.get("card") is not None]
                if len(offered) < 2:
                    continue
                me = party[0]
                cases.append(
                    {
                        "run": run["id"],
                        "seed": case["seed"],
                        "floor": fl["floor"],
                        "character": me["character"],
                        "net": run.get("net"),
                        "hp": fl.get("hpAfter") or me["hp"],
                        "maxHp": me["maxHp"],
                        "deck": [f"{c['id']}+{c['upgrade']}" if c.get("upgrade") else c["id"] for c in me["deck"]],
                        "relics": [r if isinstance(r, str) else r["id"] for r in me["relics"]],
                        "potions": [q for q in me["potions"] if q and isinstance(q, str)],
                        "offered": offered,
                        "originalBest": ev.get("best"),
                    }
                )
    if max_cases and len(cases) > max_cases:
        step = len(cases) / max_cases
        cases = [cases[int(i * step)] for i in range(max_cases)]
    return cases


def setup(wb, case):
    spec = {"characters": [case["character"]], "seed": case["seed"], "ascension": 0, "net": case.get("net") or "single"}
    wb.call("wb.run", {**spec, "map": True})
    wb.call("deck.set", {"cards": case["deck"], "player": 0})
    wb.call("relics.set", {"relics": case["relics"], "player": 0})
    wb.call("potions.set", {"potions": case["potions"], "player": 0})
    wb.call("wb.sethp", {"hp": case["hp"], "maxHp": case["maxHp"], "player": 0})


def evaluate(wb, case, config, salt, anchors, max_turns):
    search, tune = split_tuning(config)
    wb.call("wb.tune", tune)
    plan = {"maxTurns": max_turns, "maxDepth": 8, "leaf": "estimate", "beam": 3, "maxNodes": 400, **search}
    res = wb.call("wb.evaladd", {"cards": case["offered"] + anchors, "salt": salt, **plan})["evaluation"]
    return {
        "best": res["best"],
        "scores": {o["label"]: o["rollout"]["score"] for o in res["options"]},
        "micros": res["micros"],
    }


def agreement(bests):
    return sum(1 for a, b in itertools.combinations(bests, 2) if a == b) / max(1, len(bests) * (len(bests) - 1) / 2)


def mode(values):
    values = [v for v in values if v is not None]
    return max(set(values), key=values.count) if values else None


def mean_scores(trials):
    out = {}
    for t in trials:
        for k, v in t["scores"].items():
            out.setdefault(k, []).append(v)
    return {k: sum(v) / len(v) for k, v in out.items()}


def spearman(a, b):
    keys = [k for k in a if k in b]
    if len(keys) < 3:
        return None
    ra = {k: i for i, k in enumerate(sorted(keys, key=lambda k: a[k]))}
    rb = {k: i for i, k in enumerate(sorted(keys, key=lambda k: b[k]))}
    n = len(keys)
    return 1 - 6 * sum((ra[k] - rb[k]) ** 2 for k in keys) / (n * (n * n - 1))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", default="")
    ap.add_argument("--characters", default="")
    ap.add_argument("--max-cases", type=int, default=20)
    ap.add_argument("--config", action="append", default=[])
    ap.add_argument("--salts", type=int, default=3)
    ap.add_argument("--anchors", default="")
    ap.add_argument("--max-turns", type=int, default=30)
    ap.add_argument("--instance", default="wb")
    a = ap.parse_args()
    paths = [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(os.path.dirname(__file__), "..", "metrics", "runs", "*-run-*.json"))
    )
    cases = collect(paths, a.max_cases, set(filter(None, a.characters.split(","))))
    configs = {c: parse_config(c) for c in (a.config or ["fights=2,boss=true", "fights=2,boss=true,elite=true"])}
    anchors = [x for x in a.anchors.split(",") if x]
    wb = Harness(a.instance, timeout=3600)
    ping = wb.call("ping")
    t0 = time.time()
    rows = []
    for case in cases:
        row = {k: case[k] for k in ("run", "seed", "floor", "character", "hp", "maxHp", "offered", "originalBest")}
        row["deckSize"] = len(case["deck"])
        row["results"] = {}
        for name, config in configs.items():
            trials = []
            for salt in range(a.salts):
                try:
                    setup(wb, case)
                    trials.append(evaluate(wb, case, config, salt, anchors, a.max_turns))
                except HarnessError as e:
                    trials.append({"error": str(e)[:300], "best": None, "scores": {}, "micros": 0})
            bests = [t["best"] for t in trials]
            reference = row["results"][next(iter(configs))] if row["results"] else None
            ref_best = mode(reference["bests"]) if reference else mode(bests)
            ref_scores = reference["meanScores"] if reference else mean_scores(trials)
            anchor_last = [
                all(
                    t["scores"].get(x, 0) <= min(v for k, v in t["scores"].items() if k not in anchors) for x in anchors
                )
                for t in trials
                if t["scores"]
            ]
            row["results"][name] = {
                "trials": trials,
                "bests": bests,
                "meanScores": mean_scores(trials),
                "agreement": agreement(bests),
                "agreeRef": sum(1 for b in bests if b == ref_best) / max(1, len(bests)),
                "rankCorr": spearman(mean_scores(trials), ref_scores),
                "anchorsLast": sum(anchor_last) / max(1, len(anchor_last)) if anchors else None,
                "millis": round(sum(t["micros"] for t in trials) / 1000),
            }
        rows.append(row)
        cells = " | ".join(
            f"agree {r['agreement']:.2f} ref {r['agreeRef']:.2f} {'/'.join(str(t['best'])[:10] for t in r['trials'])} {r['millis']:>6}ms"
            for r in row["results"].values()
        )
        print(f"{case['seed']:<6} f{case['floor']:<3} {case['character'][:6]} deck {len(case['deck']):>2} | {cells}")
    summary = {
        "configs": configs,
        "anchors": anchors,
        "salts": a.salts,
        "patches": ping.get("patches"),
        "cases": len(rows),
        "agreement": {
            n: round(sum(r["results"][n]["agreement"] for r in rows) / max(1, len(rows)), 3) for n in configs
        },
        "agreeRef": {n: round(sum(r["results"][n]["agreeRef"] for r in rows) / max(1, len(rows)), 3) for n in configs},
        "rankCorr": {
            n: round(
                sum(r["results"][n]["rankCorr"] or 0 for r in rows)
                / max(1, sum(1 for r in rows if r["results"][n]["rankCorr"] is not None)),
                3,
            )
            for n in configs
        },
        "anchorsLast": {
            n: round(sum(r["results"][n]["anchorsLast"] or 0 for r in rows) / max(1, len(rows)), 3) for n in configs
        }
        if anchors
        else None,
        "millis": {n: sum(r["results"][n]["millis"] for r in rows) for n in configs},
        "wallSeconds": round(time.time() - t0, 1),
        "replay": False,
    }
    metrics.record(
        "decklab", summary, {"rows": rows, "cases": cases}, {"game": ping.get("game"), "mod": ping.get("mod")}
    )
    print(
        "agreement",
        " ".join(
            f"| {n}: self {summary['agreement'][n]} ref {summary['agreeRef'][n]} rho {summary['rankCorr'][n]}"
            + (f" anchors-last {summary['anchorsLast'][n]}" if anchors else "")
            + f" {summary['millis'][n]} ms"
            for n in configs
        ),
    )


if __name__ == "__main__":
    main()
