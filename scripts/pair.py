import argparse
import json
import time

import tuning
from harness import Harness, HarnessError

import metrics


def parse_config(spec):
    out = {"leaf": "estimate"}
    for part in spec.split(","):
        k, v = part.split("=")
        out[k] = v if k == "leaf" else int(v) if v.lstrip("-").isdigit() else v.lower() == "true"
    return out


def split_tuning(config):
    search = {k: v for k, v in config.items() if not k.startswith("tune.")}
    tune = {k[5:]: v for k, v in config.items() if k.startswith("tune.")}
    return search, tuning.args(tune)


def play(wb, a, seed, encounter, config, deck, potions):
    wb.call("wb.run", {"character": a.character, "seed": seed, "ascension": 0})
    if deck:
        wb.call("deck.set", {"cards": deck})
    if potions:
        wb.call("potions.set", {"potions": potions})
    wb.call("wb.start", {"character": a.character, "seed": seed, "encounter": encounter, "heal": True})
    search, tune = split_tuning(config)
    wb.call("wb.tune", tune)
    res = wb.call("wb.autoplay", {"maxTurns": a.max_turns, **search})
    me = res["state"]["players"][0]
    return {
        "won": res["won"],
        "hp": me["creature"]["hp"],
        "turns": res["turns"],
        "nodes": res["nodes"],
        "millis": round(res["micros"] / 1000, 1),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--character", default="IRONCLAD")
    ap.add_argument("--seed", default="AI4STS2,PAIR2,PAIR3,PAIR4,PAIR5")
    ap.add_argument("--encounter", action="append", default=[])
    ap.add_argument("--cards", default="")
    ap.add_argument("--deck-file", default="")
    ap.add_argument("--potions", default="")
    ap.add_argument("--a", default="turns=1,beam=4,maxNodes=600")
    ap.add_argument("--b", default="turns=2,beam=5,maxNodes=800")
    ap.add_argument("--max-turns", type=int, default=30)
    ap.add_argument("--instance", default="wb")
    a = ap.parse_args()
    a.character = a.character.upper()
    seeds = [s.strip() for s in a.seed.split(",") if s.strip()]
    encounters = [e.upper() for e in a.encounter] or ["CULTISTS_NORMAL"]
    deck = [c.strip().upper() for c in a.cards.split(",") if c.strip()]
    if a.deck_file:
        with open(a.deck_file, encoding="utf-8") as f:
            deck = json.load(f)["deck"]
    potions = [c.strip().upper() for c in a.potions.split(",") if c.strip()]
    configs = {"a": parse_config(a.a), "b": parse_config(a.b)}
    wb = Harness(a.instance, timeout=3600)
    ping = wb.call("ping")
    t0 = time.time()
    rows = []
    for enc in encounters:
        for seed in seeds:
            row = {"encounter": enc, "seed": seed}
            for name, config in configs.items():
                try:
                    row[name] = play(wb, a, seed, enc, config, deck, potions)
                except HarnessError as e:
                    row[name] = {"error": str(e)[:300], "won": False, "hp": 0, "turns": 0, "nodes": 0, "millis": 0}
            rows.append(row)
            print(
                f"{enc:<26} {seed:<8} A {'W' if row['a']['won'] else 'L'} hp={row['a']['hp']:>3} {row['a']['millis']:>7.0f}ms | B {'W' if row['b']['won'] else 'L'} hp={row['b']['hp']:>3} {row['b']['millis']:>7.0f}ms"
            )
    summary = {
        "character": a.character,
        "encounters": encounters,
        "seeds": seeds,
        "deck": deck,
        "potions": potions,
        "configs": configs,
        "patches": ping.get("patches"),
        "fights": len(rows),
        "winsA": sum(r["a"]["won"] for r in rows),
        "winsB": sum(r["b"]["won"] for r in rows),
        "hpA": sum(r["a"]["hp"] for r in rows),
        "hpB": sum(r["b"]["hp"] for r in rows),
        "millisA": round(sum(r["a"]["millis"] for r in rows)),
        "millisB": round(sum(r["b"]["millis"] for r in rows)),
        "worse": [f"{r['encounter']}/{r['seed']}" for r in rows if r["b"]["hp"] <= r["a"]["hp"] - 10],
        "better": [f"{r['encounter']}/{r['seed']}" for r in rows if r["b"]["hp"] >= r["a"]["hp"] + 10],
        "wallSeconds": round(time.time() - t0, 1),
        "replay": False,
        "rows": rows,
    }
    metrics.record("pair", summary, {"rows": rows}, {"game": ping.get("game"), "mod": ping.get("mod")})
    print(
        f"A wins {summary['winsA']}/{summary['fights']} hp {summary['hpA']} {summary['millisA']} ms | B wins {summary['winsB']}/{summary['fights']} hp {summary['hpB']} {summary['millisB']} ms | worse {summary['worse']} better {summary['better']}"
    )


if __name__ == "__main__":
    main()
