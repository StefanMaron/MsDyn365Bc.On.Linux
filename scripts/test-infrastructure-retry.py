#!/usr/bin/env python3
"""Self-test for run-tests-altool.py's infrastructure-retry gate.

The gate decides whether a codeunit that produced no results may be re-run.
Getting it wrong in one direction re-runs a real failure until it passes; in
the other it leaves the CI flake this was written for in place. Both are
silent, so the gate gets a test.

The canned stdout blocks below are real `al runtests` output captured from
StefanMaron/BusinessCentral.AL.Language.Tests CI:
  - run 33962072138, BC 28.4 leg, codeunit 60064
  - run 33964397715, BC 28.0 leg, codeunits 60069 (failed) and 60070 (passed)

Run: python3 scripts/test-infrastructure-retry.py
"""

import importlib.util
import os
import subprocess
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("altool", os.path.join(_HERE, "run-tests-altool.py"))
altool = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(altool)


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

    if failures:
        print(f"\n{len(failures)} check(s) failed: {failures}")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
