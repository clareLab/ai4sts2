import argparse
import glob
import hashlib
import itertools
import json
import os
import queue
import random
import threading
import time
import zlib

import bosslab
import tuning
from harness import Harness, HarnessError

import metrics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STATE_DIR = os.path.join(ROOT, ".local", "tune")
TYPES = ("Boss", "Elite", "Monster")
KEYS = ("won", "hp", "turns", "nodes")


def case_id(case):
    return f"{case['run']}:{case['floor']}"


def act_of(floor):
    return 1 if floor <= 17 else 2 if floor <= 33 else 3


def stratum(case):
    return f"{case['type']}/A{act_of(case['floor'])}"


def seed_of(case, salt):
    return case["seed"] if salt == 0 else f"{case['seed']}~{salt}"


def config_hash(config):
    return hashlib.sha1(json.dumps(config, sort_keys=True).encode()).hexdigest()[:12]


def margin(cell, loss_weight):
    return cell["hp"] if cell["won"] else -loss_weight * cell["remaining"]


def p_signflip(diffs, rng_seed=1):
    nz = [d for d in diffs if d != 0]
    if not nz:
        return 1.0
    total = sum(nz)
    if len(nz) <= 18:
        hits = 0
        for signs in itertools.product((1, -1), repeat=len(nz)):
            if sum(s * abs(d) for s, d in zip(signs, nz, strict=True)) >= total:
                hits += 1
        return hits / 2 ** len(nz)
    rng = random.Random(rng_seed)
    hits = 0
    for _ in range(20000):
        if sum(abs(d) if rng.random() < 0.5 else -abs(d) for d in nz) >= total:
            hits += 1
    return hits / 20000


