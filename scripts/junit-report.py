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

It only reports and always exits 0: whether the job passes is decided by the
test step, not here. JUnit files with a DOCTYPE are refused, since they come
from test code a pull request controls.

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
    body = node.text or ""
    message = (node.get("message") or body.split("AL Callstack:", 1)[0]).strip()
    frames, name, line = [], "", ""
    if "AL Callstack:" in body:
        for text in body.split("AL Callstack:", 1)[1].strip().splitlines():
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
    return [read_app(p) for p in sorted(paths)]


def _cell(text: str) -> str:
    return html.escape(text).replace("|", "\\|")


def _value(v: str | None) -> str:
    if v is None:
        return ""
    return f"`{_cell(v)}`" if v else "*(empty)*"


def _duration(seconds: float) -> str:
    m, s = divmod(round(seconds), 60)
    return f"{m}m {s:02d}s" if m else f"{s}s"


def markdown(apps: list[AppResult], title: str) -> str:
    if not apps:
        return f"## ⚠️ {title}: No test results\n\nNo JUnit files were found.\n"
    passed = sum(a.passed for a in apps)
    failed = sum(a.failed for a in apps)
    skipped = sum(a.skipped for a in apps)
    out = [f"## {'❌' if failed else '✅'} {title}: "
           f"{passed:,} passed, {failed:,} failed, {skipped:,} skipped", ""]
    failures = [(a, f) for a in apps for f in a.failures]
    if failures:
        out += ["### Failed tests", "",
                "| Test app | Codeunit | Test | Line | Expected | Actual | Message |",
                "|---|---|---|---:|---|---|---|"]
        for a, f in failures:
            codeunit = f"{f.codeunit} {f.codeunit_name}".strip()
            out.append(f"| {_cell(a.name)} | {_cell(codeunit)} | {_cell(f.method)} | {f.line} | "
                       f"{_value(f.expected)} | {_value(f.actual)} | {_cell(f.reason)} |")
        out.append("")
    out += ["| | Test app | Passed | Failed | Skipped | Time |",
            "|---|---|---:|---:|---:|---:|"]
    for a in apps:
        out.append(f"| {'❌' if a.failed else '✅'} | {_cell(a.name)} | {a.passed} | "
                   f"{a.failed} | {a.skipped} | {_duration(a.seconds)} |")
    out.append("")
    if failures:
        out += ["### Failure details", ""]
        for a, f in failures:
            head = f"{f.codeunit_name or 'Codeunit'} ({f.codeunit}) · {f.method}"
            out += [f"<details><summary>❌ {html.escape(head)}</summary>", "",
                    "<pre>" + html.escape(f.message, quote=False), ""]
            out += [html.escape(frame, quote=False) for frame in f.callstack]
            out += ["</pre>", "</details>", ""]
    return "\n".join(out)


def annotations(apps: list[AppResult]) -> list[str]:
    def esc(text: str) -> str:
        return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")

    lines = []
    for a in apps:
        for f in a.failures:
            title = f"{f.codeunit_name or 'Codeunit'} ({f.codeunit}) {f.method}"
            title = esc(title).replace(",", "%2C").replace("::", "%3A%3A")
            text = f.message
            if f.expected is not None:
                text = f"Expected {f.expected or '(empty)'}, actual {f.actual or '(empty)'}."
                text += f" {f.reason}" if f.reason else ""
            lines.append(f"::error title={title}::{esc(text)}")
    return lines


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--title", default="Tests")
    ap.add_argument("--summary-file", help="append the Markdown here (e.g. $GITHUB_STEP_SUMMARY)")
    ap.add_argument("files", nargs="*", type=Path)
    args = ap.parse_args(argv)
    try:
        apps = read_apps([f for f in args.files if f.is_file()])
    except (OSError, ValueError, ET.ParseError) as exc:
        print(f"::warning::junit-report: {exc}")
        return 0
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
