import argparse
import glob
import json
import os
import random
import threading
import time

import bosslab
import decklab
from harness import Harness, HarnessError

import metrics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TUNING_PATH = os.path.join(ROOT, "metrics", "tuning.json")
JOURNAL_DIR = os.path.join(ROOT, ".local", "tune")
FROZEN = {"Record", "FreezeMap", "StableShuffle", "ProbeTurnEnd", "TemporaryPowers", "HorizonEstimate"}


class Lab:
    name = ""

    @property
    def search(self):
        return {}

    def cases(self, a):
        raise NotImplementedError

    def score(self, wb, case, config, a):
        raise NotImplementedError

    @staticmethod
    def case_id(case):
        return f"{case['run']}:{case['floor']}"


class FightLab(Lab):
    def __init__(self, name, kinds, search, min_floor):
        self.name = name
        self.kinds = kinds
        self._search = search
        self.min_floor = min_floor

    @property
    def search(self):
        return dict(self._search)

    def cases(self, a):
        return bosslab.collect(paths(a), self.kinds, a.cases, self.min_floor)

    def score(self, wb, case, config, a):
        r = bosslab.play(wb, case, config, a.max_turns)
        return (1000 if r["won"] else 0) + r["hp"], r["millis"] / 1000


class DeckLab(Lab):
    name = "deck"

    @property
    def search(self):
        return {"fights": 2, "boss": True, "elite": True, "maxNodes": 400, "beam": 3, "samples": 1}

    def cases(self, a):
        return decklab.collect(paths(a), a.cases, set(filter(None, a.characters.split(","))))

    def score(self, wb, case, config, a):
        bad = [x for x in a.bad.split(",") if x]
        good = [x for x in a.good.split(",") if x]
        t0 = time.time()
        decklab.setup(wb, case)
        trial = decklab.evaluate(wb, case, config, 0, bad + good, a.max_turns)
        others = [v for k, v in trial["scores"].items() if k not in bad and k not in good]
        bad_last = all(trial["scores"].get(x, 0) <= min(others) for x in bad) if others and bad else False
        good_first = all(trial["scores"].get(x, 0) >= max(others) for x in good) if others and good else False
        return (100 if bad_last else 0) + (100 if good_first else 0), time.time() - t0


LABS = {
    "boss": FightLab("boss", {"Boss"}, {"turns": 3, "beam": 5, "maxNodes": 2500}, 0),
    "monster": FightLab("monster", {"Monster"}, {"turns": 2, "beam": 3, "maxNodes": 400}, 18),
    "deck": DeckLab(),
}


def paths(a):
    return [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(ROOT, "metrics", "runs", "*-run-*.json"))
    )


def load_tuning():
    if os.path.exists(TUNING_PATH):
        with open(TUNING_PATH, encoding="utf-8") as f:
            return json.load(f)
    return {"knobs": {}, "history": []}


def save_tuning(tuning):
    with open(TUNING_PATH, "w", encoding="utf-8") as f:
        json.dump(tuning, f, ensure_ascii=False, indent=1, sort_keys=True)


def knob_space(defaults, search, frozen):
    space = {}
    for name, value in defaults.items():
        if name in frozen:
            continue
        if isinstance(value, bool):
            space[f"tune.{name}"] = [not value]
        elif isinstance(value, int):
            space[f"tune.{name}"] = grid(value)
    for name, value in search.items():
        if isinstance(value, bool):
            space[name] = [not value]
        elif isinstance(value, int):
            space[name] = grid(value)
    return space


def grid(value):
    if value == 0:
        return [1, 5]
    if abs(value) <= 6:
        return sorted({max(0, value - 1), value + 1} - {value})
    return sorted({round(value * f) for f in (0.5, 0.7, 1.4, 2.0)} - {value})


def key_of(config):
    return json.dumps(config, sort_keys=True)


