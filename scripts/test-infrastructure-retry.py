#!/usr/bin/env python3
"""Self-test for the run-tests-hybrid.py / run-tests-altool.py / TestRunner
completeness-and-retry machinery: two mechanisms from issues #55 and #57,
tested together because they answer the same question from opposite ends —
"did every dispatched codeunit come back with a result" — and a fixture that
proves one without the other would miss exactly the gap between them.

1. run-tests-altool.py's infrastructure-retry gate (#55). Decides whether a
   codeunit that produced no results may be re-run. Getting it wrong in one
   direction re-runs a real failure until it passes; in the other it leaves
   the CI flake this was written for in place. Both are silent, so the gate
   gets a test.

   The canned stdout blocks below are real `al runtests` output captured
   from StefanMaron/BusinessCentral.AL.Language.Tests CI:
     - run 33962072138, BC 28.4 leg, codeunit 60064
     - run 33964397715, BC 28.0 leg, codeunits 60069 (failed), 60070 (passed)

2. run-tests-hybrid.py's completeness check (#57). "The merged total is
   non-zero" cannot see a codeunit that was dispatched but never produced a
   result at all — not even a failure. That is a path to a FALSE GREEN, not
   a false red. It has not been observed producing a green leg with
   codeunits missing — the one real instance so far (corpus PR #146, run
   33957903095) aborted at the very first codeunit of its websocket leg, so
   that leg contributed 0 tests and the overall run still exited non-zero.
   The gap is reachable, not observed: the exact same abort landing after
   even one codeunit had already passed would leave the merged summary
   reading "N passed, 0 failed" while every codeunit after it went
   unreported — nothing before this fix would catch that. This test pins
   the mechanism the fix adds, not a reproduction of an observed failure.

Run: python3 scripts/test-infrastructure-retry.py
"""

import importlib.util
import os
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("altool", os.path.join(_HERE, "run-tests-altool.py"))
altool = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(altool)

_hybrid_spec = importlib.util.spec_from_file_location("hybrid", os.path.join(_HERE, "run-tests-hybrid.py"))
hybrid = importlib.util.module_from_spec(_hybrid_spec)
_hybrid_spec.loader.exec_module(hybrid)


class _FakeCompleted:
    def __init__(self, stdout, returncode):
        self.stdout = stdout
        self.returncode = returncode


def _run_with_output(stdout, returncode):
    """Drive the REAL run_codeunit parser over canned `al runtests` output."""
    real = subprocess.run
    subprocess.run = lambda *a, **k: _FakeCompleted(stdout, returncode)
    try:
        return altool.run_codeunit("al", 60069, "http://localhost", "BC", 7049, None, {}, 60)
    finally:
        subprocess.run = real


# --- the failure this exists for: hub Initialize threw, nothing ran ----------
HUB_INIT_FAILURE = """MergeFromLaunchJson: No project path specified, skipping launch.json lookup
Targeting server 'http://localhost' and server instance 'BC'.
Using user name and password authentication. User name used is: 'BCRUNNER'.
Test hub connected.
TestRunnerHub connected.
SignalR hub connection established with context [Y0qVjrlxO19GLn__pYh8jg]
An unexpected error occurred invoking 'Initialize' on the server.
Test run failed: An unexpected error occurred invoking 'Initialize' on the server."""

# --- a real test failure: MUST NOT be retried --------------------------------
REAL_TEST_FAILURE = """Test hub connected.
TestRunnerHub connected.
Test run completed: 1 passed, 1 failed, 0 skipped.

Results:
  PASS RecordRef_SetView_FiltersRecords (104ms)
  FAIL RecordRef_Reset_ClearsFilters (88ms)
Assert.AreEqual failed. Expected: <5>. Actual: <3>."""

# --- a clean pass ------------------------------------------------------------
CLEAN_PASS = """Test hub connected.
TestRunnerHub connected.
Test run completed: 2 passed, 0 failed, 0 skipped.

Results:
  PASS RecordRef_Field_ByNumber_ReturnsFieldRef (917ms)
  PASS RecordRef_Field_ValueSetGet_Roundtrips (269ms)"""

# --- zero results, but NOT an infrastructure error: MUST NOT be retried ------
NO_RESULTS_RETURNED = """Test hub connected.
TestRunnerHub connected.
No test results were returned.
Test run completed: 0 passed, 0 failed, 0 skipped."""

UNRECOGNISED = """Some output nobody has seen before
and no summary line at all"""


