#!/usr/bin/env python3
"""
classify-handler-codeunits.py — Static AL-source scan that decides, per test
codeunit, whether it's safe to run on the fast altool/TestRunnerHub path or
needs the classic websocket runner.

Why this exists: see https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues/27.
TestRunnerHub (Dev API 7.0, BC 28+) does not run tests under an AL Test
Runner codeunit, and that codeunit is what implements [HandlerFunctions]
dispatch — including the "modal invoked with no handler registered → refuse
with Unhandled UI" behavior. Under the hub, an unhandled modal page call
silently returns instead of throwing, so a test that expects the refusal
(typically `asserterror ... .Invoke();` / `.RunModal();` with no
[HandlerFunctions] on the test method) gets the wrong answer.

We can't run a test twice to find out which ones are affected (a suite with
many real failures would make that pay for itself many times over — see the
issue #27 follow-up discussion), so the split has to be decided once, before
either runner starts, from AL source. There is no reliable way to recover
[HandlerFunctions] usage from a compiled .app (SymbolReference.json only
serializes method signatures, not attributes — checked empirically against
several .apps in this repo; only source-level scanning has the data).
Consequence: this only works for from-source workflows. A codeunit whose
source isn't found in the given paths is classified as needing websocket —
unproven safety is treated as unsafe, not the other way around.

A test method is flagged "needs websocket" when any of:
  (a) it carries [HandlerFunctions(...)] — it relies on handler dispatch,
      which this script treats conservatively as tied to the same
      Test-Runner-codeunit machinery implicated in issue #27, even though
      the issue itself only demonstrates the narrower "no handler
      registered" gap, or
  (b) it has NO [HandlerFunctions(...)] but its body contains `asserterror`
      together with a modal-style invocation (`.RunModal(`, `.Invoke(`,
      bare `RunModal(`) — the exact shape of the repro in issue #27
      (`asserterror Host.PickIt.Invoke(); Assert.ExpectedError('Unhandled
      UI');`), or
  (c) its effective TestPermissions is anything other than `Disabled`, or
  (d) its body mentions the `Unhandled UI` error text — i.e. it asserts the
      refusal itself, whatever call raised it (issue #93), or
  (e) a test calls `NumberSequence.` and also uses `asserterror` (issue #120):
      a failed sequence DDL statement dooms the SQL transaction, which the
      hub's reused session cannot recover from, or
  (f) its body names one of Microsoft's test runner codeunits ("Test Runner -
      Mgt.", "Test Runner - Isol. Codeunit", ...) (issue #140): it expects an
      error text that only a run under a test runner codeunit produces.

Rule (c) is issue #64, and unlike (a) and (b) it is measured rather than
inferred. TestPermissions works "together with the OnBeforeTestRun and
OnAfterTestRun triggers in test runner codeunits" (the property's own
documentation), so with no test runner codeunit involved the hub never
switches the permission execution context at all and every test runs as SUPER.
Both non-Disabled values change that context — `Restrictive` (the default when
the property is absent) and `NonRestrictive` both set it to 'D365 Full Access',
which does not cover an extension's own tables. Only `Disabled` leaves the run
as SUPER, which is what the hub gives unconditionally.

Measured on BC 28.4, one container, one test codeunit with no TestPermissions
declaration inserting into its own table: the websocket runner refuses it
("Sorry, the current permissions prevented the action."), the altool/hub runner
lets it through. At corpus scale that is 652 refusals the fast path silently
turned green — see the issue for the Windows-container comparison.

Rule (d) is issue #93. A part page's `OnOpenPage` calls `Confirm`, no
ConfirmHandler is bound, and the test does `asserterror Host.OpenView();
Assert.ExpectedError('Unhandled UI: Confirm');`. There is no `.RunModal(` or
`.Invoke(` anywhere in the test, so rule (b) never fires, and the hub — which
has no test runner codeunit to refuse the Confirm — opens the host silently.
That is the whole of the "BC 28.x swallows it, 27.x raises" difference: BC 28+
legs take the hub under `test_runner=auto`, 27.x legs have no hub and use the
websocket runner. Measured on BC 28.4, one container: the same four tests pass
on the websocket runner and fail on both altool transports (`cli`, `hub`),
including a Confirm in the host page's own `OnOpenPage`, so it is not specific
to parts. A test that names the error text is asserting exactly the behavior
the hub lacks, regardless of which UI call raises it.

Rule (f) is issue #140. Some BC errors name the object that called the
current AL method: `RecordRef.Open` of a table that is not in the runtime app
raises "You tried to invoke the Table object with the ID n from the object X".
Called directly from a [Test] method, X is the test method's caller. On a
Windows container and on the websocket runner that is the test runner
codeunit "Test Runner - Mgt."; the hub calls the test method with no AL caller,
so BC names the test codeunit itself. Faking a caller on the hub would mean
making up an object name in BC's error path, so the test is routed instead. A
test that checks that text names the test runner object, which is what the
rule matches.

Rule (f) reads only the lines the compiler compiles when the caller passes the
app's preprocessor symbols (`--define`, `--preprocessor-symbols` on
run-tests-hybrid.py). A codeunit with `#if BC28PLUS` ... `#else` that names
"Test Runner - Mgt." only in the BC 27 branch used to go to the websocket
runner on BC 28 and later, which names that object where Windows names the test
codeunit (issue #166). Without symbols every branch is scanned, as before.

This is a heuristic, not a proof. It cannot see a modal invoked deep inside
called business logic that the test codeunit's own source never mentions.
It exists to catch the known failure shape cheaply, not to guarantee full
equivalence between the two runners — if evidence turns up (e.g. from a
PipelinePerformanceComparison BCApps sweep) that more test shapes are
affected, tighten these rules from that evidence rather than from more
speculation.

Usage:
  python3 scripts/classify-handler-codeunits.py <al-source-path>... \
      [--codeunit-ids 50000,50001,...] [--format text|json]

Exit code is always 0 (this is an analysis tool, not a pass/fail gate).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

CODEUNIT_DECL = re.compile(r'^\s*codeunit\s+(\d+)\s+', re.IGNORECASE)
SUBTYPE_TEST = re.compile(r'^\s*Subtype\s*=\s*Test\s*;', re.IGNORECASE)
# Codeunit-level property. Absent means Restrictive, per the property's docs.
TEST_PERMISSIONS_PROP = re.compile(r'^\s*TestPermissions\s*=\s*(\w+)\s*;', re.IGNORECASE)
ATTR_LINE = re.compile(r'^\s*\[\s*([A-Za-z]+)\s*(\([^)]*\))?\s*\]\s*$')
PROC_DECL = re.compile(
    r'^(?P<indent>\s*)(local\s+|internal\s+|protected\s+)?procedure\s+'
    r'("[^"]+"|[\w]+)\s*\(',
    re.IGNORECASE,
)
END_LINE = re.compile(r'^(?P<indent>\s*)end;\s*$')
ASSERTERROR_RE = re.compile(r'\basserterror\b', re.IGNORECASE)
# The refusal the hub never raises. Matching the text rather than the call that
# produces it covers Confirm / Message / StrMenu / modal and non-modal page opens
# alike, which no call-shape rule can enumerate.
UNHANDLED_UI_RE = re.compile(r'\bUnhandled\s+UI\b', re.IGNORECASE)
# A NumberSequence call in a test that also uses asserterror (issue #120). A
# failing CREATE SEQUENCE / ALTER SEQUENCE makes SQL Server roll the whole
# transaction back, not just the statement. On a hub session that has already run
# another codeunit, the test scope then finds no transaction when asserterror
# rolls back to its savepoint, and the test fails with "Transaction was rolled
# back by the server." The websocket runner, a fresh session and a Windows
# container all give the AL error instead.
NUMBER_SEQUENCE_RE = re.compile(r'\bNumberSequence\s*\.', re.IGNORECASE)
# Microsoft's test runner codeunits: "Test Runner - Mgt.", "Test Runner - Isol.
# Codeunit", "Test Runner - Isol. Disabled" (issue #140). A test that names one
# expects an error text that only a run under that codeunit produces.
TEST_RUNNER_NAME_RE = re.compile(r'\bTest\s+Runner\s+-\s+(Mgt\.|Isol\.)', re.IGNORECASE)
MODAL_CALL_RE = re.compile(r'\.(RunModal|Invoke)\s*\(|(?<![\w.])RunModal\s*\(', re.IGNORECASE)


PREPROC_RE = re.compile(r'^\s*#\s*(if|elif|else|endif)\b(.*)$', re.IGNORECASE)
PREPROC_TOKEN_RE = re.compile(r'\s*(&&|\|\||!|\(|\)|[A-Za-z_][A-Za-z0-9_]*)')


def _eval_condition(expr: str, symbols: set[str]) -> bool:
    """Evaluate an AL preprocessor condition: symbols, !, &&, ||, parentheses.
    An expression it cannot parse counts as true, which keeps the lines (the
    behavior before symbols were known)."""
    tokens = PREPROC_TOKEN_RE.findall(expr.split("//")[0])
    pos = 0

    def peek():
        return tokens[pos] if pos < len(tokens) else None

    def take():
        nonlocal pos
        pos += 1
        return tokens[pos - 1]

    def parse_or():
        v = parse_and()
        while peek() == "||":
            take()
            r = parse_and()
            v = v or r
        return v

    def parse_and():
        v = parse_not()
        while peek() == "&&":
            take()
            r = parse_not()
            v = v and r
        return v

    def parse_not():
        if peek() == "!":
            take()
            return not parse_not()
        return parse_atom()

    def parse_atom():
        t = peek()
        if t is None:
            raise ValueError("unexpected end")
        take()
        if t == "(":
            v = parse_or()
            if peek() != ")":
                raise ValueError("missing )")
            take()
            return v
        if t in ("&&", "||", ")", "!"):
            raise ValueError("unexpected " + t)
        if t.lower() == "true":
            return True
        if t.lower() == "false":
            return False
        return t in symbols

    try:
        v = parse_or()
        return v if pos == len(tokens) else True
    except ValueError:
        return True


def apply_preprocessor(lines: list[str], symbols: set[str] | None) -> list[str]:
    """Drop the lines the AL compiler would not compile, and the directive
    lines themselves. With symbols None every branch stays, as before."""
    if symbols is None:
        return lines
    out: list[str] = []
    # One frame per open #if: [parent_active, any_branch_taken, this_branch_active]
    stack: list[list[bool]] = []
    for line in lines:
        m = PREPROC_RE.match(line)
        if not m:
            if all(f[2] for f in stack):
                out.append(line)
            continue
        kind = m.group(1).lower()
        rest = m.group(2)
        parent = all(f[2] for f in stack)
        if kind == "if":
            taken = _eval_condition(rest, symbols)
            stack.append([parent, taken, taken])
        elif kind == "elif" and stack:
            frame = stack[-1]
            taken = (not frame[1]) and _eval_condition(rest, symbols)
            frame[1] = frame[1] or taken
            frame[2] = taken
        elif kind == "else" and stack:
            frame = stack[-1]
            frame[2] = not frame[1]
            frame[1] = True
        elif kind == "endif" and stack:
            stack.pop()
    return out


def parse_symbols(value: str | None) -> set[str] | None:
    """None (option absent or empty) scans every branch; a list selects branches.
    An empty value means the caller sets no symbols through the workflow input, which is
    not the same as the app having none: app.json can define them too."""
    symbols = {x.strip() for x in re.split(r"[,;\s]+", value or "") if x.strip()}
    return symbols or None


def find_al_files(paths: list[str]) -> list[str]:
    files: list[str] = []
    for p in paths:
        if os.path.isfile(p) and p.lower().endswith(".al"):
            files.append(p)
        elif os.path.isdir(p):
            for root, _dirs, names in os.walk(p):
                for n in names:
                    if n.lower().endswith(".al"):
                        files.append(os.path.join(root, n))
    return files


# The only value that leaves a test running as SUPER, which is what the hub
# gives every test unconditionally. Everything else moves the permission
# execution context and so cannot be reproduced on the hub.
HUB_SAFE_TEST_PERMISSIONS = "disabled"


class _CodeunitState:
    def __init__(self, cuid: int):
        self.id = cuid
        self.is_test = False
        self.needs_websocket = False
        # None until a codeunit-level `TestPermissions = X;` is seen. Resolved
        # against the AL default (Restrictive) when the codeunit is flushed, so
        # the property is honoured wherever in the object it is declared.
        self.test_permissions: str | None = None
        # Method-level [TestPermissions(X)] values, one entry per [Test] method:
        # the attribute's argument, or None where the method didn't carry one
        # (which means InheritFromTestCodeunit — take the codeunit's value).
        self.method_permissions: list[str | None] = []


def classify_al_source(paths: list[str], symbols: set[str] | None = None) -> dict[int, dict]:
    """Scan every .al file under `paths` and return
    {codeunit_id: {"is_test": bool, "needs_websocket": bool}} for every
    `Subtype = Test;` codeunit found. Codeunits not found in the scanned
    source simply don't appear in the result — the caller decides how to
    treat that (this script's CLI treats it as "needs websocket").

    `symbols` is the set of AL preprocessor symbols the app is compiled with. Given,
    only the lines inside an active #if branch are scanned: a BC 27 `#else` branch
    that names "Test Runner - Mgt." must not route a codeunit that does not compile
    it (issue #166). None scans every branch."""
    result: dict[int, dict] = {}

    for path in find_al_files(paths):
        try:
            with open(path, encoding="utf-8-sig", errors="replace") as f:
                lines = f.readlines()
        except OSError:
            continue
        lines = apply_preprocessor(lines, symbols)

        cur: _CodeunitState | None = None
        pending_attrs: list[str] = []
        in_proc = False
        proc_indent = 0
        proc_is_test_attr = False
        proc_has_handler_attr = False
        proc_permissions_attr: str | None = None
        proc_body: list[str] = []

        def flush_proc():
            if cur is None or not proc_is_test_attr:
                return
            cur.method_permissions.append(proc_permissions_attr)
            if proc_has_handler_attr:
                cur.needs_websocket = True
            elif ASSERTERROR_RE.search("\n".join(proc_body)) and MODAL_CALL_RE.search(
                "\n".join(proc_body)
            ):
                cur.needs_websocket = True
            elif UNHANDLED_UI_RE.search("\n".join(proc_body)):
                cur.needs_websocket = True
            elif ASSERTERROR_RE.search("\n".join(proc_body)) and NUMBER_SEQUENCE_RE.search(
                "\n".join(proc_body)
            ):
                cur.needs_websocket = True
            elif TEST_RUNNER_NAME_RE.search("\n".join(proc_body)):
                cur.needs_websocket = True

        def flush_codeunit():
            if cur is not None and cur.is_test:
                # Resolve TestPermissions last, so a codeunit-level declaration
                # applies however late in the object it appears.
                cu_level = (cur.test_permissions or "restrictive").lower()
                for method_level in cur.method_permissions or [None]:
                    effective = (method_level or "").lower()
                    if effective in ("", "inheritfromtestcodeunit"):
                        effective = cu_level
                    if effective != HUB_SAFE_TEST_PERMISSIONS:
                        cur.needs_websocket = True
                        break
                prev = result.get(cur.id)
                if prev is None:
                    result[cur.id] = {"is_test": True, "needs_websocket": cur.needs_websocket}
                else:
                    prev["needs_websocket"] = prev["needs_websocket"] or cur.needs_websocket

        for line in lines:
            m = CODEUNIT_DECL.match(line)
            if m:
                if in_proc:
                    flush_proc()
                    in_proc = False
                flush_codeunit()
                cur = _CodeunitState(int(m.group(1)))
                pending_attrs = []
                continue

            if cur is None:
                continue

            if in_proc:
                em = END_LINE.match(line)
                if em and len(em.group("indent")) <= proc_indent:
                    flush_proc()
                    in_proc = False
                    proc_body = []
                else:
                    proc_body.append(line)
                continue

            if SUBTYPE_TEST.match(line):
                cur.is_test = True
                continue

            tp = TEST_PERMISSIONS_PROP.match(line)
            if tp:
                cur.test_permissions = tp.group(1)
                continue

            am = ATTR_LINE.match(line)
            if am:
                pending_attrs.append((am.group(1).lower(), (am.group(2) or "").strip("()").strip()))
                continue

            pm = PROC_DECL.match(line)
            if pm:
                attr_names = [a for a, _ in pending_attrs]
                proc_indent = len(pm.group("indent"))
                proc_is_test_attr = "test" in attr_names
                proc_has_handler_attr = "handlerfunctions" in attr_names
                proc_permissions_attr = next(
                    (v for a, v in pending_attrs if a == "testpermissions" and v), None
                )
                pending_attrs = []
                if proc_is_test_attr:
                    in_proc = True
                    proc_body = []
                continue

            if line.strip() and not line.strip().startswith("//"):
                pending_attrs = []

        if in_proc:
            flush_proc()
        flush_codeunit()

    return result


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+", help="AL source files or directories to scan")
    ap.add_argument(
        "--codeunit-ids",
        default="",
        help="restrict/report against this comma-separated set of codeunit ids "
        "(typically the .app-discovered Subtype=Test set); ids in this set with "
        "no matching source are reported separately as 'unmatched'",
    )
    ap.add_argument("--format", choices=["text", "json"], default="text")
    ap.add_argument(
        "--define",
        default=None,
        help="comma-separated AL preprocessor symbols the app is compiled with; "
        "lines in inactive #if branches are not scanned. Omit to scan every branch.",
    )
    args = ap.parse_args()

    info = classify_al_source(args.paths, parse_symbols(args.define))

    ids_filter = None
    if args.codeunit_ids:
        ids_filter = {int(x) for x in args.codeunit_ids.split(",") if x.strip()}

    def wanted(cuid: int) -> bool:
        return ids_filter is None or cuid in ids_filter

    hub_ids = sorted(cuid for cuid, v in info.items() if v["is_test"] and not v["needs_websocket"] and wanted(cuid))
    ws_ids = sorted(cuid for cuid, v in info.items() if v["is_test"] and v["needs_websocket"] and wanted(cuid))
    unmatched = sorted((ids_filter or set()) - set(info.keys())) if ids_filter else []

    if args.format == "json":
        print(json.dumps({"hub": hub_ids, "websocket": ws_ids, "unmatched": unmatched}))
    else:
        print("hub:", ",".join(map(str, hub_ids)))
        print("websocket:", ",".join(map(str, ws_ids)))
        if unmatched:
            print("unmatched (no AL source found — treat as websocket):", ",".join(map(str, unmatched)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
