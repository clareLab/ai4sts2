import argparse
import sys
import time

from harness import Harness

import metrics


def start_combat(h, character, seed, encounter):
    h.call("wb.start", {"character": character, "seed": seed, "encounter": encounter})


def versions(ping):
    return {"game": ping.get("game"), "mod": ping.get("mod")}


def snapshot_models(h):
    ping = h.call("ping")
    cards = h.call("models.cards")["value"]
    encounters = h.call("models.encounters")["value"]
    metrics.record(
        "models",
        {"cards": len(cards), "encounters": len(encounters)},
        {"cards": cards, "encounters": encounters},
        versions(ping),
    )


def bench(a):
    h = Harness(a.instance, timeout=a.timeout)
    ping = h.call("ping")
    start_combat(h, a.character, a.seed, a.encounter)
    t = time.time()
    result = h.call("wb.bench", {"rounds": a.rounds, "encounter": a.encounter, "bucket": a.bucket})
    wall = time.time() - t
    row = metrics.record(
        "bench",
        {
            "instance": a.instance,
            "character": a.character,
            "seed": a.seed,
            "encounter": a.encounter,
            "rounds": a.rounds,
            "bucket": a.bucket,
            "wallSeconds": round(wall, 3),
            "patches": ping.get("patches"),
            "result": {k: v for k, v in result.items() if k != "bucketMillisPerRound"},
        },
        {"result": result},
        versions(ping),
    )
    for k in ("starts", "plays", "endTurns"):
        v = result.get(k) or {}
        if v.get("count"):
            print(
                f"{k:9s} n={v['count']:5d} med={v['median']:9.1f} p95={v['p95']:9.1f} min={v['min']:8.1f} max={v['max']:9.1f}"
            )
    print(
        f"total {result['totalMillis']:.0f} ms, {result['allocatedBytes'] / 1e6:.0f} MB allocated, recorded at {row['ts']}"
    )


def warmup(a):
    h = Harness(a.instance, timeout=a.timeout)
    ping = h.call("ping")
    start_combat(h, a.character, a.seed, a.encounter)
    t = time.time()
    result = h.call("wb.warmup", {"encounter": a.encounter, "batches": a.batches, "rounds": a.rounds, "idle": a.idle})
    wall = time.time() - t
    metrics.record(
        "warmup",
        {
            "instance": a.instance,
            "character": a.character,
            "seed": a.seed,
            "encounter": a.encounter,
            "batches": a.batches,
            "rounds": a.rounds,
            "idle": a.idle,
            "wallSeconds": round(wall, 3),
            "patches": ping.get("patches"),
            "result": result,
        },
        None,
        versions(ping),
    )
    print("ms/round per batch:", result["millisPerRound"])


def models(a):
    snapshot_models(Harness(a.instance, timeout=a.timeout))
    print("models snapshot recorded")


def main():
    shared = argparse.ArgumentParser(add_help=False)
    shared.add_argument("--instance", default="wb")
    shared.add_argument("--character", default="IRONCLAD")
    shared.add_argument("--seed", default="AI4STS2")
    shared.add_argument("--encounter", default="NIBBITS_WEAK")
    shared.add_argument("--timeout", type=float, default=600.0)
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("bench", parents=[shared])
    b.add_argument("--rounds", type=int, default=300)
    b.add_argument("--bucket", type=int, default=50)
    w = sub.add_parser("warmup", parents=[shared])
    w.add_argument("--batches", type=int, default=8)
    w.add_argument("--rounds", type=int, default=150)
    w.add_argument("--idle", type=float, default=1.0)
    sub.add_parser("models", parents=[shared])
    a = ap.parse_args()
    a.encounter = a.encounter.upper()
    a.character = a.character.upper()
    {"bench": bench, "warmup": warmup, "models": models}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
