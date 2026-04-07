#!/usr/bin/env python3
"""
resolve-keep-app-ids.py — Compute the BC_KEEP_APP_IDS set for a consumer
project so the BC container can selectively uninstall stock extensions
the project does NOT depend on, dramatically improving startup time.

Inputs:
  --app-json <path>     Path to a consumer app.json (may be repeated)
  --app-file <path>     Path to a consumer .app file (may be repeated)
  --artifact-dir <path> BC artifact root that contains stock extensions
                         (e.g. .../artifact-cache/27.5)
  --extra-ids <list>    Comma-separated GUIDs to always include on top
                         of the baseline + dependency closure (optional)

Output:
  Comma-separated lowercase GUIDs to stdout — feed straight into
  BC_KEEP_APP_IDS for `BC_CLEAR_ALL_APPS=selective`.

Logic:
  baseline ∪ extras ∪ transitive_closure(consumer_immediate_deps,
                                          available_artifact_apps)

The baseline is ALWAYS included, even if --app-json / --app-file are
empty or fail to parse, so the container can never accidentally clear
an app the project might secretly depend on. The five baseline apps
(System, System Application, Business Foundation, Base Application,
Application umbrella) are stable across BC versions per Microsoft
convention.
"""

import argparse
import io
import json
import os
import re
import sys
import zipfile
from pathlib import Path

# ── Baseline (always kept) ────────────────────────────────────────────────
# Stable across BC versions. Including any of these in BC_KEEP_APP_IDS is
# harmless even if the app is not actually published in this BC build —
# the entrypoint's keep filter just won't match it.
BASELINE = {
    "8874ed3a-0643-4247-9ced-7a7002f7135d",  # System (AL platform symbols)
    "63ca2fa4-4f03-4f2b-a480-172fef340d3f",  # System Application
    "f3552374-a1f2-4356-848e-196002525837",  # Business Foundation
    "437dbf0e-84ff-417a-965d-ed2bb9650972",  # Base Application
    "c1335042-3002-4257-bf8a-75c898ccb1b8",  # Application umbrella
}


# ── .app file reader ──────────────────────────────────────────────────────
# Supports regular .app files (NavxManifest.xml at root), R2R packages
# (readytorunappmanifest.json + nested .app), and rare app.json fallback.
# Adapted from sort-apps-by-deps.py.

def _xml_attr(xml: str, attr: str) -> str | None:
    m = re.search(rf'{attr}\s*=\s*"([^"]*)"', xml, re.IGNORECASE)
    return m.group(1) if m else None


def _read_inner_app(data: bytes) -> dict | None:
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as inner_z:
            if "NavxManifest.xml" in inner_z.namelist():
                xml = inner_z.read("NavxManifest.xml").decode("utf-8", errors="replace")
                deps = []
                for m in re.finditer(
                    r"<Dependency[^>]*?/>|<Dependency[^>]*?>.*?</Dependency>",
                    xml, re.DOTALL,
                ):
                    dep_xml = m.group(0)
                    dep_id = (_xml_attr(dep_xml, "AppId") or _xml_attr(dep_xml, "Id") or "")
                    if dep_id:
                        deps.append({"id": dep_id.lower()})
                return {"dependencies": deps}
    except Exception:
        pass
    return None


def read_app_info(app_path: str) -> dict | None:
    try:
        with zipfile.ZipFile(app_path) as z:
            names = z.namelist()

            if "readytorunappmanifest.json" in names:
                manifest = json.loads(z.read("readytorunappmanifest.json"))
                app_id = manifest.get("EmbeddedAppId", "").lower()
                deps = []
                for dep in manifest.get("Dependencies", []):
                    dep_id = (dep.get("AppId") or dep.get("Id") or "").lower()
                    if dep_id:
                        deps.append({"id": dep_id})
                if not deps:
                    nested = manifest.get("EmbeddedAppFileName", "")
                    if nested and nested in names:
                        try:
                            inner = _read_inner_app(z.read(nested))
                            if inner:
                                deps = inner.get("dependencies", [])
                        except Exception:
                            pass
                return {"id": app_id, "dependencies": deps, "path": app_path}

            if "NavxManifest.xml" in names:
                xml = z.read("NavxManifest.xml").decode("utf-8", errors="replace")
                app_id = (_xml_attr(xml, "Id") or _xml_attr(xml, "AppId") or "").lower()
                deps = []
                for m in re.finditer(
                    r"<Dependency[^>]*?/>|<Dependency[^>]*?>.*?</Dependency>",
                    xml, re.DOTALL,
                ):
                    dep_xml = m.group(0)
                    dep_id = (_xml_attr(dep_xml, "AppId") or _xml_attr(dep_xml, "Id") or "")
                    if dep_id:
                        deps.append({"id": dep_id.lower()})
                return {"id": app_id, "dependencies": deps, "path": app_path}

            if "app.json" in names:
                data = json.loads(z.read("app.json").decode("utf-8-sig"))
                deps = []
                for dep in data.get("dependencies", []):
                    dep_id = (dep.get("id") or dep.get("appId") or "").lower()
                    if dep_id:
                        deps.append({"id": dep_id})
                return {
                    "id": (data.get("id") or "").lower(),
                    "dependencies": deps,
                    "path": app_path,
                }
    except Exception as e:
        print(f"WARN: cannot read {app_path}: {e}", file=sys.stderr)
    return None


