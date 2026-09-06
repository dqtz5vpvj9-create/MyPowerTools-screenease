"""macOS/Unix regression: concurrent readers must not lose accepted pipe connections.
Run with the full path to a published ScreenEase.Service executable.
"""
import concurrent.futures
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import tempfile
import time
import uuid


def read_exact(stream, length):
    data = b""
    while len(data) < length:
        chunk = stream.recv(length - len(data))
        if not chunk:
            raise EOFError("Service closed a concurrent reader before replying")
        data += chunk
    return data


with tempfile.TemporaryDirectory(prefix="mpt-pipe-regression-") as root:
    name = "se-" + uuid.uuid4().hex[:8]
    endpoint = Path(os.environ.get("TMPDIR", "/tmp")) / ("CoreFxPipe_" + name)
    env = dict(os.environ, MPT_TOOL_DATA_ROOT=root)
    with open(Path(root) / "service.log", "w+") as log:
        process = subprocess.Popen([sys.argv[1], "--pipe", name, "--logical-only"], env=env, stdout=log, stderr=log)
        try:
            deadline = time.monotonic() + 15
            while not endpoint.exists():
                if process.poll() is not None or time.monotonic() > deadline:
                    log.flush()
                    log.seek(0)
                    raise RuntimeError("Isolated service did not become ready: " + log.read())
                time.sleep(0.05)

            def request(index):
                with socket.socket(socket.AF_UNIX) as stream:
                    stream.settimeout(15)
                    stream.connect(str(endpoint))
                    payload = json.dumps({"command": ["moduleStatus", "getSettings", "ping"][index % 3]}).encode()
                    stream.sendall(struct.pack("<i", len(payload)) + payload)
                    length = struct.unpack("<i", read_exact(stream, 4))[0]
                    response = json.loads(read_exact(stream, length))
                    assert response["ok"], response.get("error")

            with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
                list(pool.map(request, range(120)))
            print("PASS: 120 concurrent reads, 12 clients, isolated data and pipe")
        finally:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
