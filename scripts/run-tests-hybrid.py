#!/usr/bin/env python3
"""
run-tests-hybrid.py — Run a test app's codeunits through the fast
altool/TestRunnerHub runner and the classic websocket runner, split
per-codeunit by static analysis, and merge the results into one report.

Why: see https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues/27 and
scripts/classify-handler-codeunits.py's docstring. The altool runner
(scripts/run-tests-altool.py) is much faster but doesn't run tests under an
AL Test Runner codeunit, so [HandlerFunctions] dispatch — including the
"unhandled modal → refuse" behavior — isn't equivalent to the classic
websocket runner (scripts/run-tests.sh). Codeunits that can't be affected by
that gap (no handler-function usage, per static analysis) run on the fast
path; everything else runs on the slow, correct path. The split is decided
ONCE, up front, from AL source — nothing ever runs twice.

This requires the test app's AL source (--al-source-dir, repeatable). If no
source directories are given, every codeunit is conservatively routed to
the websocket runner (unproven safety == unsafe) and this script behaves
like a slower version of run-tests.sh — pass --al-source-dir to get the
speedup.

The two runners are invoked ONE AFTER THE OTHER, altool first, each
restricted to its own codeunit subset via --codeunit-range with an explicit
id list. They used to run concurrently on two threads, justified as "they
hit different BC endpoints, so there's no shared-resource conflict". The
endpoints do differ; what they share is the whole of the rest of it — one
service tier, one tenant, one company, and one set of APPLICATION METADATA.
That last one is the resource that was in conflict, and it produced a real
flake: see StefanMaron/BusinessCentral.AL.Language.Tests#158 and the
comment on run_legs below for the evidence.

The altool leg defaults to --altool-transport cli, NOT hub/auto, even
though hub is ~40x faster per codeunit. Per
https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues/27#issuecomment-5239717306:
tests asserting that a SingleInstance codeunit's state resets at the
per-test-codeunit isolation boundary (RequiredTestIsolation = Codeunit, the
AL default) fail under --transport hub and pass under websocket, on
codeunits with no [HandlerFunctions] at all — so this is a second,
independent hub-transport bug that the static classification above cannot
see (it's about state leaking BETWEEN codeunits in one run, not about any
one codeunit's own source). cli spawns a fresh `al runtests` process — a
fresh connection — per codeunit, matching the per-codeunit isolation
websocket already gets from its own reconnect-before-every-codeunit design;
hub's one persistent connection for the whole run apparently doesn't tear
down and recreate that isolation scope. Until cli is verified clean of the
same leak, it's the responsible default — pass --altool-transport hub/auto
explicitly to opt back into the faster, less-proven path. Output contract is
kept identical to run-tests.sh / run-tests-altool.py so existing workflow
parsing (the "Test codeunits: ..." line, the "N total, P passed, F failed,
..." summary line, PIPESTATUS-based exit code checks) keeps working
unchanged:
  - prints "Test codeunits: <comma-separated ids>" (full set, both runners)
  - prints "<N> total, <P> passed, <F> failed, <S> skipped, <E> codeunit
    error(s) in <T>s"
  - exit 0 only when at least one test ran and nothing failed or errored
  - --junit-output merges both runners' JUnit XML into a single file

Usage:
  python3 scripts/run-tests-hybrid.py \
      --app build/MyTestApp.app \
      --al-source-dir path/to/MyTestApp/src \
      --codeunit-range "50000..50100" \
      --junit-output build/junit.xml \
      --altool-cmd "$HOME/.dotnet/tools/al"
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

_SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))


def _load_module(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, os.path.join(_SCRIPT_DIR, filename))
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


altool = _load_module("_run_tests_altool", "run-tests-altool.py")
classify = _load_module("_classify_handler_codeunits", "classify-handler-codeunits.py")


def _ids_str(ids: list[int]) -> str:
    return ",".join(str(i) for i in ids)


class _RunnerResult:
    def __init__(self, name: str):
        self.name = name
        self.rc: int | None = None
        self.stdout: str = ""
        self.junit_path: str | None = None
        self.invoked = False


def _run_altool(args, codeunit_ids: list[int], junit_path: str) -> _RunnerResult:
    r = _RunnerResult("altool")
    if not codeunit_ids:
        return r
    r.invoked = True
    cmd = [
        sys.executable, os.path.join(_SCRIPT_DIR, "run-tests-altool.py"),
        "--app", args.app,
        "--codeunit-range", _ids_str(codeunit_ids),
        "--junit-output", junit_path,
        "--altool-cmd", args.altool_cmd,
        "--transport", args.altool_transport,
        "--auth", args.auth,
        "--base-url", args.base_url,
        "--server", args.server,
        "--server-instance", args.server_instance,
        "--port", str(args.port),
        "--api-port", str(args.api_port),
        "--timeout", str(args.timeout),
        "--codeunit-timeout", str(args.codeunit_timeout),
    ]
    if args.company:
        cmd += ["--company", args.company]
    proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    r.rc = proc.returncode
    r.stdout = proc.stdout or ""
    r.junit_path = junit_path if os.path.isfile(junit_path) else None
    return r


def _run_websocket(args, codeunit_ids: list[int], junit_path: str) -> _RunnerResult:
    r = _RunnerResult("websocket")
    if not codeunit_ids:
        return r
    r.invoked = True
    cmd = [
        os.path.join(_SCRIPT_DIR, "run-tests.sh"),
        "--app", args.app,
        "--codeunit-range", _ids_str(codeunit_ids),
        "--junit-output", junit_path,
        "--auth", args.auth,
        "--base-url", args.base_url,
        "--dev-url", args.dev_url,
        "--timeout", str(args.timeout),
    ]
    if args.company:
        cmd += ["--company", args.company]
    proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    r.rc = proc.returncode
    r.stdout = proc.stdout or ""
    r.junit_path = junit_path if os.path.isfile(junit_path) else None
    return r


def run_legs(args, hub_ids: list[int], ws_ids: list[int],
             hub_junit: str, ws_junit: str) -> dict[str, _RunnerResult]:
    """Run both legs SEQUENTIALLY, altool first, and return their results.

    WHY NOT CONCURRENTLY (StefanMaron/BusinessCentral.AL.Language.Tests#158)
      These two legs used to run on two threads at once. The justification was
      that they hit different BC endpoints — the dev-endpoint hub versus the
      client-session websocket — and therefore could not conflict. They do hit
      different endpoints. They also share one service tier, one tenant, one
      company and one set of application metadata, and that last one is what
      broke: a client session that has a page open is invalidated when the
      metadata generation moves under it, and BC says so with

        The page definition has changed while opening the page, please try to
        re-open the page. Page ID: <n>

      Measured over the 18 most recent failed runs of the corpus that consumes
      this workflow: five instances, every one on a 28.x leg and none on 27.0,
      27.3 or 27.5 — which is the signature, because `test_runner: auto` falls
      back to the single websocket runner below BC 28 and there is no second
      leg there to race. Always codeunit 60933, and a DIFFERENT test inside it
      each time, because the websocket leg's codeunit order is not
      deterministic. Position predicted the outcome: five failures with 60933
      at position 2 or 3 of the leg, and the one observation of it running at
      position 12 passed. The websocket leg takes 25-29 s against the altool
      leg's 208-271 s, so it fits entirely inside the altool leg's opening
      phase — no part of it ever ran alone.

      What is NOT established: the BC event-log capture is tail-truncated
      across the failure window, so there is no server-side trace of a
      metadata generation actually changing. Serialising is justified by that
      correlation plus the mechanism, not by a proven server-side record.

    WHY ALTOOL FIRST, and not the other order
      The websocket leg republishes the app (`bc_publish_app` in
      run-tests.sh, SchemaUpdateMode=forcesync) before it runs anything. That
      is the one metadata-mutating action in either leg. Running it last means
      it happens when nothing else holds a session, instead of at the moment
      the other leg is opening its first ones.

    Cost: the websocket leg's wall time is no longer hidden inside the altool
    leg's. On the corpus that is 25-29 s added to a 208-271 s leg, per BC
    version, across an eight-version matrix on a shared Actions queue.
    """
    results: dict[str, _RunnerResult] = {}
    results["altool"] = _run_altool(args, hub_ids, hub_junit)
    results["websocket"] = _run_websocket(args, ws_ids, ws_junit)
    return results


# One <testsuite> per codeunit that ever got a CodeunitRun/RecordedResult,
# named "Codeunit <id>" by both write_junit (run-tests-altool.py) and
# JUnitWriter.Write (tools/TestRunner) — matches regardless of PASS, FAIL,
# or "produced zero results, here is why" placeholder. Only a codeunit whose
# id was silently dropped somewhere between dispatch and the report is
# missing here.
_SUITE_CODEUNIT_ID = re.compile(r"^Codeunit (\d+)$")


def merge_junit(paths: list[str], out_path: str, total_elapsed: float) -> set[int]:
    """Merge per-runner JUnit files into one, write it, and return the
    codeunit ids that produced a <testsuite> in the result — the input a
    completeness check needs. "The merged total is non-zero" cannot see a
    codeunit that was dispatched but never came back at all; this can."""
    suites: list[ET.Element] = []
    for p in paths:
        if not p or not os.path.isfile(p):
            continue
        try:
            root = ET.parse(p).getroot()
        except ET.ParseError:
            continue
        suites.extend(root.findall("testsuite"))

    def isum(attr: str) -> int:
        return sum(int(s.get(attr, "0")) for s in suites)

    merged = ET.Element(
        "testsuites",
        {
            "name": "hybrid",
            "tests": str(isum("tests")),
            "failures": str(isum("failures")),
            "errors": str(isum("errors")),
            "skipped": str(isum("skipped")),
            "time": f"{total_elapsed:.3f}",
            "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        },
    )
    seen_ids: set[int] = set()
    for s in suites:
        merged.append(s)
        m = _SUITE_CODEUNIT_ID.match(s.get("name") or "")
        if m:
            seen_ids.add(int(m.group(1)))

    parent = os.path.dirname(out_path)
    if parent:
        os.makedirs(parent, exist_ok=True)
    ET.ElementTree(merged).write(out_path, encoding="unicode", xml_declaration=True)
    return seen_ids


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--app", required=True, help="compiled test .app (published+installed already)")
    ap.add_argument("--al-source-dir", action="append", default=[], dest="al_source_dirs",
                     help="AL source directory to scan for [HandlerFunctions] usage; repeatable. "
                          "Codeunits with no matching source are routed to websocket.")
    ap.add_argument("--codeunit-range", default="", help="range filter, same syntax as run-tests.sh")
    ap.add_argument("--junit-output", default="", help="write merged JUnit XML to this path")
    ap.add_argument("--company", default="")
    ap.add_argument(
        "--auth",
        default=f"{os.environ.get('BC_SERVER_USERNAME', 'BCRUNNER')}:{os.environ.get('BC_SERVER_PASSWORD', 'Admin123!')}",
    )
    ap.add_argument("--base-url", default="http://localhost:7048/BC")
    ap.add_argument("--dev-url", default="http://localhost:7049/BC/dev", help="passed to run-tests.sh (websocket)")
    ap.add_argument("--server", default="http://localhost", help="passed to run-tests-altool.py")
    ap.add_argument("--server-instance", default="BC")
    ap.add_argument("--port", type=int, default=7049)
    ap.add_argument("--api-port", type=int, default=7052,
                     help="passed to run-tests-altool.py's --api-port (company auto-detect "
                          "fallback) — pass the same value the caller uses for its own API port, "
                          "e.g. the reusable workflows' instance_slot-derived BC_API_PORT")
    ap.add_argument("--altool-cmd", default="al")
    ap.add_argument("--altool-transport", default="cli", choices=["cli", "hub", "auto"],
                     help="transport for the fast-path leg (default cli — see module "
                          "docstring for why hub/auto aren't the default despite being faster)")
    ap.add_argument("--timeout", type=int, default=30)
    ap.add_argument("--codeunit-timeout", type=int, default=10)
    args = ap.parse_args()

    print("=== BC Test Runner (hybrid: altool fast-path + websocket fallback) ===")

    if not os.path.isfile(args.app):
        print(f"ERROR: app file not found: {args.app}")
        return 1

    spans = altool.parse_range_spans(args.codeunit_range) if args.codeunit_range else []
    try:
        all_ids = altool.discover_test_codeunits(args.app, spans)
    except (KeyError, ValueError, OSError) as ex:
        print(f"ERROR: cannot read SymbolReference.json from {args.app}: {ex}")
        return 1
    if not all_ids:
        print("ERROR: no Subtype=Test codeunits found in the .app"
              + (f" within range {args.codeunit_range}" if args.codeunit_range else ""))
        return 1
    print("Test codeunits: " + _ids_str(all_ids))

    if args.al_source_dirs:
        info = classify.classify_al_source(args.al_source_dirs)
        hub_ids = sorted(c for c in all_ids if info.get(c, {}).get("needs_websocket", True) is False)
        ws_ids = sorted(c for c in all_ids if c not in hub_ids)
    else:
        print("No --al-source-dir given — routing everything through the websocket runner.")
        hub_ids, ws_ids = [], list(all_ids)

    print(f"Fast path (altool/hub): {len(hub_ids)} codeunit(s): {_ids_str(hub_ids) or '(none)'}")
    print(f"Slow path (websocket):  {len(ws_ids)} codeunit(s): {_ids_str(ws_ids) or '(none)'}")

    overall_start = time.monotonic()
    hub_junit = f"{args.junit_output}.hub.xml" if args.junit_output else "/tmp/run-tests-hybrid-hub-junit.xml"
    ws_junit = f"{args.junit_output}.ws.xml" if args.junit_output else "/tmp/run-tests-hybrid-ws-junit.xml"

    results = run_legs(args, hub_ids, ws_ids, hub_junit, ws_junit)
    total_elapsed = time.monotonic() - overall_start

    for name in ("altool", "websocket"):
        r = results[name]
        if not r.invoked:
            continue
        print("")
        print(f"--- {name} runner output ---")
        print(r.stdout)

    # Merge — and check completeness — unconditionally, even when
    # --junit-output wasn't requested: the merged codeunit-id set is how a
    # dropped codeunit gets caught, not just how the file gets written.
    junit_paths = [r.junit_path for r in results.values() if r.junit_path]
    merged_out = args.junit_output or "/tmp/run-tests-hybrid-merged-junit.xml"
    seen_ids = merge_junit(junit_paths, merged_out, total_elapsed)
    if args.junit_output:
        print(f"Merged JUnit XML written to {args.junit_output}")
    for p in (hub_junit, ws_junit):
        try:
            os.remove(p)
        except OSError:
            pass
    if not args.junit_output:
        try:
            os.remove(merged_out)
        except OSError:
            pass

    # Aggregate counts straight from the merged JUnit so the summary line
    # matches exactly what got written, rather than re-parsing two
    # differently-shaped stdout formats.
    total = passed = failed = skipped = errors = 0
    if os.path.isfile(merged_out):
        root = ET.parse(merged_out).getroot()
        total = int(root.get("tests", "0"))
        failed = int(root.get("failures", "0"))
        errors = int(root.get("errors", "0"))
        skipped = int(root.get("skipped", "0"))
        passed = total - failed - skipped - errors

    # Completeness: every id in all_ids was dispatched to exactly one of
    # hub_ids/ws_ids (discover_test_codeunits already intersected any
    # --codeunit-range filter and reflects only what actually compiled into
    # this .app, so all_ids IS the exact expected set — nothing legitimate
    # is missing from it). A codeunit missing a <testsuite> entry never ran
    # ANYTHING, including any infrastructure-error placeholder a runner
    # would otherwise have recorded for it — that only happens when a
    # runner's own early-exit path drops it before ever creating a
    # CodeunitRun. "total != 0" cannot see this; it can look completely
    # healthy while codeunits are missing (see
    # https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues/57).
    missing_ids = sorted(set(all_ids) - seen_ids)
    if missing_ids:
        missing_hub = sorted(set(hub_ids) & set(missing_ids))
        missing_ws = sorted(set(ws_ids) & set(missing_ids))
        print("")
        print(f"ERROR: {len(missing_ids)} of {len(all_ids)} codeunit(s) produced NO result "
              f"at all — no <testsuite>, not even a failure — in the merged report: "
              f"{_ids_str(missing_ids)}")
        if missing_hub:
            print(f"       {len(missing_hub)} from the fast path (altool/hub): {_ids_str(missing_hub)}")
        if missing_ws:
            print(f"       {len(missing_ws)} from the slow path (websocket): {_ids_str(missing_ws)}")

    print("")
    # Keep this exact shape — bc-test-from-source.yml greps
    # '[0-9]+ total, [0-9]+ passed, [0-9]+ failed' for telemetry.
    print(f"{total} total, {passed} passed, {failed} failed, "
          f"{skipped} skipped, {errors} codeunit error(s) "
          f"in {total_elapsed:.0f}s")

    any_runner_failed = any(
        r.invoked and r.rc not in (0, None) for r in results.values()
    )
    if any_runner_failed:
        return 1
    if missing_ids:
        return 1
    if total == 0:
        print("ERROR: no tests ran")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
