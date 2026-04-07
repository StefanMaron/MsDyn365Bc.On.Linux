#!/usr/bin/env python3
"""
stage-symbols.py — Manifest-driven .alpackages staging for AL compile.

Walks a BC artifact tree, indexes every .app file by its declared id, then
copies into the output directory exactly the symbols a consumer's
project needs to compile: System.app + the transitive dependency closure
of every consumer app.json passed in.

This replaces the older glob-based staging that lived inline in
bc-test-from-source.yml. The glob approach broke whenever Microsoft
moved an app to a different folder (e.g. Tests-TestLibraries lives at
platform/applications/BaseApp/Test/ in BC 27.5, not app/Applications/
where the old glob looked). Indexing by manifest is robust to those
moves: an app is found by its declared id no matter where in the
artifact it sits.

Usage:
  stage-symbols.py \\
      --app-json <consumer-app-json> [--app-json <more>] \\
      --artifact-dir <bc-artifact-root> \\
      --out-dir <symbols-target-dir>

The script always stages System.app (the AL platform symbols) since AL
compile cannot proceed without it, even when no consumer dep references
it explicitly.

Exit codes:
  0 success
  1 missing input or unrecoverable failure
  2 one or more consumer dependencies could not be resolved against
    the artifact set (a warning is also printed)
"""

import argparse
import json
import os
import shutil
import sys
from pathlib import Path

# Make sibling helpers importable when invoked as `python3 .../stage-symbols.py`
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _bcapp import load_artifact_apps, version_tuple  # noqa: E402

# AL compile fails without System.app even if nothing references it
# explicitly — it provides the platform-level base types. This GUID is
# stable across BC versions.
SYSTEM_APP_ID = "8874ed3a-0643-4247-9ced-7a7002f7135d"


def read_consumer_seeds(app_json_paths: list[str]) -> tuple[set[str], set[str]]:
    """Return (seed_ids, self_ids) parsed from consumer app.json files.

    seed_ids — the set of dependency GUIDs the consumer declares.
    self_ids — the consumer's own app GUIDs (used to filter out
               "unresolved" warnings for the consumer's own production
               app, which is compiled separately and not present in the
               BC artifact).
    """
    seeds: set[str] = set()
    self_ids: set[str] = set()
    for p in app_json_paths:
        try:
            data = json.loads(Path(p).read_text(encoding="utf-8-sig"))
        except Exception as e:
            print(f"ERROR: cannot read {p}: {e}", file=sys.stderr)
            continue
        own_id = (data.get("id") or "").lower()
        if own_id:
            self_ids.add(own_id)
        for dep in data.get("dependencies", []):
            dep_id = (dep.get("id") or dep.get("appId") or "").lower()
            if dep_id:
                seeds.add(dep_id)
    return seeds, self_ids


def resolve_closure(seed_ids: set[str], apps: dict) -> tuple[set[str], set[str]]:
    """BFS the dependency graph from seed_ids through the indexed apps.

    Returns (resolved, unresolved) — resolved is the set of app ids that
    were found in the artifact and will be staged; unresolved is the set
    of seed ids (or transitively-required ids) that the artifact does
    not contain.
    """
    resolved: set[str] = set()
    unresolved: set[str] = set()
    queue: list[str] = list(seed_ids)
    while queue:
        aid = queue.pop()
        if aid in resolved:
            continue
        if aid not in apps:
            unresolved.add(aid)
            continue
        resolved.add(aid)
        for dep in apps[aid].get("dependencies", []):
            queue.append(dep["id"])
    return resolved, unresolved


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--app-json", action="append", default=[], required=True,
                        help="Path to a consumer app.json (repeatable, at least one)")
    parser.add_argument("--artifact-dir", required=True,
                        help="BC artifact root (e.g. artifact-cache/27.5)")
    parser.add_argument("--out-dir", required=True,
                        help="Directory to copy resolved .app files into "
                             "(created if missing; existing files are overwritten)")
    args = parser.parse_args()

    if not os.path.isdir(args.artifact_dir):
        print(f"ERROR: artifact dir does not exist: {args.artifact_dir}", file=sys.stderr)
        return 1
    os.makedirs(args.out_dir, exist_ok=True)

    print(f"[stage-symbols] Indexing .app files under {args.artifact_dir}...", file=sys.stderr)
    apps = load_artifact_apps(args.artifact_dir)
    print(f"[stage-symbols] Indexed {len(apps)} unique apps", file=sys.stderr)

    seeds, self_ids = read_consumer_seeds(args.app_json)
    # Drop the consumer's own app ids from the seed set — they're compiled
    # separately and provided to AL compile via build/, not from artifacts.
    seeds -= self_ids
    # System.app is implicit — every AL compile needs it.
    seeds.add(SYSTEM_APP_ID)
    print(f"[stage-symbols] Consumer dependency seeds: {len(seeds)} "
          f"({len(seeds) - 1} explicit + System)", file=sys.stderr)

    resolved, unresolved = resolve_closure(seeds, apps)
    # Filter out consumer-self ids from any transitive unresolved set too.
    unresolved -= self_ids

    if SYSTEM_APP_ID in unresolved:
        print(f"ERROR: System.app ({SYSTEM_APP_ID}) not found anywhere in the "
              f"artifact tree. AL compile cannot proceed without it. "
              f"Check that the artifact extraction included platform/ModernDev/.",
              file=sys.stderr)
        return 1

    # Copy each resolved .app to the output directory.
    copied = 0
    for aid in sorted(resolved):
        info = apps[aid]
        src = info["path"]
        dst = os.path.join(args.out_dir, os.path.basename(src))
        try:
            shutil.copy2(src, dst)
            copied += 1
        except Exception as e:
            print(f"WARN: failed to copy {src}: {e}", file=sys.stderr)

    print(f"[stage-symbols] Staged {copied} .app file(s) into {args.out_dir}", file=sys.stderr)

    if unresolved:
        # Map unresolved ids back to (publisher, name, version) where we can
        # tell from any app that referenced them, for clearer diagnostics.
        ref_index: dict[str, dict] = {}
        for app in apps.values():
            for dep in app.get("dependencies", []):
                if dep["id"] in unresolved and dep["id"] not in ref_index:
                    ref_index[dep["id"]] = dep
        # Also check the consumer app.jsons themselves for richer info.
        for p in args.app_json:
            try:
                data = json.loads(Path(p).read_text(encoding="utf-8-sig"))
            except Exception:
                continue
            for dep in data.get("dependencies", []):
                dep_id = (dep.get("id") or dep.get("appId") or "").lower()
                if dep_id in unresolved and dep_id not in ref_index:
                    ref_index[dep_id] = {
                        "id": dep_id,
                        "name": dep.get("name", ""),
                        "publisher": dep.get("publisher", ""),
                        "version": dep.get("version", ""),
                    }
        print(f"WARN: {len(unresolved)} dependency id(s) not found in artifact set:",
              file=sys.stderr)
        for aid in sorted(unresolved):
            d = ref_index.get(aid, {"id": aid})
            label = f'{d.get("publisher", "?")} / {d.get("name", "?")} '
            label += f'(>= {d.get("version", "?")})' if d.get("version") else ""
            print(f"  - {aid}  {label}", file=sys.stderr)
        # Don't hard-fail: AL compile will give a more specific error if
        # this matters. Some unresolved deps are deliberate (e.g. apps the
        # consumer ships and publishes themselves).
        return 2

    return 0


if __name__ == "__main__":
    sys.exit(main())
