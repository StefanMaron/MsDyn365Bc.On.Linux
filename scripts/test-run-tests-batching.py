#!/usr/bin/env python3
"""Drive the real scripts/run-tests.sh against a fake BC and prove its
codeunit batching loses nothing.

WHY A HARNESS AND NOT A UNIT TEST
---------------------------------
chunk-codeunit-ids.py proves the SPLIT is total and merge-junit-batches.py
proves the MERGE is total, each with its own `--self-test`. Neither can see
the thing in between: the bash loop in run-tests.sh that sets the suite up,
runs it, and collects a report once per batch. That loop is where a batch
would actually go missing, and a missing batch does not look like an error —
it looks like a green run with fewer tests in it (issue #57's shape).

So this runs the real script, unmodified, with `curl`, `docker` and `dotnet`
shimmed onto PATH. The fake BC enforces the same 2048-character `CodeunitIds`
limit real BC does, which means the pre-fix script fails here for exactly the
reason it failed on StefanMaron/BusinessCentral.AL.Language.Tests on
2026-09-05, and the fixed one has to actually batch to get past it.

Needs no container, no network and no BC — a couple of seconds:

    python3 scripts/test-run-tests-batching.py
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The exact shape that took the corpus red: 342 five-digit ids join to 2051
# characters, three over BC's field width.
CODEUNIT_IDS = [str(60000 + i) for i in range(342)]
TESTS_PER_CODEUNIT = 2

FAKE_CURL = r'''#!/usr/bin/env python3
import json, os, re, sys

STATE = os.environ["FAKEBC_STATE"]
LIMIT = 2048            # width of CodeunitIds (Text[2048]) on table 99903

def load():
    if not os.path.isfile(STATE):
        return {"requests": {}, "suite": [], "posts": [], "setups": 0,
                "runs": [], "disables": 0, "next": 1}
    with open(STATE) as f:
        return json.load(f)

args = sys.argv[1:]
out = wfmt = data = url = None
method = "GET"
fail_mode = False
i = 0
while i < len(args):
    a = args[i]
    if a == "-o":   out = args[i + 1];    i += 2; continue
    if a == "-w":   wfmt = args[i + 1];   i += 2; continue
    if a == "-X":   method = args[i + 1]; i += 2; continue
    if a == "-d":   data = args[i + 1];   i += 2; continue
    if a in ("-u", "--max-time", "-H"):   i += 2; continue
    if a.startswith("-"):
        if "f" in a.lstrip("-"):
            fail_mode = True
        i += 1
        continue
    url = a
    i += 1

s = load()
code, body = 404, ""

if url.endswith("/ODataV4/Company"):
    code, body = 200, json.dumps({"value": [
        {"Name": "CRONUS", "Id": "aaa-bbb", "Evaluation_Company": True}]})
elif url.endswith("/api/v2.0/companies"):
    code, body = 200, json.dumps({"value": [{"name": "CRONUS", "id": "aaa-bbb"}]})
elif "Microsoft.NAV.setupSuite" in url:
    rid = re.search(r"codeunitRunRequests\(([^)]+)\)", url).group(1)
    s["suite"] = [t for t in s["requests"][rid].split(",") if t]
    s["setups"] += 1
    code = 200
elif "Microsoft.NAV.disableTests" in url:
    s["disables"] += 1
    code = 200
elif url.rstrip("/").endswith("/codeunitRunRequests") and method == "POST":
    ids = json.loads(data)["CodeunitIds"]
    s["posts"].append(ids)
    if len(ids) > LIMIT:
        # Byte-for-byte the error real BC returns; this is the bug.
        code, body = 400, json.dumps({"error": {
            "code": "Application_StringExceededLength",
            "message": "The length of the string is %d, but it must be less "
                       "than or equal to %d characters." % (len(ids), LIMIT)}})
    else:
        rid = "req-%d" % s["next"]
        s["next"] += 1
        s["requests"][rid] = ids
        code, body = 200, json.dumps({"Id": rid, "CodeunitIds": ids})
elif url.rstrip("/").endswith("/codeunitRunRequests"):
    code, body = 200, json.dumps({"value": []})
elif "/testResults" in url:
    per = int(os.environ.get("FAKEBC_TESTS_PER_CU", "2"))
    rows = [{"testSuite": "DEFAULT", "lineType": "Function",
             "testCodeunit": int(cu), "functionName": "T%d" % n}
            for cu in s["suite"] for n in range(per)]
    code, body = 200, json.dumps({"value": rows})

with open(STATE, "w") as f:
    json.dump(s, f)

if out and out != "/dev/null":
    with open(out, "w") as f:
        f.write(body)
elif out != "/dev/null":
    sys.stdout.write(body)
if wfmt:
    sys.stdout.write(wfmt.replace("%{http_code}", str(code)))
sys.exit(22 if (fail_mode and code >= 400) else 0)
'''

# No compose project here, so run-tests.sh falls through to a host-side
# dotnet — which is also shimmed.
FAKE_DOCKER = "#!/bin/sh\nexit 1\n"

FAKE_DOTNET = r'''#!/usr/bin/env python3
import json, os, sys

STATE = os.environ["FAKEBC_STATE"]
with open(STATE) as f:
    s = json.load(f)

junit = None
args = sys.argv[1:]
for i, a in enumerate(args):
    if a == "--junit-output":
        junit = args[i + 1]
try:
    sys.stdin.read()          # --password-stdin
except Exception:
    pass

suite = list(s["suite"])
s["runs"].append(suite)
with open(STATE, "w") as f:
    json.dump(s, f)

per = int(os.environ.get("FAKEBC_TESTS_PER_CU", "2"))

# Sabotage hook: pretend the Nth batch's runner exited 0 but never wrote its
# report. That is the false-green the completeness check has to catch, and it
# is invisible to an exit-code check.
if os.environ.get("FAKEBC_DROP_RUN") == str(len(s["runs"])):
    print("0 total, 0 passed, 0 failed, 0 skipped")
    sys.exit(0)

parts = ['<?xml version="1.0" encoding="utf-8"?>', '<testsuites name="DEFAULT">']
for cu in suite:
    parts.append('  <testsuite name="Codeunit %s" tests="%d" failures="0" '
                 'errors="0" skipped="0" time="0.5">' % (cu, per))
    for n in range(per):
        parts.append('    <testcase classname="Codeunit %s" name="T%d" time="0.1"/>'
                     % (cu, n))
    parts.append("  </testsuite>")
parts.append("</testsuites>")
if junit:
    d = os.path.dirname(junit)
    if d:
        os.makedirs(d, exist_ok=True)
    with open(junit, "w") as f:
        f.write("\n".join(parts))

n = len(suite) * per
print("\n=== Results (1s) ===")
print("%d total, %d passed, 0 failed, 0 skipped" % (n, n))
sys.exit(0 if n else 1)
'''


def _make_shims(root: str) -> str:
    bindir = os.path.join(root, "bin")
    os.makedirs(bindir, exist_ok=True)
    for name, body in (("curl", FAKE_CURL), ("docker", FAKE_DOCKER),
                       ("dotnet", FAKE_DOTNET)):
        path = os.path.join(bindir, name)
        with open(path, "w") as fh:
            fh.write(body)
        os.chmod(path, 0o755)
    return bindir


class Run:
    def __init__(self, rc: int, out: str, state: dict, junit: str):
        self.rc, self.out, self.state, self.junit = rc, out, state, junit

    @property
    def posts(self) -> list[str]:
        return self.state["posts"]

    @property
    def dispatched(self) -> list[str]:
        """Every codeunit id the runner was actually asked to execute, in
        order, flattened across batches."""
        return [cu for run in self.state["runs"] for cu in run]

    def merged_root(self):
        """Parsed merged report, or None. Returns None rather than raising so
        a run that produced no report at all reports as failed checks instead
        of a traceback — which is what the PRE-FIX script does here."""
        try:
            return ET.parse(self.junit).getroot()
        except (OSError, ET.ParseError):
            return None


def run_case(root: str, script: str, ids: str, env: dict | None = None,
             extra: list[str] | None = None) -> Run:
    wd = tempfile.mkdtemp(dir=root)
    state = os.path.join(wd, "state.json")
    junit = os.path.join(wd, "junit.xml")
    e = dict(os.environ)
    e["PATH"] = _make_shims(root) + os.pathsep + e["PATH"]
    e["FAKEBC_STATE"] = state
    e["FAKEBC_TESTS_PER_CU"] = str(TESTS_PER_CODEUNIT)
    e.update(env or {})
    proc = subprocess.run(
        [script, "--codeunit-range", ids,
         "--base-url", "http://localhost:7048/BC",
         "--dev-url", "http://localhost:7049/BC/dev",
         "--junit-output", junit] + (extra or []),
        capture_output=True, text=True, env=e, stdin=subprocess.DEVNULL)
    with open(state) as fh:
        st = json.load(fh)
    return Run(proc.returncode, proc.stdout + proc.stderr, st, junit)


def main() -> int:
    script = os.path.join(REPO, "scripts", "run-tests.sh")
    if not os.path.isfile(script):
        print(f"ERROR: {script} not found")
        return 2

    failures: list[str] = []

    def check(label: str, cond: bool) -> None:
        print(("  PASS  " if cond else "  FAIL  ") + label)
        if not cond:
            failures.append(label)

    ids = ",".join(CODEUNIT_IDS)
    expected_total = len(CODEUNIT_IDS) * TESTS_PER_CODEUNIT

    with tempfile.TemporaryDirectory() as root:
        print(f"run-tests.sh batching ({len(CODEUNIT_IDS)} codeunits, "
              f"{len(ids)} chars):")
        check("the id list really is over BC's 2048-char field limit",
              len(ids) > 2048)

        r = run_case(root, script, ids)
        check("the run succeeds where a single request would be rejected",
              r.rc == 0)
        check("no request body exceeds BC's field limit",
              bool(r.posts) and max(len(p) for p in r.posts) <= 2048)
        check("the list was actually split into more than one batch",
              len(r.state["runs"]) > 1)
        check("every dispatched codeunit is executed exactly once, in order",
              r.dispatched == CODEUNIT_IDS)
        merged = r.merged_root()
        check("the merged report holds one testsuite per codeunit",
              merged is not None
              and len(merged.findall("testsuite")) == len(CODEUNIT_IDS))
        check("the merged roll-up counts every batch's tests",
              merged is not None and merged.get("tests") == str(expected_total))
        # bc-test-from-source.yml greps this line and takes the LAST match, so
        # a per-batch line landing last would report one batch as the run.
        summary = [ln for ln in r.out.splitlines() if " total, " in ln]
        check("the LAST summary line is the run total, not a batch's",
              bool(summary) and summary[-1].startswith(f"{expected_total} total,"))
        check("the completeness of the batching is stated in the log",
              f"All {len(CODEUNIT_IDS)} dispatched codeunit(s) reported results"
              in r.out)

        print("\na lost batch must not read as a smaller green run:")
        # Third batch's runner exits 0 and writes nothing — the exact shape an
        # exit-code check cannot see.
        r2 = run_case(root, script, ids, env={"FAKEBC_DROP_RUN": "3"})
        check("the run FAILS", r2.rc != 0)
        check("the lost batch's codeunits are named",
              "produced NO result at all" in r2.out and "60341" in r2.out)
        check("the missing batch report is named", "batch report missing" in r2.out)

        print("\na list that fits still behaves like an un-batched run:")
        r3 = run_case(root, script, "60001,60002,60003")
        check("it succeeds", r3.rc == 0)
        check("it makes exactly one suite-setup request", len(r3.posts) == 1)
        check("it runs the runner exactly once", len(r3.state["runs"]) == 1)
        check("it still reports a run total",
              any(ln.startswith("6 total, 6 passed, 0 failed")
                  for ln in r3.out.splitlines()))

    if failures:
        print(f"\n{len(failures)} check(s) failed: {', '.join(failures)}")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
