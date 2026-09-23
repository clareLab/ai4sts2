import argparse
import copy
import glob
import json
import os
import queue
import threading
import time

import tuning
from bosslab import setup
from harness import Harness, HarnessError

import metrics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def act_of(floor):
    return 1 if floor <= 17 else 2 if floor <= 33 else 3


def fights(paths, characters, tags, fatal):
    cases = []
    for path in paths:
        with open(path, encoding="utf-8") as f:
            run = json.load(f)
        if run.get("kind") != "run":
            continue
        if tags and run.get("tag") not in tags:
            continue
        if characters and run.get("character") not in characters:
            continue
        for case in (run.get("detail") or run).get("cases", []):
            floors = case.get("floorsDetail") or []
            if fatal:
                floors = floors[-1:] if case.get("outcome") == "died" else []
            for fl in floors:
                combat = fl.get("combat")
                party = fl.get("party")
                if not combat or not party or not fl.get("model") or combat.get("won") == fatal:
                    continue
                me = party[0]
                if me["hp"] <= 1 or me["hp"] > me["maxHp"]:
                    continue
                cases.append(
                    {
                        "run": run["id"],
                        "tag": run.get("tag"),
                        "seed": case["seed"],
                        "floor": fl["floor"],
                        "act": act_of(fl["floor"]),
                        "type": fl["type"],
                        "encounter": fl["model"],
                        "character": me["character"],
                        "net": run.get("net"),
                        "hp": me["hp"],
                        "maxHp": me["maxHp"],
                        "deckSize": len(me["deck"]),
                        "potions": sum(1 for q in me["potions"] if q),
                        "turns": combat.get("turns"),
                        "party": [
                            {
                                "character": q["character"],
                                "hp": q["hp"],
                                "maxHp": q["maxHp"],
                                "deck": [
                                    f"{c['id']}+{c['upgrade']}" if c.get("upgrade") else c["id"] for c in q["deck"]
                                ],
                                "relics": [x if isinstance(x, str) else x["id"] for x in q["relics"]],
                                "potions": [x for x in q["potions"] if x and isinstance(x, str)],
                            }
                            for q in party
                        ],
                    }
                )
    return cases


