#!/usr/bin/env python3
"""
sort-apps-for-publish.py — print .app files in an order BC can publish them.

    python3 scripts/sort-apps-for-publish.py extra-symbols/*.app

Publishing an app whose dependency is not installed yet fails with AL1024, so
third-party dependencies (extra_symbols_artifact_name) cannot be published in
glob order: "Vendor Connector" sorts before "Vendor Core", which it depends
on. Dependencies within the given set come first; dependencies outside
it (Microsoft apps already in the database) are ignored. Apps that do not
depend on each other keep the order they were given in, and files that cannot
be read as an app are printed last so the publish step reports them itself.
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _bcapp import read_app_info  # noqa: E402


def publish_order(paths: list[str]) -> list[str]:
    infos = {p: read_app_info(p) for p in paths}
    readable = [p for p in paths if infos[p] and infos[p].get("id")]
    by_id = {infos[p]["id"]: p for p in readable}
    deps = {p: [by_id[d["id"]] for d in infos[p]["dependencies"] if d["id"] in by_id]
            for p in readable}

    ordered: list[str] = []
    done: set[str] = set()
    while len(done) < len(readable):
        ready = [p for p in readable if p not in done and all(d in done for d in deps[p])]
        if not ready:
            cycle = sorted(infos[p]["name"] for p in readable if p not in done)
            raise ValueError("dependency cycle among: " + ", ".join(cycle))
        ordered.append(ready[0])
        done.add(ready[0])
    return ordered + [p for p in paths if p not in done]


def main(argv: list[str]) -> int:
    try:
        for path in publish_order(argv):
            print(path)
    except ValueError as exc:
        print(f"sort-apps-for-publish: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
