import argparse
import collections
import random
import sys
import time

from harness import Harness

import metrics

KEYS_CARD = ("id", "upgrade", "cost", "costsX")


def norm_cards(cards):
    return [tuple(c[k] for k in KEYS_CARD) for c in cards]


def norm_creature(c):
    return {
        "hp": c["hp"],
        "maxHp": c["maxHp"],
        "block": c["block"],
        "alive": c["alive"],
        "powers": [(p["id"], p["amount"]) for p in c["powers"]],
        "nextMove": c.get("nextMove"),
    }


def norm_state(s):
    out = {
        "inProgress": s["inProgress"],
        "round": s["round"],
        "side": s["side"],
        "rng": {k: v["counter"] for k, v in s["rng"].items()},
        "enemies": [norm_creature(e) for e in s["enemies"]],
        "players": [],
    }
    for p in s["players"]:
        out["players"].append(
            {
                "turn": p["turn"],
                "phase": p["phase"],
                "energy": p["energy"],
                "stars": p["stars"],
                "creature": norm_creature(p["creature"]),
                "hand": norm_cards(p["hand"]),
                "playable": p["playable"],
                "draw": norm_cards(p["draw"]),
                "discard": norm_cards(p["discard"]),
                "exhaust": norm_cards(p["exhaust"]),
                "play": norm_cards(p["play"]),
                "orbs": p["orbs"],
            }
        )
    return out


def diff(a, b, path=""):
    out = []
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            out += diff(a.get(k), b.get(k), f"{path}.{k}")
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append((path, a, b))
        else:
            for i, (x, y) in enumerate(zip(a, b, strict=True)):
                out += diff(x, y, f"{path}[{i}]")
    elif a != b:
        out.append((path, a, b))
    return out


def pick_action(state, rng):
    p = state["players"][0]
    if not state["inProgress"] or p["phase"] != "Play":
        return None
    alive = [i for i, e in enumerate(state["enemies"]) if e["alive"]]
    playable = [h for h in range(len(p["hand"])) if p["playable"][h]]
    if not playable or (rng is not None and rng.random() < 0.15):
        return ("end", None)
    h = playable[0] if rng is None else rng.choice(playable)
    target = None
    if alive:
        target = alive[0] if rng is None else rng.choice(alive)
    return ("play", (h, target))


