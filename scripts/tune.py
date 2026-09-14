import argparse
import glob
import json
import os
import random
import threading
import time

import bosslab
import decklab
import tuning
from harness import Harness, HarnessError

import metrics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STATE_DIR = os.path.join(ROOT, ".local", "tune")


class FightLab:
    def __init__(self, a):
        self.kinds = set(a.kinds.split(","))
        self.min_floor = a.min_floor

    def cases(self, a):
        return bosslab.collect(paths(a), self.kinds, a.cases, self.min_floor)

    def score(self, wb, case, config, a):
        r = bosslab.play(wb, case, config, a.max_turns)
        margin = r["hp"] if r["won"] else -a.loss_weight * r["remaining"]
        return (1000 if r["won"] else 0) + margin, r["nodes"]


class DeckLab:
    def cases(self, a):
        return decklab.collect(paths(a), a.cases, set(filter(None, a.characters.split(","))))

    def score(self, wb, case, config, a):
        bad = [x for x in a.bad.split(",") if x]
        good = [x for x in a.good.split(",") if x]
        decklab.setup(wb, case)
        trial = decklab.evaluate(wb, case, config, 0, bad + good, a.max_turns)
        others = [v for k, v in trial["scores"].items() if k not in bad and k not in good]
        bad_last = bool(others and bad) and all(trial["scores"].get(x, 0) <= min(others) for x in bad)
        good_first = bool(others and good) and all(trial["scores"].get(x, 0) >= max(others) for x in good)
        return (100 if bad_last else 0) + (100 if good_first else 0), trial["nodes"]


def case_id(case):
    return f"{case['run']}:{case['floor']}"


def paths(a):
    return [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(ROOT, "metrics", "runs", "*-run-*.json"))
    )


def grid(value):
    if value == 0:
        return [1, 5]
    if abs(value) <= 6:
        return sorted({max(1, value - 1), value + 1} - {value})
    return sorted({round(value * f) for f in (0.5, 0.7, 1.4, 2.0)} - {value})


def knob_space(factory, current, frozen):
    space = {}
    for name, default in factory.items():
        if name in frozen:
            continue
        value = current.get(name, default)
        if isinstance(value, bool):
            space[name] = [not value]
        elif isinstance(value, int):
            space[name] = grid(value)
    return space


def config_of(deltas):
    return {f"tune.{k}": v for k, v in deltas.items()}


def key_of(config):
    return json.dumps(config, sort_keys=True)


class Store:
    def __init__(self, name, build):
        os.makedirs(STATE_DIR, exist_ok=True)
        self.path = os.path.join(STATE_DIR, f"{name}.jsonl")
        self.inert_path = os.path.join(STATE_DIR, f"{name}-inert.json")
        self.build = build
        self.scores = {}
        self.lock = threading.Lock()
        if os.path.exists(self.path):
            with open(self.path, encoding="utf-8") as f:
                for line in f:
                    row = json.loads(line)
                    if row.get("build") == build:
                        self.scores.setdefault(row["config"], {})[row["case"]] = (row["score"], row["nodes"])
        self.inert = {}
        if os.path.exists(self.inert_path):
            with open(self.inert_path, encoding="utf-8") as f:
                self.inert = json.load(f)

    def get(self, config, cid):
        return self.scores.get(key_of(config), {}).get(cid)

    def put(self, config, cid, score, nodes):
        with self.lock:
            self.scores.setdefault(key_of(config), {})[cid] = (score, nodes)
            with open(self.path, "a", encoding="utf-8") as f:
                row = {"build": self.build, "config": key_of(config), "case": cid, "score": score, "nodes": nodes}
                f.write(json.dumps(row) + "\n")

    def is_inert(self, knob):
        return self.inert.get(knob) == self.build

    def mark_inert(self, knob):
        with self.lock:
            self.inert[knob] = self.build
            with open(self.inert_path, "w", encoding="utf-8") as f:
                json.dump(self.inert, f, indent=1, sort_keys=True)