def sample_cases(pool, sizes):
    chosen = []
    for kind in TYPES:
        by_act = {}
        for case in pool:
            if case["type"] == kind:
                by_act.setdefault(act_of(case["floor"]), []).append(case)
        if not by_act:
            continue
        per_act = max(1, sizes[kind] // len(by_act))
        for act in sorted(by_act):
            groups = {}
            for case in sorted(by_act[act], key=lambda c: c["run"], reverse=True):
                groups.setdefault(case["character"], {}).setdefault(case["encounter"], []).append(case)
            picked = []
            while len(picked) < per_act and any(any(g.values()) for g in groups.values()):
                for character in sorted(groups):
                    encounters = groups[character]
                    key = next((e for e in encounters if encounters[e]), None)
                    if key is None:
                        continue
                    picked.append(encounters[key].pop(0))
                    encounters[key] = encounters.pop(key)
                    if len(picked) >= per_act:
                        break
            chosen.extend(picked)
    return chosen


def step1(value):
    if isinstance(value, bool):
        return [not value]
    if value == 0:
        return [1, 4]
    if abs(value) <= 6:
        return sorted({max(1, value - 1), value + 1} - {value})
    return sorted({round(value * 0.7), round(value * 1.4)} - {value})


def extend(value, candidate, depth):
    if isinstance(value, bool) or value == 0:
        return None
    up = candidate > value
    if abs(value) <= 6:
        result = candidate + (1 + depth) * (1 if up else -1)
        return result if result >= 1 and result != candidate else None
    factor = (2.0, 2.8)[depth] if up else (0.5, 0.35)[depth]
    result = round(value * factor)
    return result if result != candidate and result >= 0 else None


class Cells:
    def __init__(self, fingerprint):
        os.makedirs(STATE_DIR, exist_ok=True)
        self.path = os.path.join(STATE_DIR, "cells.jsonl")
        self.fingerprint = fingerprint
        self.rows = {}
        self.lock = threading.Lock()
        if os.path.exists(self.path):
            with open(self.path, encoding="utf-8") as f:
                for line in f:
                    row = json.loads(line)
                    if row["f"] == fingerprint:
                        self.rows[(row["case"], row["salt"], row["cfg"])] = row["cell"]

    def get(self, cid, salt, cfg):
        return self.rows.get((cid, salt, cfg))

    def put(self, cid, salt, cfg, cell):
        with self.lock:
            self.rows[(cid, salt, cfg)] = cell
            with open(self.path, "a", encoding="utf-8") as f:
                f.write(json.dumps({"f": self.fingerprint, "case": cid, "salt": salt, "cfg": cfg, "cell": cell}) + "\n")


class Inert:
    def __init__(self, fingerprint):
        self.path = os.path.join(STATE_DIR, "knobs.json")
        self.fingerprint = fingerprint
        self.lock = threading.Lock()
        self.data = {}
        if os.path.exists(self.path):
            with open(self.path, encoding="utf-8") as f:
                self.data = json.load(f)

    def __contains__(self, knob):
        return self.data.get(self.fingerprint, {}).get(knob) == "inert"

    def mark(self, knob):
        with self.lock:
            self.data.setdefault(self.fingerprint, {})[knob] = "inert"
            with open(self.path, "w", encoding="utf-8") as f:
                json.dump(self.data, f, indent=1, sort_keys=True)


class Runner:
    def __init__(self, a, cases, cells, instances):
        self.a = a
        self.cases = {case_id(c): c for c in cases}
        self.cells = cells
        self.pool = queue.Queue()
        for name in instances:
            self.pool.put(name)
        self.clients = {}
        self.snapshots = False

    def borrow(self):
        name = self.pool.get()
        if name not in self.clients:
            self.clients[name] = Harness(name, timeout=3600)
        return name, self.clients[name]

    def cell(self, wb, cid, salt, config, snap_id=None):
        cached = self.cells.get(cid, salt, config_hash(config))
        if cached is not None:
            return cached
        case = self.cases[cid]
        if snap_id is None:
            bosslab.setup(wb, case, seed_of(case, salt))
        else:
            wb.call("wb.restore", {"id": snap_id})
        wb.call("wb.tune", {"reset": True, **config})
        cell = bosslab.fight(wb, case, self.a.max_turns)
        self.cells.put(cid, salt, config_hash(config), cell)
        return cell

    def evaluate(self, configs, pairs, on_block=None):
        results = [{} for _ in configs]
        live = list(range(len(configs)))
        name, wb = self.borrow()
        try:
            for i, (cid, salt) in enumerate(pairs):
                need = [k for k in live if self.cells.get(cid, salt, config_hash(configs[k])) is None]
                snap_id = None
                if len(need) > 1 and self.snapshots:
                    case = self.cases[cid]
                    bosslab.setup(wb, case, seed_of(case, salt))
                    snap_id = wb.call("wb.snap")["id"]
                for k in live:
                    try:
                        results[k][(cid, salt)] = self.cell(wb, cid, salt, configs[k], snap_id if k in need else None)
                    except HarnessError as e:
                        print("  error", cid, salt, str(e)[:120])
                if on_block and (i + 1) % (self.a.block * self.a.salts) == 0:
                    live = [k for k in live if not on_block(k, results[k])]
                    if not live:
                        break
        finally:
            self.pool.put(name)
        return results


def diffs_of(candidate, baseline, loss_weight):
    return {u: margin(candidate[u], loss_weight) - margin(baseline[u], loss_weight) for u in candidate if u in baseline}


def nodes_ratio(candidate, baseline):
    keys = [u for u in candidate if u in baseline]
    return sum(candidate[u]["nodes"] for u in keys) / max(1, sum(baseline[u]["nodes"] for u in keys))


def wins_delta(candidate, baseline, keys):
    return sum(candidate[u]["won"] for u in keys) - sum(baseline[u]["won"] for u in keys)


def screen(candidate, baseline, a, min_effect, strata):
    d = diffs_of(candidate, baseline, a.loss_weight)
    if not d:
        return None
    values = list(d.values())
    nz = [x for x in values if x != 0]
    mean = sum(values) / len(values)
    ratio = nodes_ratio(candidate, baseline)
    for keys in strata.values():
        present = [u for u in keys if u in d]
        if present and (p_signflip([-d[u] for u in present]) < a.alpha or wins_delta(candidate, baseline, present) < 0):
            return None
    p = p_signflip(values)
    if p < a.alpha and len(nz) >= 6 and mean >= min_effect and ratio <= 1.15:
        return {"kind": "quality", "mean": mean, "p": p, "nz": len(nz), "ratio": ratio}
    if ratio <= 0.8 and mean > -min_effect:
        return {"kind": "speedup", "mean": mean, "p": p, "nz": len(nz), "ratio": ratio}
    return None


def load_header(a, fingerprint, instances):
    path = os.path.join(STATE_DIR, f"{a.tag}.json")
    os.makedirs(STATE_DIR, exist_ok=True)
    if os.path.exists(path) and not a.fresh:
        with open(path, encoding="utf-8") as f:
            header = json.load(f)
        if header["fingerprint"] != fingerprint:
            raise SystemExit(f"{path} was made for another build; pass --fresh")
        return header
    paths = [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(ROOT, "metrics", "runs", "*-run-*.json"))
    )
    pool = [c for c in bosslab.collect(paths, set(TYPES), 0, 0) if len(c["party"]) == 1]
    cases = sample_cases(pool, {"Boss": a.boss, "Elite": a.elite, "Monster": a.monster})
    counts = [sum(1 for c in cases if zlib.crc32(c["run"].encode()) % 3 == k) for k in range(3)]
    rotation = sum(1 for row in metrics.load_index() if row.get("kind") == "tune")
    ranked = sorted(range(3), key=lambda k: (abs(counts[k] - len(cases) / 3), (k - rotation) % 3))
    confirm_fold = ranked[0]
    header = {"fingerprint": fingerprint, "cases": cases, "confirmFold": confirm_fold, "salts": a.salts}
    with open(path, "w", encoding="utf-8") as f:
        json.dump(header, f)
    return header


