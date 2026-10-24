#!/usr/bin/env python3
"""Fail when examples/ drifts from the reusable workflows in .github/workflows/.

Why this exists
---------------
The four starter pipelines under examples/ duplicate, in two CI dialects,
logic that also lives in the reusable workflows. Nothing executes them — they
are templates for consumers to copy — so a fix applied to the reusable
workflow reaches everyone using it while the examples silently rot. Issue #20
was reported after issue #19 found three separate instances of exactly that:
glob-based symbol staging, a `dotnet tool install` that fails silently on
BC 28, and an Azure Pipelines license guard that misreads an undefined
variable. Every consumer who copied an example rather than calling the
reusable workflow was broken, for weeks, invisibly.

The structural fix is for the examples to CALL the shared scripts rather than
inline copies of their logic — then a fix to a script reaches them the same
way it reaches the reusable workflow. This check enforces that property:

  1. Every shared script the reusable workflow uses must also be used by the
     corresponding examples, unless explicitly exempted here with a reason.
     This is the check that catches "I added a script and wired it into the
     reusable workflow only" — which is how resolve-al-tool-version.py drifted
     within an hour of being written.

  2. The two dialects must use the same set of shared scripts as each other.
     Fixing github-workflows/ and forgetting azure-pipelines/ is a recurring
     failure mode; parity is cheap to assert.

  3. Constants that now come from a resolver must not be hardcoded anywhere.
     A pinned AL compiler version in an example is drift waiting to happen —
     it will be wrong at the next BC release and fail in a way that points
     somewhere unrelated.

This is a static check. It cannot prove an example RUNS. Running them in CI
would be a stronger guard and is worth doing eventually; this catches the
class of defect that has actually occurred so far, in about a second.

Usage:
    python3 scripts/check-example-drift.py          # report and exit non-zero on drift
    python3 scripts/check-example-drift.py --list   # just show what each file uses
"""
import argparse
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent

# reusable workflow -> the example files that mirror it
PAIRS = {
    ".github/workflows/bc-test-from-source.yml": [
        "examples/github-workflows/bc-test-from-source.yml",
        "examples/azure-pipelines/bc-test-from-source.yml",
    ],
    ".github/workflows/bc-test-prebuilt.yml": [
        "examples/github-workflows/bc-test-prebuilt.yml",
        "examples/azure-pipelines/bc-test-prebuilt.yml",
    ],
}

# Shared scripts the examples are allowed NOT to use, with the reason.
# Add to this deliberately — an entry here is a decision that the examples
# intentionally do less than the reusable workflow, not a place to silence
# the check.
EXEMPT = {
    "scripts/junit-report.py":
        "writes the GitHub job summary and annotations; the examples upload "
        "the JUnit files for the consumer's own reporter",
    "scripts/run-tests-altool.py":
        "the altool runner is opt-in and BC 28+ only; the examples "
        "deliberately show the websocket runner, which works everywhere",
    "scripts/run-tests-hybrid.py":
        "test_runner=auto's hybrid split installs and probes the altool "
        "runner (see the run-tests-altool.py exemption above) before "
        "falling back; same reason, one level up — the examples "
        "deliberately show only the universal websocket runner",
    "scripts/classify-handler-codeunits.py":
        "only used internally by run-tests-hybrid.py's static AL-source "
        "scan (see that exemption above); not called directly by the "
        "reusable workflow either",
    "scripts/discover-bc-versions.py":
        "version discovery drives bc-linux's own matrix; a consumer "
        "pipeline targets one version it chooses itself",
}

