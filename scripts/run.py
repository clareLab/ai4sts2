import argparse
import json
import os
import time

import tuning
from harness import Harness, HarnessError

import metrics


def autoplay(wb, a, hard=False):
    return wb.call("wb.autoplay", {"hard": hard})


def combat_entry(entry, auto):
    entry["combat"] = {
        "won": auto["won"],
        "turns": auto["turns"],
        "nodes": auto["nodes"],
        "millis": round(auto["micros"] / 1000, 1),
        "enemies": [e["name"] for e in auto["state"]["enemies"]],
        "trace": auto.get("trace", []),
    }
    return auto["won"]


def handle_event(wb, a, entry):
    chosen = []
    for _ in range(6):
        v = wb.call("wb.view")["view"]
        if v["room"] != "EventRoom" or v["eventFinished"]:
            break
        options = [o for o in v["eventOptions"] if not o["locked"] and not o["proceed"] and not o["chosen"]]
        if not options:
            break
        pick = options[0]
        try:
            if a.no_event_eval:
                raise StopIteration
            ev = wb.call("wb.evalevent")["evaluation"]
            entry.setdefault("eventEvaluations", []).append(ev)
            best = ev.get("best")
            if best is None or best["col"] < 0:
                exits = [o for o in v["eventOptions"] if o["proceed"] and not o["locked"] and not o["chosen"]]
                if exits:
                    pick = exits[0]
                else:
                    scored = [o for o in ev["options"] if o["choice"]["col"] >= 0 and not o.get("error")] or [
                        o for o in ev["options"] if o["choice"]["col"] >= 0
                    ]
                    if not scored:
                        chosen.append("leave")
                        break
                    top = max(scored, key=lambda o: o["score"])
                    pick = next(o for o in options if o["index"] == top["choice"]["col"])
            else:
                pick = next(o for o in options if o["index"] == best["col"])
        except StopIteration:
            pass
        except HarnessError as e:
            entry.setdefault("eventEvaluations", []).append({"error": str(e)[:300]})
            chosen.append("leave (evaluation failed)")
            break
        try:
            res = wb.call("wb.event", {"index": pick["index"]})
        except HarnessError as e:
            chosen.append(pick["key"] + " (failed)")
            entry["event"] = {"id": v["event"], "chosen": chosen, "error": str(e)[:300]}
            print(f"    event {v['event']} option {pick['key']} failed: {str(e)[:120]}")
            return True
        chosen.append(pick["key"])
        if res["view"]["inCombat"]:
            auto = autoplay(wb, a)
            won = combat_entry(entry, auto)
            if not won:
                entry["event"] = {"id": v["event"], "chosen": chosen}
                return False
            take_rewards(wb, a, entry)
            wb.call("wb.proceed")
    entry["event"] = {"id": entry.get("model"), "chosen": chosen}
    return True


def handle_treasure(wb, a, entry):
    gold = wb.call("wb.chest")["micros"]
    v = wb.call("wb.view")["view"]
    relics = v["treasureRelics"]
    pick = 0
    if len(relics) > 1:
        ev = wb.call("wb.evalrelic")["evaluation"]
        entry["evaluation"] = ev
        best = next((o for o in ev["options"] if o["label"] == ev["best"]), None)
        if best is not None and best.get("index") is not None:
            pick = best["index"]
    votes = [pick if relics else None for _ in range(a.players)]
    res = wb.call("wb.relic", {"votes": votes})
    entry["treasure"] = {
        "gold": gold,
        "offered": relics,
        "picked": res.get("picked"),
        "pickedAll": res.get("pickedAll"),
    }