def run_case(dev, wb, character, seed, encounter, cards, max_steps, verbose, rng, played, case_played):
    t0 = time.time()
    dev.call("run.new", {"character": character, "seed": seed, "ascension": 0})
    wb.call("wb.run", {"character": character, "seed": seed, "ascension": 0})
    if cards:
        dev.call("deck.set", {"cards": cards})
        wb.call("deck.set", {"cards": cards})
    run = dev.call("run.state")
    dev_state = dev.call("combat.enter", {"encounter": encounter})["state"]
    wb_start = wb.call(
        "wb.start", {"character": character, "seed": seed, "encounter": encounter, "rng": run["rng"], "heal": True}
    )
    wb_state = wb_start["state"]
    mismatches = []
    steps = []
    trace = {
        "initial": dev_state,
        "initialDiffs": [],
        "steps": [],
        "rngStart": run["rng"],
        "startMicros": wb_start.get("startMicros"),
    }
    d = diff(norm_state(dev_state), norm_state(wb_state))
    if d:
        mismatches.append(("start", d))
        trace["initialDiffs"] = [{"path": p, "dev": x, "wb": y} for p, x, y in d[:40]]
        trace["initialWb"] = wb_state
    for step in range(max_steps):
        if mismatches:
            break
        action = pick_action(dev_state, rng)
        if action is None:
            break
        kind, arg = action
        if kind == "play":
            h, t = arg
            card_info = dev_state["players"][0]["hand"][h]
            card = card_info["id"]
            played[card] += 1
            case_played[card] += 1
            dev_res = dev.call("combat.play", {"hand": h, "target": t})
            wb_res = wb.call("wb.play", {"hand": h, "target": t})
            label = f"play {step} {card} hand={h} target={t}"
            act = {"kind": "play", "hand": h, "target": t, "card": card, "upgrade": card_info.get("upgrade", 0)}
            micros = {"dev": dev_res.get("playMicros"), "wb": wb_res.get("playMicros")}
        else:
            dev_res = dev.call("combat.endturn")
            wb_res = wb.call("wb.endturn")
            label = f"endturn {step}"
            act = {"kind": "end"}
            micros = {"dev": dev_res.get("endTurnMicros"), "wb": wb_res.get("endTurnMicros")}
        dev_state = dev_res["state"]
        wb_state = wb_res["state"]
        steps.append(label)
        d = diff(norm_state(dev_state), norm_state(wb_state))
        entry = {"action": act, "state": dev_state, "micros": micros, "diffs": []}
        if d:
            mismatches.append((label, d))
            entry["diffs"] = [{"path": p, "dev": x, "wb": y} for p, x, y in d[:40]]
            entry["wbState"] = wb_state
            if verbose:
                for path, a, b in d[:12]:
                    print(f"  {label}: {path}: dev={a} wb={b}")
        trace["steps"].append(entry)
        if not dev_state["inProgress"]:
            break
    trace["wallSeconds"] = round(time.time() - t0, 3)
    return steps, mismatches, trace


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--character", default="IRONCLAD")
    ap.add_argument("--seed", default="AI4STS2")
    ap.add_argument("--encounter", action="append", default=[])
    ap.add_argument("--cards", default="")
    ap.add_argument("--steps", type=int, default=60)
    ap.add_argument("-v", action="store_true")
    ap.add_argument("--random", type=int, default=None)
    a = ap.parse_args()
    a.character = a.character.upper()
    a.encounter = [e.upper() for e in a.encounter]
    a.seed = ",".join(s.strip() for s in a.seed.split(",") if s.strip())
    played = collections.Counter()
    rng = random.Random(a.random) if a.random is not None else None
    cases = []
    traces = []
    t0 = time.time()
    dev = Harness("dev")
    wb = Harness("wb")
    dev.call("mode.set", {"fastMode": "Instant"})
    ping = wb.call("ping")
    dev_ping = dev.call("ping")
    cards = [c.strip().upper() for c in a.cards.split(",") if c.strip()]
    total = 0
    failed = 0
    for enc in a.encounter or ["NIBBITS_WEAK"]:
        for seed in a.seed.split(","):
            case_played = collections.Counter()
            steps, mismatches, trace = run_case(
                dev, wb, a.character, seed, enc, cards, a.steps, a.v, rng, played, case_played
            )
            total += len(steps)
            failed += len(mismatches)
            final = trace["steps"][-1]["state"] if trace["steps"] else trace["initial"]
            me = final["players"][0] if final["players"] else None
            summary = {
                "encounter": enc,
                "seed": seed,
                "steps": len(steps),
                "mismatches": len(mismatches),
                "played": dict(case_played),
                "wallSeconds": trace["wallSeconds"],
                "outcome": "mismatch"
                if mismatches
                else ("running" if final["inProgress"] else ("lost" if me and not me["creature"]["alive"] else "won")),
                "turns": me["turn"] if me else None,
                "hpLeft": me["creature"]["hp"] if me else None,
            }
            cases.append(summary)
            traces.append({**summary, **trace})
            print(f"{enc} seed={seed}: steps={len(steps)} mismatches={len(mismatches)}")
    print(f"total steps={total} mismatches={failed} distinct_cards={len(played)}")
    print(dict(played))
    metrics.record(
        "diff",
        {
            "character": a.character,
            "encounters": a.encounter or ["NIBBITS_WEAK"],
            "seeds": a.seed.split(","),
            "deck": cards,
            "policy": "random" if rng is not None else "first",
            "randomSeed": a.random,
            "maxSteps": a.steps,
            "wallSeconds": round(time.time() - t0, 3),
            "steps": total,
            "mismatches": failed,
            "played": dict(played),
            "replay": True,
            "patches": ping.get("patches"),
            "oracle": {"game": dev_ping.get("game"), "mod": dev_ping.get("mod"), "fastMode": dev_ping.get("fastMode")},
            "cases": cases,
        },
        {"cases": traces},
        {"game": ping.get("game"), "mod": ping.get("mod")},
    )
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