# Literals that must come from a resolver rather than being pinned.
FORBIDDEN = [
    (r"16\.2\.28\.57946",
     "hardcoded AL compiler version — use scripts/resolve-al-tool-version.py"),
    (r"17\.0\.34\.45391",
     "hardcoded AL compiler version — use scripts/resolve-al-tool-version.py"),
    (r'EFFECTIVE_RUNTIME="1[4-9]\.0"',
     "hardcoded AL runtime version — use resolve-al-tool-version.py --runtime"),
    # Matches the real shape of this bug: the TFM appears on the line that
    # ASSIGNS the tool dir (or in the find -path filter), while `alc` itself
    # is referenced further down. An earlier version of this rule required
    # both on one line and therefore caught nothing.
    (r'AL_TOOL_DIR=.*(lib|tools)/net[0-9]+\.[0-9]+',
     "hardcoded TFM directory for the AL tool dir — the AL package moves the "
     "alc binary between majors (v17 lib/net8.0, v18 lib/net10.0), so this "
     "breaks at the next release. Locate it with "
     "`find \"$EXTRACT_DIR\" -type f -name alc` instead."),
    (r'-path .*net[0-9]+\.[0-9]+',
     "hardcoded TFM directory in a find filter — search for the alc file by "
     "name rather than assuming which TFM directory holds it"),
    (r'-d "\$EXTRACT_DIR/(lib|tools)/net[0-9]+\.[0-9]+',
     "directory test on a hardcoded TFM — v18 ships a lib/net8.0 that holds "
     "only analyzer DLLs, so this test succeeds and then points at a "
     "directory with no alc in it"),
]

SCRIPT_REF = re.compile(r"scripts/[A-Za-z0-9_.-]+\.(?:sh|py)")


def scripts_used(path):
    text = (REPO / path).read_text()
    return {m.group(0) for m in SCRIPT_REF.finditer(text)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true",
                    help="print what each file uses and exit 0")
    args = ap.parse_args()

    problems = []

    for reusable, examples in PAIRS.items():
        if not (REPO / reusable).is_file():
            problems.append(f"{reusable}: missing")
            continue
        want = scripts_used(reusable)

        if args.list:
            print(f"{reusable}")
            for s in sorted(want):
                print(f"    {s}")

        per_example = {}
        for ex in examples:
            if not (REPO / ex).is_file():
                problems.append(f"{ex}: missing")
                continue
            got = scripts_used(ex)
            per_example[ex] = got

            if args.list:
                print(f"  {ex}")
                for s in sorted(got):
                    print(f"    {s}")

            # 1. reusable -> example coverage
            for missing in sorted(want - got):
                if missing in EXEMPT:
                    continue
                problems.append(
                    f"{ex}\n"
                    f"    does not use {missing}, but {reusable} does.\n"
                    f"    Either call it from the example, or add it to EXEMPT in\n"
                    f"    scripts/check-example-drift.py with the reason it does not apply."
                )

        # 2. cross-dialect parity
        if len(per_example) == 2:
            (a, sa), (b, sb) = per_example.items()
            for only_in, other, diff in ((a, b, sa - sb), (b, a, sb - sa)):
                for s in sorted(diff):
                    problems.append(
                        f"{only_in}\n"
                        f"    uses {s} but {other} does not.\n"
                        f"    The two CI dialects should stay in step; fixing one and\n"
                        f"    forgetting the other is how examples drift."
                    )

    # 3. forbidden literals
    for ex in [e for exs in PAIRS.values() for e in exs]:
        p = REPO / ex
        if not p.is_file():
            continue
        for lineno, line in enumerate(p.read_text().splitlines(), 1):
            if line.lstrip().startswith("#"):
                continue
            for pattern, why in FORBIDDEN:
                if re.search(pattern, line):
                    problems.append(f"{ex}:{lineno}\n    {why}\n    {line.strip()[:100]}")

    if args.list:
        return 0

    if problems:
        print("examples/ has drifted from the reusable workflows:\n", file=sys.stderr)
        for p in problems:
            print(f"  {p}\n", file=sys.stderr)
        print(f"{len(problems)} problem(s). See the docstring in "
              f"scripts/check-example-drift.py for why this matters.", file=sys.stderr)
        return 1

    print("examples/ is in step with the reusable workflows.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