def handle_rest(wb, a, entry, v, players):
    options = []
    upgraded = []
    for slot, p in enumerate(players):
        upgraded.append(None)
        if p["hp"] <= 0:
            options.append(None)
            continue
        ev = wb.call("wb.evalrest", {"player": slot})["evaluation"]
        entry.setdefault("smithEvaluations", []).append(ev)
        if slot == 0:
            entry["evaluation"] = ev
        best = next((o for o in ev["options"] if o["label"] == ev["best"]), None)
        if best is None:
            options.append(v["restOptions"][0])
            continue
        if best["label"].startswith("SMITH") and best.get("index") is not None:
            wb.call("selector.enqueue", {"choice": [best["index"]]})
            upgraded[slot] = best["label"][6:]
            options.append(next((o for o in v["restOptions"] if o.upper() == "SMITH"), v["restOptions"][0]))
        else:
            options.append(next((o for o in v["restOptions"] if o.upper() == "HEAL"), v["restOptions"][0]))
    res = wb.call("wb.rest", {"options": options})
    entry["rest"] = {
        "option": options[0],
        "options": options,
        "ok": res["ok"],
        "upgraded": upgraded[0],
        "upgradedAll": upgraded,
    }


def handle_shop(wb, a, entry):
    v = wb.call("wb.view")["view"]
    bought = []
    evaluations = []
    for slot in range(a.players):
        shop_player(wb, a, slot, bought, evaluations)
    entry["shopEvaluations"] = evaluations
    entry["shop"] = {"bought": bought, "offered": [(e["kind"], e.get("id"), e["cost"]) for e in v["shop"]]}


def shop_player(wb, a, slot, bought, evaluations):
    for _ in range(3):
        ev = wb.call("wb.evalshop", {"player": slot})["evaluation"]
        evaluations.append(ev)
        best = next(o for o in ev["options"] if o["label"] == ev["best"])
        if best.get("index") is None:
            break
        shop = wb.call("wb.view", {"player": slot})["view"]["shop"]
        e = shop[best["index"]]
        if e["kind"] == "removal":
            deck = wb.call("run.state")["players"][slot]["deck"]
            card, _, level = best["label"][7:].partition("+")
            idx = next(
                (i for i, c in enumerate(deck) if c["id"] == card and (c.get("upgrade") or 0) == int(level or 0)), None
            )
            ok = idx is not None and wb.call("wb.remove", {"deck": idx, "player": slot})["ok"]
        else:
            ok = wb.call("wb.buy", {"index": e["index"], "player": slot})["ok"]
        if not ok:
            break
        bought.append({"kind": e["kind"], "id": e.get("id"), "cost": e["cost"], "label": best["label"], "player": slot})
    shop = wb.call("wb.view", {"player": slot})["view"]["shop"]
    for e in sorted((e for e in shop if e["kind"] == "potion" and e["stocked"]), key=lambda e: e["cost"]):
        state = wb.call("run.state")["players"][slot]
        affordable = e["cost"] <= state["gold"] and any(p is None for p in state["potions"])
        if affordable and wb.call("wb.buy", {"index": e["index"], "player": slot})["ok"]:
            bought.append({"kind": "potion", "id": e.get("id"), "cost": e["cost"], "player": slot})


def take_rewards(wb, a, entry):
    view = wb.call("wb.rewards")["view"]
    taken = []
    for set_view in view["rewards"]:
        player = set_view["player"]
        for reward in set_view["rewards"]:
            if reward["kind"] == "card":
                ev = wb.call("wb.evalreward", {"index": reward["index"], "player": player})["evaluation"]
                entry.setdefault("evaluations", []).append(ev)
                if player == 0:
                    entry["evaluation"] = ev
                best = next((o for o in ev["options"] if o["label"] == ev["best"]), None)
                if best is not None and best.get("card") is not None:
                    res = wb.call("wb.take", {"index": reward["index"], "card": best["card"], "player": player})
                    taken.append({"kind": "card", "card": best["label"], "ok": res["ok"], "player": player})
                else:
                    taken.append({"kind": "card", "card": None, "ok": True, "player": player})
            else:
                res = wb.call("wb.take", {"index": reward["index"], "player": player})
                taken.append(
                    {
                        "kind": reward["kind"],
                        "value": reward.get("gold") or reward.get("potion") or reward.get("relic"),
                        "ok": res["ok"],
                        "player": player,
                    }
                )
    view = wb.call("wb.view")["view"]
    for set_view in view["rewards"]:
        if not set_view["completed"]:
            wb.call("wb.skip", {"player": set_view["player"]})
    entry["taken"] = taken


