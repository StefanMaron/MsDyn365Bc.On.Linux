#!/usr/bin/env python3
"""
resolve-install-order.py — Resolve a topologically-sorted install order
for a set of "seed" apps and their transitive dependencies, walking the
manifests of every .app file in a BC artifact tree.

This replaces the hand-curated TEST_FRAMEWORK_APPS array that
entrypoint.sh used to maintain. Instead of someone manually adding
"oh and System App Test Library" the first time a publish fails with
AL1024, the entrypoint just asks "what does Tests-TestLibraries
transitively need?" and the resolver walks the graph.

Seeds are specified as "Publisher/Name" strings (publisher and name as
they appear in NavxManifest.xml — case-insensitive match). The resolver
finds each seed by name in the artifact index and walks every dependency
declared in its manifest, recursively, until it has the full closure.
The result is emitted as one absolute path per line in topological
order: every app's dependencies appear before it.

Usage:
  resolve-install-order.py \\
      --artifact-dir /bc/artifacts \\
      --seed "Microsoft/Library Assert" \\
      --seed "Microsoft/Tests-TestLibraries" \\
      --seed "Microsoft/Library-NoTransactions" \\
      --seed "Microsoft/Test Runner"

Output (stdout):
  /bc/artifacts/platform/.../Microsoft_Any_27.5.46862.48612.app
  /bc/artifacts/platform/.../Microsoft_Library Assert_27.5.46862.48612.app
  /bc/artifacts/platform/.../Microsoft_Library Variable Storage_27.5.46862.48612.app
  ...
  /bc/artifacts/platform/.../Microsoft_Tests-TestLibraries.app

Diagnostics go to stderr. Empty stdout + non-zero exit if a seed
can't be found in the artifact tree (the entrypoint can then either
fail loudly or skip gracefully).
"""

from __future__ import annotations

import argparse
import os
import sys

# Make sibling helpers importable
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _bcapp import load_artifact_apps  # noqa: E402


def parse_seed(s: str) -> tuple[str, str]:
    """Parse 'Publisher/Name' into (publisher_lc, name_lc)."""
    if "/" not in s:
        raise ValueError(f"seed must be 'Publisher/Name', got: {s}")
    publisher, name = s.split("/", 1)
    return publisher.strip().lower(), name.strip().lower()


def index_by_publisher_name(apps: dict) -> dict:
    """Return dict[(publisher_lc, name_lc) -> id] over the loaded apps."""
    out: dict = {}
    for app_id, info in apps.items():
        key = (info.get("publisher", "").lower(), info.get("name", "").lower())
        # First wins; load_artifact_apps already keeps the highest version per id.
        out.setdefault(key, app_id)
    return out


def topo_sort(seed_ids: list[str], apps: dict) -> list[str]:
    """Post-order DFS over the dependency graph.

    Returns the list of resolved app ids in dependency order: every
    app's transitive deps appear before it. Apps that aren't present
    in the artifact tree are skipped silently (they're typically
    pre-installed in BC's database — System Application, Business
    Foundation, Base Application, etc. — and don't need republishing).
    """
    visited: set[str] = set()
    result: list[str] = []

    def visit(app_id: str) -> None:
        if app_id in visited:
            return
        visited.add(app_id)
        info = apps.get(app_id)
        if info is None:
            # Not in the artifact tree — assume pre-installed by BC.
            return
        for dep in info.get("dependencies", []):
            visit(dep["id"])
        result.append(app_id)

    for sid in seed_ids:
        visit(sid)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--artifact-dir", required=True,
                        help="BC artifact root (e.g. /bc/artifacts)")
    parser.add_argument("--seed", action="append", default=[], required=True,
                        help='Seed app as "Publisher/Name" (repeatable)')
    parser.add_argument("--missing-seed-is-error", action="store_true",
                        help="Exit non-zero if any seed can't be located in the artifact")
    args = parser.parse_args()

    if not os.path.isdir(args.artifact_dir):
        print(f"ERROR: artifact dir does not exist: {args.artifact_dir}", file=sys.stderr)
        return 1

    print(f"[resolve-install-order] Indexing .app files under {args.artifact_dir}...",
          file=sys.stderr)
    apps = load_artifact_apps(args.artifact_dir)
    print(f"[resolve-install-order] Indexed {len(apps)} unique apps", file=sys.stderr)

    by_pub_name = index_by_publisher_name(apps)

    seed_ids: list[str] = []
    missing_seeds: list[str] = []
    for raw_seed in args.seed:
        try:
            key = parse_seed(raw_seed)
        except ValueError as e:
            print(f"ERROR: {e}", file=sys.stderr)
            return 1
        app_id = by_pub_name.get(key)
        if app_id is None:
            missing_seeds.append(raw_seed)
            print(f"WARN: seed not found in artifact: {raw_seed}", file=sys.stderr)
            continue
        seed_ids.append(app_id)
        info = apps[app_id]
        print(f"[resolve-install-order] seed: {raw_seed} -> {info.get('name')} "
              f"v{info.get('version')} ({app_id})", file=sys.stderr)

    if missing_seeds and args.missing_seed_is_error:
        print(f"ERROR: {len(missing_seeds)} seed(s) missing from artifact",
              file=sys.stderr)
        return 1

    if not seed_ids:
        print("ERROR: no seeds resolved — nothing to install", file=sys.stderr)
        return 1

    ordered = topo_sort(seed_ids, apps)
    print(f"[resolve-install-order] Transitive closure: {len(ordered)} apps "
          f"(from {len(seed_ids)} seeds)", file=sys.stderr)

    for app_id in ordered:
        info = apps[app_id]
        path = info["path"]
        # Stderr: human-readable name + version, for the entrypoint log
        print(f"  {info.get('name')} v{info.get('version')}", file=sys.stderr)
        # Stdout: path only, one per line, for the shell consumer
        print(path)

    return 0


if __name__ == "__main__":
    sys.exit(main())
