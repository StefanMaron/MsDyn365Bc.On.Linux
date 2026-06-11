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
them from --auth (default BCRUNNER:Admin123!, same as run-tests.sh).
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
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
# level failure (e.g. OnRun error) can't vanish silently.
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


def detect_company(base_urls: list[str], user: str, password: str) -> str | None:
    """Auto-detect the first company name via the OData companies API."""
    token = base64.b64encode(f"{user}:{password}".encode()).decode()
    for base in base_urls:
        url = f"{base}/api/v2.0/companies"
        req = urllib.request.Request(url, headers={"Authorization": f"Basic {token}"})
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                data = json.loads(resp.read().decode("utf-8", errors="replace"))
            companies = data.get("value", [])
            if companies:
                return companies[0].get("name") or companies[0].get("Name")
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
    return run


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
            lines.append(
                f"    <testcase classname={quoteattr(cls)} "
                f"name=\"(codeunit run)\" time=\"{run.elapsed_seconds:.3f}\">"
            )
            lines.append(
                f"      <error message={quoteattr(run.error[:500])} "
                f'type="RunError">{escape(run.error)}</error>'
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
    ap.add_argument("--auth", default="BCRUNNER:Admin123!", help="user:pass (default matches run-tests.sh)")
    ap.add_argument("--server", default="http://localhost", help="BC server URL, no port (default http://localhost)")
    ap.add_argument("--server-instance", default="BC", help="NST instance name (default BC)")
    ap.add_argument("--port", type=int, default=7049, help="dev endpoint port (default 7049)")
    ap.add_argument("--base-url", default="http://localhost:7048/BC", help="OData base URL for company auto-detect")
    ap.add_argument("--timeout", type=int, default=30, help="overall timeout, minutes (default 30)")
    ap.add_argument("--codeunit-timeout", type=int, default=10, help="per-codeunit timeout, minutes (default 10)")
    ap.add_argument("--altool-cmd", default="al", help="AL dotnet tool command or path (default 'al')")
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
    if not company:
        origin = re.sub(r"(https?://[^:/]+).*", r"\1", args.base_url)
        company = detect_company(
            [args.base_url, f"{origin}:7052/BC"], user, password
        )
        if company:
            print(f"Company: {company}")
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
    for cuid in codeunits:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            print(f"ERROR: overall timeout ({args.timeout} min) reached — "
                  f"{len(codeunits) - len(runs)} codeunit(s) not run")
            timed_out = CodeunitRun(cuid)
            timed_out.error = "not run: overall timeout reached"
            runs.append(timed_out)
            break
        print(f"=== Codeunit {cuid} ===")
        run = run_codeunit(
            args.altool_cmd, cuid, args.server, args.server_instance, args.port,
            company, env, min(args.codeunit_timeout * 60, remaining),
        )
        if run.error:
            print(f"    ERROR: {run.error}")
        runs.append(run)
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