def finish_floor(wb, a, entry, floors, t1):
    players_after = wb.call("run.state")["players"]
    after = players_after[0]
    entry["hpAfter"] = after["hp"]
    entry["hpAfterAll"] = [p["hp"] for p in players_after]
    entry["deckSize"] = len(after["deck"])
    entry["gold"] = after["gold"]
    entry["wallSeconds"] = round(time.time() - t1, 3)
    floors.append(entry)
    label = f"  floor {entry['floor']:>2} {entry['type']:<8} {entry.get('model') or entry['room']:<28}"
    if all(h <= 0 for h in entry["hpAfterAll"]):
        print(f"{label} died")
        return
    hp_before = "+".join(str(h) for h in entry["hpBeforeAll"]) if a.players > 1 else f"{entry['hpBefore']:>3}"
    hp_after = "+".join(str(h) for h in entry["hpAfterAll"]) if a.players > 1 else f"{entry['hpAfter']:>3}"
    print(
        f"{label} hp {hp_before} -> {hp_after}"
        + (f"  combat {entry['combat']['turns']} turns {entry['combat']['nodes']} nodes" if "combat" in entry else "")
        + (f"  took {[t.get('card') or t.get('value') for t in entry['taken']]}" if entry.get("taken") else "")
        + (
            f"  rest {'/'.join(o or '-' for o in entry['rest']['options'])} {'/'.join(u or '-' for u in entry['rest']['upgradedAll'])}"
            if entry.get("rest")
            else ""
        )
        + (f"  event {entry['event']['chosen']}" if entry.get("event") else "")
        + (
            f"  relic {'/'.join(str(r) for r in entry['treasure'].get('pickedAll') or [entry['treasure']['picked']])}"
            if entry.get("treasure")
            else ""
        )
        + (f"  shop {[b.get('id') or b.get('card') for b in entry['shop']['bought']]}" if entry.get("shop") else "")
    )