# ── Resolver ──────────────────────────────────────────────────────────────

def parse_id_list(s: str) -> set[str]:
    return {x.strip().lower() for x in s.split(",") if x.strip()}


def load_artifact_apps(artifact_dir: str) -> dict:
    apps = {}
    for root, _, files in os.walk(artifact_dir):
        for f in files:
            if not f.endswith(".app"):
                continue
            info = read_app_info(os.path.join(root, f))
            if info and info.get("id"):
                # Don't overwrite an already-loaded app id (first write wins).
                apps.setdefault(info["id"], info)
    return apps


def read_consumer_seeds(app_json_paths, app_file_paths) -> set[str]:
    """Return the set of immediate dependency GUIDs declared by the consumer."""
    seeds = set()
    for p in app_json_paths:
        try:
            data = json.loads(Path(p).read_text(encoding="utf-8-sig"))
            for dep in data.get("dependencies", []):
                dep_id = (dep.get("id") or dep.get("appId") or "").lower()
                if dep_id:
                    seeds.add(dep_id)
        except Exception as e:
            print(f"WARN: cannot read {p}: {e}", file=sys.stderr)
    for p in app_file_paths:
        info = read_app_info(p)
        if info:
            for dep in info.get("dependencies", []):
                seeds.add(dep["id"])
    return seeds


def walk_closure(seed_ids: set[str], apps: dict) -> set[str]:
    """Walk the transitive dependency closure starting from seed_ids."""
    out = set()

    def visit(aid: str):
        if aid in out:
            return
        if aid not in apps:
            return  # not in this BC build — skip
        out.add(aid)
        for dep in apps[aid].get("dependencies", []):
            visit(dep["id"])

    for sid in seed_ids:
        visit(sid)
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--app-json", action="append", default=[],
                        help="Path to a consumer app.json (repeatable)")
    parser.add_argument("--app-file", action="append", default=[],
                        help="Path to a consumer .app file (repeatable)")
    parser.add_argument("--artifact-dir", required=True,
                        help="BC artifact root (e.g. artifact-cache/27.5)")
    parser.add_argument("--extra-ids", default="",
                        help="Comma-separated extra GUIDs to always include")
    args = parser.parse_args()

    keep = set(BASELINE)
    extras = parse_id_list(args.extra_ids)
    if extras:
        keep |= extras
        print(f"+ extras: {len(extras)}", file=sys.stderr)

    seeds = read_consumer_seeds(args.app_json, args.app_file)
    print(f"consumer seeds: {len(seeds)}", file=sys.stderr)

    if seeds:
        artifact_apps = load_artifact_apps(args.artifact_dir)
        print(f"artifact apps loaded: {len(artifact_apps)}", file=sys.stderr)
        closure = walk_closure(seeds, artifact_apps)
        print(f"transitive closure: {len(closure)}", file=sys.stderr)
        unresolved = seeds - closure
        if unresolved:
            print(f"WARN: {len(unresolved)} consumer dep(s) not found in artifact "
                  f"set (will rely on baseline): {sorted(unresolved)}", file=sys.stderr)
        keep |= closure

    closure_added = len(keep - BASELINE - extras) if seeds else 0
    print(f"final keep set: {len(keep)} apps "
          f"(baseline={len(BASELINE)}, "
          f"closure_added={closure_added}, "
          f"extras={len(extras)})", file=sys.stderr)

    # Output: comma-separated lowercase GUIDs, sorted for determinism.
    print(",".join(sorted(keep)))


if __name__ == "__main__":
    main()