def verify_snapshots(runner, baseline, probe, a):
    fresh = runner.evaluate([baseline], probe)[0]
    again = {}
    for cid, salt in probe:
        case = runner.cases[cid]
        name, wb = runner.borrow()
        try:
            bosslab.setup(wb, case, seed_of(case, salt))
            snap_id = wb.call("wb.snap")["id"]
            wb.call("wb.restore", {"id": snap_id})
            wb.call("wb.tune", {"reset": True, **baseline})
            again[(cid, salt)] = bosslab.fight(wb, case, a.max_turns)
        finally:
            runner.pool.put(name)
    return all(tuple(fresh[u][k] for k in KEYS) == tuple(again[u][k] for k in KEYS) for u in probe if u in fresh)


def noise_floor(cases, base, a):
    spreads = []
    for c in cases:
        u0, u1 = (case_id(c), 0), (case_id(c), 1)
        if u0 in base and u1 in base:
            spreads.append(margin(base[u0], a.loss_weight) - margin(base[u1], a.loss_weight))
    if len(spreads) < 2:
        return 0.0
    mean = sum(spreads) / len(spreads)
    sd = (sum((x - mean) ** 2 for x in spreads) / (len(spreads) - 1)) ** 0.5
    return sd / len(spreads) ** 0.5


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--instances", default="dev")
    ap.add_argument("--tag", default="tune")
    ap.add_argument("--runs", default="")
    ap.add_argument("--salts", type=int, default=2)
    ap.add_argument("--boss", type=int, default=18)
    ap.add_argument("--elite", type=int, default=18)
    ap.add_argument("--monster", type=int, default=36)
    ap.add_argument("--max-turns", type=int, default=40)
    ap.add_argument("--loss-weight", type=float, default=50.0)
    ap.add_argument("--alpha", type=float, default=0.1)
    ap.add_argument("--nominate", type=int, default=5)
    ap.add_argument("--block", type=int, default=8)
    ap.add_argument("--knobs", default="")
    ap.add_argument("--fresh", action="store_true")
    ap.add_argument("--no-write", action="store_true")
    ap.add_argument("--dry", action="store_true")
    a = ap.parse_args()
    instances = [x for x in a.instances.split(",") if x]
    wb = Harness(instances[0], timeout=3600)
    ping = wb.call("ping")
    built = metrics.build_info().get("modBuilt")
    stamp = f"{ping.get('game')}|{ping.get('mod')}|{ping.get('patches')}|{built}"
    fingerprint = hashlib.sha1(stamp.encode()).hexdigest()[:12]
    factory = wb.call("wb.tune", {"reset": True})["tuning"]
    baseline = tuning.apply(wb)["tuning"]
    header = load_header(a, fingerprint, instances)
    cases = header["cases"]
    confirm_fold = header["confirmFold"]
    fold = {case_id(c): zlib.crc32(c["run"].encode()) % 3 for c in cases}
    pairs = [(case_id(c), s) for c in cases for s in range(a.salts)]
    tune_pairs = [u for u in pairs if fold[u[0]] != confirm_fold]
    hold_pairs = [u for u in pairs if fold[u[0]] == confirm_fold]
    strata_t, strata_h = {}, {}
    for c in cases:
        for s in range(a.salts):
            u = (case_id(c), s)
            (strata_h if fold[u[0]] == confirm_fold else strata_t).setdefault(stratum(c), []).append(u)
    inert = Inert(fingerprint)
    space = [k for k in factory if k not in inert]
    if a.knobs:
        wanted = set(a.knobs.split(","))
        space = [k for k in space if k in wanted]
    counts = {t: sum(1 for c in cases if c["type"] == t) for t in TYPES}
    deltas0 = {k: v for k, v in baseline.items() if v != factory[k]}
    print(
        f"{a.tag}: build {fingerprint}, cases {counts}, {len(tune_pairs)} tuning pairs, {len(hold_pairs)} confirm pairs (fold {confirm_fold}), {len(space)} knobs, deltas {deltas0}"
    )
    if a.dry:
        for k in space:
            print(" ", k, baseline[k], "->", step1(baseline[k]))
        return
    t0 = time.time()
    runner = Runner(a, cases, Cells(fingerprint), instances)
    runner.snapshots = verify_snapshots(runner, baseline, tune_pairs[:3], a)
    print(f"snapshot reuse {'verified' if runner.snapshots else 'differs; using fresh setups'}")
    chunks = [pairs[i :: len(instances)] for i in range(len(instances))]
    parts = [None] * len(chunks)

    def base_work(i):
        parts[i] = runner.evaluate([baseline], chunks[i])[0]

    threads = [threading.Thread(target=base_work, args=(i,), daemon=True) for i in range(len(chunks))]
    for th in threads:
        th.start()
    for th in threads:
        th.join()
    base_t, base_h = {}, {}
    for part in parts:
        for u, cell in part.items():
            (base_h if fold[u[0]] == confirm_fold else base_t)[u] = cell
    noise = noise_floor(cases, {**base_t, **base_h}, a) if a.salts >= 2 else 0.0
    min_effect = max(0.5, noise)
    print(f"baseline done: noise floor {noise:.2f}, min effect {min_effect:.2f}, {round(time.time() - t0)} s")

    def race_rule(candidates):
        def on_block(k, results):
            d = diffs_of(results, base_t, a.loss_weight)
            values = list(d.values())
            nz = [x for x in values if x != 0]
            same = all(tuple(results[u][key] for key in KEYS) == tuple(base_t[u][key] for key in KEYS) for u in d)
            if len(values) >= 3 * a.salts and same:
                candidates[k]["stop"] = "inert"
            elif len(nz) >= 4 and sum(values) / len(values) <= 0:
                candidates[k]["stop"] = "negative"
            elif len(nz) >= 8 and p_signflip(values) > 0.5:
                candidates[k]["stop"] = "p>0.5"
            elif len(nz) >= 8 and nodes_ratio(results, base_t) > 1.15 and sum(values) / len(values) < min_effect:
                candidates[k]["stop"] = "slower"
            return candidates[k]["stop"] is not None

        return on_block

    nominees = []
    report = {}
    lock = threading.Lock()

    def sweep(knob):
        current = baseline[knob]
        tried = {}
        frontier = step1(current)
        depth = 0
        while frontier:
            candidates = [{"value": v, "config": {**baseline, knob: v}, "stop": None} for v in frontier]
            results = runner.evaluate([c["config"] for c in candidates], tune_pairs, race_rule(candidates))
            for cand, res in zip(candidates, results, strict=True):
                verdict = None if cand["stop"] else screen(res, base_t, a, min_effect, strata_t)
                tried[cand["value"]] = {"stop": cand["stop"], "screen": verdict, "results": res}
            passing = [(v, t) for v, t in tried.items() if t["screen"] and v in frontier]
            if not passing or depth >= 2:
                break
            best_v = max(passing, key=lambda vt: vt[1]["screen"]["mean"])[0]
            nxt = extend(current, best_v, depth)
            frontier = [nxt] if nxt is not None and nxt not in tried else []
            depth += 1
        with lock:
            report[knob] = {
                str(v): {"stop": t["stop"], "screen": t["screen"], "n": len(t["results"])} for v, t in tried.items()
            }
            for v, t in tried.items():
                s = t["screen"]
                line = t["stop"] or (
                    f"{s['kind']} mean {s['mean']:+.2f} p {s['p']:.3f} nz {s['nz']} nodes x{s['ratio']:.2f}"
                    if s
                    else "no"
                )
                print(f"  {knob}={v}: {line}")
            if tried and all(t["stop"] == "inert" for t in tried.values()):
                inert.mark(knob)
            for v, t in tried.items():
                if t["screen"]:
                    nominees.append({"knob": knob, "from": current, "to": v, **t["screen"]})

    work = queue.Queue()
    order = list(space)
    random.Random(a.tag).shuffle(order)
    for knob in order:
        work.put(knob)

    def worker():
        while True:
            try:
                knob = work.get_nowait()
            except queue.Empty:
                return
            try:
                sweep(knob)
            except HarnessError as e:
                print(f"  {knob}: error {str(e)[:120]}")

    threads = [threading.Thread(target=worker, daemon=True) for _ in instances]
    for th in threads:
        th.start()
    for th in threads:
        th.join()
    nominees.sort(key=lambda n: (n["kind"] != "quality", -n["mean"]))
    shortlist = []
    for n in nominees:
        if all(m["knob"] != n["knob"] for m in shortlist):
            shortlist.append(n)
    shortlist = shortlist[: a.nominate]
    confirmed = []
    for n in shortlist:
        res = runner.evaluate([{**baseline, n["knob"]: n["to"]}], hold_pairs)[0]
        verdict = screen(res, base_h, a, min_effect, strata_h)
        d = diffs_of(res, base_h, a.loss_weight)
        hold_mean = sum(d.values()) / len(d) if d else 0.0
        print(
            f"* {n['knob']}: {n['from']} -> {n['to']} tune {n['mean']:+.2f} holdout {hold_mean:+.2f} {'confirmed' if verdict else 'rejected'}"
        )
        if verdict:
            confirmed.append({**n, "holdout": round(hold_mean, 3)})
    joint = {**baseline, **{n["knob"]: n["to"] for n in confirmed}}
    if len(confirmed) > 1:
        res = runner.evaluate([joint], hold_pairs)[0]
        if (
            sum(diffs_of(res, base_h, a.loss_weight).values()) < 0
            or screen(res, base_h, a, min_effect, strata_h) is None
        ):
            joint = dict(baseline)
            for n in sorted(confirmed, key=lambda n: -n["holdout"]):
                trial = {**joint, n["knob"]: n["to"]}
                res = runner.evaluate([trial], hold_pairs)[0]
                if sum(diffs_of(res, base_h, a.loss_weight).values()) >= 0:
                    joint = trial
    deltas = {k: v for k, v in joint.items() if v != factory[k]}
    if confirmed and not a.no_write:
        tuning.save(deltas)
    summary = {
        "tag": a.tag,
        "fingerprint": fingerprint,
        "cases": len(cases),
        "salts": a.salts,
        "confirmFold": confirm_fold,
        "noise": round(noise, 3),
        "minEffect": round(min_effect, 3),
        "knobs": len(space),
        "inert": sorted(k for k in space if k in inert),
        "nominees": shortlist,
        "confirmed": confirmed,
        "deltas": deltas,
        "wallSeconds": round(time.time() - t0, 1),
        "replay": False,
    }
    metrics.record(
        "tune", summary, {"report": report, "cases": cases}, {"game": ping.get("game"), "mod": ping.get("mod")}
    )
    print("confirmed", json.dumps(confirmed, ensure_ascii=False), "deltas", json.dumps(deltas))


if __name__ == "__main__":
    main()
