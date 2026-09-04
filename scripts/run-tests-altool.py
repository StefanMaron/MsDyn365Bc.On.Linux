#!/usr/bin/env python3
"""
run-tests-altool.py — Run AL tests via the AL dotnet tool's native test runner.

EXPERIMENTAL alternative to run-tests.sh. Instead of the OData suite-population
+ WebSocket client-session flow, this drives the NST's built-in SignalR hub at
<dev-endpoint>/dev/TestRunnerHub through `al runtests` from the
Microsoft.Dynamics.BusinessCentral.Development.Tools dotnet tool (the same
mechanism the VS Code Test Explorer uses since BC 2026 wave 1).

Why run-tests.sh still exists (limitations of this path):
  - BC 28.0+ ONLY. The server-side TestRunnerHub (Dev API 7.0) does not exist
    in BC 27.x — the tool reports "Server does not support test running."
  - The `runtests` CLI command only ships in the 18.x PRERELEASE of the dotnet
    tool (stable 17.x has publishapp but not runtests).
  - Tests do NOT run under an AL test runner codeunit (Microsoft's design):
    AI tests are unsupported, test-runner-published setup/teardown events
    don't fire, and isolation comes from the RequiredTestIsolation property
    (default: Codeunit). Suites that depend on standard Test Runner codeunit
    semantics (e.g. Microsoft's BCApps buckets) can behave differently.
  - The test app must already be PUBLISHED AND INSTALLED for the tenant
    before this script runs (bc_publish_app with SchemaUpdateMode=forcesync
    does both). This script does not publish anything.

Output contract — kept compatible with bc-test-from-source.yml's parser
(the workflow greps these exact shapes; see the "Run AL tests" step):
  - prints "Test codeunits: <comma-separated ids>"
  - prints a "<N> total, <P> passed, <F> failed" summary line
  - exit 0 only when at least one test ran and nothing failed or errored
  - --junit-output writes the same JUnit shape as tools/TestRunner:
    one <testsuite> per codeunit, classname "Codeunit <id>"

Usage:
  python3 scripts/run-tests-altool.py \
      --app MyTestApp.app --codeunit-range "50000..50100" \
      --junit-output build/junit.xml

Authentication: the AL tool reads BC_SERVER_USERNAME / BC_SERVER_PASSWORD
from the environment for --authentication UserPassword. This script sets
them from --auth, which itself defaults from those same two env vars
(falling back to BCRUNNER:Admin123!, same as run-tests.sh) — so a container
booted with a non-default BC_SERVER_USERNAME/BC_SERVER_PASSWORD (see
docker-compose.yml) needs no extra flag here.

--transport {cli,hub,auto}: 'cli' (default) shells out to `al runtests <id>`
once per codeunit — this is the path validated above. 'auto' tries hub and
falls back to cli when the hub connection can't be established (the fallback
can only trigger before any test has run). 'hub' opens
ONE persistent SignalR connection to /dev/TestRunnerHub and runs every
codeunit over it (protocol reverse-engineered from the al dotnet tool's own
HubBasedTestRunnerService — no `al` CLI needed at test-run time, stdlib-only
WebSocket client). Measured against a live BC 28.3 server: 43 codeunits took
18.6s over --transport cli (~433ms/codeunit — process start + SignalR
negotiate + auth, paid per invocation) vs 0.47s over --transport hub
(~11ms/codeunit after one shared connection setup) — identical JUnit output
and pass/fail counts in both cases. Known hub-protocol quirk: the server
closes the connection if the SAME codeunit id is invoked twice on one
connection (doesn't matter in practice — a test app's codeunit ids are
always unique, and this script never re-invokes one). See CodeunitRun /
run_codeunits_via_hub for the full protocol writeup.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import queue
import random
import re
import socket
import ssl
import struct
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from datetime import datetime, timezone
from xml.sax.saxutils import escape, quoteattr

# `al runtests` per-method result line, e.g. "  PASS MyTest (123ms)".
# Anchored on the trailing "(<n>ms)" so method names containing spaces or
# parentheses don't break the match. The name group is .* (not .+) because
# the hub emits one EXTRA result per codeunit with an EMPTY method name —
# a codeunit-level completion pseudo-result ("  PASS  (656ms)", observed
# live against BC 28.1). Empty-name PASS lines are dropped from the counts
# (they aren't [Test] procedures and the legacy runner never counted them);
# empty-name FAIL/SKIP lines are recorded as "(codeunit)" so a codeunit-
# level failure (e.g. OnRun error) can't vanish silently. A "(codeunit)"
# FAIL is then dropped again if a NAMED test in the same codeunit already
# failed — see CodeunitRun.drop_redundant_codeunit_result; there it is only
# the rollup of that failure, and keeping it double-counts.
RESULT_LINE = re.compile(r"^\s{2}(PASS|FAIL|SKIP)\s(.*)\((\d+)ms\)$")
SUMMARY_LINE = re.compile(
    r"Test run completed: (\d+) passed, (\d+) failed, (\d+) skipped\."
)
NO_RESULTS_MARKER = "No test results were returned"
UNSUPPORTED_MARKER = "Server does not support test running"


def parse_range_spans(expr: str) -> list[tuple[int, int]]:
    """Parse a codeunit range expression into (lo, hi) spans.

    Accepts the same shapes as run-tests.sh: "50000", "50000..50100",
    "50000..50100|130450..130459", "50000,50001", and mixed. The
    already-normalized "lo-hi" form is accepted too.
    """
    spans: list[tuple[int, int]] = []
    for part in expr.replace("|", ",").split(","):
        part = part.strip()
        if not part:
            continue
        if ".." in part:
            lo, hi = part.split("..", 1)
        elif "-" in part:
            lo, hi = part.split("-", 1)
        else:
            lo = hi = part
        try:
            spans.append((int(lo), int(hi)))
        except ValueError:
            print(f"WARN: ignoring unparseable range part '{part}'", file=sys.stderr)
    return spans


def in_spans(spans: list[tuple[int, int]], cuid: int) -> bool:
    return any(lo <= cuid <= hi for lo, hi in spans)


def discover_test_codeunits(app_path: str, spans: list[tuple[int, int]]) -> list[int]:
    """Extract Subtype=Test codeunit IDs from the .app's SymbolReference.json.

    Same strategy as run-tests.sh: always discover from the symbol so we
    only invoke the runner for codeunits that actually exist (a literal
    "50000..99999" range would otherwise mean tens of thousands of hub
    round-trips). The optional range filter intersects.
    """
    with zipfile.ZipFile(app_path) as z:
        raw = z.read("SymbolReference.json").decode("utf-8-sig", errors="replace")
    data = json.loads(raw.lstrip("﻿"))

    ids: list[int] = []

    def collect(node: dict) -> None:
        for cu in node.get("Codeunits", []):
            props = {p["Name"]: p["Value"] for p in cu.get("Properties", [])}
            if props.get("Subtype") != "Test":
                continue
            cuid = cu.get("Id")
            if not isinstance(cuid, int):
                continue
            if spans and not in_spans(spans, cuid):
                continue
            ids.append(cuid)
        for ns in node.get("Namespaces", []):
            collect(ns)

    collect(data)
    return sorted(set(ids))


def _pick_company(rows: list[dict]) -> str | None:
    """Choose the demo company out of a companies payload.

    Prefers the evaluation company over "first row wins": the CRONUS demo
    database ships more than one company ("My Company" is in there too),
    and which one sorts first isn't something we control. Keys are
    lowercased because the OData Company page and the API v2.0 entity
    spell them differently (Evaluation_Company vs evaluationCompany).
    """
    norm = [{k.lower(): v for k, v in r.items()} for r in rows]
    if not norm:
        return None
    chosen = next(
        (r for r in norm if r.get("evaluation_company") or r.get("evaluationcompany")),
        norm[0],
    )
    return chosen.get("name") or None


def detect_company(base_urls: list[str], user: str, password: str) -> str | None:
    """Auto-detect the demo company name.

    ODataV4/Company is tried first, ahead of API v2.0, for two reasons:

      * It's the exact URL the bc-runner healthcheck polls every 2s, so
        it's already warm — measured at 3.8ms against a live container.
      * API v2.0 lives in the _Exclude_APIV2_ extension, which is not in
        the keep set on a minimal selective-clear boot. Asking for it
        first returns 404 on exactly the lean configurations we
        recommend, and only the port-7052 fallback saves the run.

    API v2.0 remains as a fallback for servers that don't expose the
    OData Company page. Returns None when nothing answers, which the
    caller treats as "let the server pick its default company".
    """
    token = base64.b64encode(f"{user}:{password}".encode()).decode()
    urls = [f"{base}/ODataV4/Company" for base in base_urls]
    urls += [f"{base}/api/v2.0/companies" for base in base_urls]
    for url in urls:
        req = urllib.request.Request(url, headers={"Authorization": f"Basic {token}"})
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                data = json.loads(resp.read().decode("utf-8", errors="replace"))
            name = _pick_company(data.get("value", []))
            if name:
                return name
        except (urllib.error.URLError, OSError, ValueError, KeyError):
            continue
    return None


def probe_test_running_support(
    server: str, instance: str, port: int, user: str, password: str
) -> tuple[bool, str]:
    """Check whether the NST's dev endpoint advertises Dev API 7.0+.

    GET <server>:<port>/<instance>/dev/metadata returns a ServerInfo JSON
    whose WebApiVersion gates the AL tool's feature checks — TestRunning
    (the /dev/TestRunnerHub SignalR hub) requires 7.0, which shipped with
    BC 28.0. Returns (supported, human-readable reason). Key lookup is
    case-insensitive since the exact casing the server emits isn't part
    of any contract we control.
    """
    url = f"{server}:{port}/{instance}/dev/metadata"
    token = base64.b64encode(f"{user}:{password}".encode()).decode()
    req = urllib.request.Request(url, headers={"Authorization": f"Basic {token}"})
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8", errors="replace"))
    except (urllib.error.URLError, OSError, ValueError) as ex:
        return False, f"dev/metadata unreachable or unparseable at {url}: {ex}"
    api_version = next(
        (v for k, v in data.items() if k.lower() == "webapiversion"), None
    )
    if not api_version:
        return False, f"dev/metadata has no WebApiVersion field (keys: {sorted(data)})"
    try:
        major = int(str(api_version).split(".")[0])
    except ValueError:
        return False, f"unparseable WebApiVersion '{api_version}'"
    if major >= 7:
        return True, f"Dev API {api_version} (TestRunnerHub requires 7.0)"
    return False, f"Dev API {api_version} < 7.0 — no TestRunnerHub on this server"


class CodeunitRun:
    """Parsed outcome of one `al runtests <id>` invocation."""

    def __init__(self, codeunit_id: int):
        self.codeunit_id = codeunit_id
        # Each result: (status, method_name, duration_ms, failure_detail)
        self.results: list[tuple[str, str, int, str]] = []
        self.error: str | None = None  # codeunit-level hard error (no results)
        self.started = datetime.now(timezone.utc)
        self.elapsed_seconds = 0.0

    def drop_redundant_codeunit_result(self) -> None:
        """Drop the "(codeunit)" rollup when a named test already reports the failure.

        The hub emits one extra result per codeunit with an empty method name (see
        RESULT_LINE). An empty-name PASS is discarded at parse time; an empty-name
        FAIL is kept as "(codeunit)" so a codeunit-level failure with no named test
        attached to it — an OnRun error, a failed codeunit-level setup — cannot vanish
        silently.

        But when a named [Test] procedure in the same codeunit ALSO failed, the rollup
        is just that failure counted a second time: one real failure is reported as
        "2 failed", and the JUnit file carries a phantom "(codeunit)" test case whose AL
        call stack points at whichever test ran last (often one that passed). The
        websocket runner reports the same codeunit as 1 failure, so the two runners
        disagree about the same run.

        Keep the rollup only when it is the sole evidence of the failure.
        """
        named_failed = any(
            status == "FAIL" and name != "(codeunit)" for status, name, _, _ in self.results
        )
        if not named_failed:
            return
        self.results = [
            r for r in self.results if not (r[0] == "FAIL" and r[1] == "(codeunit)")
        ]


def run_codeunit(
    altool_cmd: str,
    cuid: int,
    server: str,
    instance: str,
    port: int,
    company: str | None,
    env: dict,
    timeout_seconds: float,
) -> CodeunitRun:
    run = CodeunitRun(cuid)
    cmd = [
        altool_cmd,
        "runtests",
        str(cuid),
        "--server", server,
        "--serverinstance", instance,
        "--port", str(port),
        "--authentication", "UserPassword",
        "--environmenttype", "OnPrem",
    ]
    if company:
        cmd += ["--company", company]

    start = time.monotonic()
    try:
        proc = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,  # failure summaries go to stderr; merge
            env=env,
            timeout=timeout_seconds,
            text=True,
            errors="replace",
        )
        output = proc.stdout or ""
        rc = proc.returncode
    except subprocess.TimeoutExpired as ex:
        partial = ex.stdout or b""
        if isinstance(partial, bytes):
            partial = partial.decode("utf-8", errors="replace")
        print(partial)
        run.error = f"timed out after {int(timeout_seconds)}s"
        run.elapsed_seconds = time.monotonic() - start
        return run
    except FileNotFoundError:
        run.error = f"AL tool not found: '{altool_cmd}' — install the " \
                    "Microsoft.Dynamics.BusinessCentral.Development.Tools dotnet tool"
        return run
    run.elapsed_seconds = time.monotonic() - start

    # Echo the raw tool output (indented) — verbose-by-default is a repo
    # invariant; silent failures cost debugging cycles (see run-tests.sh).
    for line in output.splitlines():
        print(f"    {line}")

    # Parse per-method result lines plus any failure detail between a FAIL
    # line and the next result line. Failure output can be multi-line (BC
    # error message + AL call stack).
    in_results = False
    last_fail_idx: int | None = None
    for line in output.splitlines():
        m = RESULT_LINE.match(line)
        if m:
            in_results = True
            status, name, ms = m.group(1), m.group(2).strip(), int(m.group(3))
            if not name:
                if status == "PASS":
                    last_fail_idx = None
                    continue  # codeunit-level pseudo-result, see RESULT_LINE
                name = "(codeunit)"
            run.results.append((status, name, ms, ""))
            last_fail_idx = len(run.results) - 1 if status == "FAIL" else None
            continue
        if in_results and last_fail_idx is not None and line.strip():
            status, name, ms, detail = run.results[last_fail_idx]
            detail = f"{detail}\n{line.strip()}" if detail else line.strip()
            run.results[last_fail_idx] = (status, name, ms, detail)

    if UNSUPPORTED_MARKER in output:
        run.error = (
            "server does not support test running — the /dev/TestRunnerHub "
            "(Dev API 7.0) requires BC 28.0+"
        )
        return run

    # Hard-error detection. `al runtests` exits 0 with "No test results were
    # returned" when the hub connection silently dies (or the codeunit has no
    # runnable tests) — for a codeunit we discovered as Subtype=Test that is
    # a failure, not a pass. Treat "exit != 0 with zero parsed results" the
    # same way: connection/auth errors land here.
    if not run.results:
        if NO_RESULTS_MARKER in output:
            run.error = "no test results returned for a Subtype=Test codeunit"
        elif rc != 0:
            tail = "\n".join(output.splitlines()[-5:])
            run.error = f"al runtests exited {rc} with no results: {tail}"
        elif not SUMMARY_LINE.search(output):
            tail = "\n".join(output.splitlines()[-5:])
            run.error = f"unrecognized al runtests output: {tail}"
        else:
            run.error = "no test results returned for a Subtype=Test codeunit"
    run.drop_redundant_codeunit_result()
    return run



# --------------------------------------------------------------------------
# --transport hub: direct TestRunnerHub SignalR client.
#
# Reverse-engineered from the al dotnet tool's own implementation
# (Microsoft.Dynamics.Nav.LanguageModelTools.dll, HubBasedTestRunnerService /
# TestRunService — decompiled with ilspycmd) and confirmed against a live
# BC 28.3 server by capturing real traffic. Protocol summary (see the final
# report for the full writeup):
#
#   Hub URL:   <server>:<port>/<instance>/dev/TestRunnerHub
#   Negotiate: POST <hub-url>/negotiate?negotiateVersion=1
#              -> {"connectionToken": "...", ...}
#   Connect:   GET  ws://<hub-url>?id=<connectionToken>&Authentication=<auth>
#              (Authorization header set too, redundant with the query param
#              — same pattern as DebuggerHub)
#   Handshake: client sends {"protocol":"json","version":1}<RS>, server
#              replies {}<RS> (RS = 0x1e, the SignalR JSON-protocol record
#              separator)
#   Server->client callbacks (type 1, "target" = method name):
#     HubConnected, LogServerInfoMessage, LogServerMessage, RuntimeInitialized,
#     IsAlive (client must reply with a "AcknowledgeIsAlive" invocation or the
#     hub disconnects it), TestStarted(codeunitId, methodName),
#     TestCompleted(codeunitId, methodName, status, output, durationMs),
#     TestRunCompleted(codeCoverageInfo-or-null)
#   Client->server invocations (type 1, with optional invocationId to get a
#   type-3 completion ack back):
#     Initialize(companyName, debuggingContext, coverageMode) — coverageMode
#       0 = None (the only mode this client uses)
#     RunTests(codeunitId, testMethodNames[])  — testMethodNames=[] runs all
#     StopTestExecution()
#   TestResultStatus enum (transmitted as a plain int, no StringEnumConverter
#   on the wire): Passed=0, Failed=1, Skipped=2.
#   SignalR keepalive pings arrive as {"type":6} — no ack needed for a
#   short-lived client; ignore.
#
# One connection can run an arbitrary sequence of codeunits: after a
# TestRunCompleted event for codeunit N, invoking RunTests(N+1, []) on the
# SAME connection reuses the already-authenticated session — this is what
# eliminates the ~0.4s per-codeunit process-start + negotiate + auth
# overhead that --transport cli pays on every `al runtests` invocation.
#
# Implemented with the stdlib only (raw socket + hand-rolled RFC6455 framing)
# so --transport hub has no extra pip dependency in CI images.

_RS = "\x1e"  # SignalR JSON-Hub-Protocol record separator
_WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


class _WebSocketClient:
    """Minimal RFC6455 client: text frames only, stdlib socket + ssl."""

    def __init__(self, url: str, headers: dict[str, str], connect_timeout: float = 15.0):
        parsed = urllib.parse.urlparse(url)
        self.host = parsed.hostname
        self.port = parsed.port or (443 if parsed.scheme == "wss" else 80)
        self.path = parsed.path + (("?" + parsed.query) if parsed.query else "")
        self.use_ssl = parsed.scheme == "wss"
        self.headers = headers
        self.connect_timeout = connect_timeout
        self.sock: socket.socket | None = None
        self._recv_buf = b""
        self._msg_queue: queue.Queue = queue.Queue()
        self._send_lock = threading.Lock()
        self._reader_thread: threading.Thread | None = None

    def connect(self) -> None:
        raw = socket.create_connection((self.host, self.port), timeout=self.connect_timeout)
        raw.settimeout(None)
        if self.use_ssl:
            ctx = ssl.create_default_context()
            raw = ctx.wrap_socket(raw, server_hostname=self.host)
        self.sock = raw
        key = base64.b64encode(bytes(random.getrandbits(8) for _ in range(16))).decode()
        lines = [
            f"GET {self.path} HTTP/1.1",
            f"Host: {self.host}:{self.port}",
            "Upgrade: websocket",
            "Connection: Upgrade",
            f"Sec-WebSocket-Key: {key}",
            "Sec-WebSocket-Version: 13",
        ]
        for k, v in self.headers.items():
            lines.append(f"{k}: {v}")
        self.sock.sendall(("\r\n".join(lines) + "\r\n\r\n").encode())
        head = self._recv_http_headers()
        status_line = head.split(b"\r\n", 1)[0]
        if b" 101 " not in status_line:
            raise ConnectionError(f"WebSocket handshake failed: {status_line!r} — {head[:500]!r}")
        expected = base64.b64encode(
            hashlib.sha1((key + _WS_GUID).encode()).digest()
        ).decode().encode()
        if expected not in head:
            raise ConnectionError("WebSocket handshake Sec-WebSocket-Accept mismatch")
        self._reader_thread = threading.Thread(target=self._reader_loop, daemon=True)
        self._reader_thread.start()

    def _recv_http_headers(self) -> bytes:
        buf = b""
        while b"\r\n\r\n" not in buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise ConnectionError("connection closed during WebSocket handshake")
            buf += chunk
        head, _, rest = buf.partition(b"\r\n\r\n")
        self._recv_buf = rest
        return head

    def _recv_exact(self, n: int) -> bytes:
        while len(self._recv_buf) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("WebSocket connection closed")
            self._recv_buf += chunk
        data = self._recv_buf[:n]
        self._recv_buf = self._recv_buf[n:]
        return data

    def _read_message(self) -> bytes | None:
        payload = b""
        while True:
            hdr = self._recv_exact(2)
            b0, b1 = hdr[0], hdr[1]
            fin = b0 & 0x80
            opcode = b0 & 0x0F
            masked = b1 & 0x80
            length = b1 & 0x7F
            if length == 126:
                length = struct.unpack(">H", self._recv_exact(2))[0]
            elif length == 127:
                length = struct.unpack(">Q", self._recv_exact(8))[0]
            mask_key = self._recv_exact(4) if masked else None
            data = self._recv_exact(length)
            if masked:
                data = bytes(b ^ mask_key[i % 4] for i, b in enumerate(data))
            if opcode == 0x8:  # close
                return None
            if opcode == 0x9:  # ping -> pong
                self._send_frame(0xA, data)
                continue
            if opcode == 0xA:  # pong
                continue
            payload += data
            if fin:
                return payload

    def _reader_loop(self) -> None:
        try:
            while True:
                msg = self._read_message()
                if msg is None:
                    break
                self._msg_queue.put(("message", msg))
        except Exception as ex:  # noqa: BLE001 - surfaced to the consumer thread
            self._msg_queue.put(("error", ex))
            return
        self._msg_queue.put(("closed", None))

    def _send_frame(self, opcode: int, data: bytes) -> None:
        with self._send_lock:
            b0 = 0x80 | opcode
            length = len(data)
            mask_key = bytes(random.getrandbits(8) for _ in range(4))
            if length < 126:
                header = bytes([b0, 0x80 | length])
            elif length < 65536:
                header = bytes([b0, 0x80 | 126]) + struct.pack(">H", length)
            else:
                header = bytes([b0, 0x80 | 127]) + struct.pack(">Q", length)
            masked = bytes(b ^ mask_key[i % 4] for i, b in enumerate(data))
            self.sock.sendall(header + mask_key + masked)

    def send_text(self, text: str) -> None:
        self._send_frame(0x1, text.encode("utf-8"))

    def recv(self, timeout: float | None = None) -> bytes | None:
        try:
            kind, val = self._msg_queue.get(timeout=timeout)
        except queue.Empty:
            raise TimeoutError("timed out waiting for WebSocket message") from None
        if kind == "error":
            raise val
        if kind == "closed":
            return None
        return val

    def close(self) -> None:
        try:
            self._send_frame(0x8, b"")
        except Exception:
            pass
        try:
            if self.sock:
                self.sock.close()
        except Exception:
            pass


class _SignalRHub:
    """JSON Hub Protocol framing (record-separated JSON) over a WebSocket."""

    def __init__(self, ws: _WebSocketClient):
        self.ws = ws
        self._buf = ""

    def handshake(self, timeout: float = 15.0) -> None:
        self.ws.send_text(json.dumps({"protocol": "json", "version": 1}) + _RS)
        frame = self.next_frame(timeout=timeout)
        if frame.get("error"):
            raise ConnectionError(f"SignalR handshake rejected: {frame['error']}")

    def next_frame(self, timeout: float | None = None) -> dict:
        deadline = None if timeout is None else time.monotonic() + timeout
        while _RS not in self._buf:
            remaining = None if deadline is None else max(0.0, deadline - time.monotonic())
            if deadline is not None and remaining <= 0:
                raise TimeoutError("timed out waiting for hub message")
            raw = self.ws.recv(timeout=remaining)
            if raw is None:
                raise ConnectionError("hub connection closed")
            self._buf += raw.decode("utf-8", errors="replace")
        idx = self._buf.index(_RS)
        frame_str = self._buf[:idx]
        self._buf = self._buf[idx + 1:]
        if not frame_str:
            return {}
        return json.loads(frame_str)

    def send(self, obj: dict) -> None:
        self.ws.send_text(json.dumps(obj) + _RS)

    def invoke(self, target: str, args: list, invocation_id: str | None = None) -> None:
        msg = {"type": 1, "target": target, "arguments": args}
        if invocation_id is not None:
            msg["invocationId"] = invocation_id
        self.send(msg)


def _hub_negotiate(hub_http_base: str, auth_header: str, timeout: float = 15.0) -> str:
    """POST .../negotiate?negotiateVersion=1, return the connectionToken."""
    req = urllib.request.Request(
        hub_http_base + "/negotiate?negotiateVersion=1",
        method="POST",
        headers={"Authorization": auth_header},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    token = data.get("connectionToken") or data.get("connectionId")
    if not token:
        raise ConnectionError(f"negotiate response had no connectionToken: {data}")
    return token


def _hub_connect(
    hub_http_base: str, ws_base: str, auth_header: str, company: str | None
) -> tuple[_WebSocketClient, _SignalRHub]:
    """Negotiate, connect, SignalR-handshake, wait for HubConnected, then
    Initialize. Returns (ws, hub) ready for RunTests invocations.

    The HubConnected wait mirrors the required-callback gate documented for
    DebuggerHub; TestRunnerHub follows the same HubBasedService base class.
    """
    token = _hub_negotiate(hub_http_base, auth_header)
    ws_url = f"{ws_base}?id={urllib.parse.quote(token)}&Authentication={urllib.parse.quote(auth_header)}"
    ws = _WebSocketClient(ws_url, headers={"Authorization": auth_header})
    ws.connect()
    hub = _SignalRHub(ws)
    try:
        hub.handshake()
        connect_deadline = time.monotonic() + 15
        while True:
            remaining = connect_deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("timed out waiting for HubConnected")
            frame = hub.next_frame(timeout=remaining)
            target = frame.get("target")
            if target == "HubConnected":
                break
            if target in ("LogServerInfoMessage", "LogServerMessage"):
                for arg in frame.get("arguments", []):
                    print(f"    [hub] {arg}")

        hub.invoke("Initialize", [company or "", "", 0], invocation_id="init")
        # Wait for the type-3 completion ack for "init" (or RuntimeInitialized,
        # whichever arrives — either confirms the server accepted the call).
        init_deadline = time.monotonic() + 15
        while True:
            remaining = init_deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("timed out waiting for Initialize to complete")
            frame = hub.next_frame(timeout=remaining)
            if frame.get("type") == 3 and frame.get("invocationId") == "init":
                break
            if frame.get("target") == "RuntimeInitialized":
                break
    except BaseException:
        ws.close()
        raise
    return ws, hub


def _run_one_codeunit_on_hub(
    hub: _SignalRHub, run: CodeunitRun, cuid: int,
    per_deadline: float, codeunit_timeout_seconds: float,
) -> None:
    """Invoke RunTests for one codeunit and collect results into `run`
    until TestRunCompleted. Raises TimeoutError on the per-codeunit
    deadline and ConnectionError/OSError when the connection drops."""
    hub.invoke("RunTests", [cuid, []])
    last_fail_output = ""
    while True:
        remaining = per_deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError(f"timed out after {int(codeunit_timeout_seconds)}s")
        frame = hub.next_frame(timeout=remaining)
        ftype = frame.get("type")
        target = frame.get("target")
        if ftype == 6:
            continue  # SignalR keepalive ping
        if target == "IsAlive":
            hub.invoke("AcknowledgeIsAlive", [])
            continue
        if target in ("LogServerInfoMessage", "LogServerMessage"):
            for arg in frame.get("arguments", []):
                print(f"    [hub] {arg}")
            continue
        if target == "TestStarted":
            continue
        if target == "TestCompleted":
            args = frame.get("arguments", [])
            if len(args) < 5:
                continue
            _cuid, name, status_code, output, duration_ms = args[:5]
            status = {0: "PASS", 1: "FAIL", 2: "SKIP"}.get(status_code, "FAIL")
            if not name and status == "PASS":
                continue  # codeunit-level pseudo-result, see CLI parser docstring
            print(f"    {status} {name or '(codeunit)'} ({duration_ms}ms)")
            if status == "FAIL" and output:
                last_fail_output = output
                for line in output.strip().splitlines():
                    print(f"        {line}")
            if not name:
                run.results.append((status, "(codeunit)", duration_ms, last_fail_output))
            else:
                run.results.append(
                    (status, name, duration_ms, last_fail_output if status == "FAIL" else "")
                )
            continue
        if target == "TestRunCompleted":
            return
        # Unrecognized frame — ignore rather than fail the run.


def run_codeunits_via_hub(
    codeunits: list[int],
    server: str,
    instance: str,
    port: int,
    company: str | None,
    user: str,
    password: str,
    deadline: float,
    codeunit_timeout_seconds: float,
) -> list[CodeunitRun]:
    """Run every codeunit over a persistent TestRunnerHub connection,
    RECONNECTING when the server drops it.

    Mirrors HubBasedTestRunnerService.SetupAndRunTests from the al dotnet
    tool: Initialize once, then RunTests per codeunit sequentially on the
    same connection, using TestRunCompleted as the per-codeunit completion
    signal. This eliminates the process-start + negotiate + auth overhead
    the CLI transport pays on every codeunit.

    Reconnect logic (learned from real Microsoft suites): some tests kill
    the server session — the same class of failure StartupHook patch #21
    exists for — which closes the hub connection mid-suite. The CLI
    transport is naturally immune (fresh connection per codeunit); here we
    recover by reconnecting and retrying the interrupted codeunit ONCE on
    the fresh connection (safe: the duplicate-codeunit-id server quirk is
    per-connection). A codeunit that kills the session twice is recorded
    as errored and skipped. After a per-codeunit timeout the connection
    state is unknown (the server may still be streaming), so it is also
    recycled. Reconnect failures are handled here and never propagate —
    --transport auto's cli fallback must only ever trigger before the
    first test has run.

    IMPORTANT for callers: exceptions escaping this function mean NO test
    has executed (initial connection phase only).
    """
    auth_header = "Basic " + base64.b64encode(f"{user}:{password}".encode()).decode()
    hub_http_base = f"{server}:{port}/{instance}/dev/TestRunnerHub"
    ws_scheme = "wss" if hub_http_base.startswith("https:") else "ws"
    ws_base = re.sub(r"^https?:", ws_scheme + ":", hub_http_base)

    runs: list[CodeunitRun] = []
    # Generous budget: each reconnect costs ~0.5s, and a suite with many
    # session-killer codeunits legitimately needs many. The budget only
    # guards against a server in a crash loop.
    reconnects_left = max(20, len(codeunits) // 4)

    # Initial connection: exceptions propagate (no test has run yet — this
    # is the window where --transport auto may fall back to cli).
    ws, hub = _hub_connect(hub_http_base, ws_base, auth_header, company)

    def reconnect() -> bool:
        """Close the dead connection and open a fresh one. Returns False
        when the budget is exhausted or the server won't accept us."""
        nonlocal ws, hub, reconnects_left
        ws.close()
        while reconnects_left > 0:
            reconnects_left -= 1
            try:
                time.sleep(1)
                ws, hub = _hub_connect(hub_http_base, ws_base, auth_header, company)
                return True
            except (ConnectionError, OSError, TimeoutError) as ex:
                print(f"    WARN: hub reconnect failed ({ex}) — "
                      f"{reconnects_left} attempt(s) left")
        return False

    try:
        i = 0
        while i < len(codeunits):
            cuid = codeunits[i]
            remaining_overall = deadline - time.monotonic()
            if remaining_overall <= 0:
                run = CodeunitRun(cuid)
                run.error = "not run: overall timeout reached"
                runs.append(run)
                i += 1
                continue

            print(f"=== Codeunit {cuid} (hub) ===")
            retried = False
            while True:
                run = CodeunitRun(cuid)  # fresh on retry — discard partials
                cu_start = time.monotonic()
                per_deadline = cu_start + min(codeunit_timeout_seconds, remaining_overall)
                try:
                    _run_one_codeunit_on_hub(hub, run, cuid, per_deadline,
                                             codeunit_timeout_seconds)
                except TimeoutError as ex:
                    run.error = str(ex)
                    run.elapsed_seconds = time.monotonic() - cu_start
                    # Connection state unknown after a timeout — recycle it.
                    if not reconnect():
                        runs.append(run)
                        return _abandon_remaining(runs, codeunits, i + 1,
                                                  "not run: hub reconnect budget exhausted")
                except (ConnectionError, OSError) as ex:
                    run.elapsed_seconds = time.monotonic() - cu_start
                    if not retried:
                        print(f"    WARN: hub connection lost ({ex}) — "
                              f"reconnecting and retrying codeunit {cuid} once")
                        if not reconnect():
                            run.error = f"hub connection lost: {ex}"
                            runs.append(run)
                            return _abandon_remaining(runs, codeunits, i + 1,
                                                      "not run: hub reconnect budget exhausted")
                        retried = True
                        continue
                    # Second kill on the same codeunit: record and move on.
                    run.error = f"session killed twice by this codeunit: {ex}"
                    if not reconnect():
                        runs.append(run)
                        return _abandon_remaining(runs, codeunits, i + 1,
                                                  "not run: hub reconnect budget exhausted")
                else:
                    run.elapsed_seconds = time.monotonic() - cu_start
                    if not run.results and not run.error:
                        run.error = "no test results returned for a Subtype=Test codeunit"
                break

            if run.error:
                print(f"    ERROR: {run.error}")
            # Applies on every exit path above, including the timeout /
            # connection-lost ones that carry partial results.
            run.drop_redundant_codeunit_result()
            runs.append(run)
            i += 1

        try:
            hub.invoke("StopTestExecution", [])
        except Exception:
            pass
    finally:
        ws.close()

    return runs


