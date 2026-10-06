#!/usr/bin/env python3
"""Discover the BC version matrix for .github/workflows/test-versions.yml.

Two groups come out of this, from two different Microsoft artifact indexes:

  released  — the public sandbox index. These legs are REQUIRED: a failure
              here is a real regression in bc-linux. Emitted as SHORT
              versions ("27.5", "28.3") because scripts/download-artifacts.sh
              resolves a short version to the newest build itself, so the leg
              stays correct even when Microsoft ships a hotfix mid-run.

  preview   — the insider sandbox index, which carries the not-yet-released
              next major and next minor. These legs are NON-BLOCKING. They
              are emitted as FULL versions plus a full artifact URL, because
              nothing in the normal download path knows about the insider
              host, and because insider builds move daily — pinning the exact
              build is what makes a preview failure reproducible.

Insider artifacts need no SAS token; a plain anonymous GET works, same path
shape as the public index.

Scope control: --min-major N keeps every BC major from N upward that is
present in the public index (default 27). The set grows by itself when
Microsoft releases a new major, and the floor only moves when someone decides
an old major is no longer supported. The index also carries majors back to 24
and lower; a floor keeps those out, since each major is about six BC boots.
See CLAUDE.md's CI wall-clock section for why that matters.

Usage:
    discover-bc-versions.py --mode released [--min-major 27]
    discover-bc-versions.py --mode preview  [--min-major 27]
    discover-bc-versions.py --mode both --format text   # human-readable

Output (default --format json) is a GitHub Actions matrix object, ready for
`fromJson`:

    released: {"bc_version": ["27.0", ..., "28.3"]}
    preview:  {"include": [{"bc_version": "29.0.53450.0",
                            "label": "29.0 (next major)",
                            "artifact_url": "https://bcinsider-.../sandbox/29.0.53450.0/w1"}, ...]}
"""

import argparse
import json
import sys
import urllib.request

PUBLIC_INDEX = (
    "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/{type}/indexes/{country}.json"
)
INSIDER_INDEX = (
    "https://bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net/{type}/indexes/{country}.json"
)
INSIDER_BASE = "https://bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net"


def fetch_versions(url, retries=3):
    """Return every parseable version tuple from a Microsoft artifact index.

    The index is a plain JSON array of objects with a "Version" field. Any
    entry that isn't a 4-part numeric version is skipped rather than fatal —
    Microsoft has put odd rows in these files before.
    """
    last = None
    for attempt in range(1, retries + 1):
        try:
            with urllib.request.urlopen(url, timeout=60) as resp:
                data = json.load(resp)
            break
        except Exception as exc:  # network / JSON / HTTP
            last = exc
            if attempt == retries:
                raise SystemExit(f"ERROR: could not read {url}: {exc}")
    out = []
    for entry in data:
        raw = (entry or {}).get("Version", "")
        parts = raw.split(".")
        if len(parts) < 2:
            continue
        try:
            key = tuple(int(p) for p in parts)
        except ValueError:
            continue
        out.append((key, raw))
    if not out:
        raise SystemExit(f"ERROR: no usable versions in {url}")
    return out


def newest_per_major_minor(versions):
    """{(major, minor): "full.version"} keeping the highest build per pair."""
    best = {}
    for key, raw in versions:
        mm = key[:2]
        if mm not in best or key > best[mm][0]:
            best[mm] = (key, raw)
    return {mm: raw for mm, (key, raw) in best.items()}


def released_matrix(index_url, min_major):
    versions = fetch_versions(index_url)
    per_mm = newest_per_major_minor(versions)
    legs = sorted(mm for mm in per_mm if mm[0] >= min_major)
    return ["%d.%d" % mm for mm in legs], per_mm


def preview_matrix(insider_url, released_per_mm, country):
    """Next major + next minor of the newest released major, from insider.

    Deliberately NOT "every insider version we can see". The insider index
    also carries older majors' internal builds (27.11 today, well past the
    public 27.5); adding one preview leg per supported major would grow the
    matrix by as many legs as there are majors, on every push, for versions
    nobody is shipping against yet. Two legs answer the two questions that
    matter: "does the next release still boot" and "does the next hotfix
    train still boot".
    """
    insider = fetch_versions(insider_url)
    per_mm = newest_per_major_minor(insider)
    if not released_per_mm:
        return []
    top_major = max(mm[0] for mm in released_per_mm)
    top_released_mm = max(mm for mm in released_per_mm if mm[0] == top_major)

    legs = []

    # Next major: the newest insider build of top_major + 1, when it exists.
    next_major_mms = [mm for mm in per_mm if mm[0] == top_major + 1]
    if next_major_mms:
        mm = max(next_major_mms)
        legs.append((mm, per_mm[mm], "next major"))

    # Next minor: the newest insider build of the current major, but only
    # when it is genuinely ahead of what the public index already serves —
    # otherwise it is the same code as a required leg and just burns CI.
    same_major_mms = [mm for mm in per_mm if mm[0] == top_major]
    if same_major_mms:
        mm = max(same_major_mms)
        if mm > top_released_mm:
            legs.append((mm, per_mm[mm], "next minor"))

    return [
        {
            "bc_version": full,
            "label": "%d.%d (%s)" % (mm[0], mm[1], why),
            "artifact_url": "%s/sandbox/%s/%s" % (INSIDER_BASE, full, country),
        }
        for mm, full, why in legs
    ]


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--mode", choices=["released", "preview", "both"],
                    default="released")
    ap.add_argument("--min-major", type=int, default=27,
                    help="Lowest BC major the required matrix covers "
                         "(default 27). Every major from here up in the "
                         "public index is included.")
    ap.add_argument("--country", default="w1")
    ap.add_argument("--type", default="sandbox")
    ap.add_argument("--format", choices=["json", "text"], default="json")
    args = ap.parse_args()

    pub_url = PUBLIC_INDEX.format(type=args.type, country=args.country)
    ins_url = INSIDER_INDEX.format(type=args.type, country=args.country)

    legs, per_mm = released_matrix(pub_url, args.min_major)
    if not legs:
        raise SystemExit("ERROR: released discovery produced no versions")

    preview = []
    if args.mode in ("preview", "both"):
        preview = preview_matrix(ins_url, per_mm, args.country)

    if args.format == "text":
        print("released (REQUIRED, %d legs):" % len(legs))
        for v in legs:
            print("  BC %-6s -> resolves to %s" % (v, per_mm[tuple(int(x) for x in v.split('.'))]))
        print("preview (NON-BLOCKING, %d legs):" % len(preview))
        for p in preview:
            print("  BC %-22s %s" % (p["label"], p["artifact_url"]))
        return

    if args.mode == "released":
        print(json.dumps({"bc_version": legs}))
    elif args.mode == "preview":
        print(json.dumps({"include": preview}))
    else:
        print(json.dumps({"released": {"bc_version": legs},
                          "preview": {"include": preview}}))


if __name__ == "__main__":
    sys.exit(main())
