import json
import os

PATH = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "metrics", "tuning.json")


def load(path=PATH):
    if not path or not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save(deltas):
    with open(PATH, "w", encoding="utf-8") as f:
        json.dump(dict(sorted(deltas.items())), f, ensure_ascii=False, indent=1)
        f.write("\n")


def args(overrides=None, path=PATH):
    return {"reset": True, **load(path), **(overrides or {})}


def apply(wb, overrides=None, path=PATH):
    echo = wb.call("wb.tune", args(overrides, path))
    known = {k.lower() for group in echo.values() if isinstance(group, dict) for k in group}
    unknown = [k for k in load(path) if k.lower() not in known]
    if unknown:
        raise KeyError(f"{path} names knobs the instance does not have: {unknown}")
    return echo
