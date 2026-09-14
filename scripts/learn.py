import argparse
import glob
import hashlib
import json
import os
import zlib

import numpy as np

import metrics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PHASES = ("start", "leaf")


def load(paths):
    fights = {}
    rows = []
    meta = {}
    for path in paths:
        names = {}
        run = None
        with open(path, encoding="utf-8") as f:
            for line in f:
                r = json.loads(line)
                k = r["k"]
                if k == "run":
                    run = r["run"]
                    meta.setdefault("game", r.get("game"))
                    meta.setdefault("mod", r.get("mod"))
                elif k == "name":
                    names[r["i"]] = r["n"]
                elif k == "fight":
                    fights[(run, r["fight"])] = {**r, "run": run}
                elif k == "end":
                    fights[(run, r["fight"])].update(r)
                elif k == "x":
                    rows.append(
                        {
                            "run": run,
                            "fight": r["fight"],
                            "turn": r["turn"],
                            "phase": r["phase"],
                            "f": {names[i]: v for i, v in zip(r["i"], r["v"], strict=True)},
                            "extra": r.get("extra"),
                        }
                    )
    return fights, rows, meta


def fold(run):
    return zlib.crc32(run.encode()) % 3


def matrix(rows, names, index):
    x = np.zeros((len(rows), len(names)))
    for i, r in enumerate(rows):
        for name, v in r["f"].items():
            j = index.get(name)
            if j is not None:
                x[i, j] = v
    return x


def ridge(x, y, lam):
    a = x.T @ x + np.diag(lam)
    b = x.T @ y
    return np.linalg.solve(a, b)


def logistic(x, y, lam, iterations=12):
    w = np.zeros(x.shape[1])
    for _ in range(iterations):
        z = x @ w
        p = 1 / (1 + np.exp(-np.clip(z, -30, 30)))
        s = np.clip(p * (1 - p), 1e-6, None)
        a = x.T @ (x * s[:, None]) + np.diag(lam)
        g = x.T @ (y - p) - lam * w
        w = w + np.linalg.solve(a, g)
    return w


def standardize(x, numeric):
    mu = np.zeros(x.shape[1])
    sigma = np.ones(x.shape[1])
    for j in numeric:
        col = x[:, j]
        mu[j] = col.mean()
        sd = col.std()
        sigma[j] = sd if sd > 1e-9 else 1.0
    return (x - mu) / sigma, mu, sigma


def bake(w, mu, sigma):
    raw = w / sigma
    bias = -float(np.sum(w * mu / sigma))
    return raw, bias


def fit_phase(rows, fights, a, hold=None):
    labeled = []
    for r in rows:
        fight = fights.get((r["run"], r["fight"]))
        if fight is None or "won" not in fight or fight.get("truncated"):
            continue
        if hold is not None and (fold(r["run"]) == hold) != a.holdout_side:
            continue
        labeled.append((r, fight))
    if len(labeled) < 50:
        return None
    support = {}
    for r, _ in labeled:
        for name in r["f"]:
            support[name] = support.get(name, 0) + 1
    names = sorted(n for n, c in support.items() if c >= a.min_support and not (a.phase_mask and n.startswith("hand:")))
    index = {n: j for j, n in enumerate(names)}
    numeric = [j for j, n in enumerate(names) if ":" not in n]
    x = matrix([r for r, _ in labeled], names, index)
    x = np.hstack([x, np.ones((x.shape[0], 1))])
    xs, mu, sigma = standardize(x, numeric)
    lam = np.array(
        [
            a.lambda_num if j in set(numeric) else a.lambda_cat * max(1.0, 50.0 / support[names[j]])
            for j in range(len(names))
        ]
        + [1e-6]
    )
    y_win = np.array([1.0 if f["won"] else 0.0 for _, f in labeled])
    hp = np.array([r["f"].get("hp", 0.0) for r, _ in labeled])
    hp_end = np.array([f["hpEnd"] for _, f in labeled])
    wp = logistic(xs, y_win, lam)
    won = y_win > 0.5
    wh = ridge(xs[won], (hp - hp_end)[won], lam) if won.sum() > 10 else np.zeros(xs.shape[1])
    wp_raw, bp = bake(wp[:-1], mu[:-1], sigma[:-1])
    wh_raw, bh = bake(wh[:-1], mu[:-1], sigma[:-1])
    return {
        "names": names,
        "wp": wp_raw.tolist(),
        "bp": bp + float(wp[-1]),
        "wh": wh_raw.tolist(),
        "bh": bh + float(wh[-1]),
        "rows": len(labeled),
        "won": int(won.sum()),
    }


def predict(model, feats):
    zp = model["bp"]
    zh = model["bh"]
    index = model.setdefault("index", {n: j for j, n in enumerate(model["names"])})
    for name, v in feats.items():
        j = index.get(name)
        if j is not None:
            zp += model["wp"][j] * v
            zh += model["wh"][j] * v
    p = 1 / (1 + np.exp(-np.clip(zp, -30, 30)))
    return p, zh


def value(model, feats, loss_hp):
    p, h = predict(model, feats)
    hp = feats.get("hp", 0.0)
    return p * (hp - min(max(h, 0.0), hp)) + (1 - p) * (-loss_hp)


