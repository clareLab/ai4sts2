import datetime
import json
import os
import re
import secrets
import subprocess
import sys

SCHEMA = 2
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
METRICS_DIR = os.path.join(ROOT, "metrics")
INDEX_PATH = os.path.join(METRICS_DIR, "runs.jsonl")
RUNS_DIR = os.path.join(METRICS_DIR, "runs")
MOD_DLL = os.path.join(ROOT, "src", "Ai4Sts2", ".godot", "mono", "temp", "bin", "Release", "ai4sts2.dll")
REPORT_DIR = os.path.join(ROOT, ".local", "report")
ID_RE = re.compile(r"^[0-9]{8}-[0-9]{6}-[a-z]+-[0-9a-f]{4}$")


def now():
    return datetime.datetime.now(datetime.UTC).astimezone().isoformat(timespec="seconds")


def build_info():
    info = {}
    try:
        info["commit"] = (
            subprocess.run(
                ["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, capture_output=True, text=True, timeout=5
            ).stdout.strip()
            or None
        )
    except (OSError, subprocess.SubprocessError):
        info["commit"] = None
    try:
        info["modBuilt"] = (
            datetime.datetime.fromtimestamp(os.path.getmtime(MOD_DLL)).astimezone().isoformat(timespec="seconds")
        )
    except OSError:
        info["modBuilt"] = None
    return info


def make_id(ts, kind):
    stamp = re.sub(r"[^0-9]", "", ts[:19])
    return f"{stamp[:8]}-{stamp[8:14]}-{kind}-{secrets.token_hex(2)}"


def run_path(run_id):
    if not ID_RE.match(run_id):
        raise ValueError(f"bad run id {run_id!r}")
    return os.path.join(RUNS_DIR, run_id + ".json")


def record(kind, summary, detail=None, versions=None):
    os.makedirs(RUNS_DIR, exist_ok=True)
    migrate_if_needed()
    ts = now()
    run_id = make_id(ts, kind)
    versions = versions or {}
    row = {
        "schema": SCHEMA,
        "id": run_id,
        "ts": ts,
        "kind": kind,
        "game": versions.get("game"),
        "mod": versions.get("mod"),
        "build": build_info(),
    }
    row.update(summary)
    with open(run_path(run_id), "w", encoding="utf-8") as f:
        json.dump({**row, "detail": detail}, f, ensure_ascii=False)
    with open(INDEX_PATH, "a", encoding="utf-8") as f:
        f.write(json.dumps(row, ensure_ascii=False) + "\n")
    refresh_report()
    return row


def refresh_report():
    import monitor

    try:
        monitor.export(REPORT_DIR, with_status=False, embed=10)
    except Exception as e:
        print(f"report not refreshed: {e}", file=sys.stderr)


def read_lines(path):
    rows = []
    if not os.path.exists(path):
        return rows
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return rows


def load_index():
    return [r for r in read_lines(INDEX_PATH) if "id" in r]


def migrate_if_needed():
    rows = read_lines(INDEX_PATH)
    if any("id" not in r for r in rows):
        migrate(rows)


def load_run(run_id):
    path = run_path(run_id)
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


LEGACY_SUMMARY_KEYS = {
    "bench": ("instance", "character", "seed", "encounter", "rounds", "bucket", "wallSeconds", "patches"),
    "warmup": ("instance", "character", "seed", "encounter", "batches", "rounds", "idle", "wallSeconds"),
    "diff": (
        "character",
        "encounters",
        "seeds",
        "deck",
        "policy",
        "randomSeed",
        "maxSteps",
        "wallSeconds",
        "steps",
        "mismatches",
        "played",
    ),
    "models": (),
}


def migrate(rows):
    os.makedirs(RUNS_DIR, exist_ok=True)
    out = []
    for r in rows:
        if "id" in r:
            out.append(r)
            continue
        kind = r.get("kind", "unknown")
        ts = r.get("ts") or now()
        run_id = make_id(ts, kind)
        keep = LEGACY_SUMMARY_KEYS.get(kind, ())
        row = {
            "schema": SCHEMA,
            "id": run_id,
            "ts": ts,
            "kind": kind,
            "game": r.get("game"),
            "mod": None,
            "build": r.get("build"),
        }
        detail = {}
        for k, v in r.items():
            if k in ("ts", "kind", "build", "game"):
                continue
            if k in keep:
                row[k] = v
            else:
                detail[k] = v
        if kind == "bench":
            res = r.get("result") or {}
            row["result"] = {k: v for k, v in res.items() if k != "bucketMillisPerRound"}
        if kind == "warmup":
            row["result"] = r.get("result")
        if kind == "models":
            row["cards"] = len(r.get("cards") or [])
            row["encounters"] = len(r.get("encounters") or [])
        if kind == "diff":
            row["cases"] = [{k: v for k, v in c.items() if k != "detail"} for c in r.get("cases") or []]
            detail["cases"] = r.get("cases") or []
        with open(run_path(run_id), "w", encoding="utf-8") as f:
            json.dump({**row, "detail": detail or None}, f, ensure_ascii=False)
        out.append(row)
    tmp = INDEX_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        for row in out:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")
    os.replace(tmp, INDEX_PATH)
    return out


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "migrate":
        before = len(read_lines(INDEX_PATH))
        migrate_if_needed()
        print(f"{before} rows, {len(load_index())} indexed")
        return 0
    print("usage: metrics.py migrate", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
