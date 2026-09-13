import argparse
import time

from harness import Harness, HarnessError

import metrics

PREFERENCE = ["Monster", "Unknown", "RestSite", "Elite", "Shop", "Treasure", "Boss"]


def choose_point(view, hp, max_hp, floor):
    choices = view["choices"]
    if not choices:
        return None
    ratio = hp / max(1, max_hp)
    order = list(PREFERENCE)
    if ratio < 0.5 and any(c["type"] == "RestSite" for c in choices):
        order.remove("RestSite")
        order.insert(0, "RestSite")
    if ratio < 0.7 or floor < 4:
        order.remove("Elite")
        order.append("Elite")
    for kind in order:
        for c in choices:
            if c["type"] == kind:
                return c
    return choices[0]


def take_rewards(wb, a, entry):
    view = wb.call("wb.rewards")["view"]
    taken = []
    for set_view in view["rewards"]:
        for reward in set_view["rewards"]:
            if reward["kind"] == "card":
                ev = wb.call(
                    "wb.evalreward",
                    {
                        "index": reward["index"],
                        "fights": a.fights,
                        "maxTurns": a.max_turns,
                        "maxNodes": a.max_nodes,
                        "maxDepth": a.max_depth,
                        "leaf": "estimate",
                        "beam": a.beam,
                    },
                )["evaluation"]
                entry["evaluation"] = ev
                best = next(o for o in ev["options"] if o["label"] == ev["best"])
                if best.get("card") is not None:
                    res = wb.call("wb.take", {"index": reward["index"], "card": best["card"]})
                    taken.append({"kind": "card", "card": best["label"], "ok": res["ok"]})
                else:
                    taken.append({"kind": "card", "card": None, "ok": True})
            else:
                res = wb.call("wb.take", {"index": reward["index"]})
                taken.append(
                    {
                        "kind": reward["kind"],
                        "value": reward.get("gold") or reward.get("potion") or reward.get("relic"),
                        "ok": res["ok"],
                    }
                )
        break
    view = wb.call("wb.view")["view"]
    if view["rewards"] and not view["rewards"][0]["completed"]:
        wb.call("wb.skip")
    entry["taken"] = taken


