#!/usr/bin/env python3
"""
compile-workspace.py — compile AL apps with `altool workspace compile`, one
dependency wave at a time.

    python3 scripts/compile-workspace.py --altool "$AL_TOOL_DIR/altool" \\
        --package-cache symbols --out-dir build [--max-cpu N] \\
        app/dir ... -- [altool options, e.g. --customanalyzers ... --ruleset ...]

altool compiles independent projects in parallel, which is much faster than one
`alc` per app. But a project's dependencies must be in --packagecachepath, and
when that is also the --outfolder, a compiler process can open an .app another
one is still writing: "AL1023: The package file ... is not valid" on Linux,
"AL1085 ... is locked" on Windows, a different app on every run.

So every wave holds only apps whose dependencies (within the given dirs) are in
an earlier wave. A wave compiles into a staging folder outside the package
cache, and its apps are copied into the package cache and --out-dir before the
next wave starts. Nothing is ever written where a compiler is reading.

--max-cpu is passed as altool's --maxcpucount; without it altool uses the
number of processors. Per-project compiler logs go to --log-dir if given.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile


def read_app(folder: str) -> dict:
    with open(os.path.join(folder, "app.json"), encoding="utf-8-sig") as f:
        manifest = json.load(f)
    return {
        "dir": folder,
        "id": str(manifest.get("id", "")).lower(),
        "name": manifest.get("name", folder),
        "deps": [str(d.get("id") or d.get("appId") or "").lower()
                 for d in manifest.get("dependencies", [])],
    }


def waves(apps: list[dict]) -> list[list[dict]]:
    """Dependency levels; dependencies outside ``apps`` are ignored."""
    ids = {a["id"] for a in apps}
    done: set[str] = set()
    remaining = list(apps)
    result = []
    while remaining:
        wave = [a for a in remaining if all(d in done or d not in ids for d in a["deps"])]
        if not wave:
            names = ", ".join(sorted(a["name"] for a in remaining))
            raise ValueError(f"dependency cycle among: {names}")
        result.append(wave)
        done.update(a["id"] for a in wave)
        remaining = [a for a in remaining if a["id"] not in done]
    return result


def read_app_id(app_file: str) -> str:
    """The app id from a compiled .app (NavxManifest.xml); "" when unreadable."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from _bcapp import read_app_info  # noqa: E402
    info = read_app_info(app_file)
    return (info or {}).get("id", "")


def dir_name(folder: str) -> str:
    """The name the per-dir compile gives an app: build/<basename of its dir>.app."""
    return os.path.basename(os.path.normpath(folder))


def compile_wave(altool: list[str], wave: list[dict], package_cache: str, staging: str,
                 max_cpu: int | None, log_dir: str | None, options: list[str]) -> int:
    with tempfile.NamedTemporaryFile("w", suffix=".code-workspace", delete=False) as ws:
        json.dump({"folders": [{"path": os.path.abspath(a["dir"])} for a in wave]}, ws)
    try:
        cmd = [*altool, "workspace", "compile", ws.name,
               "--packagecachepath", os.path.abspath(package_cache),
               "--outfolder", os.path.abspath(staging)]
        if max_cpu is not None:
            cmd += ["--maxcpucount", str(max_cpu)]
        if log_dir:
            cmd += ["--logdirectory", os.path.abspath(log_dir)]
        cmd += options
        # altool's progress display crashes with ArgumentOutOfRangeException
        # (count -1) when TERM is unset, as it is for a CI runner service.
        env = dict(os.environ)
        env.setdefault("TERM", "dumb")
        # stdin from /dev/null: a caller's `while read` loop must keep its input
        return subprocess.run(cmd, stdin=subprocess.DEVNULL, env=env).returncode
    finally:
        os.unlink(ws.name)


NO_WORKSPACE_COMPILE = (
    "compile-workspace: this altool has no 'workspace compile' command. It needs "
    "AL 17 or newer (AL 16's altool has none): set al_tool_version to 'latest' "
    "or to an AL 17+ version, or turn workspace_compile off.")


def has_workspace_compile(altool: list[str]) -> bool:
    env = dict(os.environ)
    env.setdefault("TERM", "dumb")
    probe = subprocess.run([*altool, "workspace", "compile", "--help"], stdin=subprocess.DEVNULL,
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=env)
    return probe.returncode == 0 and "workspace compile" in probe.stdout


def main(argv: list[str]) -> int:
    if "--" in argv:
        split = argv.index("--")
        argv, options = argv[:split], argv[split + 1:]
    else:
        options = []
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    tool = ap.add_mutually_exclusive_group(required=True)
    tool.add_argument("--altool", help="native altool executable")
    tool.add_argument("--altool-dll", help="framework-dependent altool.dll, run with dotnet")
    ap.add_argument("--name-from-dir", action="store_true",
                    help="name each .app in --out-dir after its dir, like the per-dir compile "
                         "(build/<dir basename>.app); default keeps altool's file name")
    ap.add_argument("--package-cache", required=True)
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--max-cpu", type=int)
    ap.add_argument("--log-dir")
    ap.add_argument("dirs", nargs="+")
    args = ap.parse_args(argv)

    dirs = []
    for d in args.dirs:
        if os.path.isfile(os.path.join(d, "app.json")):
            dirs.append(d)
        else:
            print(f"skip {d} (no app.json)", file=sys.stderr)
    try:
        apps = [read_app(d) for d in dirs]
        plan = waves(apps)
    except (OSError, ValueError) as exc:
        print(f"compile-workspace: {exc}", file=sys.stderr)
        return 1

    altool = [args.altool] if args.altool else ["dotnet", args.altool_dll]
    if apps and not has_workspace_compile(altool):
        print(NO_WORKSPACE_COMPILE, file=sys.stderr)
        return 1
    os.makedirs(args.out_dir, exist_ok=True)
    os.makedirs(args.package_cache, exist_ok=True)
    print(f"compile-workspace: {len(apps)} apps in {len(plan)} waves")
    for number, wave in enumerate(plan, start=1):
        names = ", ".join(a["name"] for a in wave)
        print(f"compile-workspace: wave {number}/{len(plan)}: {names}", flush=True)
        staging = tempfile.mkdtemp(prefix="compile-workspace-")
        try:
            rc = compile_wave(altool, wave, args.package_cache, staging,
                              args.max_cpu, args.log_dir, options)
            built = {}
            for name in os.listdir(staging):
                if name.endswith(".app"):
                    path = os.path.join(staging, name)
                    built[read_app_id(path)] = path
                    shutil.copy2(path, os.path.join(args.package_cache, name))
            for app in wave:
                path = built.get(app["id"])
                if path is None:
                    continue
                target = dir_name(app["dir"]) + ".app" if args.name_from_dir \
                    else os.path.basename(path)
                shutil.move(path, os.path.join(args.out_dir, target))
        finally:
            shutil.rmtree(staging, ignore_errors=True)
        if rc != 0:
            print(f"compile-workspace: wave {number} failed ({names}), altool exit code {rc}",
                  file=sys.stderr)
            return rc
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
