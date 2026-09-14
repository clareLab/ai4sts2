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


def local_outcome(floors, start, horizon):
    losses = []
    for fl in floors[start + 1 :]:
        combat = fl.get("combat")
        if not combat or fl.get("type") == "Boss":
            continue
        if not combat.get("won"):
            losses.append(fl.get("hpBefore") or 0)
        else:
            losses.append((fl.get("hpBefore") or 0) - (fl.get("hpAfter") or 0))
        if len(losses) >= horizon:
            break
    return sum(losses) / len(losses) if losses else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", default="")
    ap.add_argument("--character", default="")
    ap.add_argument("--horizon", type=int, default=6)
    ap.add_argument("--min", type=int, default=4)
    a = ap.parse_args()
    paths = [p for spec in a.runs.split(",") if spec for p in glob.glob(spec)] or sorted(
        glob.glob(os.path.join(os.path.dirname(__file__), "..", "metrics", "runs", "*-run-*.json"))
    )
    offered = collections.Counter()
    taken = collections.Counter()
    after_taken = collections.defaultdict(list)
    after_passed = collections.defaultdict(list)
    reach_taken = collections.defaultdict(list)
    for path in paths:
        with open(path, encoding="utf-8") as f:
            run = json.load(f)
        if run.get("kind") != "run" or (a.character and run.get("character") != a.character):
            continue
        for case, floors in floors_of(run):
            for i, fl in enumerate(floors):
                ev = fl.get("evaluation")
                if not ev or fl.get("room") != "CombatRoom":
                    continue
                outcome = local_outcome(floors, i, a.horizon)
                for o in ev["options"]:
                    if o.get("card") is None:
                        continue
                    key = (run["character"], o["label"])
                    offered[key] += 1
                    if o["label"] == ev["best"]:
                        taken[key] += 1
                        if outcome is not None:
                            after_taken[key].append(outcome)
                        reach_taken[key].append(case.get("floors", 0))
                    elif outcome is not None:
                        after_passed[key].append(outcome)
    rows = []
    for key in offered:
        if offered[key] < a.min:
            continue
        t = after_taken[key]
        p = after_passed[key]
        rows.append(
            (
                key[0],
                key[1],
                offered[key],
                taken[key],
                sum(t) / len(t) if t else None,
                sum(p) / len(p) if p else None,
                sum(reach_taken[key]) / len(reach_taken[key]) if reach_taken[key] else None,
            )
        )
    rows.sort(key=lambda r: (r[0], -(r[3] / r[2])))
    print(
        f"{'character':<12} {'card':<26} {'offered':>7} {'taken':>5} {'pick%':>5} {'loss/fight taken':>16} {'passed':>7} {'floors':>6}"
    )
    for ch, card, off, tk, lt, lp, reach in rows:
        print(f"{ch:<12} {card:<26} {off:>7} {tk:>5} {100 * tk / off:5.0f} {fmt(lt):>16} {fmt(lp):>7} {fmt(reach):>6}")


if __name__ == "__main__":
    main()
