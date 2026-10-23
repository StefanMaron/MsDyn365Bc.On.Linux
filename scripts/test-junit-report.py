#!/usr/bin/env python3
"""Tests for scripts/junit-report.py. Run: python3 scripts/test-junit-report.py"""

import importlib.util
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "junit-report.py"
spec = importlib.util.spec_from_file_location("junit_report", SCRIPT)
report = importlib.util.module_from_spec(spec)
sys.modules["junit_report"] = report
spec.loader.exec_module(report)

# The shape run-tests-altool.py writes (and run-tests-hybrid.py merges): one file per
# test app, suites named after the codeunit id, the AL call stack after "AL Callstack:".
SALES = """<?xml version='1.0' encoding='utf-8'?>
<testsuites name="hybrid" tests="4" failures="1" errors="1" skipped="1" time="3.5">
  <testsuite name="Codeunit 50100" tests="3" failures="1" errors="0" skipped="1" time="2.5">
    <testcase classname="Codeunit 50100" name="PostOrder" time="1.0" />
    <testcase classname="Codeunit 50100" name="PostOrderWithDiscount" time="0.5">
      <failure message="Assert.AreEqual failed. Expected:&lt;1&gt; (Integer). Actual:&lt;0&gt; (Integer). The line must be posted.." type="AssertionFailure">Assert.AreEqual failed. Expected:&lt;1&gt; (Integer). Actual:&lt;0&gt; (Integer). The line must be posted..
AL Callstack:
"Library Assert"(CodeUnit 130002).AreEqual line 3 - Library Assert by Microsoft version 28.3.0.0
"Sales Test"(CodeUnit 50100).PostOrderWithDiscount line 24 - Sales Tests by Contoso version 1.0.0.0
</failure>
    </testcase>
    <testcase classname="Codeunit 50100" name="Later" time="0"><skipped /></testcase>
  </testsuite>
  <testsuite name="Codeunit 50101" tests="1" failures="0" errors="1" skipped="0" time="1.0">
    <testcase classname="Codeunit 50101" name="OnOpenPage" time="1.0">
      <error message="Unhandled UI: Confirm">Unhandled UI: Confirm</error>
    </testcase>
  </testsuite>
</testsuites>
"""

SHIPPING = """<?xml version='1.0' encoding='utf-8'?>
<testsuites name="hybrid" tests="2" failures="0" errors="0" skipped="0" time="0.7">
  <testsuite name="Codeunit 50200" tests="2" failures="0" errors="0" skipped="0" time="0.7">
    <testcase classname="Codeunit 50200" name="CreateParcel" time="0.4" />
    <testcase classname="Codeunit 50200" name="OnRun" time="0.3" />
  </testsuite>
</testsuites>
"""



# The shape tools/TestRunner (the websocket runner) writes: the call stack after
# "Call stack:", one frame per line, indented by two spaces.
WEBSOCKET = """<?xml version="1.0" encoding="utf-8"?>
<testsuites name="BC Tests" tests="2" failures="1" errors="0" skipped="0" time="1.2">
  <testsuite name="Codeunit 50300" tests="2" failures="1" errors="0" skipped="0" time="1.2">
    <testcase classname="Codeunit 50300" name="ReleaseOrder" time="0.4"/>
    <testcase classname="Codeunit 50300" name="ReleaseBlockedOrder" time="0.8">
      <failure message="Assert.IsTrue failed. Expected:&lt;True&gt; (Boolean). Actual:&lt;False&gt; (Boolean). The order must stay open.." type="AssertionFailure">Assert.IsTrue failed. Expected:&lt;True&gt; (Boolean). Actual:&lt;False&gt; (Boolean). The order must stay open..

Call stack:
  "Library Assert"(CodeUnit 130002).IsTrue line 2 - Library Assert by Microsoft version 28.3.0.0
  "Release Test"(CodeUnit 50300).ReleaseBlockedOrder line 17 - Sales Tests by Contoso version 1.0.0.0
</failure>
    </testcase>
  </testsuite>
</testsuites>
"""

