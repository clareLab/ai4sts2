import argparse
import os
import queue
import re
import subprocess
import sys
import threading
import time

import stats
from harness import Instance

import metrics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BATCHES = os.path.join(ROOT, ".local", "batches")


def expand_seeds(spec):
    seeds = []
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        m = re.fullmatch(r"([A-Za-z_]*)(\d+)-[A-Za-z_]*(\d+)", part)
        if m:
            prefix, first, last = m.group(1), int(m.group(2)), int(m.group(3))
            seeds.extend(f"{prefix}{i}" for i in range(first, last + 1))
        else:
            seeds.append(part)
    return seeds


def done_keys(tag):
    keys = set()
    for row in metrics.load_index():
        if row.get("kind") == "run" and row.get("tag") == tag:
            for case in row.get("cases", []):
                if case.get("outcome") != "error":
                    keys.add((row.get("character"), row.get("players", 1), case["seed"]))
    return keys


def restart(name):
    script = os.path.join(ROOT, "scripts", "headless.ps1")
    subprocess.run(["pwsh", "-NoProfile", "-File", script, "-Action", "stop", "-Instance", name], check=False)
    subprocess.Popen(
        ["pwsh", "-NoProfile", "-File", script, "-Action", "start", "-Instance", name],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    inst = Instance(name)
    for _ in range(120):
        time.sleep(1)
        if inst.status()["ready"]:
            return True
    return False


def worker(name, jobs, a, log_dir, results, lock):
    inst = Instance(name)
    while True:
        try:
            character, seed = jobs.get_nowait()
        except queue.Empty:
            return
        if not inst.status()["ready"]:
            with lock:
                print(f"[{name}] restarting instance")
            if not restart(name):
                with lock:
                    print(f"[{name}] instance failed to start; giving job back")
                jobs.put((character, seed))
                return
        cmd = [
            sys.executable,
            "-u",
            os.path.join(ROOT, "scripts", "run.py"),
            "--instance",
            name,
            "--character",
            character,
            "--seed",
            seed,
            "--players",
            str(a.players),
            "--tag",
            a.tag,
            *a.run_args.split(),
        ]
        log_path = os.path.join(log_dir, f"{character}-{a.players}p-{seed}.log")
        t0 = time.time()
        with open(log_path, "w", encoding="utf-8") as log:
            proc = subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT, cwd=ROOT, check=False)
        outcome = "?"
        with open(log_path, encoding="utf-8") as log:
            for line in log:
                if line.startswith("  => "):
                    outcome = line[5:].strip()
        with lock:
            results.append((name, character, seed, proc.returncode, outcome, round(time.time() - t0)))
            print(f"[{name}] {character} {seed} rc={proc.returncode} {outcome} {round(time.time() - t0)} s")
        jobs.task_done()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--instances", default="wb,wb2,wb3,wb4,wb5")
    ap.add_argument("--characters", default="IRONCLAD")
    ap.add_argument("--seeds", default="B1-B20")
    ap.add_argument("--players", type=int, default=1)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--run-args", default="--boss --fights 2 --max-floors 60")
    ap.add_argument("--fresh", action="store_true")
    a = ap.parse_args()
    seeds = expand_seeds(a.seeds)
    characters = [c.strip().upper() for c in a.characters.split(",") if c.strip()]
    done = set() if a.fresh else done_keys(a.tag)
    jobs = queue.Queue()
    skipped = 0
    for character in characters:
        for seed in seeds:
            if (character, a.players, seed) in done:
                skipped += 1
                continue
            jobs.put((character, seed))
    log_dir = os.path.join(BATCHES, a.tag)
    os.makedirs(log_dir, exist_ok=True)
    print(f"fleet {a.tag}: {jobs.qsize()} jobs ({skipped} already recorded) on {a.instances}")
    results = []
    lock = threading.Lock()
    threads = [
        threading.Thread(target=worker, args=(name.strip(), jobs, a, log_dir, results, lock), daemon=True)
        for name in a.instances.split(",")
        if name.strip()
    ]
    t0 = time.time()
    for t in threads:
        t.start()
        time.sleep(2)
    for t in threads:
        t.join()
    print(f"fleet {a.tag}: {len(results)} jobs in {round(time.time() - t0)} s")
    stats.report(tag=a.tag)


if __name__ == "__main__":
    main()