def _write_junit(path: str, codeunit_ids: list[int]) -> None:
    """A minimal per-leg JUnit file with one passing <testsuite> per id — the
    shape both write_junit (run-tests-altool.py) and JUnitWriter.Write
    (tools/TestRunner) actually produce for a codeunit that ran cleanly."""
    root = ET.Element("testsuites", {"name": "leg", "tests": str(len(codeunit_ids)),
                                      "failures": "0", "errors": "0", "skipped": "0",
                                      "time": "0.0"})
    for cuid in codeunit_ids:
        ET.SubElement(root, "testsuite", {
            "name": f"Codeunit {cuid}", "tests": "1", "failures": "0",
            "errors": "0", "skipped": "0", "time": "0.1",
            "timestamp": "2026-09-05T00:00:00Z",
        })
    ET.ElementTree(root).write(path, encoding="unicode", xml_declaration=True)


def check_hybrid_completeness(check) -> None:
    """Drive the REAL merge_junit over fixture per-leg JUnit files simulating
    exactly the shape that dropped codeunit 60064/60069/60942 would have
    produced: two legs report back, a third id was dispatched and NEVER
    produced a <testsuite> anywhere — no pass, no fail, no error entry,
    because whatever runner owned it exited before creating one."""
    with tempfile.TemporaryDirectory() as tmp:
        hub_path = os.path.join(tmp, "hub.xml")
        ws_path = os.path.join(tmp, "ws.xml")
        out_path = os.path.join(tmp, "merged.xml")
        # Dispatched: {1, 2, 3, 4}. Hub leg reports 1, 2. Websocket leg
        # reports only 3 — 4 was dispatched to it and never came back.
        _write_junit(hub_path, [1, 2])
        _write_junit(ws_path, [3])
        seen_ids = hybrid.merge_junit([hub_path, ws_path], out_path, total_elapsed=1.0)

        check("merge_junit reports every id that DID come back",
              seen_ids == {1, 2, 3})

        all_ids = {1, 2, 3, 4}
        missing_ids = sorted(all_ids - seen_ids)
        check("the completeness diff catches the dropped id (4), and only it",
              missing_ids == [4])

        hub_ids, ws_ids = {1, 2}, {3, 4}
        missing_ws = sorted(ws_ids & set(missing_ids))
        check("the missing id is correctly attributed to the leg that lost it (websocket)",
              missing_ws == [4])

        check("nothing is reported missing when every dispatched id came back",
              sorted({1, 2, 3} - hybrid.merge_junit([hub_path, ws_path],
                                                      os.path.join(tmp, "merged2.xml"),
                                                      total_elapsed=1.0)) == [])

        # The bug this closes: the OLD completeness signal was "merged total
        # != 0" — true here even though id 4 never ran anything at all.
        merged_root = ET.parse(out_path).getroot()
        old_signal_would_have_passed = int(merged_root.get("tests", "0")) != 0
        check("the old signal ('total != 0') would have missed this — proving "
              "the new check adds real coverage, not a no-op",
              old_signal_would_have_passed and missing_ids == [4])


def main() -> int:
    failures = []

    def check(label, cond):
        if cond:
            print(f"  PASS  {label}")
        else:
            print(f"  FAIL  {label}")
            failures.append(label)

    print("infrastructure-retry gate:")

    r = _run_with_output(HUB_INIT_FAILURE, 1)
    check("hub Initialize failure has no parsed results", r.results == [])
    check("hub Initialize failure is classified as infrastructure",
          altool.is_infrastructure_error(r))

    r = _run_with_output(REAL_TEST_FAILURE, 1)
    check("a real FAIL is parsed into results", any(s == "FAIL" for s, *_ in r.results))
    check("a real FAIL is NOT retried", not altool.is_infrastructure_error(r))

    r = _run_with_output(CLEAN_PASS, 0)
    check("a clean pass records no error", r.error is None)
    check("a clean pass is NOT retried", not altool.is_infrastructure_error(r))

    r = _run_with_output(NO_RESULTS_RETURNED, 0)
    check("'no test results were returned' is an error", bool(r.error))
    check("'no test results were returned' is NOT retried (could be a real empty codeunit)",
          not altool.is_infrastructure_error(r))

    r = _run_with_output(UNRECOGNISED, 0)
    check("unrecognized output is NOT retried", not altool.is_infrastructure_error(r))

    r = altool.CodeunitRun(60069)
    r.error = "timed out after 600s"
    check("a timeout is NOT retried (a hang can be a real deadlock)",
          not altool.is_infrastructure_error(r))

    print("")
    print("run-tests-hybrid.py completeness check:")
    check_hybrid_completeness(check)

    if failures:
        print(f"\n{len(failures)} check(s) failed: {failures}")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
