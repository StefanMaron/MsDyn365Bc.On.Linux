#!/usr/bin/env python3
"""Turn the JUnit files of a test run into a readable report.

    python3 scripts/junit-report.py [--title T] [--summary-file F] build/junit-*.xml

Writes Markdown to --summary-file (appended; $GITHUB_STEP_SUMMARY in a GitHub
job), or to stdout without one, and prints one `::error` workflow command per
failed test, which GitHub shows as an annotation on the run and the pull
request. The report starts with a table of the failed tests: test app,
codeunit, test, line, and the expected and actual values Library Assert puts
in its message. Then one row per test app, then each failure's message and AL
call stack.

Reads what every runner here writes: the altool runner's "AL Callstack:" body
(also what run-tests-hybrid.py and merge-junit-batches.py pass through), the
websocket runner's "Call stack:" body (tools/TestRunner), and the altool
runner's "(codeunit run)" errors and "(codeunit)" rollups.

Stays readable at any size: the details stop after MAX_DETAILS failures and the
whole report stays under GitHub's 1 MiB step summary limit; annotations stop
after GitHub's 10 errors per step, with a notice saying how many more failed.

It only reports and always exits 0: whether the job passes is decided by the
test step, not here. A file that cannot be read is listed as unreadable and
the others are still reported. JUnit files with a DOCTYPE are refused, since
they come from test code a pull request controls.

Tests: python3 scripts/test-junit-report.py
"""

from __future__ import annotations

import argparse
import html
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

MAX_DETAILS = 50          # failures with a details block; the table lists more
MAX_TABLE_ROWS = 500      # rows in the failed-tests table
MAX_ANNOTATIONS = 10      # GitHub shows 10 error annotations per step
SUMMARY_BUDGET = 900_000  # bytes; GitHub rejects a step summary over 1 MiB

# "AL Callstack:" (altool runner) or "Call stack:" (websocket runner)
_STACK_HEADER = re.compile(r"^\s*(?:AL Callstack|Call stack):\s*$", re.M)
# "Sales Test"(CodeUnit 50100).PostOrder line 24 - Sales Tests by Contoso version 1.0.0.0
_FRAME = re.compile(r'^(?P<frame>"(?P<name>[^"]+)"\(CodeUnit (?P<id>\d+)\)\.\S+ line (?P<line>\d+))')
# Assert.AreEqual failed. Expected:<1> (Integer). Actual:<0> (Integer). The item must ...
_ASSERT = re.compile(r"^Assert\.\w+ failed\. Expected:<(?P<exp>.*?)>(?P<et> \([^)]*\))?\. "
                     r"Actual:<(?P<act>.*?)>(?P<at> \([^)]*\))?\.\s*(?P<reason>.*)$", re.S)


@dataclass
class Failure:
    codeunit: str
    codeunit_name: str
    method: str
    message: str
    callstack: list[str]
    line: str = ""
    expected: str | None = None
    actual: str | None = None
    reason: str = ""


@dataclass
class AppResult:
    name: str
    unreadable: str = ""
    passed: int = 0
    failed: int = 0
    skipped: int = 0
    seconds: float = 0.0
    failures: list[Failure] = field(default_factory=list)


def parse_assert(message: str) -> tuple[str, str, str] | None:
    """(expected, actual, reason) of a Library Assert message, else None."""
    m = _ASSERT.match(message.strip())
    if not m:
        return None
    expected = m.group("exp") + (m.group("et") or "") if m.group("exp") else ""
    actual = m.group("act") + (m.group("at") or "") if m.group("act") else ""
    reason = m.group("reason").strip()
    # Library Assert adds its own "." after the caller's message
    if reason.endswith(".."):
        reason = reason[:-1]
    return expected, actual, reason


def _parse(path: Path) -> ET.Element:
    text = path.read_text(encoding="utf-8", errors="replace")
    if "<!DOCTYPE" in text or "<!ENTITY" in text:
        raise ValueError(f"{path.name}: refusing a JUnit file with a DOCTYPE")
    return ET.fromstring(text.encode("utf-8"))  # noqa: S314 - DOCTYPE refused above


def _failure(case: ET.Element, node: ET.Element) -> Failure:
    codeunit = re.sub(r"^Codeunit\s+", "", case.get("classname", ""))
    body = (node.text or "").replace("\r\n", "\n").replace("\r", "\n")
    parts = _STACK_HEADER.split(body, maxsplit=1)
    message = (node.get("message") or parts[0]).strip()
    frames, name, line = [], "", ""
    if len(parts) == 2:
        for text in parts[1].strip().splitlines():
            m = _FRAME.match(text.strip())
            if m:
                frames.append(m.group("frame"))
                if m.group("id") == codeunit:
                    name = m.group("name")
                    line = line or m.group("line")
    failure = Failure(codeunit, name, case.get("name", ""), message, frames, line, reason=message)
    parsed = parse_assert(message)
    if parsed:
        failure.expected, failure.actual, failure.reason = parsed
    return failure


def read_app(path: Path) -> AppResult:
    app = AppResult(path.name.removesuffix(".xml").removeprefix("junit-"))
    for case in _parse(path).iter("testcase"):
        app.seconds += float(case.get("time") or 0)
        node = case.find("failure")
        if node is None:
            node = case.find("error")
        if node is not None:
            app.failed += 1
            app.failures.append(_failure(case, node))
        elif case.find("skipped") is not None:
            app.skipped += 1
        else:
            app.passed += 1
    return app