def spread(cases, limit):
    if not limit or len(cases) <= limit:
        return cases
    buckets = {}
    for case in cases:
        buckets.setdefault((case["character"], case["act"]), []).append(case)
    order = sorted(buckets)
    chosen = []
    i = 0
    while len(chosen) < limit and any(buckets[k] for k in order):
        pool = buckets[order[i % len(order)]]
        if pool:
            chosen.append(pool.pop(len(pool) // 2))
        i += 1
    return chosen


def healed(case):
    full = copy.deepcopy(case)
    for player in full["party"]:
        player["hp"] = player["maxHp"]
    return full


def load_priors():
    path = os.path.join(ROOT, "metrics", "priors.json")
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def surgery(case, priors, count, upgrade):
    table = priors.get(case["character"], {})
    cut = copy.deepcopy(case)
    for player in cut["party"]:
        deck = player["deck"]
        ranked = sorted(range(len(deck)), key=lambda i: table.get(deck[i].split("+")[0], {}).get("prior", 0.0))
        if upgrade:
            for i in ranked[-count:]:
                base = deck[i].split("+")[0]
                deck[i] = base + "+1"
        else:
            for i in sorted(ranked[:count], reverse=True):
                if len(deck) > 10:
                    deck.pop(i)
    return cut


def fight(wb, case, seed, search, max_turns):
    setup(wb, case, seed)
    hard = case["type"] in ("Boss", "Elite")
    res = wb.call("wb.autoplay", {"maxTurns": max_turns, "hard": hard, **search})
    enemies = [e for e in res["state"]["enemies"] if e["maxHp"] < 1_000_000]
    return {
        "won": res["won"],
        "hp": sum(p["creature"]["hp"] for p in res["state"]["players"]),
        "remaining": round(sum(max(0, e["hp"]) for e in enemies) / max(1, sum(e["maxHp"] for e in enemies)), 3),
        "turns": res["turns"],
        "nodes": res["nodes"],
        "seconds": round(res["micros"] / 1e6, 1),
    }


def label(rungs):
    if any(r["won"] for name, r in rungs.items() if name.startswith("base")):
        return "variance"
    if rungs.get("deep", {}).get("won"):
        return "tactical"
    if rungs.get("full", {}).get("won"):
        return "resource"
    if rungs.get("deepFull", {}).get("won"):
        return "resource+tactical"
    if rungs.get("trim", {}).get("won") or rungs.get("sharp", {}).get("won"):
        return "deck"
    return "structural"


def census(wb, case, a):
    rungs = {}
    for i in range(a.repeat):
        rungs[f"roll{i}"] = fight(wb, case, case["seed"] if i == 0 else f"{case['seed']}~{i}", {}, a.max_turns)
    return rungs


def autopsy(wb, case, a, priors):
    deep = {"maxNodes": a.deep_nodes, "beam": a.deep_beam, "turns": a.deep_turns}
    rungs = {}
    rungs["base0"] = fight(wb, case, case["seed"], {}, a.max_turns)
    if not rungs["base0"]["won"]:
        rungs["base1"] = fight(wb, case, case["seed"] + "~1", {}, a.max_turns)
    if not any(r["won"] for r in rungs.values()):
        rungs["deep"] = fight(wb, case, case["seed"], deep, a.max_turns)
        rungs["full"] = fight(wb, healed(case), case["seed"], {}, a.max_turns)
        if not rungs["deep"]["won"] and not rungs["full"]["won"]:
            rungs["deepFull"] = fight(wb, healed(case), case["seed"], deep, a.max_turns)
    if a.surgery and not any(r["won"] for r in rungs.values()) and priors.get(case["character"]):
        rungs["trim"] = fight(wb, surgery(healed(case), priors, a.surgery, False), case["seed"], {}, a.max_turns)
        if not rungs["trim"]["won"]:
            rungs["sharp"] = fight(wb, surgery(healed(case), priors, a.surgery, True), case["seed"], {}, a.max_turns)
    return rungs


def worker(name, jobs, a, rows, lock, priors):
    wb = Harness(name, timeout=7200)
    tuning.apply(wb)
    while True:
        try:
            case = jobs.get_nowait()
        except queue.Empty:
            return
        t0 = time.time()
        try:
            rungs = census(wb, case, a) if a.repeat else autopsy(wb, case, a, priors)
            verdict = f"p={sum(r['won'] for r in rungs.values()) / len(rungs):.2f}" if a.repeat else label(rungs)
        except HarnessError as e:
            rungs = {}
            verdict = "error: " + str(e)[:200]
        row = {**{k: v for k, v in case.items() if k != "party"}, "rungs": rungs, "verdict": verdict}
        with lock:
            rows.append(row)
            cells = " ".join(f"{n}:{'W' if r['won'] else 'L'}{r['remaining']:.2f}" for n, r in rungs.items())
            print(
                f"[{name}] {case['character'][:5]:<5} f{case['floor']:<2} {case['encounter'][:26]:<26} "
                f"hp {case['hp']:>3}/{case['maxHp']:<3} deck {case['deckSize']:>2} | {cells} => {verdict} "
                f"{round(time.time() - t0)} s",
                flush=True,
            )
        jobs.task_done()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--instances", default="dev")
    ap.add_argument("--runs", default="")
    ap.add_argument("--tags", default="nightA,nightB,nightC,nightD,nightW,nightX")
    ap.add_argument("--characters", default="")
    ap.add_argument("--cases", type=int, default=20)
    ap.add_argument("--max-turns", type=int, default=40)
    ap.add_argument("--deep-nodes", type=int, default=12000)
    ap.add_argument("--deep-beam", type=int, default=8)
    ap.add_argument("--deep-turns", type=int, default=4)
    ap.add_argument("--surgery", type=int, default=0)
    ap.add_argument("--repeat", type=int, default=0)
    ap.add_argument("--won", action="store_true")
    a = ap.parse_args()
    paths = [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(ROOT, "metrics", "runs", "*-run-*.json"))
    )
    pool = fights(
        paths,
        {c.strip().upper() for c in a.characters.split(",") if c.strip()},
        {t.strip() for t in a.tags.split(",") if t.strip()},
        not a.won,
    )
    cases = spread(pool, a.cases)
    instances = [n.strip() for n in a.instances.split(",") if n.strip()]
    ping = Harness(instances[0]).call("ping")
    print(f"autopsy: {len(cases)} of {len(pool)} deaths on {','.join(instances)}", flush=True)
    jobs = queue.Queue()
    for case in cases:
        jobs.put(case)
    rows = []
    lock = threading.Lock()
    t0 = time.time()
    threads = [
        threading.Thread(target=worker, args=(n, jobs, a, rows, lock, load_priors()), daemon=True) for n in instances
    ]
    for t in threads:
        t.start()
        time.sleep(1)
    for t in threads:
        t.join()
    verdicts = {}
    for row in rows:
        key = row["verdict"].split(":")[0]
        verdicts[key] = verdicts.get(key, 0) + 1
    if a.repeat:
        scored = [(r, sum(x["won"] for x in r["rungs"].values()) / max(1, len(r["rungs"]))) for r in rows if r["rungs"]]
        verdicts = {
            "fights": len(scored),
            "meanP": round(sum(p for _, p in scored) / max(1, len(scored)), 3),
            "certain": sum(1 for _, p in scored if p >= 1.0),
            "risky": sum(1 for _, p in scored if p < 1.0),
            "byType": {
                t: round(
                    sum(p for r, p in scored if r["type"] == t) / max(1, sum(1 for r, _ in scored if r["type"] == t)), 3
                )
                for t in sorted({r["type"] for r, _ in scored})
            },
            "byAct": {
                str(act): round(
                    sum(p for r, p in scored if r["act"] == act) / max(1, sum(1 for r, _ in scored if r["act"] == act)),
                    3,
                )
                for act in sorted({r["act"] for r, _ in scored})
            },
        }
    summary = {
        "cases": len(rows),
        "pool": len(pool),
        "tags": a.tags,
        "deep": {"nodes": a.deep_nodes, "beam": a.deep_beam, "turns": a.deep_turns},
        "surgery": a.surgery,
        "repeat": a.repeat,
        "verdicts": verdicts,
        "byAct": {
            str(act): {v: sum(1 for r in rows if r["act"] == act and r["verdict"].split(":")[0] == v) for v in verdicts}
            for act in sorted({r["act"] for r in rows})
        },
        "wallSeconds": round(time.time() - t0),
        "replay": False,
    }
    metrics.record("autopsy", summary, {"rows": rows}, {"game": ping.get("game"), "mod": ping.get("mod")})
    print(json.dumps(verdicts, ensure_ascii=False), summary["byAct"], f"{summary['wallSeconds']} s")


if __name__ == "__main__":
    main()