# run-tests-altool.py: a codeunit that failed to run, and a codeunit-level failure
# with no named test ("(codeunit)" rollup).
ALTOOL_RUN_ERRORS = """<?xml version="1.0" encoding="UTF-8"?>
<testsuites name="altool" tests="2" failures="1" errors="1" skipped="0" time="2.0">
  <testsuite name="Codeunit 50400" tests="1" failures="0" errors="1" skipped="0" time="0.000">
    <testcase classname="Codeunit 50400" name="(codeunit run)" time="1.500">
      <error message="Codeunit 50400 not found in the published app" type="RunError">Codeunit 50400 not found in the published app</error>
    </testcase>
  </testsuite>
  <testsuite name="Codeunit 50401" tests="1" failures="1" errors="0" skipped="0" time="0.500">
    <testcase classname="Codeunit 50401" name="(codeunit)" time="0.500">
      <failure message="The record in table Setup does not exist." type="AssertionFailure">The record in table Setup does not exist.
AL Callstack:
"Setup Test"(CodeUnit 50401).OnRun line 4 - Sales Tests by Contoso version 1.0.0.0</failure>
    </testcase>
  </testsuite>
</testsuites>
"""


class JunitReportTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.sales = self.root / "junit-apps-Sales-test.xml"
        self.sales.write_text(SALES, encoding="utf-8")
        self.shipping = self.root / "junit-apps-Shipping-test.xml"
        self.shipping.write_text(SHIPPING, encoding="utf-8")

    def apps(self):
        return report.read_apps([self.sales, self.shipping])

    def test_one_row_per_junit_file_named_after_it(self):
        apps = self.apps()
        self.assertEqual([a.name for a in apps], ["apps-Sales-test", "apps-Shipping-test"])
        self.assertEqual([(a.passed, a.failed, a.skipped) for a in apps], [(1, 2, 1), (2, 0, 0)])

    def test_failure_keeps_codeunit_name_line_and_our_call_stack_frames(self):
        f = self.apps()[0].failures[0]
        self.assertEqual((f.codeunit, f.codeunit_name, f.method, f.line),
                         ("50100", "Sales Test", "PostOrderWithDiscount", "24"))
        self.assertEqual(f.callstack, [
            '"Library Assert"(CodeUnit 130002).AreEqual line 3',
            '"Sales Test"(CodeUnit 50100).PostOrderWithDiscount line 24'])

    def test_expected_and_actual_are_split_out_of_the_assert_message(self):
        f = self.apps()[0].failures[0]
        self.assertEqual((f.expected, f.actual, f.reason),
                         ("1 (Integer)", "0 (Integer)", "The line must be posted."))
        self.assertEqual(report.parse_assert(
            "Assert.AreEqual failed. Expected:<> (Date). Actual:<03/01/27> (Date). Must be cleared.."),
            ("", "03/01/27 (Date)", "Must be cleared."))
        other = self.apps()[0].failures[1]
        self.assertEqual((other.expected, other.actual, other.reason),
                         (None, None, "Unhandled UI: Confirm"))

    def test_markdown_starts_with_the_failed_tests(self):
        md = report.markdown(self.apps(), "Tests (de)")
        lines = md.splitlines()
        self.assertEqual(lines[0], "## ❌ Tests (de): 3 passed, 2 failed, 1 skipped")
        self.assertEqual(lines[2], "### Failed tests")
        self.assertIn("| apps-Sales-test | 50100 Sales Test | PostOrderWithDiscount | 24 | "
                      "<code>1 (Integer)</code> | <code>0 (Integer)</code> | The line must be posted. |", md)
        self.assertIn("| apps-Sales-test | 50101 | OnOpenPage |  |  |  | Unhandled UI: Confirm |", md)
        self.assertIn("| ❌ | apps-Sales-test | 1 | 2 | 1 |", md)
        self.assertIn("<summary>❌ Sales Test (50100) · PostOrderWithDiscount</summary>", md)
        self.assertNotIn("Library Assert by Microsoft version", md)

    def test_empty_expected_value_reads_as_empty(self):
        self.sales.write_text(SALES.replace("Expected:&lt;1&gt; (Integer)", "Expected:&lt;&gt; (Date)"),
                              encoding="utf-8")
        md = report.markdown(self.apps(), "Tests")
        self.assertIn("| 24 | *(empty)* | <code>0 (Integer)</code> |", md)

    def test_all_green_has_no_failure_section(self):
        md = report.markdown(report.read_apps([self.shipping]), "Tests")
        self.assertTrue(md.startswith("## ✅ Tests: 2 passed, 0 failed, 0 skipped"))
        self.assertNotIn("Failed tests", md)

    def test_no_files_is_reported_not_hidden(self):
        self.assertIn("No test results", report.markdown([], "Tests"))

    def test_annotations_carry_expected_and_actual(self):
        lines = report.annotations(self.apps())
        self.assertEqual(len(lines), 2)
        self.assertEqual(lines[0], "::error title=Sales Test (50100) PostOrderWithDiscount::"
                                   "Expected 1 (Integer), actual 0 (Integer). The line must be posted.")
        self.assertNotIn("\n", "".join(lines))

    def test_markup_in_messages_cannot_break_the_page(self):
        self.sales.write_text(SALES.replace("The line must be posted", "&lt;/details&gt; | x"),
                              encoding="utf-8")
        md = report.markdown(self.apps(), "Tests")
        self.assertEqual(md.count("</details>"), md.count("<details>"))

    def test_a_file_with_a_doctype_is_refused(self):
        evil = self.root / "junit-evil.xml"
        evil.write_text('<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "a">]><testsuites/>',
                        encoding="utf-8")
        app = report.read_apps([evil])[0]
        self.assertIn("DOCTYPE", app.unreadable)
        self.assertEqual((app.passed, app.failed), (0, 0))

    def test_websocket_runner_call_stack_is_read(self):
        path = self.root / "junit-apps-Release-test.xml"
        path.write_text(WEBSOCKET, encoding="utf-8")
        f = report.read_apps([path])[0].failures[0]
        self.assertEqual((f.codeunit_name, f.line), ("Release Test", "17"))
        self.assertEqual(f.callstack, [
            '"Library Assert"(CodeUnit 130002).IsTrue line 2',
            '"Release Test"(CodeUnit 50300).ReleaseBlockedOrder line 17'])
        self.assertEqual((f.expected, f.actual, f.reason),
                         ("True (Boolean)", "False (Boolean)", "The order must stay open."))

    def test_codeunit_run_errors_and_rollups_are_reported(self):
        path = self.root / "junit-apps-Setup-test.xml"
        path.write_text(ALTOOL_RUN_ERRORS, encoding="utf-8")
        app = report.read_apps([path])[0]
        self.assertEqual(app.failed, 2)
        run, rollup = app.failures
        self.assertEqual((run.codeunit, run.method, run.reason),
                         ("50400", "(codeunit run)", "Codeunit 50400 not found in the published app"))
        self.assertEqual((rollup.codeunit_name, rollup.method, rollup.line),
                         ("Setup Test", "(codeunit)", "4"))

    def test_crlf_bodies_are_read_like_lf(self):
        path = self.root / "junit-apps-Release-test.xml"
        path.write_bytes(WEBSOCKET.replace("\n", "\r\n").encode("utf-8"))
        f = report.read_apps([path])[0].failures[0]
        self.assertEqual(f.line, "17")
        self.assertNotIn("\r", "".join(f.callstack))

    def test_one_unreadable_file_does_not_hide_the_others(self):
        broken = self.root / "junit-apps-Broken-test.xml"
        broken.write_text(SALES[:300], encoding="utf-8")
        apps = report.read_apps([self.sales, broken, self.shipping])
        self.assertEqual([a.name for a in apps],
                         ["apps-Broken-test", "apps-Sales-test", "apps-Shipping-test"])
        self.assertTrue(apps[0].unreadable)
        md = report.markdown(apps, "Tests")
        self.assertIn("| ⚠️ | apps-Broken-test | unreadable:", md)
        self.assertIn("| apps-Sales-test | 50100 Sales Test | PostOrderWithDiscount |", md)

    def test_multiline_messages_and_backticks_stay_in_one_table_row(self):
        body = "First line\r\nsecond `line` | x"
        self.sales.write_text(SALES.replace(
            '<error message="Unhandled UI: Confirm">Unhandled UI: Confirm</error>',
            f"<error>{body}</error>").replace(
            "Expected:&lt;1&gt; (Integer)", "Expected:&lt;a`b&gt; (Text)"), encoding="utf-8")
        md = report.markdown(self.apps(), "Tests")
        row = [l for l in md.splitlines() if "OnOpenPage" in l and l.startswith("|")][0]
        self.assertIn("First line second `line` \\| x", row)
        self.assertIn("<code>a`b (Text)</code>", md)

    def test_many_failures_are_capped_below_the_summary_limit(self):
        cases = "".join(
            f'<testcase classname="Codeunit 50500" name="T{i}" time="0.1">'
            f'<failure message="Assert.AreEqual failed. Expected:&lt;1&gt; (Integer). '
            f'Actual:&lt;0&gt; (Integer). {"x" * 400}..">{"y" * 2000}</failure></testcase>'
            for i in range(2000))
        many = self.root / "junit-apps-Many-test.xml"
        many.write_text(f'<testsuites><testsuite name="Codeunit 50500">{cases}</testsuite></testsuites>',
                        encoding="utf-8")
        md = report.markdown(report.read_apps([many]), "Tests")
        self.assertLess(len(md.encode("utf-8")), 1024 * 1024)
        self.assertIn("2,000 failed", md)
        self.assertIn("more failed tests are not listed here", md)
        self.assertEqual(md.count("</details>"), md.count("<details>"))

    def test_annotations_stop_at_ten_with_a_notice(self):
        cases = "".join(f'<testcase classname="Codeunit 50500" name="T{i}"><failure message="m"/></testcase>'
                        for i in range(12))
        many = self.root / "junit-apps-Many-test.xml"
        many.write_text(f'<testsuites><testsuite name="Codeunit 50500">{cases}</testsuite></testsuites>',
                        encoding="utf-8")
        lines = report.annotations(report.read_apps([many]))
        self.assertEqual(sum(l.startswith("::error ") for l in lines), 10)
        self.assertTrue(lines[-1].startswith("::notice::2 more failed tests"))

    def test_annotation_properties_escape_colons_and_commas(self):
        line = report.annotations(self.apps())[0]
        title = line.split("::", 2)[1]
        self.assertNotIn(":", title.removeprefix("error title="))
        self.assertIn("Sales Test (50100) PostOrderWithDiscount", title)

    def test_unknown_codeunit_name_reads_as_codeunit_id(self):
        md = report.markdown(self.apps(), "Tests")
        self.assertIn("<summary>❌ Codeunit 50101 · OnOpenPage</summary>", md)

    def test_cli_appends_to_the_summary_file_and_prints_annotations(self):
        summary = self.root / "summary.md"
        summary.write_text("before\n", encoding="utf-8")
        r = subprocess.run([sys.executable, str(SCRIPT), "--title", "Tests (de)",
                            "--summary-file", str(summary), str(self.sales), str(self.shipping)],
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        text = summary.read_text(encoding="utf-8")
        self.assertTrue(text.startswith("before\n## ❌ Tests (de)"))
        self.assertEqual(r.stdout.count("::error "), 2)

    def test_cli_with_no_files_still_succeeds(self):
        r = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("No test results", r.stdout)


if __name__ == "__main__":
    unittest.main()
