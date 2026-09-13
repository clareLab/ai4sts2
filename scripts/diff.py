import argparse
import collections
import random
import sys
import time

from harness import Harness, HarnessError

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
        entry = {
            "turn": p["turn"],
            "phase": p["phase"],
            "stars": p["stars"],
            "creature": norm_creature(p["creature"]),
            "orbs": p["orbs"],
        }
        if s["inProgress"]:
            entry.update(
                {
                    "energy": p["energy"],
                    "hand": norm_cards(p["hand"]),
                    "playable": p["playable"],
                    "draw": norm_cards(p["draw"]),
                    "discard": norm_cards(p["discard"]),
                    "exhaust": norm_cards(p["exhaust"]),
                    "play": norm_cards(p["play"]),
                }
            )
        out["players"].append(entry)
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
    if not state["inProgress"] or state["players"][0]["phase"] != "Play":
        return None
    alive = [i for i, e in enumerate(state["enemies"]) if e["alive"]]
    options = []
    for pi, p in enumerate(state["players"]):
        if p["phase"] != "Play" or not p["creature"]["alive"]:
            continue
        options += [(pi, h) for h in range(len(p["hand"])) if p["playable"][h]]
    if not options or (rng is not None and rng.random() < 0.15):
        return (0, "end", None)
    pi, h = options[0] if rng is None else rng.choice(options)
    target = None
    if alive:
        target = alive[0] if rng is None else rng.choice(alive)
    return (pi, "play", (h, target))


def detour(wb, state, rng, length):
    actions = []
    for _ in range(length):
        action = pick_action(state, rng)
        if action is None:
            break
        pi, kind, arg = action
        if kind == "play":
            h, t = arg
            res = wb.call("wb.play", {"player": pi, "hand": h, "target": t})
            actions.append(
                {"kind": "play", "player": pi, "hand": h, "target": t, "card": state["players"][pi]["hand"][h]["id"]}
            )
        else:
            res = wb.call("wb.endturn", {"player": pi})
            actions.append({"kind": "end", "player": pi})
        state = res["state"]
        if not state["inProgress"]:
            break
    return actions, state


def restore_probe(wb, wb_state, rng, length, label):
    snap = wb.call("wb.snap")
    actions, detoured = detour(wb, wb_state, rng, length)
    res = wb.call("wb.restore", {"id": snap["id"]})
    d = diff(norm_state(wb_state), norm_state(res["state"]))
    return {
        "at": label,
        "snapMicros": snap["micros"],
        "objects": snap["objects"],
        "arrays": snap["arrays"],
        "fields": snap["fields"],
        "detour": actions,
        "detourEnded": not detoured["inProgress"],
        "restoreMicros": res["stats"]["restoreMicros"],
        "resyncMicros": res["stats"]["resyncMicros"],
        "diffs": [{"path": p, "before": x, "after": y} for p, x, y in d[:40]],
    }


def run_case(
    dev, wb, character, seed, encounter, cards, max_steps, verbose, rng, played, case_played, restore=None, players=1
):
    t0 = time.time()
    detour_rng = random.Random(1)
    dev.call("run.new", {"character": character, "players": players, "seed": seed, "ascension": 0})
    wb.call("wb.run", {"character": character, "players": players, "seed": seed, "ascension": 0})
    if cards:
        dev.call("deck.set", {"cards": cards})
        wb.call("deck.set", {"cards": cards})
    run = dev.call("run.state")
    dev_state = dev.call("combat.enter", {"encounter": encounter})["state"]
    wb_start = wb.call(
        "wb.start",
        {
            "character": character,
            "players": players,
            "seed": seed,
            "encounter": encounter,
            "rng": run["rng"],
            "heal": True,
        },
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
        "restores": [],
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
        pi, kind, arg = action
        if kind == "play":
            h, t = arg
            card_info = dev_state["players"][pi]["hand"][h]
            card = card_info["id"]
            played[card] += 1
            case_played[card] += 1
            dev_res = dev.call("combat.play", {"player": pi, "hand": h, "target": t})
            wb_res = wb.call("wb.play", {"player": pi, "hand": h, "target": t})
            label = f"play {step} P{pi + 1} {card} hand={h} target={t}"
            act = {
                "kind": "play",
                "player": pi,
                "hand": h,
                "target": t,
                "card": card,
                "upgrade": card_info.get("upgrade", 0),
            }
            micros = {"dev": dev_res.get("playMicros"), "wb": wb_res.get("playMicros")}
        else:
            dev_res = dev.call("combat.endturn", {"player": pi})
            wb_res = wb.call("wb.endturn", {"player": pi})
            label = f"endturn {step}"
            act = {"kind": "end", "player": pi}
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
        if restore and (step + 1) % restore[0] == 0 and wb_state["players"][0]["phase"] == "Play":
            probe = restore_probe(wb, wb_state, rng or detour_rng, restore[1], f"step {step + 1}")
            trace["restores"].append(probe)
            if probe["diffs"]:
                mismatches.append(
                    (f"restore@{step + 1}", [(x["path"], x["before"], x["after"]) for x in probe["diffs"]])
                )
                if verbose:
                    for x in probe["diffs"][:12]:
                        print(f"  restore@{step + 1}: {x['path']}: before={x['before']} after={x['after']}")
    trace["wallSeconds"] = round(time.time() - t0, 3)
    return steps, mismatches, trace


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--character", default="IRONCLAD")
    ap.add_argument("--players", type=int, default=1)
    ap.add_argument("--seed", default="AI4STS2")
    ap.add_argument("--encounter", action="append", default=[])
    ap.add_argument("--cards", default="")
    ap.add_argument("--steps", type=int, default=60)
    ap.add_argument("-v", action="store_true")
    ap.add_argument("--random", type=int, default=None)
    ap.add_argument("--restore-every", type=int, default=0)
    ap.add_argument("--detour", type=int, default=6)
    a = ap.parse_args()
    restore = (a.restore_every, a.detour) if a.restore_every > 0 else None
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
            try:
                steps, mismatches, trace = run_case(
                    dev,
                    wb,
                    a.character,
                    seed,
                    enc,
                    cards,
                    a.steps,
                    a.v,
                    rng,
                    played,
                    case_played,
                    restore,
                    a.players,
                )
            except HarnessError as e:
                print(f"{enc} seed={seed}: error {str(e)[:200]}")
                cases.append(
                    {
                        "encounter": enc,
                        "seed": seed,
                        "steps": 0,
                        "mismatches": 0,
                        "played": {},
                        "outcome": "error",
                        "error": str(e)[:500],
                    }
                )
                traces.append({**cases[-1], "initial": None, "initialDiffs": [], "steps": [], "restores": []})
                failed += 1
                continue
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
                "restores": len(trace["restores"]),
                "restoreMicros": round(
                    sum(r["restoreMicros"] + r["resyncMicros"] for r in trace["restores"]) / len(trace["restores"]), 1
                )
                if trace["restores"]
                else None,
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
            "players": a.players,
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
            "restore": {"every": restore[0], "detour": restore[1]} if restore else None,
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
