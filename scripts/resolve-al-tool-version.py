#!/usr/bin/env python3
"""Resolve the Linux AL compiler version (and AL runtime) for a BC version.

This replaces the hand-maintained lookup tables in the reusable workflows,
which had entries for BC 27 and BC 28 only. Anything else fell through to a
`::warning::` and the BC 27 compiler — which does not fail loudly, it just
produces confusing downstream errors. BC 29 hit exactly that.

The mapping is arithmetic, not a table:

    BC 29 and older:  AL major = BC major - 11
    BC 30 and newer:  AL major = BC major

    BC 27 -> AL 16   (16.2.28.57946)
    BC 28 -> AL 17   (17.0.34.45391)
    BC 29 -> AL 18   (18.0.39.10160-beta)
    BC 30 -> AL 30   (30.0.43.45776-beta)

Microsoft renumbered the AL tool to the BC major after 18: nuget.org has no
19.x, and the next package is 30.0.42.11883-beta. BC 30's own apps declare
Runtime="30.0" in their NavxManifest.xml, so the runtime moved with it.
Checked 2026-10-20 against the BC 30.0.56593.0 insider artifacts.

Prerelease handling is the point of this script. A BC major that is still in
preview has NO stable compiler published — as of 2026-08 the newest AL 18.x
on nuget.org is `18.0.39.10160-beta` and there is no 18.x stable at all. So:
prefer the newest stable of the target major, and fall back to the newest
prerelease when no stable exists, saying so on stderr. A preview BC version
is unbuildable without that fallback.

The AL runtime version follows the AL major: BC 29 -> runtime "18.0",
BC 30 -> runtime "30.0".

Policies
--------
Which compiler to pick is a choice, not a fact, so it is selectable:

    matching    (default) newest stable of the AL major that matches the BC
                version, falling back to that major's newest prerelease when
                no stable exists. This is what Microsoft ships for that BC
                version.
    latest      newest stable across ALL majors, regardless of BC version.
                A newer compiler accepts older runtimes, so this is a valid
                choice and is what some AL-Go setups do. It is the answer when
                an app declares a runtime its own major's compiler rejects —
                e.g. runtime 16.1 is refused by AL 16.2.28.57946 (which accepts
                16.0 only) but accepted by AL 17.0.34.45391.
    prerelease  newest version across all majors including prereleases.

Usage:
    resolve-al-tool-version.py 29.0                       # -> 18.0.39.10160-beta
    resolve-al-tool-version.py 27.5 --policy latest       # -> 17.0.34.45391
    resolve-al-tool-version.py 27.5 --policy prerelease
    resolve-al-tool-version.py 29.0 --runtime             # -> 18.0
"""
import argparse
import json
import sys
import urllib.error
import urllib.request

PACKAGE = "microsoft.dynamics.businesscentral.development.tools.linux"
INDEX = f"https://api.nuget.org/v3-flatcontainer/{PACKAGE}/index.json"

# BC major - AL major up to BC 29. Verified against the two versions the
# workflows previously hardcoded (BC 27 -> 16.x, BC 28 -> 17.x).
BC_TO_AL_OFFSET = 11

# The first BC major whose AL tool carries the BC major as its own major.
FIRST_ALIGNED_BC_MAJOR = 30


def al_major_for(bc_major):
    if bc_major >= FIRST_ALIGNED_BC_MAJOR:
        return bc_major
    return bc_major - BC_TO_AL_OFFSET


def version_key(v):
    """Sort key: numeric parts first, stable ahead of prerelease at equal numbers."""
    core, _, pre = v.partition("-")
    parts = []
    for p in core.split("."):
        parts.append(int(p) if p.isdigit() else 0)
    while len(parts) < 4:
        parts.append(0)
    # A stable release outranks any prerelease of the same numeric version.
    return (tuple(parts), 1 if not pre else 0, pre)


def fetch_versions(timeout):
    with urllib.request.urlopen(INDEX, timeout=timeout) as r:
        return json.load(r)["versions"]


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bc_version", help="BC version, e.g. 29.0 or 28.3.52162.53447")
    ap.add_argument("--runtime", action="store_true",
                    help="print the AL runtime version (e.g. 18.0) instead of the tool version")
    ap.add_argument("--policy", choices=("matching", "latest", "prerelease"),
                    default="matching",
                    help="which compiler to select; see the module docstring")
    ap.add_argument("--stable-only", action="store_true",
                    help="fail instead of falling back to a prerelease")
    ap.add_argument("--timeout", type=float, default=30)
    args = ap.parse_args()

    try:
        bc_major = int(str(args.bc_version).split(".")[0])
    except ValueError:
        print(f"ERROR: could not read a major version from '{args.bc_version}'", file=sys.stderr)
        return 2

    al_major = al_major_for(bc_major)
    if al_major < 1:
        print(f"ERROR: BC major {bc_major} maps to AL major {al_major}, which is not valid",
              file=sys.stderr)
        return 2

    # The runtime version needs no network call.
    if args.runtime:
        print(f"{al_major}.0")
        return 0

    try:
        versions = fetch_versions(args.timeout)
    except (urllib.error.URLError, OSError, ValueError) as e:
        print(f"ERROR: could not read {INDEX}: {e}", file=sys.stderr)
        return 1

    if args.policy in ("latest", "prerelease"):
        # Not scoped to a major: a newer compiler accepts older runtimes, and
        # the caller has asked for the newest one rather than the one matched
        # to their BC version.
        pool = versions if args.policy == "prerelease" else [v for v in versions if "-" not in v]
        if not pool:
            print(f"ERROR: no versions available under policy '{args.policy}'", file=sys.stderr)
            return 1
        print(sorted(pool, key=version_key)[-1])
        return 0

    same_major = [v for v in versions if v.split(".")[0] == str(al_major)]
    if not same_major:
        print(f"ERROR: no AL {al_major}.x compiler published for BC {bc_major}. "
              f"Majors available: {sorted({int(v.split('.')[0]) for v in versions})}",
              file=sys.stderr)
        return 1

    stable = sorted([v for v in same_major if "-" not in v], key=version_key)
    prerelease = sorted([v for v in same_major if "-" in v], key=version_key)

    if stable:
        print(stable[-1])
        return 0

    if args.stable_only:
        print(f"ERROR: no stable AL {al_major}.x compiler exists for BC {bc_major} "
              f"(newest prerelease is {prerelease[-1]}), and --stable-only was given.",
              file=sys.stderr)
        return 1

    print(f"NOTE: no stable AL {al_major}.x compiler exists yet for BC {bc_major} — "
          f"using prerelease {prerelease[-1]}. This is expected for a BC version "
          f"that is still in preview.", file=sys.stderr)
    print(prerelease[-1])
    return 0


if __name__ == "__main__":
    sys.exit(main())