def evaluate(model, rows, fights, hold, a):
    brier = []
    base = []
    sq = []
    fight_res = {}
    for r in rows:
        fight = fights.get((r["run"], r["fight"]))
        if fight is None or "won" not in fight or fight.get("truncated") or fold(r["run"]) != hold:
            continue
        p, h = predict(model, r["f"])
        y = 1.0 if fight["won"] else 0.0
        brier.append((p - y) ** 2)
        base.append(y)
        if fight["won"]:
            err = (r["f"].get("hp", 0.0) - fight["hpEnd"]) - h
            sq.append(err**2)
            fight_res.setdefault((r["run"], r["fight"]), []).append((r["f"].get("hp", 0.0) - fight["hpEnd"], h))
    rate = float(np.mean(base)) if base else 0.0
    within = []
    for pairs in fight_res.values():
        if len(pairs) < 2:
            continue
        ys = np.array([t for t, _ in pairs])
        hs = np.array([h for _, h in pairs])
        ss_tot = float(np.sum((ys - ys.mean()) ** 2))
        if ss_tot > 0:
            within.append(1 - float(np.sum(((ys - ys.mean()) - (hs - hs.mean())) ** 2)) / ss_tot)
    return {
        "rows": len(brier),
        "brier": round(float(np.mean(brier)), 4) if brier else None,
        "brierBase": round(rate * (1 - rate), 4),
        "rmseLoss": round(float(np.sqrt(np.mean(sq))), 2) if sq else None,
        "withinFightR2": round(float(np.mean(within)), 3) if within else None,
    }


def beam_accuracy(model, rows, fights, hold, a):
    groups = {}
    for r in rows:
        if r["phase"] != "beam" or fold(r["run"]) != hold or r["extra"] is None:
            continue
        fight = fights.get((r["run"], r["fight"]))
        if fight is None or "won" not in fight:
            continue
        groups.setdefault((r["run"], r["fight"], r["turn"]), []).append(r)
    learned = hand = total = 0
    for members in groups.values():
        for i in range(len(members)):
            for j in range(i + 1, len(members)):
                ri, rj = members[i], members[j]
                real_i, real_j = ri["extra"]["real"], rj["extra"]["real"]
                if real_i == real_j or ri["extra"]["terminal"] or rj["extra"]["terminal"]:
                    continue
                target = np.sign(real_i - real_j)
                vi = value(model, ri["f"], a.loss_hp)
                vj = value(model, rj["f"], a.loss_hp)
                total += 1
                learned += np.sign(vi - vj) == target
                hand += np.sign(ri["extra"]["est"] - rj["extra"]["est"]) == target
    return {
        "pairs": total,
        "learned": round(learned / total, 3) if total else None,
        "hand": round(hand / total, 3) if total else None,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--records", default="")
    ap.add_argument("--tags", default="")
    ap.add_argument("--out", default=os.path.join(ROOT, "metrics", "value.candidate.json"))
    ap.add_argument("--lambda-num", type=float, default=10.0)
    ap.add_argument("--lambda-cat", type=float, default=30.0)
    ap.add_argument("--min-support", type=int, default=20)
    ap.add_argument("--loss-hp", type=float, default=60.0)
    ap.add_argument("--holdout-side", action="store_true", default=False)
    a = ap.parse_args()
    paths = [p for spec in a.records.split(",") if spec for p in glob.glob(spec)]
    for tag in [t for t in a.tags.split(",") if t]:
        paths += glob.glob(os.path.join(ROOT, ".local", "batches", tag, "records", "*.jsonl"))
    if not paths:
        raise SystemExit("no record files")
    fights, rows, meta = load(paths)
    runs = sorted({r["run"] for r in rows})
    print(f"{len(paths)} files, {len(fights)} fights, {len(rows)} rows, {len(runs)} runs")
    report = {}
    for phase in PHASES:
        phase_rows = [r for r in rows if r["phase"] == phase]
        a.phase_mask = phase == "leaf"
        folds = {}
        for hold in range(3):
            model = fit_phase(phase_rows, fights, a, hold)
            if model is None:
                continue
            folds[hold] = evaluate(model, phase_rows, fights, hold, a)
            if phase == "leaf":
                folds[hold]["beam"] = beam_accuracy(model, rows, fights, hold, a)
        full = fit_phase(phase_rows, fights, a)
        report[phase] = {"folds": folds, "model": full}
        print(phase, json.dumps({k: v for k, v in folds.items()}, ensure_ascii=False))
    models = {
        p: {k: v for k, v in report[p]["model"].items() if k != "index"}
        for p in PHASES
        if report.get(p, {}).get("model")
    }
    if not models:
        raise SystemExit("not enough labeled rows")
    payload = {
        "game": meta.get("game"),
        "mod": meta.get("mod"),
        "lossHp": a.loss_hp,
        "trainedRuns": runs,
        "phases": models,
    }
    digest = hashlib.sha1(json.dumps(payload, sort_keys=True).encode()).hexdigest()[:12]
    payload["hash"] = digest
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(payload, f)
    summary = {
        "files": len(paths),
        "fights": len(fights),
        "rows": len(rows),
        "runs": len(runs),
        "hash": digest,
        "metrics": {p: report[p]["folds"] for p in report},
        "names": {p: len(models[p]["names"]) for p in models},
        "replay": False,
    }
    metrics.record("learn", summary, None, {"game": meta.get("game"), "mod": meta.get("mod")})
    print("wrote", a.out, digest, {p: len(models[p]["names"]) for p in models})


if __name__ == "__main__":
    main()
