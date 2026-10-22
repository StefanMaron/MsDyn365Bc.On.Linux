#!/usr/bin/env python3
"""Tests for scripts/compile-workspace.py. Run: python3 scripts/test-compile-workspace.py

A fake altool stands in for the compiler: per workspace folder it writes an
.app into --outfolder, and fails a folder whose dependency is not in
--packagecachepath yet, the way altool fails with AL1022.
"""

from __future__ import annotations

import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "compile-workspace.py")

FAKE_ALTOOL = r'''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
ws = json.load(open(args[2]))
opt = {args[i]: args[i + 1] for i in range(3, len(args) - 1, 2)}
with open(os.environ["FAKE_ALTOOL_CALLS"], "a") as log:
    log.write(json.dumps({"folders": [f["path"] for f in ws["folders"]], "opt": opt,
                          "stdin_is_null": os.path.samefile("/dev/stdin", "/dev/null"),
                          "term": os.environ.get("TERM")}) + "\n")
failed = False
for f in ws["folders"]:
    app = json.load(open(os.path.join(f["path"], "app.json")))
    missing = [d["name"] for d in app.get("dependencies", [])
               if not os.path.exists(os.path.join(opt["--packagecachepath"], d["name"] + ".app"))]
    if missing or app["name"] in os.environ.get("FAKE_FAIL", ""):
        failed = True
        continue
    import zipfile
    with zipfile.ZipFile(os.path.join(opt["--outfolder"], app["name"] + ".app"), "w") as z:
        z.writestr("NavxManifest.xml",
                   f'<Package><App Id="{app["id"]}" Name="{app["name"]}" Publisher="P" '
                   f'Version="1.0.0.0" /></Package>')
sys.exit(1 if failed else 0)
'''


class CompileWorkspaceTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = tmp.name
        self.altool = os.path.join(self.root, "altool")
        with open(self.altool, "w") as f:
            f.write(FAKE_ALTOOL)
        os.chmod(self.altool, os.stat(self.altool).st_mode | stat.S_IEXEC)
        self.calls = os.path.join(self.root, "calls.jsonl")
        self.cache = os.path.join(self.root, "symbols")
        os.mkdir(self.cache)
        self.out = os.path.join(self.root, "build")
        # Core <- Sales <- Sales Test, and Shipping independent
        self.core = self.app("Core")
        self.sales = self.app("Sales", deps=("Core",))
        self.test = self.app("Sales Test", deps=("Sales", "Core"))
        self.shipping = self.app("Shipping")

    def app(self, name, deps=()):
        folder = os.path.join(self.root, "apps", name.replace(" ", "_"))
        os.makedirs(folder)
        with open(os.path.join(folder, "app.json"), "w") as f:
            json.dump({"id": name.lower(), "name": name, "publisher": "P", "version": "1.0.0.0",
                       "dependencies": [{"id": d.lower(), "name": d, "publisher": "P",
                                         "version": "1.0.0.0"} for d in deps]}, f)
        return folder

    def run_script(self, *dirs, flags=(), altool_options=(), env=None):
        e = dict(os.environ, FAKE_ALTOOL_CALLS=self.calls, **(env or {}))
        cmd = [sys.executable, SCRIPT, "--altool", self.altool, "--package-cache", self.cache,
               "--out-dir", self.out, *flags, *dirs]
        if altool_options:
            cmd += ["--", *altool_options]
        return subprocess.run(cmd, capture_output=True, text=True, env=e)

    def altool_calls(self):
        if not os.path.exists(self.calls):
            return []
        with open(self.calls) as f:
            return [json.loads(line) for line in f]

    def test_dependencies_compile_in_an_earlier_wave(self):
        r = self.run_script(self.test, self.sales, self.core, self.shipping)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        waves = [sorted(os.path.basename(p) for p in c["folders"]) for c in self.altool_calls()]
        self.assertEqual(waves, [["Core", "Shipping"], ["Sales"], ["Sales_Test"]])

    def test_altool_never_writes_into_the_package_cache(self):
        self.run_script(self.core, self.sales)
        for call in self.altool_calls():
            self.assertEqual(call["opt"]["--packagecachepath"], self.cache)
            self.assertNotEqual(os.path.realpath(call["opt"]["--outfolder"]),
                                os.path.realpath(self.cache))

    def test_every_app_ends_up_in_out_dir_and_package_cache(self):
        self.run_script(self.core, self.sales, self.test)
        for name in ("Core", "Sales", "Sales Test"):
            self.assertTrue(os.path.isfile(os.path.join(self.out, name + ".app")), name)
            self.assertTrue(os.path.isfile(os.path.join(self.cache, name + ".app")), name)

    def test_options_after_double_dash_reach_every_wave(self):
        self.run_script(self.core, self.sales, flags=("--max-cpu", "3"),
                        altool_options=("--ruleset", "r.json"))
        self.assertEqual({c["opt"]["--maxcpucount"] for c in self.altool_calls()}, {"3"})
        self.assertEqual({c["opt"]["--ruleset"] for c in self.altool_calls()}, {"r.json"})

    def test_without_max_cpu_altool_picks_its_default(self):
        self.run_script(self.core)
        self.assertNotIn("--maxcpucount", self.altool_calls()[0]["opt"])

    def test_altool_gets_no_stdin(self):
        self.run_script(self.core)
        self.assertTrue(self.altool_calls()[0]["stdin_is_null"])

    def test_altool_gets_a_term_when_the_caller_has_none(self):
        # altool's progress display crashes (ArgumentOutOfRangeException) when
        # TERM is unset, as it is for a CI runner service
        env = {k: v for k, v in os.environ.items() if k != "TERM"}
        env["FAKE_ALTOOL_CALLS"] = self.calls
        r = subprocess.run([sys.executable, SCRIPT, "--altool", self.altool, "--package-cache",
                            self.cache, "--out-dir", self.out, self.core],
                           capture_output=True, text=True, env=env)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.altool_calls()[0]["term"], "dumb")

    def test_a_term_the_caller_set_is_kept(self):
        self.run_script(self.core, env={"TERM": "xterm"})
        self.assertEqual(self.altool_calls()[0]["term"], "xterm")

    def test_failed_wave_stops_and_fails(self):
        r = self.run_script(self.core, self.sales, self.test, env={"FAKE_FAIL": "Sales"})
        self.assertNotEqual(r.returncode, 0)
        self.assertEqual(len(self.altool_calls()), 2)
        self.assertIn("Sales", r.stdout + r.stderr)

    def test_name_from_dir_names_each_app_after_its_dir(self):
        r = self.run_script(self.core, self.sales, flags=("--name-from-dir",))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(sorted(os.listdir(self.out)), ["Core.app", "Sales.app"])
        self.run_script(self.test, flags=("--name-from-dir",))
        self.assertIn("Sales_Test.app", os.listdir(self.out))

    def test_altool_dll_runs_through_dotnet(self):
        fake_dotnet_dir = os.path.join(self.root, "bin")
        os.mkdir(fake_dotnet_dir)
        dotnet = os.path.join(fake_dotnet_dir, "dotnet")
        with open(dotnet, "w") as f:
            f.write('#!/bin/sh\nshift\nexec "$ALTOOL_SCRIPT" "$@"\n')
        os.chmod(dotnet, 0o755)
        e = dict(os.environ, FAKE_ALTOOL_CALLS=self.calls, ALTOOL_SCRIPT=self.altool,
                 PATH=fake_dotnet_dir + os.pathsep + os.environ["PATH"])
        r = subprocess.run([sys.executable, SCRIPT, "--altool-dll", "/x/altool.dll",
                            "--package-cache", self.cache, "--out-dir", self.out, self.core],
                           capture_output=True, text=True, env=e)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(len(self.altool_calls()), 1)

    def test_dependency_cycle_fails_before_compiling(self):
        a = self.app("A", deps=("B",))
        b = self.app("B", deps=("A",))
        r = self.run_script(a, b)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("cycle", r.stdout + r.stderr)
        self.assertEqual(self.altool_calls(), [])


if __name__ == "__main__":
    unittest.main()
