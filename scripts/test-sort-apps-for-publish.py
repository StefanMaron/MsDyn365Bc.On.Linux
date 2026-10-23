#!/usr/bin/env python3
"""Tests for scripts/sort-apps-for-publish.py. Run: python3 scripts/test-sort-apps-for-publish.py"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
import zipfile

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sort-apps-for-publish.py")


def write_app(folder: str, file_name: str, app_id: str, name: str, deps: tuple[str, ...] = ()) -> str:
    deps_xml = "".join(f'<Dependency Id="{d}" Name="{d}" Publisher="P" MinVersion="1.0.0.0" />'
                       for d in deps)
    manifest = (f'<Package><App Id="{app_id}" Name="{name}" Publisher="P" Version="1.0.0.0" />'
                f"<Dependencies>{deps_xml}</Dependencies></Package>")
    path = os.path.join(folder, file_name)
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("NavxManifest.xml", manifest)
    return path


def run(*paths: str) -> list[str]:
    out = subprocess.run([sys.executable, SCRIPT, *paths], capture_output=True, text=True, check=True)
    return out.stdout.splitlines()


class SortAppsForPublishTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = self.tmp.name

    def test_dependencies_come_first_whatever_the_file_names(self):
        # alphabetical order is the wrong one: Connector needs Core, Core needs System
        connector = write_app(self.dir, "a_Connector.app", "c", "Connector", deps=("b", "s"))
        core = write_app(self.dir, "b_Core.app", "b", "Core", deps=("s",))
        system = write_app(self.dir, "c_System.app", "s", "System")
        self.assertEqual(run(connector, core, system), [system, core, connector])

    def test_dependencies_outside_the_set_are_ignored(self):
        app = write_app(self.dir, "x.app", "x", "X", deps=("437dbf0e-84ff-417a-965d-ed2bb9650972",))
        self.assertEqual(run(app), [app])

    def test_independent_apps_keep_their_given_order(self):
        b = write_app(self.dir, "b.app", "b", "B")
        a = write_app(self.dir, "a.app", "a", "A")
        self.assertEqual(run(b, a), [b, a])

    def test_unreadable_app_keeps_its_place_at_the_end(self):
        bad = os.path.join(self.dir, "broken.app")
        with open(bad, "wb") as f:
            f.write(b"not a zip")
        good = write_app(self.dir, "good.app", "g", "G")
        self.assertEqual(run(bad, good), [good, bad])

    def test_cycle_is_an_error(self):
        a = write_app(self.dir, "a.app", "a", "A", deps=("b",))
        b = write_app(self.dir, "b.app", "b", "B", deps=("a",))
        result = subprocess.run([sys.executable, SCRIPT, a, b], capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("cycle", result.stderr)


if __name__ == "__main__":
    unittest.main()