def read_apps(paths: list[Path]) -> list[AppResult]:
    apps = []
    for path in sorted(paths):
        try:
            apps.append(read_app(path))
        except (OSError, ValueError, ET.ParseError) as exc:
            name = path.name.removesuffix(".xml").removeprefix("junit-")
            apps.append(AppResult(name, unreadable=" ".join(str(exc).split())))
    return apps


def _cell(text: str) -> str:
    return html.escape(" ".join(text.split())).replace("|", "\\|")


def _value(v: str | None) -> str:
    if v is None:
        return ""
    return f"<code>{_cell(v)}</code>" if v else "*(empty)*"


def _codeunit(f: Failure) -> str:
    return f"{f.codeunit_name} ({f.codeunit})" if f.codeunit_name else f"Codeunit {f.codeunit}"


def _duration(seconds: float) -> str:
    m, s = divmod(round(seconds), 60)
    return f"{m}m {s:02d}s" if m else f"{s}s"


def markdown(apps: list[AppResult], title: str) -> str:
    if not apps:
        return f"## ⚠️ {title}: No test results\n\nNo JUnit files were found.\n"
    passed = sum(a.passed for a in apps)
    failed = sum(a.failed for a in apps)
    skipped = sum(a.skipped for a in apps)
    bad = any(a.unreadable for a in apps)
    out = [f"## {'❌' if failed else '⚠️' if bad else '✅'} {title}: "
           f"{passed:,} passed, {failed:,} failed, {skipped:,} skipped", ""]
    failures = [(a, f) for a in apps for f in a.failures]
    if failures:
        out += ["### Failed tests", "",
                "| Test app | Codeunit | Test | Line | Expected | Actual | Message |",
                "|---|---|---|---:|---|---|---|"]
        for a, f in failures[:MAX_TABLE_ROWS]:
            codeunit = f"{f.codeunit} {f.codeunit_name}".strip()
            out.append(f"| {_cell(a.name)} | {_cell(codeunit)} | {_cell(f.method)} | {f.line} | "
                       f"{_value(f.expected)} | {_value(f.actual)} | {_cell(f.reason)} |")
        out.append("")
    out += ["| | Test app | Passed | Failed | Skipped | Time |",
            "|---|---|---:|---:|---:|---:|"]
    for a in apps:
        if a.unreadable:
            out.append(f"| ⚠️ | {_cell(a.name)} | unreadable: {_cell(a.unreadable)} | | | |")
            continue
        out.append(f"| {'❌' if a.failed else '✅'} | {_cell(a.name)} | {a.passed} | "
                   f"{a.failed} | {a.skipped} | {_duration(a.seconds)} |")
    out.append("")
    shown = 0
    if failures:
        out += ["### Failure details", ""]
        size = len("\n".join(out).encode("utf-8"))
        for a, f in failures[:MAX_DETAILS]:
            block = [f"<details><summary>❌ {html.escape(_codeunit(f))} · "
                     f"{html.escape(f.method)}</summary>", "",
                     "<pre>" + html.escape(f.message, quote=False), ""]
            block += [html.escape(frame, quote=False) for frame in f.callstack]
            block += ["</pre>", "</details>", ""]
            size += len("\n".join(block).encode("utf-8")) + 1
            if size > SUMMARY_BUDGET:
                break
            out += block
            shown += 1
    if len(failures) > shown or len(failures) > MAX_TABLE_ROWS:
        out.append(f"{len(failures) - shown:,} more failed tests are not listed here; "
                   "the JUnit files have all of them.")
    text = "\n".join(out)
    if len(text.encode("utf-8")) > SUMMARY_BUDGET:
        text = text.encode("utf-8")[:SUMMARY_BUDGET].decode("utf-8", "ignore")
        text += "\n\n… cut to stay under GitHub's summary limit.\n"
    return text


def annotations(apps: list[AppResult]) -> list[str]:
    def esc(text: str) -> str:
        return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")

    def esc_property(text: str) -> str:
        return esc(text).replace(":", "%3A").replace(",", "%2C")

    failures = [f for a in apps for f in a.failures]
    lines = []
    for f in failures[:MAX_ANNOTATIONS]:
        text = f.message
        if f.expected is not None:
            text = f"Expected {f.expected or '(empty)'}, actual {f.actual or '(empty)'}."
            text += f" {f.reason}" if f.reason else ""
        lines.append(f"::error title={esc_property(f'{_codeunit(f)} {f.method}')}::{esc(text)}")
    if len(failures) > MAX_ANNOTATIONS:
        lines.append(f"::notice::{len(failures) - MAX_ANNOTATIONS} more failed tests; "
                     "see the job summary.")
    for a in apps:
        if a.unreadable:
            lines.append(f"::warning::junit-report: {esc(a.name)} is unreadable: {esc(a.unreadable)}")
    return lines


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--title", default="Tests")
    ap.add_argument("--summary-file", help="append the Markdown here (e.g. $GITHUB_STEP_SUMMARY)")
    ap.add_argument("files", nargs="*", type=Path)
    args = ap.parse_args(argv)
    apps = read_apps([f for f in args.files if f.is_file()])
    md = markdown(apps, args.title)
    if args.summary_file:
        with open(args.summary_file, "a", encoding="utf-8") as fh:
            fh.write(md + "\n")
    else:
        print(md)
    for line in annotations(apps):
        print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
