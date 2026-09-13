import argparse
import contextlib
import datetime
import http.server
import json
import os
import re
import shutil
import sys
import webbrowser

from harness import Instance, headless_root

import metrics

DASHBOARD = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dashboard.html")
MARKER = '<script id="data" type="application/json">null</script>'
RUN_RE = re.compile(r"^/data/runs/([0-9]{8}-[0-9]{6}-[a-z]+-[0-9a-f]{4})\.json$")


def status():
    root = headless_root()
    names = []
    if os.path.isdir(root):
        names = sorted(d for d in os.listdir(root) if os.path.isdir(os.path.join(root, d)))
    return {
        "generatedAt": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
        "live": True,
        "root": root,
        "instances": [Instance(n, root).status() for n in names],
    }


def json_text(obj):
    return json.dumps(obj, ensure_ascii=False).replace("</", "<\\/")


def page(embedded, wrap=True):
    with open(DASHBOARD, encoding="utf-8") as f:
        html = f.read()
    if MARKER not in html:
        raise RuntimeError("dashboard.html has no data slot")
    payload = "null" if embedded is None else json_text(embedded)
    body = html.replace(MARKER, f'<script id="data" type="application/json">{payload}</script>')
    if not wrap:
        return body
    return (
        '<!doctype html><html><head><meta charset="utf-8">'
        '<meta name="viewport" content="width=device-width, initial-scale=1">' + body + "</html>"
    )


def export(out_dir, with_status=False, embed=10, wrap=True):
    index = metrics.load_index()
    runs_dir = os.path.join(out_dir, "data", "runs")
    os.makedirs(runs_dir, exist_ok=True)
    for row in index:
        src = metrics.run_path(row["id"])
        if os.path.exists(src):
            shutil.copyfile(src, os.path.join(runs_dir, row["id"] + ".json"))
    snapshot = status() if with_status else None
    if snapshot:
        snapshot["live"] = False
    with open(os.path.join(out_dir, "data", "index.json"), "w", encoding="utf-8") as f:
        f.write(json_text(index))
    with open(os.path.join(out_dir, "data", "status.json"), "w", encoding="utf-8") as f:
        f.write(json_text(snapshot))
    embedded_runs = {}
    chosen = index if embed < 0 else (index[-embed:] if embed > 0 else [])
    models = [r for r in index if r["kind"] == "models"]
    latest_models = {}
    for r in models:
        latest_models[r.get("game")] = r
    for row in list(latest_models.values()) + list(chosen):
        if row["id"] in embedded_runs:
            continue
        run = metrics.load_run(row["id"])
        if run is not None:
            embedded_runs[row["id"]] = run
    embedded = {"live": False, "index": index, "status": snapshot, "runs": embedded_runs, "exportedAt": metrics.now()}
    path = os.path.join(out_dir, "index.html")
    with open(path, "w", encoding="utf-8") as f:
        f.write(page(embedded, wrap))
    return path


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        return

    def send(self, code, body, ctype):
        data = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path in ("/", "/index.html"):
            self.send(200, page(None), "text/html; charset=utf-8")
        elif path == "/data/index.json":
            self.send(200, json_text(metrics.load_index()), "application/json; charset=utf-8")
        elif path == "/data/status.json":
            self.send(200, json_text(status()), "application/json; charset=utf-8")
        elif RUN_RE.match(path):
            run = metrics.load_run(RUN_RE.match(path).group(1))
            if run is None:
                self.send(404, "{}", "application/json; charset=utf-8")
            else:
                self.send(200, json_text(run), "application/json; charset=utf-8")
        else:
            self.send(404, "not found", "text/plain; charset=utf-8")


def serve(port, open_browser):
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    url = f"http://127.0.0.1:{server.server_address[1]}/"
    print(f"monitor at {url}", flush=True)
    if open_browser:
        webbrowser.open(url)
    with contextlib.suppress(KeyboardInterrupt):
        server.serve_forever()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=9418)
    ap.add_argument("--open", action="store_true")
    ap.add_argument("--export", default=None)
    ap.add_argument("--embed", type=int, default=10)
    ap.add_argument("--status", action="store_true")
    ap.add_argument("--bare", action="store_true")
    a = ap.parse_args()
    if a.export:
        print(export(a.export, a.status, a.embed, not a.bare))
        return 0
    serve(a.port, a.open)
    return 0


if __name__ == "__main__":
    sys.exit(main())