def _abandon_remaining(
    runs: list[CodeunitRun], codeunits: list[int], start_idx: int, reason: str
) -> list[CodeunitRun]:
    for cuid in codeunits[start_idx:]:
        err_run = CodeunitRun(cuid)
        err_run.error = reason
        runs.append(err_run)
    return runs


# Control characters are invalid in XML 1.0 even when entity-escaped — BC test output
# can contain them (observed in Tests-Misc failure bodies), and emitting them raw makes
# the whole JUnit file unparseable for downstream consumers.
_XML_INVALID_RE = re.compile("[\x00-\x08\x0b\x0c\x0e-\x1f￾￿]|[\ud800-\udfff]")


def _xml_text(s: str) -> str:
    return _XML_INVALID_RE.sub("", s or "")


def write_junit(path: str, runs: list[CodeunitRun], total_elapsed: float) -> None:
    """Same schema as tools/TestRunner's JUnitWriter: one <testsuite> per
    codeunit, classname "Codeunit <id>", <failure> bodies carry the error
    detail, pass cases self-close. Codeunit-level hard errors become a
    single <error> testcase so reporters surface them instead of silently
    showing a shrunken suite."""
    total = sum(len(r.results) for r in runs)
    failures = sum(1 for r in runs for s, *_ in r.results if s == "FAIL")
    skipped = sum(1 for r in runs for s, *_ in r.results if s == "SKIP")
    errors = sum(1 for r in runs if r.error)

    lines = ['<?xml version="1.0" encoding="UTF-8"?>']
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    lines.append(
        f'<testsuites name="altool" tests="{total}" failures="{failures}" '
        f'errors="{errors}" skipped="{skipped}" time="{total_elapsed:.3f}" '
        f'timestamp="{stamp}">'
    )
    for run in runs:
        cls = f"Codeunit {run.codeunit_id}"
        suite_failures = sum(1 for s, *_ in run.results if s == "FAIL")
        suite_skipped = sum(1 for s, *_ in run.results if s == "SKIP")
        suite_errors = 1 if run.error else 0
        suite_tests = len(run.results) + suite_errors
        suite_time = sum(ms for _, _, ms, _ in run.results) / 1000.0
        suite_stamp = run.started.strftime("%Y-%m-%dT%H:%M:%SZ")
        lines.append(
            f"  <testsuite name={quoteattr(cls)} tests=\"{suite_tests}\" "
            f"failures=\"{suite_failures}\" errors=\"{suite_errors}\" "
            f"skipped=\"{suite_skipped}\" time=\"{suite_time:.3f}\" "
            f"timestamp=\"{suite_stamp}\">"
        )
        for status, name, ms, detail in run.results:
            name = _xml_text(name)
            detail = _xml_text(detail)
            case = (
                f"    <testcase classname={quoteattr(cls)} "
                f"name={quoteattr(name)} time=\"{ms / 1000.0:.3f}\""
            )
            if status == "FAIL":
                first_line = (detail.splitlines() or [""])[0][:500]
                lines.append(case + ">")
                lines.append(
                    f"      <failure message={quoteattr(first_line)} "
                    f'type="AssertionFailure">{escape(detail)}</failure>'
                )
                lines.append("    </testcase>")
            elif status == "SKIP":
                lines.append(case + ">")
                lines.append("      <skipped/>")
                lines.append("    </testcase>")
            else:
                lines.append(case + "/>")
        if run.error:
            err = _xml_text(run.error)
            lines.append(
                f"    <testcase classname={quoteattr(cls)} "
                f"name=\"(codeunit run)\" time=\"{run.elapsed_seconds:.3f}\">"
            )
            lines.append(
                f"      <error message={quoteattr(err[:500])} "
                f'type="RunError">{escape(err)}</error>'
            )
            lines.append("    </testcase>")
        lines.append("  </testsuite>")
    lines.append("</testsuites>")

    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Run AL tests via `al runtests` (dev-endpoint TestRunnerHub, BC 28+)"
    )
    ap.add_argument("--app", default="", help="compiled test .app (for codeunit discovery; must already be published+installed). Required unless --probe.")
    ap.add_argument("--probe", action="store_true", help="don't run tests — just check whether the server supports the TestRunnerHub. Exit 0 = supported, 2 = not supported/unreachable. Used by the workflow's test_runner=auto mode.")
    ap.add_argument("--codeunit-range", default="", help="range filter, same syntax as run-tests.sh")
    ap.add_argument("--junit-output", default="", help="write JUnit XML to this path")
    ap.add_argument("--company", default="", help="company name (default: auto-detect via OData)")
    ap.add_argument(
        "--auth",
        default=f"{os.environ.get('BC_SERVER_USERNAME', 'BCRUNNER')}:{os.environ.get('BC_SERVER_PASSWORD', 'Admin123!')}",
        help="user:pass (default: BC_SERVER_USERNAME/BC_SERVER_PASSWORD env vars, falling back to BCRUNNER:Admin123!, matching run-tests.sh)",
    )
    ap.add_argument("--server", default="http://localhost", help="BC server URL, no port (default http://localhost)")
    ap.add_argument("--server-instance", default="BC", help="NST instance name (default BC)")
    ap.add_argument("--port", type=int, default=7049, help="dev endpoint port (default 7049)")
    ap.add_argument("--base-url", default="http://localhost:7048/BC", help="OData base URL for company auto-detect")
    ap.add_argument("--api-port", type=int, default=7052,
                    help="API port used for the company auto-detect fallback (default 7052). "
                         "Only differs from the default when the caller has moved BC's published "
                         "ports — see the reusable workflows' instance_slot input.")
    ap.add_argument("--timeout", type=int, default=30, help="overall timeout, minutes (default 30)")
    ap.add_argument("--codeunit-timeout", type=int, default=10, help="per-codeunit timeout, minutes (default 10)")
    ap.add_argument("--altool-cmd", default="al", help="AL dotnet tool command or path (default 'al')")
    ap.add_argument("--transport", choices=["cli", "hub", "auto"], default="cli",
                     help="'cli' (default) shells out to `al runtests` once per codeunit — "
                          "battle-tested but pays a fresh process start + SignalR negotiate + "
                          "auth per codeunit. 'hub' opens one persistent TestRunnerHub "
                          "connection and runs every codeunit over it — meaningfully faster "
                          "on suites with many codeunits, no `al` CLI dependency at test-run "
                          "time. 'auto' tries hub and falls back to cli when the hub "
                          "connection can't be established (fallback can only trigger before "
                          "any test has run, so no double execution). Same stdout/JUnit "
                          "contract in all modes.")
    args = ap.parse_args()

    user, _, password = args.auth.partition(":")

    if args.probe:
        supported, reason = probe_test_running_support(
            args.server, args.server_instance, args.port, user, password
        )
        print(f"[probe] {'supported' if supported else 'NOT supported'}: {reason}")
        return 0 if supported else 2

    print("=== BC Test Runner (altool / TestRunnerHub) ===")

    if not args.app:
        print("ERROR: --app is required (unless --probe)")
        return 1
    if not os.path.isfile(args.app):
        print(f"ERROR: app file not found: {args.app}")
        return 1

    spans = parse_range_spans(args.codeunit_range) if args.codeunit_range else []
    try:
        codeunits = discover_test_codeunits(args.app, spans)
    except (KeyError, zipfile.BadZipFile, ValueError) as ex:
        print(f"ERROR: cannot read SymbolReference.json from {args.app}: {ex}")
        return 1
    if not codeunits:
        print("ERROR: no Subtype=Test codeunits found in the .app"
              + (f" within range {args.codeunit_range}" if args.codeunit_range else ""))
        return 1
    print("Test codeunits: " + ",".join(str(c) for c in codeunits))

    # Fail fast with one clear message instead of N per-codeunit
    # "Server does not support test running" errors.
    supported, reason = probe_test_running_support(
        args.server, args.server_instance, args.port, user, password
    )
    if not supported:
        print(f"ERROR: {reason}")
        print("       The altool runner needs BC 28.0+ (Dev API 7.0 / TestRunnerHub).")
        print("       Use run-tests.sh (websocket runner) for this server.")
        return 1

    company = args.company
    if company:
        print(f"Company: {company} (pinned via --company)")
    else:
        origin = re.sub(r"(https?://[^:/]+).*", r"\1", args.base_url)
        detect_start = time.monotonic()
        company = detect_company(
            [args.base_url, f"{origin}:{args.api_port}/BC"], user, password
        )
        detect_elapsed = time.monotonic() - detect_start
        if company:
            print(f"Company: {company} (auto-detected in {detect_elapsed:.2f}s)")
            # The warm OData path answers in milliseconds. Anything slow
            # means we fell through to an endpoint that had to be compiled
            # on demand, which is worth telling the caller about once
            # rather than having them pay it on every run.
            if detect_elapsed > 5:
                print("      Tip: pin the company to skip this lookup — pass "
                      "--company, or set the workflow's test_company input.")
        else:
            print("WARN: company auto-detect failed — letting the server pick the default company")

    env = dict(os.environ)
    env["BC_SERVER_USERNAME"] = user
    env["BC_SERVER_PASSWORD"] = password
    env.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    env.setdefault("DOTNET_NOLOGO", "1")

    deadline = time.monotonic() + args.timeout * 60
    runs: list[CodeunitRun] = []
    overall_start = time.monotonic()

    def run_via_cli() -> list[CodeunitRun]:
        cli_runs: list[CodeunitRun] = []
        for cuid in codeunits:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                print(f"ERROR: overall timeout ({args.timeout} min) reached — "
                      f"{len(codeunits) - len(cli_runs)} codeunit(s) not run")
                timed_out = CodeunitRun(cuid)
                timed_out.error = "not run: overall timeout reached"
                cli_runs.append(timed_out)
                break
            print(f"=== Codeunit {cuid} ===")
            run = run_codeunit(
                args.altool_cmd, cuid, args.server, args.server_instance, args.port,
                company, env, min(args.codeunit_timeout * 60, remaining),
            )
            if run.error:
                print(f"    ERROR: {run.error}")
            cli_runs.append(run)
        return cli_runs

    if args.transport in ("hub", "auto"):
        print(f"Transport: hub (persistent TestRunnerHub connection, {len(codeunits)} codeunit(s))")
        try:
            runs = run_codeunits_via_hub(
                codeunits, args.server, args.server_instance, args.port, company,
                user, password, deadline, args.codeunit_timeout * 60,
            )
        except (ConnectionError, OSError, TimeoutError) as ex:
            # These can only escape from the connection/handshake/Initialize
            # phase — run_codeunits_via_hub handles mid-run failures itself —
            # so at this point no test has executed and a cli fallback cannot
            # double-run anything.
            if args.transport == "auto":
                print(f"WARN: hub transport unavailable ({ex}) — falling back to cli transport")
                runs = run_via_cli()
            else:
                print(f"ERROR: hub transport failed: {ex}")
                print("       Falling back is not automatic — rerun with --transport cli or auto.")
                return 1
    else:
        runs = run_via_cli()
    total_elapsed = time.monotonic() - overall_start

    total = sum(len(r.results) for r in runs)
    passed = sum(1 for r in runs for s, *_ in r.results if s == "PASS")
    failed = sum(1 for r in runs for s, *_ in r.results if s == "FAIL")
    skipped = sum(1 for r in runs for s, *_ in r.results if s == "SKIP")
    errors = sum(1 for r in runs if r.error)

    if args.junit_output:
        write_junit(args.junit_output, runs, total_elapsed)
        print(f"JUnit XML written to {args.junit_output}")

    print("")
    # Keep this exact shape — bc-test-from-source.yml greps
    # '[0-9]+ total, [0-9]+ passed, [0-9]+ failed' for telemetry.
    print(f"{total} total, {passed} passed, {failed} failed, "
          f"{skipped} skipped, {errors} codeunit error(s) "
          f"in {total_elapsed:.0f}s")

    if errors:
        for r in runs:
            if r.error:
                print(f"  ERROR codeunit {r.codeunit_id}: {r.error}")
        return 1
    if failed:
        return 1
    if total == 0:
        print("ERROR: no tests ran")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
