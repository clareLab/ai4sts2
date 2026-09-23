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


VALUE_PATH = os.path.join(os.path.dirname(PATH), "value.json")
SURVIVAL_PATH = os.path.join(os.path.dirname(PATH), "survival.json")


def value_hash(wb, path=VALUE_PATH):
    if not os.path.exists(path):
        return None
    return wb.call("wb.value", {"path": os.path.abspath(path)})["hash"]


def survival_hash(wb, path=SURVIVAL_PATH):
    if not os.path.exists(path):
        return None
    return wb.call("wb.survival", {"path": os.path.abspath(path)})["hash"]


def apply(wb, overrides=None, path=PATH, value_path=None):
    merged = dict(overrides or {})
    base = os.path.dirname(os.path.abspath(path))
    if "Value" not in merged and "Value" not in load(path):
        digest = value_hash(wb, value_path or os.path.join(base, "value.json"))
        if digest:
            merged["Value"] = digest
    if "Survival" not in merged and "Survival" not in load(path):
        digest = survival_hash(wb, os.path.join(base, "survival.json"))
        if digest:
            merged["Survival"] = digest
    echo = wb.call("wb.tune", args(merged, path))
    known = {k.lower() for group in echo.values() if isinstance(group, dict) for k in group}
    unknown = [k for k in load(path) if k.lower() not in known]
    if unknown:
        raise KeyError(f"{path} names knobs the instance does not have: {unknown}")
    return echo
