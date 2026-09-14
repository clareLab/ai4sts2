import argparse
import glob
import json
import os
import time

from harness import Harness, HarnessError
from pair import parse_config, split_tuning

import metrics


def collect(paths, kinds, max_cases, min_floor=0):
    cases = []
    for path in paths:
        with open(path, encoding="utf-8") as f:
            run = json.load(f)
        if run.get("kind") != "run":
            continue
        detail = run.get("detail") or run
        for case in detail.get("cases", []):
            floors = case.get("floorsDetail") or []
            for fl in floors:
                if fl.get("type") not in kinds or "combat" not in fl or fl.get("floor", 0) < min_floor:
                    continue
                party = fl.get("party")
                if party is None:
                    if fl is not floors[-1] or case.get("outcome") != "died":
                        continue
                    party = [
                        {
                            "character": run.get("character", "IRONCLAD"),
                            "hp": fl["hpBefore"],
                            "maxHp": case.get("maxHp") or fl["hpBefore"],
                            "deck": [
                                {"id": c.split("+")[0], "upgrade": int(c.split("+")[1]) if "+" in c else 0}
                                for c in case["deck"]
                            ],
                            "relics": case.get("relics", []),
                            "potions": [None, None, None],
                        }
                    ]
                me = party[0]
                if me["hp"] <= 1:
                    continue
                cases.append(
                    {
                        "run": run["id"],
                        "seed": case["seed"],
                        "floor": fl["floor"],
                        "type": fl["type"],
                        "encounter": fl["model"],
                        "net": run.get("net"),
                        "party": [
                            {
                                "character": p["character"],
                                "hp": p["hp"],
                                "maxHp": p["maxHp"],
                                "deck": [
                                    f"{c['id']}+{c['upgrade']}" if c.get("upgrade") else c["id"] for c in p["deck"]
                                ],
                                "relics": [r if isinstance(r, str) else r["id"] for r in p["relics"]],
                                "potions": [q if q is None or isinstance(q, str) else q["id"] for q in p["potions"]],
                            }
                            for p in party
                        ],
                        "character": me["character"],
                        "hp": sum(p["hp"] for p in party),
                        "maxHp": sum(p["maxHp"] for p in party),
                        "deck": [f"{c['id']}+{c['upgrade']}" if c.get("upgrade") else c["id"] for c in me["deck"]],
                        "original": {
                            "won": fl["combat"]["won"],
                            "hpAfter": fl.get("hpAfter"),
                            "turns": fl["combat"]["turns"],
                        },
                    }
                )
    if max_cases and len(cases) > max_cases:
        step = len(cases) / max_cases
        cases = [cases[int(i * step)] for i in range(max_cases)]
    return cases


def play(wb, case, config, max_turns):
    characters = [p["character"] for p in case["party"]]
    setup = {"characters": characters, "seed": case["seed"], "ascension": 0, "net": case.get("net") or "single"}
    wb.call("wb.run", setup)
    for slot, p in enumerate(case["party"]):
        wb.call("deck.set", {"cards": p["deck"], "player": slot})
        wb.call("relics.set", {"relics": p["relics"], "player": slot})
        wb.call("potions.set", {"potions": [q for q in p["potions"] if q], "player": slot})
        wb.call("wb.sethp", {"hp": p["hp"], "maxHp": p["maxHp"], "player": slot})
    wb.call("wb.start", {**setup, "encounter": case["encounter"], "heal": False})
    search, tune = split_tuning(config)
    wb.call("wb.tune", tune)
    hard = case["type"] in ("Boss", "Elite")
    res = wb.call("wb.autoplay", {"maxTurns": max_turns, "hard": hard, **search})
    enemies = [e for e in res["state"]["enemies"] if e["maxHp"] < 1_000_000]
    remaining = sum(max(0, e["hp"]) for e in enemies) / max(1, sum(e["maxHp"] for e in enemies))
    return {
        "won": res["won"],
        "hp": sum(p["creature"]["hp"] for p in res["state"]["players"]),
        "remaining": round(remaining, 3),
        "turns": res["turns"],
        "nodes": res["nodes"],
        "millis": round(res["micros"] / 1000, 1),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", default="")
    ap.add_argument("--kinds", default="Boss")
    ap.add_argument("--max-cases", type=int, default=0)
    ap.add_argument("--min-floor", type=int, default=0)
    ap.add_argument("--config", action="append", default=[])
    ap.add_argument("--max-turns", type=int, default=40)
    ap.add_argument("--instance", default="wb")
    a = ap.parse_args()
    paths = [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(os.path.dirname(__file__), "..", "metrics", "runs", "*-run-*.json"))
    )
    cases = collect(paths, set(a.kinds.split(",")), a.max_cases, a.min_floor)
    configs = {
        c: parse_config(c) for c in (a.config or ["turns=2,beam=5,maxNodes=800", "turns=2,beam=8,maxNodes=2500"])
    }
    wb = Harness(a.instance, timeout=3600)
    ping = wb.call("ping")
    t0 = time.time()
    rows = []
    for case in cases:
        row = {k: case[k] for k in ("run", "seed", "floor", "type", "encounter", "hp", "maxHp", "original")}
        row["players"] = len(case["party"])
        row["deckSize"] = len(case["deck"])
        row["results"] = {}
        for name, config in configs.items():
            try:
                row["results"][name] = play(wb, case, config, a.max_turns)
            except HarnessError as e:
                row["results"][name] = {
                    "error": str(e)[:300],
                    "won": False,
                    "hp": 0,
                    "turns": 0,
                    "nodes": 0,
                    "millis": 0,
                }
        rows.append(row)
        cells = " | ".join(
            f"{'W' if r['won'] else 'L'} hp={r['hp']:>3} t={r['turns']:>2} {r['millis']:>7.0f}ms"
            for r in row["results"].values()
        )
        print(
            f"{case['seed']:<8} f{case['floor']:<3} {case['encounter']:<24} hp {case['hp']:>3}/{case['maxHp']:<3} deck {len(case['deck']):>2} orig {'W' if case['original']['won'] else 'L'} | {cells}"
        )
    summary = {
        "kinds": a.kinds,
        "configs": configs,
        "patches": ping.get("patches"),
        "cases": len(rows),
        "wins": {name: sum(r["results"][name]["won"] for r in rows) for name in configs},
        "hp": {name: sum(r["results"][name]["hp"] for r in rows) for name in configs},
        "millis": {name: round(sum(r["results"][name]["millis"] for r in rows)) for name in configs},
        "originalWins": sum(r["original"]["won"] for r in rows),
        "wallSeconds": round(time.time() - t0, 1),
        "replay": False,
        "rows": rows,
    }
    metrics.record(
        "bosslab", summary, {"rows": rows, "cases": cases}, {"game": ping.get("game"), "mod": ping.get("mod")}
    )
    print(
        "original wins",
        summary["originalWins"],
        "/",
        len(rows),
        " ".join(f"| {n}: {summary['wins'][n]} wins hp {summary['hp'][n]} {summary['millis'][n]} ms" for n in configs),
    )


if __name__ == "__main__":
    main()
