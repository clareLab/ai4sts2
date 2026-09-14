import json
import os

PATH = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "metrics", "tuning.json")


def load():
    if not os.path.exists(PATH):
        return {}
    with open(PATH, encoding="utf-8") as f:
        return json.load(f)


def save(deltas):
    with open(PATH, "w", encoding="utf-8") as f:
        json.dump(dict(sorted(deltas.items())), f, ensure_ascii=False, indent=1)
        f.write("\n")


def args(overrides=None):
    return {"reset": True, **load(), **(overrides or {})}


def apply(wb, overrides=None):
    return wb.call("wb.tune", args(overrides))
