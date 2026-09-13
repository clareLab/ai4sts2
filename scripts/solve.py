import argparse
import sys
import time

from diff import diff, norm_state
from harness import Harness

import metrics


def play_line(wb, line):
    steps = []
    state = None
    for action in line:
        if action["kind"] == "play":
            res = wb.call("wb.play", {"hand": action["hand"], "target": action.get("target")})
            micros = res.get("playMicros")
        else:
            res = wb.call("wb.endturn")
            micros = res.get("endTurnMicros")
        state = res["state"]
        steps.append({"action": action, "state": state, "micros": {"wb": micros}})
        if not state["inProgress"]:
            break
    return steps, state


def verify_on_oracle(dev, character, seed, encounter, cards, rng_start, steps):
    dev.call("run.new", {"character": character, "seed": seed, "ascension": 0})
    if cards:
        dev.call("deck.set", {"cards": cards})
    dev.call("run.rng.set", {"rng": rng_start})
    dev_state = dev.call("combat.enter", {"encounter": encounter})["state"]
    mismatches = 0
    for entry in steps:
        a = entry["action"]
        if a["kind"] == "play":
            res = dev.call("combat.play", {"hand": a["hand"], "target": a.get("target")})
            entry["micros"]["dev"] = res.get("playMicros")
        else:
            res = dev.call("combat.endturn")
            entry["micros"]["dev"] = res.get("endTurnMicros")
        dev_state = res["state"]
        d = diff(norm_state(dev_state), norm_state(entry["state"]))
        entry["diffs"] = [{"path": p, "dev": x, "wb": y} for p, x, y in d[:40]]
        if d:
            mismatches += 1
            entry["wbState"] = entry["state"]
            entry["state"] = dev_state
            break
        if not dev_state["inProgress"]:
            break
    return mismatches


def solve_case(wb, dev, a, seed, encounter, cards):
    t0 = time.time()
    wb.call("wb.run", {"character": a.character, "seed": seed, "ascension": 0})
    if cards:
        wb.call("deck.set", {"cards": cards})
    rng_start = wb.call("run.state")["rng"]
    start = wb.call("wb.start", {"character": a.character, "seed": seed, "encounter": encounter, "heal": True})
    state = start["state"]
    trace = {
        "initial": state,
        "initialDiffs": [],
        "steps": [],
        "rngStart": rng_start,
        "startMicros": start.get("startMicros"),
        "restores": [],
        "searches": [],
    }
    turns = 0
    while state["inProgress"] and turns < a.max_turns:
        res = wb.call("wb.search", {"maxNodes": a.max_nodes, "maxDepth": a.max_depth})["result"]
        line = res["line"]
        trace["searches"].append(
            {
                "turn": state["players"][0]["turn"],
                "step": len(trace["steps"]),
                **{
                    k: res[k]
                    for k in (
                        "score",
                        "nodes",
                        "leaves",
                        "transpositions",
                        "snapshots",
                        "restores",
                        "micros",
                        "snapMicros",
                        "restoreMicros",
                        "actMicros",
                        "exhausted",
                    )
                },
                "line": line,
            }
        )
        if not line:
            line = [{"kind": "end", "hand": -1}]
        steps, state = play_line(wb, line)
        for s in steps:
            s["search"] = len(trace["searches"]) - 1
            s["diffs"] = []
        trace["steps"].extend(steps)
        turns += 1
    mismatches = 0
    if dev is not None:
        mismatches = verify_on_oracle(dev, a.character, seed, encounter, cards, rng_start, trace["steps"])
    me = state["players"][0] if state["players"] else None
    summary = {
        "encounter": encounter,
        "seed": seed,
        "steps": len(trace["steps"]),
        "turns": me["turn"] if me else None,
        "outcome": "mismatch"
        if mismatches
        else ("running" if state["inProgress"] else ("lost" if me and not me["creature"]["alive"] else "won")),
        "hpLeft": me["creature"]["hp"] if me else None,
        "mismatches": mismatches,
        "played": {},
        "searches": len(trace["searches"]),
        "nodes": sum(s["nodes"] for s in trace["searches"]),
        "searchMillis": round(sum(s["micros"] for s in trace["searches"]) / 1000, 1),
        "wallSeconds": round(time.time() - t0, 3),
    }
    for s in trace["steps"]:
        if s["action"]["kind"] == "play":
            summary["played"][s["action"]["card"]] = summary["played"].get(s["action"]["card"], 0) + 1
    trace["wallSeconds"] = summary["wallSeconds"]
    return summary, {**summary, **trace}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--character", default="IRONCLAD")
    ap.add_argument("--seed", default="AI4STS2")
    ap.add_argument("--encounter", action="append", default=[])
    ap.add_argument("--cards", default="")
    ap.add_argument("--max-nodes", type=int, default=2000)
    ap.add_argument("--max-depth", type=int, default=8)
    ap.add_argument("--max-turns", type=int, default=40)
    ap.add_argument("--no-verify", action="store_true")
    a = ap.parse_args()
    a.character = a.character.upper()
    encounters = [e.upper() for e in a.encounter] or ["NIBBITS_WEAK"]
    seeds = [s.strip() for s in a.seed.split(",") if s.strip()]
    cards = [c.strip().upper() for c in a.cards.split(",") if c.strip()]
    wb = Harness("wb", timeout=900)
    dev = None if a.no_verify else Harness("dev", timeout=600)
    if dev is not None:
        dev.call("mode.set", {"fastMode": "Instant"})
    ping = wb.call("ping")
    t0 = time.time()
    cases = []
    traces = []
    for enc in encounters:
        for seed in seeds:
            summary, trace = solve_case(wb, dev, a, seed, enc, cards)
            cases.append(summary)
            traces.append(trace)
            print(
                f"{enc} seed={seed}: {summary['outcome']} turns={summary['turns']} hp={summary['hpLeft']} nodes={summary['nodes']} search={summary['searchMillis']} ms mismatches={summary['mismatches']}"
            )
    won = sum(c["outcome"] == "won" for c in cases)
    metrics.record(
        "solve",
        {
            "character": a.character,
            "encounters": encounters,
            "seeds": seeds,
            "deck": cards,
            "maxNodes": a.max_nodes,
            "maxDepth": a.max_depth,
            "verified": dev is not None,
            "patches": ping.get("patches"),
            "wallSeconds": round(time.time() - t0, 3),
            "steps": sum(c["steps"] for c in cases),
            "mismatches": sum(c["mismatches"] for c in cases),
            "won": won,
            "lost": sum(c["outcome"] == "lost" for c in cases),
            "nodes": sum(c["nodes"] for c in cases),
            "searchMillis": round(sum(c["searchMillis"] for c in cases), 1),
            "replay": True,
            "cases": cases,
        },
        {"cases": traces},
        {"game": ping.get("game"), "mod": ping.get("mod")},
    )
    print(f"won {won}/{len(cases)} mismatches={sum(c['mismatches'] for c in cases)}")
    return 1 if any(c["mismatches"] for c in cases) else 0


if __name__ == "__main__":
    sys.exit(main())
