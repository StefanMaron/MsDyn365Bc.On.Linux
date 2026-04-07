"""
_bcapp.py — Shared helpers for reading Business Central .app package files.

A BC .app file is a zip containing one of:
  - NavxManifest.xml at root (regular AL package)
  - readytorunappmanifest.json + nested .app (R2R package)
  - app.json (rare fallback)

This module exposes a single uniform reader (`read_app_info`) plus a
walker that indexes every .app under a directory tree, keeping the
highest-versioned copy when the same app id appears more than once.

Used by:
  - stage-symbols.py    (manifest-driven .alpackages staging)
  - resolve-keep-app-ids.py historically inlined a copy of these helpers
    — kept there for now to avoid risk of regression; new code should
    import from here.
"""

from __future__ import annotations

import io
import json
import os
import re
import sys
import zipfile


# ── Manifest parsing primitives ───────────────────────────────────────────

def xml_attr(xml: str, attr: str) -> str | None:
    """Extract an attribute value from an XML fragment, case-insensitive."""
    m = re.search(rf'{attr}\s*=\s*"([^"]*)"', xml, re.IGNORECASE)
    return m.group(1) if m else None


def _parse_navx_manifest(xml: str) -> dict:
    """Parse a NavxManifest.xml string into id/name/publisher/version + deps."""
    info: dict = {
        "id": (xml_attr(xml, "Id") or xml_attr(xml, "AppId") or "").lower(),
        "name": xml_attr(xml, "Name") or "",
        "publisher": xml_attr(xml, "Publisher") or "",
        "version": xml_attr(xml, "Version") or "0.0.0.0",
        "dependencies": [],
    }
    for m in re.finditer(
        r"<Dependency[^>]*?/>|<Dependency[^>]*?>.*?</Dependency>",
        xml, re.DOTALL,
    ):
        dep_xml = m.group(0)
        dep_id = (xml_attr(dep_xml, "AppId") or xml_attr(dep_xml, "Id") or "").lower()
        if dep_id:
            info["dependencies"].append({
                "id": dep_id,
                "name": xml_attr(dep_xml, "Name") or "",
                "publisher": xml_attr(dep_xml, "Publisher") or "",
                "version": xml_attr(dep_xml, "MinVersion")
                           or xml_attr(dep_xml, "Version")
                           or "0.0.0.0",
            })
    return info


def _read_inner_navx(data: bytes) -> dict | None:
    """Read NavxManifest.xml out of a nested .app blob (R2R inner app)."""
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as inner_z:
            if "NavxManifest.xml" in inner_z.namelist():
                xml = inner_z.read("NavxManifest.xml").decode("utf-8", errors="replace")
                return _parse_navx_manifest(xml)
    except Exception:
        pass
    return None


def read_app_info(app_path: str) -> dict | None:
    """Read app metadata from a .app file. Returns None on failure.

    The returned dict has: id, name, publisher, version, path, dependencies
    (list of {id, name, publisher, version}). Versions are kept as strings;
    use `version_tuple()` to compare.
    """
    try:
        with zipfile.ZipFile(app_path) as z:
            names = z.namelist()

            # R2R package: readytorunappmanifest.json points at a nested .app
            if "readytorunappmanifest.json" in names:
                manifest = json.loads(z.read("readytorunappmanifest.json"))
                inner = None
                nested = manifest.get("EmbeddedAppFileName", "")
                if nested and nested in names:
                    inner = _read_inner_navx(z.read(nested))
                if inner is None:
                    # Fall back to whatever the R2R manifest itself tells us.
                    inner = {
                        "id": (manifest.get("EmbeddedAppId") or "").lower(),
                        "name": manifest.get("Name", ""),
                        "publisher": manifest.get("Publisher", ""),
                        "version": manifest.get("Version", "0.0.0.0"),
                        "dependencies": [
                            {
                                "id": (d.get("AppId") or d.get("Id") or "").lower(),
                                "name": d.get("Name", ""),
                                "publisher": d.get("Publisher", ""),
                                "version": d.get("MinVersion") or d.get("Version") or "0.0.0.0",
                            }
                            for d in manifest.get("Dependencies", [])
                            if (d.get("AppId") or d.get("Id"))
                        ],
                    }
                inner["path"] = app_path
                return inner

            # Regular AL package
            if "NavxManifest.xml" in names:
                xml = z.read("NavxManifest.xml").decode("utf-8", errors="replace")
                info = _parse_navx_manifest(xml)
                info["path"] = app_path
                return info

            # Rare fallback: source .app with app.json instead of NavxManifest
            if "app.json" in names:
                data = json.loads(z.read("app.json").decode("utf-8-sig"))
                return {
                    "id": (data.get("id") or "").lower(),
                    "name": data.get("name", ""),
                    "publisher": data.get("publisher", ""),
                    "version": data.get("version", "0.0.0.0"),
                    "dependencies": [
                        {
                            "id": (d.get("id") or d.get("appId") or "").lower(),
                            "name": d.get("name", ""),
                            "publisher": d.get("publisher", ""),
                            "version": d.get("version", "0.0.0.0"),
                        }
                        for d in data.get("dependencies", [])
                        if (d.get("id") or d.get("appId"))
                    ],
                    "path": app_path,
                }
    except Exception as e:
        print(f"WARN: cannot read {app_path}: {e}", file=sys.stderr)
    return None


# ── Indexing ──────────────────────────────────────────────────────────────

def version_tuple(v: str) -> tuple:
    """Parse a dotted version string into a tuple of ints, padded to 4."""
    try:
        parts = [int(p) for p in v.split(".")]
    except ValueError:
        parts = [0]
    while len(parts) < 4:
        parts.append(0)
    return tuple(parts[:4])


def load_artifact_apps(artifact_dir: str) -> dict:
    """Walk an artifact tree and index every .app by its app id.

    When the same id appears multiple times (different paths or versions),
    keep the highest version. Returns dict: id -> info dict.
    """
    apps: dict = {}
    for root, _, files in os.walk(artifact_dir):
        for f in files:
            if not f.endswith(".app"):
                continue
            info = read_app_info(os.path.join(root, f))
            if not info or not info.get("id"):
                continue
            existing = apps.get(info["id"])
            if existing is None or version_tuple(info["version"]) > version_tuple(existing["version"]):
                apps[info["id"]] = info
    return apps
