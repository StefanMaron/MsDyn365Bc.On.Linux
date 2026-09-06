#!/usr/bin/env python3
"""Merge the per-batch JUnit reports of one websocket run into a single file,
and prove nothing went missing on the way.

WHY THIS EXISTS
---------------
run-tests.sh has to split its codeunit list across several suite-setup
requests once the joined list passes BC's 2048-character `CodeunitIds` field
(see scripts/chunk-codeunit-ids.py). Each batch is set up and executed on its
own, so each produces its own JUnit XML and its own "N total, P passed"
summary. Downstream must not be able to tell a batched run from a
single-request one, so the batches are merged back into one report here.

The merge is also where a batching bug would hide. A dropped batch does not
look like an error: it looks like a green run with fewer tests in it — the
false-green shape of issue #57. So this refuses to stay quiet about it:

  * the merged `tests` count must equal the sum of the batches' own counts,
    or the merge itself lost something;
  * every dispatched codeunit must appear as a `<testsuite name="Codeunit N">`
    in the result. In `--strict` mode (used when the list actually WAS split)
    a codeunit reporting nothing at all fails the run.

The per-batch files are disjoint by construction — chunk-codeunit-ids.py
guarantees each codeunit id lands in exactly one batch — so merging is a
concatenation of `<testsuite>` elements with the roll-up attributes
recomputed. The output shape matches run-tests-hybrid.py's merge, which is
what consumers (GitHub Checks reporters, Azure DevOps, AL-Go's AnalyzeTests)
already read.

Self-test, no BC and no network needed:

    python3 scripts/merge-junit-batches.py --self-test
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import tempfile
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

# One <testsuite> per codeunit, named by tools/TestRunner's JUnitWriter.Write
# and by run-tests-altool.py's write_junit alike. A codeunit missing from this
# set produced no result at all — not a pass, not a failure, not an
# infrastructure-error placeholder.
_SUITE_CODEUNIT_ID = re.compile(r"^Codeunit (\d+)$")


def expand_expected(raw: str) -> set[int] | None:
    """The dispatched ids as a set, or None when the list is not a definite
    set of codeunits that exist.

    run-tests.sh dispatches an EXPLICIT id list whenever it discovered the
    codeunits from a .app's SymbolReference.json — that list is exactly what
    compiled, so every id in it must report something. Without a .app it falls
    back to the raw --codeunit-range, which is a literal span like
    "50000-99999" covering mostly ids that do not exist; holding that to
    "every id reported a result" would flag thousands of codeunits that were
    never there. So a range token means "no definite expectation" and the
    completeness check is skipped rather than guessed at.
    """
    out: set[int] = set()
    for tok in raw.split(","):
        tok = tok.strip()
        if not tok:
            continue
        if "-" in tok:
            return None
        try:
            out.add(int(tok))
        except ValueError:
            return None
    return out or None


def merge(paths: list[str], out_path: str, expected_raw: str,
          elapsed: float, strict: bool,
          emit=print) -> int:
    """Merge `paths` into `out_path`. Returns a process exit code."""
    problems: list[str] = []
    suites: list[ET.Element] = []
    per_file_tests: list[int] = []

    for p in paths:
        if not os.path.isfile(p):
            problems.append(f"batch report missing: {p}")
            per_file_tests.append(0)
            continue
        try:
            root = ET.parse(p).getroot()
        except ET.ParseError as ex:
            problems.append(f"batch report unreadable ({ex}): {p}")
            per_file_tests.append(0)
            continue
        found = root.findall("testsuite")
        suites.extend(found)
        per_file_tests.append(sum(int(s.get("tests", "0")) for s in found))

    def isum(attr: str) -> int:
        return sum(int(s.get(attr, "0")) for s in suites)

    total, failed = isum("tests"), isum("failures")
    errors, skipped = isum("errors"), isum("skipped")
    passed = total - failed - skipped - errors

    merged = ET.Element("testsuites", {
        "name": "DEFAULT",
        "tests": str(total),
        "failures": str(failed),
        "errors": str(errors),
        "skipped": str(skipped),
        "time": f"{elapsed:.3f}",
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
    for s in suites:
        merged.append(s)
    parent = os.path.dirname(out_path)
    if parent:
        os.makedirs(parent, exist_ok=True)
    ET.ElementTree(merged).write(out_path, encoding="unicode", xml_declaration=True)

    # If the roll-up and the batches disagree, the merge dropped something and
    # no count printed below can be trusted.
    if total != sum(per_file_tests):
        problems.append(
            f"merged total {total} != sum of batch totals {sum(per_file_tests)} "
            f"({', '.join(str(n) for n in per_file_tests)})")

    seen = {int(m.group(1)) for s in suites
            for m in [_SUITE_CODEUNIT_ID.match(s.get("name") or "")] if m}
    expected = expand_expected(expected_raw)

    emit("")
    if expected is None:
        if len(paths) > 1:
            emit(f"Merged {len(paths)} batches; the dispatched list is a literal "
                 f"range, so per-codeunit completeness is not checkable.")
    else:
        missing = sorted(expected - seen)
        if missing:
            shown = ",".join(str(i) for i in missing[:40])
            emit(f"{'ERROR' if strict else 'WARN'}: {len(missing)} of {len(expected)} "
                 f"codeunit(s) produced NO result at all — no <testsuite>, not even a "
                 f"failure — in the merged report: {shown}"
                 f"{' ...' if len(missing) > 40 else ''}")
            if strict:
                problems.append(f"{len(missing)} codeunit(s) reported nothing")
        elif len(paths) > 1:
            emit(f"All {len(expected)} dispatched codeunit(s) reported results "
                 f"across {len(paths)} batches.")

    for problem in problems:
        emit(f"ERROR: {problem}")

    # Keep this exact shape — bc-test-from-source.yml greps
    # '[0-9]+ total, [0-9]+ passed, [0-9]+ failed' and takes the LAST match,
    # so this has to be the run's aggregate and has to come after every
    # batch's own summary line.
    emit(f"{total} total, {passed} passed, {failed} failed, "
         f"{skipped} skipped, {errors} codeunit error(s) in {elapsed:.0f}s")

    return 1 if problems else 0


# --------------------------------------------------------------------------
# Self-test
# --------------------------------------------------------------------------

def _write_batch(path: str, cases: dict[int, tuple[int, int, int]]) -> None:
    """cases: {codeunit_id: (passed, failed, skipped)}."""
    parts = ['<?xml version="1.0" encoding="utf-8"?>', '<testsuites name="DEFAULT">']
    for cu, (p, f, s) in cases.items():
        parts.append(
            f'  <testsuite name="Codeunit {cu}" tests="{p + f + s}" '
            f'failures="{f}" errors="0" skipped="{s}" time="1.000">')
        for i in range(p):
            parts.append(f'    <testcase classname="Codeunit {cu}" name="p{i}" time="0.1"/>')
        for i in range(f):
            parts.append(f'    <testcase classname="Codeunit {cu}" name="f{i}" time="0.1">'
                         f'<failure message="boom" type="AssertionFailure">boom</failure></testcase>')
        for i in range(s):
            parts.append(f'    <testcase classname="Codeunit {cu}" name="s{i}" time="0.1">'
                         f'<skipped/></testcase>')
        parts.append("  </testsuite>")
    parts.append("</testsuites>")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(parts))


def self_test() -> int:
    failures: list[str] = []
    lines: list[str] = []

    def check(label: str, cond: bool) -> None:
        print(("  PASS  " if cond else "  FAIL  ") + label)
        if not cond:
            failures.append(label)

    def run(paths, expected, strict, out):
        lines.clear()
        rc = merge(paths, out, expected, 12.0, strict, emit=lines.append)
        return rc, "\n".join(lines)

    print("merge:")
    with tempfile.TemporaryDirectory() as d:
        b1 = os.path.join(d, "b1.xml")
        b2 = os.path.join(d, "b2.xml")
        out = os.path.join(d, "merged.xml")
        _write_batch(b1, {60100: (3, 0, 0), 60101: (2, 1, 0)})
        _write_batch(b2, {60102: (4, 0, 1)})

        rc, text = run([b1, b2], "60100,60101,60102", True, out)
        check("a clean two-batch merge succeeds", rc == 0)
        check("the aggregate line sums BOTH batches, not just the last",
              "11 total, 9 passed, 1 failed, 1 skipped" in text)
        check("the aggregate line is the LAST line printed",
              text.strip().splitlines()[-1].startswith("11 total,"))
        root = ET.parse(out).getroot()
        check("the merged file carries every codeunit's testsuite",
              {s.get("name") for s in root.findall("testsuite")} ==
              {"Codeunit 60100", "Codeunit 60101", "Codeunit 60102"})
        check("the merged roll-up matches the elements it contains",
              root.get("tests") == "11" and root.get("failures") == "1"
              and root.get("skipped") == "1")
        check("completeness is reported positively when nothing is missing",
              "All 3 dispatched codeunit(s) reported results" in text)

        # The failure this file exists to catch: batch 2 never made it.
        rc, text = run([b1], "60100,60101,60102", True, out)
        check("a DROPPED batch fails the run in strict mode", rc == 1)
        check("the dropped batch's codeunit is named", "60102" in text)
        check("a dropped batch does NOT quietly report a smaller green run",
              "ERROR:" in text)

        # A batch that ran but whose report never landed.
        rc, text = run([b1, os.path.join(d, "gone.xml")], "60100,60101,60102", True, out)
        check("a MISSING batch report fails the run", rc == 1)
        check("the missing report path is named", "gone.xml" in text)

        # Unreadable report (truncated write, killed process).
        bad = os.path.join(d, "bad.xml")
        with open(bad, "w", encoding="utf-8") as fh:
            fh.write("<testsuites name=\"DEFAULT\"><testsuite ")
        rc, text = run([b1, bad], "60100,60101", True, out)
        check("an UNREADABLE batch report fails the run", rc == 1)

        # Non-strict: the same missing codeunit is reported, not fatal. This is
        # the un-batched path, where nothing was split and a codeunit with no
        # results is a pre-existing condition, not something batching caused.
        rc, text = run([b1], "60100,60101,60102", False, out)
        check("non-strict reports the same gap as a WARN and does not fail",
              rc == 0 and "WARN:" in text and "60102" in text)

        # A literal range says nothing about which ids exist, so the
        # completeness check stands down rather than flagging every id in the
        # span. It must NOT quietly become a pass-everything mode for explicit
        # lists, which the checks above cover.
        rc, text = run([b1, b2], "50000-99999", True, out)
        check("a literal range skips the completeness check instead of guessing",
              rc == 0 and "not checkable" in text)
        rc, text = run([b1, b2], "60100,60101,60102,60103", True, out)
        check("an explicit list right after a range still catches a gap",
              rc == 1 and "60103" in text)

        # Single batch, everything present: the ordinary un-batched run.
        rc, text = run([b1], "60100,60101", True, out)
        check("a single complete batch succeeds", rc == 0)
        check("a single batch still prints an aggregate line",
              "6 total, 5 passed, 1 failed" in text)

    if failures:
        print(f"\n{len(failures)} check(s) failed: {', '.join(failures)}")
        return 1
    print("\nall checks passed")
    return 0


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(
        description="Merge per-batch JUnit reports and verify nothing was lost.")
    ap.add_argument("batches", nargs="*", help="per-batch JUnit XML files, in order")
    ap.add_argument("--out", default="", help="where to write the merged report")
    ap.add_argument("--expected", default="",
                    help="comma-separated codeunit ids that were dispatched")
    ap.add_argument("--elapsed", type=float, default=0.0, help="wall seconds")
    ap.add_argument("--strict", action="store_true",
                    help="fail when a dispatched codeunit reported nothing")
    ap.add_argument("--self-test", action="store_true",
                    help="run the built-in checks and exit; needs no BC")
    args = ap.parse_args(argv)

    if args.self_test:
        return self_test()
    if not args.out:
        print("ERROR: --out is required", file=sys.stderr)
        return 2
    if not args.batches:
        print("ERROR: no batch reports given", file=sys.stderr)
        return 2
    return merge(args.batches, args.out, args.expected, args.elapsed, args.strict)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