def bootstrap_p(diffs, resamples=4000, seed=1):
    rng = random.Random(seed)
    n = len(diffs)
    if sum(diffs) <= 0:
        return 1.0
    worse = 0
    for _ in range(resamples):
        if sum(diffs[rng.randrange(n)] for _ in range(n)) <= 0:
            worse += 1
    return worse / resamples


def objective(entry, lam):
    score, nodes = entry
    return score - lam * nodes / 1000


def paired(candidate, baseline, lam):
    diffs = [objective(candidate[k], lam) - objective(baseline[k], lam) for k in candidate if k in baseline]
    if not diffs:
        return 0.0, 1.0, 0
    return sum(diffs) / len(diffs), bootstrap_p(diffs), len(diffs)


class Evaluator:
    def __init__(self, lab, cases, instances, store, a):
        self.lab = lab
        self.cases = {case_id(c): c for c in cases}
        self.store = store
        self.a = a
        self.pool = list(instances)
        self.lock = threading.Lock()
        self.clients = {}

    def acquire(self):
        while True:
            with self.lock:
                if self.pool:
                    name = self.pool.pop()
                    if name not in self.clients:
                        self.clients[name] = Harness(name, timeout=3600)
                    return name, self.clients[name]
            time.sleep(0.5)

    def release(self, name):
        with self.lock:
            self.pool.append(name)

    def evaluate(self, config, ids, baseline=None):
        results = {}
        name, wb = self.acquire()
        try:
            for i, cid in enumerate(ids):
                entry = self.store.get(config, cid)
                if entry is None:
                    try:
                        entry = self.lab.score(wb, self.cases[cid], config, self.a)
                        self.store.put(config, cid, *entry)
                    except HarnessError as e:
                        print("  error", cid, str(e)[:120])
                        continue
                results[cid] = entry
                if baseline is None or i + 1 >= len(ids):
                    continue
                diffs = [objective(results[k], self.a.lam) - objective(baseline[k], self.a.lam) for k in results]
                if len(diffs) >= self.a.same_min and all(d == 0 for d in diffs):
                    return results, "inert"
                racing = len(diffs) >= self.a.race_min and (i + 1) % self.a.race_every == 0
                if racing and sum(diffs) / len(diffs) < -self.a.race_margin:
                    return results, "raced out"
        finally:
            self.release(name)
        return results, None

    def evaluate_many(self, configs, ids, baseline):
        outcomes = [None] * len(configs)

        def work(index):
            outcomes[index] = self.evaluate(configs[index], ids, baseline)

        threads = [threading.Thread(target=work, args=(i,), daemon=True) for i in range(len(configs))]
        for th in threads:
            th.start()
        for th in threads:
            th.join()
        return outcomes


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lab", choices=["fight", "deck"], default="fight")
    ap.add_argument("--kinds", default="Boss")
    ap.add_argument("--min-floor", type=int, default=0)
    ap.add_argument("--instances", default="dev")
    ap.add_argument("--runs", default="")
    ap.add_argument("--characters", default="")
    ap.add_argument("--cases", type=int, default=40)
    ap.add_argument("--holdout", type=float, default=0.3)
    ap.add_argument("--max-turns", type=int, default=40)
    ap.add_argument("--loss-weight", type=float, default=300.0)
    ap.add_argument("--bad", default="")
    ap.add_argument("--good", default="")
    ap.add_argument("--knobs", default="")
    ap.add_argument("--freeze", default="")
    ap.add_argument("--passes", type=int, default=2)
    ap.add_argument("--alpha", type=float, default=0.1)
    ap.add_argument("--lam", type=float, default=0.0)
    ap.add_argument("--race-every", type=int, default=8)
    ap.add_argument("--race-min", type=int, default=8)
    ap.add_argument("--race-margin", type=float, default=30.0)
    ap.add_argument("--same-min", type=int, default=6)
    ap.add_argument("--dry", action="store_true")
    a = ap.parse_args()
    lab = FightLab(a) if a.lab == "fight" else DeckLab()
    name = f"{a.lab}-{a.kinds.lower()}" if a.lab == "fight" else "deck"
    cases = lab.cases(a)
    rng = random.Random(7)
    rng.shuffle(cases)
    split = int(len(cases) * (1 - a.holdout))
    tune_ids = [case_id(c) for c in cases[:split]]
    hold_ids = [case_id(c) for c in cases[split:]]
    instances = [x for x in a.instances.split(",") if x]
    wb = Harness(instances[0], timeout=3600)
    ping = wb.call("ping")
    factory = wb.call("wb.tune", {"reset": True})["tuning"]
    deltas = {k: v for k, v in tuning.load().items() if k in factory}
    frozen = set(filter(None, a.freeze.split(",")))
    space = knob_space(factory, deltas, frozen)
    if a.knobs:
        wanted = set(a.knobs.split(","))
        space = {k: v for k, v in space.items() if k in wanted}
    build = f"{ping.get('mod')}:{metrics.build_info().get('modBuilt')}"
    store = Store(name, build)
    space = {k: v for k, v in space.items() if not store.is_inert(k)}
    print(f"{name}: {len(tune_ids)} tuning cases, {len(hold_ids)} holdout, {len(space)} knobs, deltas {deltas}")
    if a.dry:
        for k, v in space.items():
            print(" ", k, factory[k], "->", v)
        return
    ev = Evaluator(lab, cases, instances, store, a)
    t0 = time.time()
    base_scores, _ = ev.evaluate(config_of(deltas), tune_ids)
    accepted = []
    for _ in range(a.passes):
        improved = False
        for knob, values in list(space.items()):
            current = deltas.get(knob, factory[knob])
            candidates = [{**deltas, knob: v} for v in values if v != current]
            outcomes = ev.evaluate_many([config_of(c) for c in candidates], tune_ids, base_scores)
            best = None
            inert = True
            for cand, (res, stop) in zip(candidates, outcomes, strict=True):
                value = cand[knob]
                if stop == "inert":
                    print(f"  {knob}={value}: inert")
                    continue
                inert = False
                if stop or len(res) < len(tune_ids):
                    print(f"  {knob}={value}: {stop or 'incomplete'} after {len(res)} cases")
                    continue
                mean, pval, n = paired(res, base_scores, a.lam)
                print(f"  {knob}={value}: mean {mean:+.1f} p {pval:.3f} n {n}")
                if mean > 0 and pval < a.alpha and (best is None or mean > best[1]):
                    best = (value, mean, pval, cand, res)
            if inert and candidates:
                store.mark_inert(knob)
                continue
            if best is None:
                continue
            value, mean, pval, cand, res = best
            hold_base, _ = ev.evaluate(config_of(deltas), hold_ids)
            hold_new, _ = ev.evaluate(config_of(cand), hold_ids)
            hmean, _, hn = paired(hold_new, hold_base, a.lam)
            verdict = "accept" if hmean >= 0 else "reject"
            print(
                f"* {knob}: {current} -> {value} tune {mean:+.1f} (p {pval:.3f}) holdout {hmean:+.1f} (n {hn}) {verdict}"
            )
            if hmean < 0:
                continue
            deltas = cand
            base_scores = res
            improved = True
            accepted.append(
                {
                    "knob": knob,
                    "from": current,
                    "to": value,
                    "tune": round(mean, 2),
                    "p": round(pval, 3),
                    "holdout": round(hmean, 2),
                }
            )
            tuning.save({**tuning.load(), **{k: v for k, v in deltas.items() if v != factory[k]}})
        if not improved:
            break
    summary = {
        "lab": name,
        "cases": len(tune_ids),
        "holdout": len(hold_ids),
        "knobs": len(space),
        "accepted": accepted,
        "deltas": {k: v for k, v in deltas.items() if v != factory[k]},
        "wallSeconds": round(time.time() - t0, 1),
        "replay": False,
    }
    metrics.record(
        "tune",
        summary,
        {"space": space, "cases": tune_ids, "holdout": hold_ids},
        {"game": ping.get("game"), "mod": ping.get("mod")},
    )
    print("accepted", json.dumps(accepted, ensure_ascii=False))


if __name__ == "__main__":
    main()