class Journal:
    def __init__(self, lab):
        os.makedirs(JOURNAL_DIR, exist_ok=True)
        self.path = os.path.join(JOURNAL_DIR, f"{lab}.jsonl")
        self.scores = {}
        self.lock = threading.Lock()
        if os.path.exists(self.path):
            with open(self.path, encoding="utf-8") as f:
                for line in f:
                    row = json.loads(line)
                    self.scores.setdefault(row["config"], {})[row["case"]] = (row["score"], row["seconds"])

    def get(self, config, case_id):
        return self.scores.get(key_of(config), {}).get(case_id)

    def put(self, config, case_id, score, seconds):
        with self.lock:
            self.scores.setdefault(key_of(config), {})[case_id] = (score, seconds)
            with open(self.path, "a", encoding="utf-8") as f:
                f.write(
                    json.dumps({"config": key_of(config), "case": case_id, "score": score, "seconds": seconds}) + "\n"
                )


def bootstrap_p(diffs, resamples=4000, seed=1):
    rng = random.Random(seed)
    n = len(diffs)
    mean = sum(diffs) / n
    if mean <= 0:
        return 1.0
    worse = 0
    for _ in range(resamples):
        sample = [diffs[rng.randrange(n)] for _ in range(n)]
        if sum(sample) / n <= 0:
            worse += 1
    return worse / resamples


class Evaluator:
    def __init__(self, lab, cases, instances, journal, a):
        self.lab = lab
        self.cases = cases
        self.journal = journal
        self.a = a
        self.pool = list(instances)
        self.lock = threading.Lock()
        self.clients = {}

    def client(self, instance):
        if instance not in self.clients:
            self.clients[instance] = Harness(instance, timeout=3600)
        return self.clients[instance]

    def acquire(self):
        while True:
            with self.lock:
                if self.pool:
                    return self.pool.pop()
            time.sleep(0.5)

    def release(self, instance):
        with self.lock:
            self.pool.append(instance)

    def evaluate(self, config, case_ids=None, baseline=None, lam=0.0):
        wanted = [c for c in self.cases if case_ids is None or self.lab.case_id(c) in case_ids]
        results = {}
        instance = self.acquire()
        try:
            wb = self.client(instance)
            for i, case in enumerate(wanted):
                cid = self.lab.case_id(case)
                cached = self.journal.get(config, cid)
                if cached is None:
                    try:
                        cached = self.lab.score(wb, case, config, self.a)
                    except HarnessError as e:
                        cached = (float("-inf"), 0.0)
                        print("  error", cid, str(e)[:120])
                    if cached[0] != float("-inf"):
                        self.journal.put(config, cid, cached[0], cached[1])
                results[cid] = cached
                if baseline is not None and i + 1 < len(wanted):
                    diffs = [objective(results[k], lam) - objective(baseline[k], lam) for k in results if k in baseline]
                    if len(diffs) >= self.a.same_min and all(d == 0 for d in diffs):
                        return results, "no effect"
                    if (
                        len(diffs) >= self.a.race_min
                        and (i + 1) % self.a.race_every == 0
                        and sum(diffs) / len(diffs) < -self.a.race_margin
                    ):
                        return results, "raced out"
        finally:
            self.release(instance)
        return results, None

    def evaluate_many(self, configs, case_ids, baseline, lam):
        outcomes = [None] * len(configs)

        def work(index):
            outcomes[index] = self.evaluate(configs[index], case_ids, baseline, lam)

        threads = [threading.Thread(target=work, args=(i,), daemon=True) for i in range(len(configs))]
        for th in threads:
            th.start()
        for th in threads:
            th.join()
        return outcomes


def objective(entry, lam):
    score, seconds = entry
    return score - lam * seconds


