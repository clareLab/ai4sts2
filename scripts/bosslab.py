import argparse
import glob
import json
import os
import time

from harness import Harness, HarnessError
from pair import parse_config

import metrics


def collect(paths, kinds, max_cases):
    cases = []
    for path in paths:
        with open(path, encoding="utf-8") as f:
            run = json.load(f)
        if run.get("kind") != "run" or run.get("players", 1) != 1:
            continue
        detail = run.get("detail") or run
        for case in detail.get("cases", []):
            floors = case.get("floorsDetail") or []
            for fl in floors:
                if fl.get("type") not in kinds or "combat" not in fl:
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
                        "character": me["character"],
                        "hp": me["hp"],
                        "maxHp": me["maxHp"],
                        "deck": [f"{c['id']}+{c['upgrade']}" if c.get("upgrade") else c["id"] for c in me["deck"]],
                        "relics": [r if isinstance(r, str) else r["id"] for r in me["relics"]],
                        "potions": [p if p is None or isinstance(p, str) else p["id"] for p in me["potions"]],
                        "original": {
                            "won": fl["combat"]["won"],
                            "hpAfter": fl.get("hpAfter"),
                            "turns": fl["combat"]["turns"],
                        },
                    }
                )
    return cases[-max_cases:] if max_cases else cases


def play(wb, case, config, max_turns):
    wb.call("wb.run", {"character": case["character"], "seed": case["seed"], "ascension": 0})
    wb.call("deck.set", {"cards": case["deck"]})
    wb.call("relics.set", {"relics": case["relics"]})
    wb.call("potions.set", {"potions": [p for p in case["potions"] if p]})
    wb.call("wb.sethp", {"hp": case["hp"], "maxHp": case["maxHp"]})
    wb.call(
        "wb.start",
        {"character": case["character"], "seed": case["seed"], "encounter": case["encounter"], "heal": False},
    )
    res = wb.call("wb.autoplay", {"maxTurns": max_turns, **config})
    me = res["state"]["players"][0]
    return {
        "won": res["won"],
        "hp": me["creature"]["hp"],
        "turns": res["turns"],
        "nodes": res["nodes"],
        "millis": round(res["micros"] / 1000, 1),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", default="")
    ap.add_argument("--kinds", default="Boss")
    ap.add_argument("--max-cases", type=int, default=0)
    ap.add_argument("--config", action="append", default=[])
    ap.add_argument("--max-turns", type=int, default=40)
    ap.add_argument("--instance", default="wb")
    a = ap.parse_args()
    paths = [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(os.path.dirname(__file__), "..", "metrics", "runs", "*-run-*.json"))
    )
    cases = collect(paths, set(a.kinds.split(",")), a.max_cases)
    configs = {
        c: parse_config(c) for c in (a.config or ["turns=2,beam=5,maxNodes=800", "turns=2,beam=8,maxNodes=2500"])
    }
    wb = Harness(a.instance, timeout=3600)
    ping = wb.call("ping")
    t0 = time.time()
    rows = []
    for case in cases:
        row = {k: case[k] for k in ("run", "seed", "floor", "type", "encounter", "hp", "maxHp", "original")}
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