def play_run(wb, a, seed):
    t0 = time.time()
    run = wb.call(
        "wb.run",
        {"character": a.character, "players": a.players, "seed": seed, "ascension": 0, "map": True, "net": a.net},
    )
    floors = []
    outcome = "running"
    handled_event = None
    for _ in range(a.max_floors):
        view = wb.call("wb.view")["view"]
        state = wb.call("run.state")
        me = state["players"][0]
        pending = [o for o in view["eventOptions"] if not o["locked"] and not o["chosen"]]
        if (
            view["room"] == "EventRoom"
            and view["event"]
            and not view["eventFinished"]
            and pending
            and handled_event != (view["floor"], view["event"])
        ):
            handled_event = (view["floor"], view["event"])
            t1 = time.time()
            entry = {
                "floor": view["floor"],
                "actFloor": view["actFloor"],
                "coord": view["coord"],
                "type": "Ancient",
                "room": view["room"],
                "model": view["event"],
                "hpBefore": me["hp"],
                "hpBeforeAll": [p["hp"] for p in state["players"]],
                "choices": [],
                "routeScores": None,
                "pathEvaluation": None,
            }
            alive = handle_event(wb, a, entry)
            finish_floor(wb, a, entry, floors, t1)
            if not alive:
                outcome = "died"
                break
            after = wb.call("wb.view")["view"]
            if after["room"] == "EventRoom" and after["event"] == view["event"] and not after["eventFinished"]:
                stuck = [o for o in after["eventOptions"] if not o["locked"] and not o["chosen"]]
                if stuck:
                    try:
                        wb.call("wb.event", {"index": stuck[0]["index"]})
                    except HarnessError as e:
                        print(f"    event {after['event']} could not be finished: {str(e)[:120]}")
            continue
        plan = wb.call("wb.route", {"paths": a.paths})["plan"] if view["choices"] else None
        choice = plan["choice"] if plan else None
        if choice is None:
            outcome = "stuck"
            break
        t1 = time.time()
        route_scores = {f"{o['col']},{o['row']}": round(o["score"], 1) for o in plan["options"]}
        path_eval = plan.get("paths")
        if path_eval is not None:
            path_eval["combined"] = {
                f"{o['col']},{o['row']}": round(o["combined"], 1)
                for o in plan["options"]
                if o.get("combined") is not None
            }
            path_eval["reason"] = plan["reason"]
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
            "hpBeforeAll": [p["hp"] for p in state["players"]],
            "choices": view["choices"],
            "routeScores": route_scores,
            "pathEvaluation": path_eval,
        }
        alive = True
        if v["inCombat"]:
            entry["party"] = [
                {k: p[k] for k in ("character", "hp", "maxHp", "gold", "deck", "relics", "potions")}
                for p in state["players"]
            ]
        if v["inCombat"]:
            alive = combat_entry(entry, autoplay(wb, a, choice["type"] in ("Boss", "Elite")))
            if alive:
                take_rewards(wb, a, entry)
                if choice["type"] == "Boss":
                    after_boss = wb.call("wb.view")["view"]
                    if after_boss["lastAct"]:
                        outcome = "act-cleared"
                    else:
                        wb.call("wb.nextact")
                        entry["nextAct"] = True
        elif v["room"] == "EventRoom":
            alive = handle_event(wb, a, entry)
        elif v["room"] == "TreasureRoom":
            handle_treasure(wb, a, entry)
        elif v["room"] == "MerchantRoom":
            handle_shop(wb, a, entry)
        elif v["restOptions"]:
            handle_rest(wb, a, entry, v, state["players"])
        finish_floor(wb, a, entry, floors, t1)
        if not alive:
            outcome = "died"
            break
        if outcome == "act-cleared":
            break
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
    ap.add_argument("--record", action="store_true")
    ap.add_argument("--tune", action="append", default=[])
    ap.add_argument("--priors", default=os.path.join(os.path.dirname(__file__), "..", "metrics", "priors.json"))
    ap.add_argument("--tuning", default=tuning.PATH)
    ap.add_argument("--records", default=os.path.join(metrics.ROOT, ".local", "records"))
    ap.add_argument("--no-event-eval", action="store_true")
    ap.add_argument("--instance", default="wb")
    ap.add_argument("--net", choices=["host", "single"], default="host")
    ap.add_argument("--paths", action="store_true")
    ap.add_argument("--tag", default="")
    a = ap.parse_args()
    a.character = a.character.upper()
    seeds = [s.strip() for s in a.seed.split(",") if s.strip()]
    wb = Harness(a.instance, timeout=3600)
    ping = wb.call("ping")
    tune = {
        k: (int(v) if v.lstrip("-").isdigit() else v.lower() == "true")
        for k, _, v in (t.partition("=") for t in a.tune)
    }
    knobs = tuning.apply(wb, {"Record": a.record, **tune}, a.tuning)
    run_id = metrics.make_id(metrics.now(), "run")
    if a.record:
        wb.call("wb.record", {"dir": os.path.abspath(a.records), "run": run_id})
    priors = {}
    if a.priors and os.path.exists(a.priors):
        with open(a.priors, encoding="utf-8") as f:
            priors = json.load(f).get(a.character, {})
    wb.call("wb.priors", {"cards": priors})
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
            "net": a.net,
            "tag": a.tag,
            "seeds": seeds,
            "maxNodes": knobs["tuning"]["SearchNodes"],
            "maxDepth": knobs["tuning"]["MaxDepth"],
            "beam": knobs["tuning"]["SearchBeam"],
            "turns": knobs["tuning"]["SearchTurns"],
            "bossSearch": {
                "maxNodes": knobs["tuning"]["HardNodes"],
                "beam": knobs["tuning"]["HardBeam"],
                "turns": knobs["tuning"]["HardTurns"],
            },
            "fights": knobs["tuning"]["Fights"],
            "paths": a.paths,
            "boss": knobs["tuning"]["BossProbe"],
            "bossTurns": knobs["tuning"]["BossTurns"],
            "elite": knobs["tuning"]["EliteProbe"],
            "tune": tune,
            "tuning": knobs["tuning"],
            "modes": knobs["modes"],
            "priors": len(priors),
            "records": os.path.join(a.records, run_id + ".jsonl") if a.record else None,
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
        run_id=run_id,
    )


if __name__ == "__main__":
    main()