def play_run(wb, a, seed):
    t0 = time.time()
    run = wb.call("wb.run", {"character": a.character, "players": a.players, "seed": seed, "ascension": 0, "map": True})
    floors = []
    outcome = "running"
    for _ in range(a.max_floors):
        view = wb.call("wb.view")["view"]
        state = wb.call("run.state")
        me = state["players"][0]
        choice = choose_point(view, me["hp"], me["maxHp"], view["floor"])
        if choice is None:
            outcome = "act-cleared" if view["room"] == "CombatRoom" and view["combatFinished"] else "stuck"
            break
        t1 = time.time()
        res = wb.call("wb.travel", {"col": choice["col"], "row": choice["row"]})
        v = res["view"]
        entry = {
            "floor": v["floor"],
            "actFloor": v["actFloor"],
            "coord": v["coord"],
            "type": choice["type"],
            "room": v["room"],
            "model": v.get("roomModel"),
            "hpBefore": me["hp"],
            "choices": view["choices"],
        }
        if v["inCombat"]:
            auto = wb.call(
                "wb.autoplay",
                {
                    "maxTurns": a.max_turns,
                    "maxNodes": a.max_nodes,
                    "maxDepth": a.max_depth,
                    "leaf": "estimate",
                    "beam": a.beam,
                    "turns": a.turns,
                },
            )
            entry["combat"] = {
                "won": auto["won"],
                "turns": auto["turns"],
                "nodes": auto["nodes"],
                "millis": round(auto["micros"] / 1000, 1),
                "enemies": [e["name"] for e in auto["state"]["enemies"]],
            }
            if not auto["won"]:
                entry["hpAfter"] = 0
                entry["wallSeconds"] = round(time.time() - t1, 3)
                floors.append(entry)
                outcome = "died"
                break
            take_rewards(wb, a, entry)
        elif v["restOptions"]:
            wanted = "HEAL" if me["hp"] < me["maxHp"] * 0.6 else "SMITH"
            option = next((o for o in v["restOptions"] if o.upper() == wanted), v["restOptions"][0])
            res = wb.call("wb.rest", {"option": option})
            entry["rest"] = {"option": option, "ok": res["ok"]}
        after = wb.call("run.state")["players"][0]
        entry["hpAfter"] = after["hp"]
        entry["deckSize"] = len(after["deck"])
        entry["gold"] = after["gold"]
        entry["wallSeconds"] = round(time.time() - t1, 3)
        floors.append(entry)
        print(
            f"  floor {entry['floor']:>2} {choice['type']:<8} {entry.get('model') or entry['room']:<28} hp {entry['hpBefore']:>3} -> {entry['hpAfter']:>3}"
            + (
                f"  combat {entry['combat']['turns']} turns {entry['combat']['nodes']} nodes"
                if "combat" in entry
                else ""
            )
            + (f"  took {[t.get('card') or t.get('value') for t in entry['taken']]}" if entry.get("taken") else "")
            + (f"  rest {entry['rest']['option']}" if entry.get("rest") else "")
        )
    final = wb.call("run.state")
    me = final["players"][0]
    summary = {
        "seed": seed,
        "outcome": outcome,
        "floors": len(floors),
        "hpLeft": me["hp"],
        "maxHp": me["maxHp"],
        "gold": me["gold"],
        "deck": [c["id"] + ("+" + str(c["upgrade"]) if c.get("upgrade") else "") for c in me["deck"]],
        "relics": me["relics"],
        "combats": sum("combat" in f for f in floors),
        "won": sum(f.get("combat", {}).get("won", False) for f in floors),
        "nodes": sum(f.get("combat", {}).get("nodes", 0) for f in floors),
        "evaluations": sum("evaluation" in f for f in floors),
        "wallSeconds": round(time.time() - t0, 3),
    }
    return summary, {**summary, "start": run, "floorsDetail": floors}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--character", default="IRONCLAD")
    ap.add_argument("--players", type=int, default=1)
    ap.add_argument("--seed", default="AI4STS2")
    ap.add_argument("--max-floors", type=int, default=20)
    ap.add_argument("--max-nodes", type=int, default=1500)
    ap.add_argument("--max-depth", type=int, default=8)
    ap.add_argument("--max-turns", type=int, default=30)
    ap.add_argument("--beam", type=int, default=3)
    ap.add_argument("--turns", type=int, default=1)
    ap.add_argument("--fights", type=int, default=3)
    a = ap.parse_args()
    a.character = a.character.upper()
    seeds = [s.strip() for s in a.seed.split(",") if s.strip()]
    wb = Harness("wb", timeout=3600)
    ping = wb.call("ping")
    t0 = time.time()
    cases = []
    traces = []
    for seed in seeds:
        print(f"seed {seed}")
        try:
            summary, trace = play_run(wb, a, seed)
        except HarnessError as e:
            summary = {"seed": seed, "outcome": "error", "error": str(e)[:500], "floors": 0, "wallSeconds": 0}
            trace = {**summary, "floorsDetail": []}
            print(f"  error {str(e)[:300]}")
        cases.append(summary)
        traces.append(trace)
        print(
            f"  => {summary['outcome']} floors={summary['floors']} hp={summary.get('hpLeft')} deck={len(summary.get('deck', []))} {summary['wallSeconds']} s"
        )
    metrics.record(
        "run",
        {
            "character": a.character,
            "players": a.players,
            "seeds": seeds,
            "maxNodes": a.max_nodes,
            "maxDepth": a.max_depth,
            "beam": a.beam,
            "turns": a.turns,
            "fights": a.fights,
            "patches": ping.get("patches"),
            "wallSeconds": round(time.time() - t0, 3),
            "floors": sum(c["floors"] for c in cases),
            "died": sum(c["outcome"] == "died" for c in cases),
            "cleared": sum(c["outcome"] == "act-cleared" for c in cases),
            "errors": sum(c["outcome"] == "error" for c in cases),
            "replay": False,
            "cases": cases,
        },
        {"cases": traces},
        {"game": ping.get("game"), "mod": ping.get("mod")},
    )


if __name__ == "__main__":
    main()