def paired(candidate, baseline, lam):
    keys = [k for k in candidate if k in baseline]
    diffs = [objective(candidate[k], lam) - objective(baseline[k], lam) for k in keys]
    diffs = [d for d in diffs if d == d and abs(d) != float("inf")]
    if not diffs:
        return 0.0, 1.0, 0
    return sum(diffs) / len(diffs), bootstrap_p(diffs), len(diffs)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lab", choices=sorted(LABS), required=True)
    ap.add_argument("--instances", default="dev")
    ap.add_argument("--runs", default="")
    ap.add_argument("--characters", default="")
    ap.add_argument("--cases", type=int, default=40)
    ap.add_argument("--holdout", type=float, default=0.3)
    ap.add_argument("--max-turns", type=int, default=40)
    ap.add_argument("--bad", default="")
    ap.add_argument("--good", default="")
    ap.add_argument("--knobs", default="")
    ap.add_argument("--passes", type=int, default=2)
    ap.add_argument("--alpha", type=float, default=0.1)
    ap.add_argument("--lam", type=float, default=0.0)
    ap.add_argument("--race-every", type=int, default=8)
    ap.add_argument("--race-min", type=int, default=8)
    ap.add_argument("--same-min", type=int, default=4)
    ap.add_argument("--race-margin", type=float, default=30.0)
    ap.add_argument("--dry", action="store_true")
    a = ap.parse_args()
    lab = LABS[a.lab]
    cases = lab.cases(a)
    rng = random.Random(7)
    rng.shuffle(cases)
    split = int(len(cases) * (1 - a.holdout))
    tune_cases, hold_cases = cases[:split], cases[split:]
    tune_ids = {lab.case_id(c) for c in tune_cases}
    hold_ids = {lab.case_id(c) for c in hold_cases}
    tuning = load_tuning()
    instances = [x for x in a.instances.split(",") if x]
    wb = Harness(instances[0], timeout=3600)
    defaults = wb.call("wb.tune", {"reset": True})
    baseline = {**{k: v for k, v in lab.search.items()}, **{f"tune.{k}": v for k, v in tuning["knobs"].items()}}
    space = knob_space(defaults, lab.search, FROZEN)
    if a.knobs:
        wanted = set(a.knobs.split(","))
        space = {k: v for k, v in space.items() if k in wanted or k.removeprefix("tune.") in wanted}
    print(
        f"lab {a.lab}: {len(tune_cases)} tuning cases, {len(hold_cases)} holdout, {len(space)} knobs, baseline {baseline}"
    )
    if a.dry:
        for k, v in space.items():
            print(" ", k, v)
        return
    journal = Journal(a.lab)
    ev = Evaluator(lab, cases, instances, journal, a)
    t0 = time.time()
    base_scores, _ = ev.evaluate(baseline, tune_ids)
    accepted = []
    for _ in range(a.passes):
        improved = False
        for knob, values in space.items():
            current = baseline.get(knob, defaults.get(knob.removeprefix("tune."), lab.search.get(knob)))
            candidates = [{**baseline, knob: v} for v in values if v != current]
            outcomes = ev.evaluate_many(candidates, tune_ids, base_scores, a.lam)
            best = None
            for cfg, (res, raced) in zip(candidates, outcomes, strict=True):
                value = cfg[knob]
                if raced or len(res) < len(tune_ids):
                    print(f"  {knob}={value}: stopped after {len(res)} cases ({raced})")
                    continue
                mean, pval, n = paired(res, base_scores, a.lam)
                print(f"  {knob}={value}: mean {mean:+.1f} p {pval:.3f} n {n}")
                if mean > 0 and pval < a.alpha and (best is None or mean > best[1]):
                    best = (value, mean, pval, cfg, res)
            if best is None:
                continue
            value, mean, pval, cfg, res = best
            hold_base, _ = ev.evaluate(baseline, hold_ids)
            hold_new, _ = ev.evaluate(cfg, hold_ids)
            hmean, _, hn = paired(hold_new, hold_base, a.lam)
            verdict = "accept" if hmean >= 0 else "reject"
            print(
                f"* {knob}: {current} -> {value} tune {mean:+.1f} (p {pval:.3f}) holdout {hmean:+.1f} (n {hn}) {verdict}"
            )
            if hmean < 0:
                continue
            baseline = cfg
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
            if knob.startswith("tune."):
                tuning["knobs"][knob.removeprefix("tune.")] = value
                tuning["history"].append({**accepted[-1], "lab": a.lab, "at": metrics.now()})
                save_tuning(tuning)
        if not improved:
            break
    summary = {
        "lab": a.lab,
        "cases": len(tune_cases),
        "holdout": len(hold_cases),
        "knobs": len(space),
        "accepted": accepted,
        "final": baseline,
        "wallSeconds": round(time.time() - t0, 1),
        "replay": False,
    }
    metrics.record("tune", summary, {"space": space}, {})
    print("accepted", json.dumps(accepted, ensure_ascii=False))


if __name__ == "__main__":
    main()
