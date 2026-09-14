import argparse
import glob
import json
import os
import zlib

import numpy as np

import metrics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def act_of(floor):
    return 1 if floor <= 17 else 2 if floor <= 33 else 3


def loss_of(fl):
    combat = fl.get("combat")
    if not combat or fl.get("type") == "Boss":
        return None
    if not combat.get("won"):
        return fl.get("hpBefore") or 0
    return (fl.get("hpBefore") or 0) - (fl.get("hpAfter") or 0)


def future_loss(floors, start, horizon):
    losses = []
    for fl in floors[start + 1 :]:
        loss = loss_of(fl)
        if loss is not None:
            losses.append(loss)
        if len(losses) >= horizon:
            break
    return sum(losses) / len(losses) if len(losses) >= 3 else None


def samples(paths, character, horizon=6):
    out = []
    for path in paths:
        with open(path, encoding="utf-8") as f:
            run = json.load(f)
        if run.get("kind") != "run" or (character and run.get("character") != character):
            continue
        for case in (run.get("detail") or run).get("cases", []):
            floors = case.get("floorsDetail") or []
            for i, fl in enumerate(floors):
                party = fl.get("party")
                if not party or "combat" not in fl:
                    continue
                target = future_loss(floors, i, horizon)
                if target is None:
                    continue
                me = party[0]
                act = act_of(fl["floor"])
                feats = {
                    "bias": 1.0,
                    "act": act,
                    "actFloor": fl.get("actFloor", 0) / 17,
                    "hpFrac": me["hp"] / max(1, me["maxHp"]),
                    "deckSize": len(me["deck"]) / 20,
                }
                feats["upgrades"] = sum(1 for c in me["deck"] if c.get("upgrade")) / 5
                for c in me["deck"]:
                    feats["deck:" + c["id"]] = feats.get("deck:" + c["id"], 0) + 1
                for r in me["relics"]:
                    rid = r if isinstance(r, str) else r["id"]
                    feats["relic:" + rid] = 1
                feats["char:" + me["character"]] = 1
                out.append((run["id"], run["character"], feats, target))
    return out


def fit(rows, lam_num, lam_cat, min_support):
    support = {}
    for _, _, f, _ in rows:
        for n in f:
            support[n] = support.get(n, 0) + 1
    names = sorted(n for n, c in support.items() if c >= min_support or ":" not in n)
    index = {n: j for j, n in enumerate(names)}
    x = np.zeros((len(rows), len(names)))
    y = np.zeros(len(rows))
    for i, (_, _, f, label) in enumerate(rows):
        y[i] = label
        for n, v in f.items():
            j = index.get(n)
            if j is not None:
                x[i, j] = v
    lam = np.array(
        [0.0 if n == "bias" else lam_num if ":" not in n else lam_cat * max(1.0, 30.0 / support[n]) for n in names]
    )
    w = np.linalg.solve(x.T @ x + np.diag(lam), x.T @ y)
    return names, w, support


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", default="")
    ap.add_argument("--lambda-num", type=float, default=1.0)
    ap.add_argument("--lambda-cat", type=float, default=4.0)
    ap.add_argument("--min-support", type=int, default=8)
    ap.add_argument("--write", default="")
    ap.add_argument("--top", type=int, default=15)
    a = ap.parse_args()
    paths = [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(ROOT, "metrics", "runs", "*-run-*.json"))
    )
    table = {}
    report = {}
    for character in ("IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"):
        rows = samples(paths, character)
        if len(rows) < 100:
            continue
        holds = []
        for hold in range(3):
            train = [r for r in rows if zlib.crc32(r[0].encode()) % 3 != hold]
            test = [r for r in rows if zlib.crc32(r[0].encode()) % 3 == hold]
            if len(train) < 50 or not test:
                continue
            names, w, _ = fit(train, a.lambda_num, a.lambda_cat, a.min_support)
            index = {n: j for j, n in enumerate(names)}
            base = float(np.mean([r[3] for r in train]))
            sq = []
            sq_base = []
            for _, _, f, target in test:
                pred = sum(w[index[n]] * v for n, v in f.items() if n in index)
                sq.append((pred - target) ** 2)
                sq_base.append((base - target) ** 2)
            holds.append((float(np.sqrt(np.mean(sq))), float(np.sqrt(np.mean(sq_base)))))
        names, w, support = fit(rows, a.lambda_num, a.lambda_cat, a.min_support)
        cards = {
            n[5:]: {"prior": round(-float(w[j]), 4), "support": support[n]}
            for j, n in enumerate(names)
            if n.startswith("deck:")
        }
        table[character] = cards
        report[character] = {
            "rows": len(rows),
            "meanLoss": round(float(np.mean([r[3] for r in rows])), 3),
            "rmse": round(float(np.mean([h[0] for h in holds])), 3) if holds else None,
            "rmseBase": round(float(np.mean([h[1] for h in holds])), 3) if holds else None,
        }
        ranked = sorted(cards.items(), key=lambda kv: -kv[1]["prior"])
        print(character, report[character])
        print("  top", [(k, v["prior"]) for k, v in ranked[: a.top]])
        print("  bottom", [(k, v["prior"]) for k, v in ranked[-a.top :]])
    if a.write:
        with open(a.write, "w", encoding="utf-8") as f:
            json.dump(table, f, ensure_ascii=False, indent=1, sort_keys=True)
        print("wrote", a.write)
    metrics.record("deckvalue", {"report": report, "replay": False}, None, {})


if __name__ == "__main__":
    main()
