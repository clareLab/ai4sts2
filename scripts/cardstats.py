import argparse
import collections
import glob
import json
import os


def floors_of(run):
    for case in (run.get("detail") or run).get("cases", []):
        yield case, case.get("floorsDetail") or []


def fmt(v):
    return f"{v:6.1f}" if v is not None else "     -"


def loss_of(fl):
    combat = fl.get("combat")
    if not combat or fl.get("type") == "Boss":
        return None
    if not combat.get("won"):
        return fl.get("hpBefore") or 0
    return (fl.get("hpBefore") or 0) - (fl.get("hpAfter") or 0)


def window(floors, indices, horizon):
    losses = []
    for i in indices:
        loss = loss_of(floors[i])
        if loss is not None:
            losses.append(loss)
        if len(losses) >= horizon:
            break
    return sum(losses) / len(losses) if losses else None


def shift(floors, i, horizon):
    after = window(floors, range(i + 1, len(floors)), horizon)
    before = window(floors, range(i, -1, -1), horizon)
    return None if after is None else after - (before or 0)


def gather(paths, character, horizon):
    offered = collections.Counter()
    taken = collections.Counter()
    shift_taken = collections.defaultdict(list)
    shift_passed = collections.defaultdict(list)
    reach = collections.defaultdict(list)
    for path in paths:
        with open(path, encoding="utf-8") as f:
            run = json.load(f)
        if run.get("kind") != "run" or (character and run.get("character") != character):
            continue
        for case, floors in floors_of(run):
            for i, fl in enumerate(floors):
                ev = fl.get("evaluation")
                if not ev or fl.get("room") != "CombatRoom":
                    continue
                delta = shift(floors, i, horizon)
                for o in ev["options"]:
                    if o.get("card") is None:
                        continue
                    key = (run["character"], o["label"])
                    offered[key] += 1
                    if o["label"] == ev["best"]:
                        taken[key] += 1
                        reach[key].append(case.get("floors", 0))
                        if delta is not None:
                            shift_taken[key].append(delta)
                    elif delta is not None:
                        shift_passed[key].append(delta)
    return offered, taken, shift_taken, shift_passed, reach


def priors(offered, taken, shift_taken, shift_passed, shrink):
    table = collections.defaultdict(dict)
    for key in offered:
        t = shift_taken[key]
        p = shift_passed[key]
        if not t or not p:
            continue
        n = min(len(t), len(p))
        raw = (sum(p) / len(p)) - (sum(t) / len(t))
        table[key[0]][key[1]] = {
            "offered": offered[key],
            "taken": taken[key],
            "prior": round(raw * n / (n + shrink), 3),
        }
    return table


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", default="")
    ap.add_argument("--character", default="")
    ap.add_argument("--horizon", type=int, default=6)
    ap.add_argument("--min", type=int, default=4)
    ap.add_argument("--shrink", type=int, default=8)
    ap.add_argument("--write", default="")
    a = ap.parse_args()
    paths = [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(os.path.dirname(__file__), "..", "metrics", "runs", "*-run-*.json"))
    )
    offered, taken, shift_taken, shift_passed, reach = gather(paths, a.character, a.horizon)
    table = priors(offered, taken, shift_taken, shift_passed, a.shrink)
    if a.write:
        with open(a.write, "w", encoding="utf-8") as f:
            json.dump(table, f, ensure_ascii=False, indent=1, sort_keys=True)
        print("wrote", a.write, sum(len(v) for v in table.values()), "cards")
    rows = []
    for key in offered:
        if offered[key] < a.min:
            continue
        t = shift_taken[key]
        p = shift_passed[key]
        rows.append(
            (
                key[0],
                key[1],
                offered[key],
                taken[key],
                sum(t) / len(t) if t else None,
                sum(p) / len(p) if p else None,
                table[key[0]].get(key[1], {}).get("prior"),
                sum(reach[key]) / len(reach[key]) if reach[key] else None,
            )
        )
    rows.sort(key=lambda r: (r[0], -(r[6] or -99)))
    print(
        f"{'character':<12} {'card':<26} {'offered':>7} {'taken':>5} {'pick%':>5} {'shift taken':>11} {'passed':>7} {'prior':>6} {'floors':>6}"
    )
    for ch, card, off, tk, st, sp, prior, floors in rows:
        print(
            f"{ch:<12} {card:<26} {off:>7} {tk:>5} {100 * tk / off:5.0f} {fmt(st):>11} {fmt(sp):>7} {fmt(prior):>6} {fmt(floors):>6}"
        )


if __name__ == "__main__":
    main()
