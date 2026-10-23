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

# The shape run-tests-hybrid.py writes: one file per test app, suites named after the
# codeunit id, the AL call stack in the failure text.
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
                      "`1 (Integer)` | `0 (Integer)` | The line must be posted. |", md)
        self.assertIn("| apps-Sales-test | 50101 | OnOpenPage |  |  |  | Unhandled UI: Confirm |", md)
        self.assertIn("| ❌ | apps-Sales-test | 1 | 2 | 1 |", md)
        self.assertIn("<summary>❌ Sales Test (50100) · PostOrderWithDiscount</summary>", md)
        self.assertNotIn("Library Assert by Microsoft version", md)

    def test_empty_expected_value_reads_as_empty(self):
        self.sales.write_text(SALES.replace("Expected:&lt;1&gt; (Integer)", "Expected:&lt;&gt; (Date)"),
                              encoding="utf-8")
        md = report.markdown(self.apps(), "Tests")
        self.assertIn("| 24 | *(empty)* | `0 (Integer)` |", md)

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
        with self.assertRaises(ValueError):
            report.read_apps([evil])

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
