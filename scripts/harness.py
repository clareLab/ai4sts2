import contextlib
import ctypes
import json
import os
import re
import sys
import time
import uuid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class HarnessError(RuntimeError):
    pass


def headless_root():
    env = os.environ.get("AI4STS2_HEADLESS_ROOT")
    if env:
        return env
    try:
        with open(os.path.join(ROOT, "local.props"), encoding="utf-8") as f:
            m = re.search(r"<Sts2Dir>(.*?)</Sts2Dir>", f.read())
    except OSError:
        m = None
    volume = os.path.splitdrive(m.group(1).strip())[0] if m else "F:"
    return os.path.join(volume + os.sep, "ai4sts2", "headless")


def process_image(pid):
    if sys.platform != "win32":
        try:
            return os.readlink(f"/proc/{pid}/exe")
        except OSError:
            return None
    kernel32 = ctypes.windll.kernel32
    handle = kernel32.OpenProcess(0x1000, False, pid)
    if not handle:
        return None
    try:
        code = ctypes.c_ulong()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value != 259:
            return None
        size = ctypes.c_ulong(1024)
        buf = ctypes.create_unicode_buffer(size.value)
        if not kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
            return None
        return buf.value
    finally:
        kernel32.CloseHandle(handle)


class Instance:
    def __init__(self, name, root=None):
        self.name = name
        self.root = os.path.join(root or headless_root(), name)
        self.exe = os.path.join(self.root, "game", "SlayTheSpire2.exe")
        self.harness = os.path.join(self.root, "user", "Roaming", "SlayTheSpire2", "ai4sts2", "harness")

    def pid(self):
        try:
            with open(os.path.join(self.root, "pid"), encoding="utf-8") as f:
                return int(f.read().strip())
        except (OSError, ValueError):
            return None

    def alive(self):
        pid = self.pid()
        if pid is None:
            return False
        image = process_image(pid)
        return image is not None and os.path.normcase(image) == os.path.normcase(self.exe)

    def status(self):
        alive = self.alive()
        return {
            "name": self.name,
            "pid": self.pid(),
            "alive": alive,
            "ready": alive and os.path.exists(os.path.join(self.harness, "ready")),
            "busy": alive and os.path.exists(os.path.join(self.harness, "running.json")),
        }


class Harness:
    def __init__(self, instance, root=None, timeout=180.0):
        self.instance = Instance(instance, root)
        self.dir = self.instance.harness
        self.timeout = timeout
        if not self.instance.alive():
            raise HarnessError(f"instance '{instance}' not running")
        if not os.path.exists(os.path.join(self.dir, "ready")):
            raise HarnessError(f"instance '{instance}' not ready")

    def acquire(self, op):
        lock = os.path.join(self.dir, "client.lock")
        wait_until = time.time() + min(self.timeout, 30.0)
        while True:
            try:
                fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            except FileExistsError:
                owner = None
                with contextlib.suppress(OSError, ValueError), open(lock, encoding="utf-8") as f:
                    owner = int(f.read().strip())
                if owner is not None and owner != os.getpid() and process_image(owner) is None:
                    with contextlib.suppress(OSError):
                        os.remove(lock)
                    continue
                if time.time() >= wait_until:
                    raise HarnessError(f"{op}: instance '{self.instance.name}' is busy with another client") from None
                time.sleep(0.05)
                continue
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write(str(os.getpid()))
            return lock

    def call(self, op, args=None):
        rid = uuid.uuid4().hex
        result = os.path.join(self.dir, f"result-{rid}.json")
        request = os.path.join(self.dir, "request.json")
        running = os.path.join(self.dir, "running.json")
        lock = self.acquire(op)
        try:
            wait_until = time.time() + min(self.timeout, 30.0)
            while (os.path.exists(request) or os.path.exists(running)) and time.time() < wait_until:
                time.sleep(0.05)
            if os.path.exists(request) or os.path.exists(running):
                raise HarnessError(f"{op}: instance '{self.instance.name}' has a request in flight")
            tmp = os.path.join(self.dir, f"request-{rid}.tmp")
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump({"id": rid, "op": op, "args": args}, f)
            os.replace(tmp, request)
            deadline = time.time() + self.timeout
            while time.time() < deadline:
                time.sleep(0.01)
                if os.path.exists(result):
                    try:
                        with open(result, encoding="utf-8") as f:
                            data = json.load(f)
                    except (json.JSONDecodeError, OSError):
                        continue
                    if data.get("id") != rid:
                        continue
                    with contextlib.suppress(OSError):
                        os.remove(result)
                    if not data.get("ok"):
                        raise HarnessError(data.get("error"))
                    return data.get("payload")
                if not self.instance.alive():
                    raise HarnessError(f"{op}: instance '{self.instance.name}' died")
            raise HarnessError(f"{op}: timed out after {self.timeout}s")
        finally:
            with contextlib.suppress(OSError):
                os.remove(lock)
