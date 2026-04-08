#!/usr/bin/env python3
"""
resolve-analyzers.py — Resolve analyzer + ruleset configuration into AL
compile flag fragments.

Inputs (all optional; defaults produce zero output → no analyzers):
  --enable-code-cop                       Microsoft CodeCop
  --enable-ui-cop                         Microsoft UICop
  --enable-app-source-cop                 Microsoft AppSourceCop
  --enable-per-tenant-extension-cop       Microsoft PerTenantExtensionCop
  --custom-code-cops <list>               Comma- or newline-separated list of
                                          local paths and/or https:// URLs.
                                          .nupkg URLs are extracted (every
                                          DLL in lib/net8.0/ added). Bare
                                          .dll URLs are downloaded as-is.
  --ruleset-file <path|url>               Local path or https:// URL.
  --enable-external-rulesets              Pass /enableexternalrulesets so
                                          the ruleset can include remote
                                          rulesets via includedRuleSets[].path.
  --temp-dir <path>                       Where downloads + nupkg extracts go.
                                          Default: $RUNNER_TEMP or /tmp.
  --al-tool-dir <path>                    Override the directory containing
                                          Microsoft.Dynamics.Nav.*.dll cops.
                                          Default: auto-detect under
                                          ~/.dotnet/tools/.store/...
  --project-root <path>                   Prepended to all NON-URL entries in
                                          --custom-code-cops and --ruleset-file
                                          before path validation. Use this when
                                          the consumer's repo is checked out
                                          under a subdirectory (e.g. project/)
                                          but their input paths are written
                                          relative to their repo root. URLs
                                          pass through untouched. Empty by
                                          default — paths used as-is.

Output (one flag per line, suitable for `mapfile -t` in bash):
  /analyzer:<comma-separated DLL paths>
  /ruleset:<resolved-path>
  /enableexternalrulesets

A flag line is omitted entirely when its inputs are empty. Zero output
means "no analyzer flags" — the caller can blindly splat the array into
the AL compile command.

Logic:
  - Microsoft cop DLLs are resolved from the installed AL compiler tool's
    tools/net8.0/any/ directory.
  - Custom cops: each entry classified as URL or local path. URLs go to
    the temp dir; .nupkg URLs are unzipped and every DLL under
    lib/net8.0/ (preferred) or lib/netstandard2.1/ (fallback) is added —
    this is what makes ALCops "just work" (its 7 cop DLLs + the
    ALCops.Common.dll runtime helper all come along automatically).
  - Ruleset URLs: same download dance but no extraction.
"""

import argparse
import os
import shutil
import sys
import urllib.request
import zipfile
from glob import glob
from pathlib import Path

# ── Microsoft cop DLL filenames ──────────────────────────────────────────
# These ship inside the Linux AL compiler tool at
# tools/net8.0/any/. Verified empirically against
# Microsoft.Dynamics.BusinessCentral.Development.Tools.Linux 16.2.28.57946.
MS_COPS = {
    "code_cop": "Microsoft.Dynamics.Nav.CodeCop.dll",
    "ui_cop": "Microsoft.Dynamics.Nav.UICop.dll",
    "app_source_cop": "Microsoft.Dynamics.Nav.AppSourceCop.dll",
    "per_tenant_extension_cop": "Microsoft.Dynamics.Nav.PerTenantExtensionCop.dll",
}


def auto_detect_al_tool_dir() -> str | None:
    """Find tools/net8.0/any/ under the user's installed dotnet tool store.

    The tool installs to:
      ~/.dotnet/tools/.store/microsoft.dynamics.businesscentral.development.tools.linux/<ver>/
        microsoft.dynamics.businesscentral.development.tools.linux/<ver>/tools/net8.0/any/

    Picks the highest version when multiple are installed.
    """
    home = os.path.expanduser("~")
    pattern = os.path.join(
        home, ".dotnet", "tools", ".store",
        "microsoft.dynamics.businesscentral.development.tools.linux",
        "*", "microsoft.dynamics.businesscentral.development.tools.linux",
        "*", "tools", "net8.0", "any",
    )
    matches = sorted(glob(pattern))
    return matches[-1] if matches else None


def resolve_ms_cops(al_tool_dir: str, enabled: dict[str, bool]) -> list[str]:
    """Return absolute paths for the Microsoft cops the caller enabled."""
    out = []
    for key, dll in MS_COPS.items():
        if not enabled.get(key):
            continue
        path = os.path.join(al_tool_dir, dll)
        if not os.path.isfile(path):
            print(f"WARN: requested {key} but {path} does not exist — skipping",
                  file=sys.stderr)
            continue
        out.append(path)
    return out


def is_url(s: str) -> bool:
    return s.startswith("http://") or s.startswith("https://")


def apply_project_root(value: str, project_root: str) -> str:
    """Prepend project_root to value when value is a non-URL local path.

    URLs and absolute paths pass through unchanged. Empty project_root is
    a no-op. This is what makes the helper usable from a workflow that
    checks the consumer repo into a subdirectory: the consumer writes
    paths relative to their repo root, and the workflow tells the helper
    where that root lives on disk.
    """
    if not project_root or not value:
        return value
    if is_url(value):
        return value
    if os.path.isabs(value):
        return value
    return os.path.join(project_root, value)


def download(url: str, dest_dir: str) -> str:
    """Fetch url into dest_dir, return the local file path.

    Filename is taken from the URL's last path segment. Existing files
    are overwritten so re-runs in a stable temp dir don't pick up stale
    bytes from a previous run.
    """
    Path(dest_dir).mkdir(parents=True, exist_ok=True)
    name = url.rsplit("/", 1)[-1] or "download.bin"
    dest = os.path.join(dest_dir, name)
    print(f"  ↓ {url} → {dest}", file=sys.stderr)
    try:
        with urllib.request.urlopen(url, timeout=60) as resp, open(dest, "wb") as out:
            shutil.copyfileobj(resp, out)
    except Exception as e:
        print(f"ERROR: download failed for {url}: {e}", file=sys.stderr)
        sys.exit(1)
    return dest


def extract_nupkg_dlls(nupkg_path: str, dest_dir: str) -> list[str]:
    """Extract every DLL under lib/net8.0/ (preferred) or lib/netstandard2.1/
    from a NuGet package. Returns absolute paths to the extracted files.

    This is the path that makes ALCops "just work": the consumer points
    customCodeCops at the .nupkg URL, we unzip it, every cop DLL plus
    ALCops.Common.dll lands on disk, and the caller passes them all as
    /analyzer: arguments.
    """
    extract_root = os.path.join(dest_dir, Path(nupkg_path).stem)
    Path(extract_root).mkdir(parents=True, exist_ok=True)
    out: list[str] = []
    try:
        with zipfile.ZipFile(nupkg_path) as z:
            names = z.namelist()
            preferred = [n for n in names if n.startswith("lib/net8.0/") and n.endswith(".dll")]
            fallback = [n for n in names if n.startswith("lib/netstandard2.1/") and n.endswith(".dll")]
            chosen = preferred or fallback
            if not chosen:
                print(f"WARN: {nupkg_path} contains no lib/net8.0/ or lib/netstandard2.1/ DLLs",
                      file=sys.stderr)
                return out
            for entry in chosen:
                # Flatten: lib/net8.0/Foo.dll → <extract_root>/Foo.dll
                dest = os.path.join(extract_root, os.path.basename(entry))
                with z.open(entry) as src, open(dest, "wb") as dst:
                    shutil.copyfileobj(src, dst)
                out.append(dest)
        print(f"  unpacked {len(out)} DLL(s) from {os.path.basename(nupkg_path)}",
              file=sys.stderr)
    except Exception as e:
        print(f"ERROR: failed to extract {nupkg_path}: {e}", file=sys.stderr)
        sys.exit(1)
    return out


def parse_list(raw: str) -> list[str]:
    """Split on commas AND newlines, strip empties."""
    if not raw:
        return []
    parts: list[str] = []
    for line in raw.splitlines():
        for p in line.split(","):
            p = p.strip()
            if p:
                parts.append(p)
    return parts


def resolve_custom_cops(entries: list[str], temp_dir: str) -> list[str]:
    """Turn a mixed list of local paths and https:// URLs into a list of
    absolute DLL paths. .nupkg URLs are extracted; bare .dll URLs are
    downloaded as-is; local paths are validated and used directly.
    """
    out: list[str] = []
    for entry in entries:
        if is_url(entry):
            local = download(entry, temp_dir)
            if local.lower().endswith(".nupkg"):
                out.extend(extract_nupkg_dlls(local, temp_dir))
            else:
                out.append(local)
        else:
            if not os.path.isfile(entry):
                print(f"ERROR: custom cop not found: {entry}", file=sys.stderr)
                sys.exit(1)
            out.append(os.path.abspath(entry))
    return out


def resolve_ruleset(value: str, temp_dir: str) -> str | None:
    if not value:
        return None
    if is_url(value):
        return download(value, temp_dir)
    if not os.path.isfile(value):
        print(f"ERROR: ruleset file not found: {value}", file=sys.stderr)
        sys.exit(1)
    return os.path.abspath(value)


def main():
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--enable-code-cop", action="store_true")
    parser.add_argument("--enable-ui-cop", action="store_true")
    parser.add_argument("--enable-app-source-cop", action="store_true")
    parser.add_argument("--enable-per-tenant-extension-cop", action="store_true")
    parser.add_argument("--custom-code-cops", default="",
                        help="Comma- or newline-separated paths/URLs")
    parser.add_argument("--ruleset-file", default="")
    parser.add_argument("--enable-external-rulesets", action="store_true")
    parser.add_argument("--temp-dir", default=os.environ.get("RUNNER_TEMP") or "/tmp")
    parser.add_argument("--al-tool-dir", default="",
                        help="Override Microsoft cop DLL search dir")
    parser.add_argument("--project-root", default="",
                        help="Prepend to non-URL entries in --custom-code-cops "
                             "and --ruleset-file (e.g. 'project' when the "
                             "consumer repo is checked out at ./project/)")
    args = parser.parse_args()

    enabled = {
        "code_cop": args.enable_code_cop,
        "ui_cop": args.enable_ui_cop,
        "app_source_cop": args.enable_app_source_cop,
        "per_tenant_extension_cop": args.enable_per_tenant_extension_cop,
    }
    any_ms_cop = any(enabled.values())

    al_tool_dir = args.al_tool_dir
    if any_ms_cop and not al_tool_dir:
        al_tool_dir = auto_detect_al_tool_dir() or ""
        if not al_tool_dir:
            print("ERROR: --al-tool-dir not given and could not auto-detect "
                  "the installed Microsoft.Dynamics.BusinessCentral.Development."
                  "Tools.Linux tool. Install it first or pass --al-tool-dir.",
                  file=sys.stderr)
            sys.exit(1)
        print(f"al tool dir: {al_tool_dir}", file=sys.stderr)

    analyzer_paths: list[str] = []
    if any_ms_cop:
        analyzer_paths.extend(resolve_ms_cops(al_tool_dir, enabled))

    custom_entries = parse_list(args.custom_code_cops)
    if custom_entries:
        # Apply --project-root to local entries so a workflow that checks
        # the consumer repo into ./project/ can pass paths relative to
        # the consumer's repo root.
        custom_entries = [apply_project_root(e, args.project_root) for e in custom_entries]
        # Stage all downloads under a single subdir so re-runs are tidy
        # and the workflow can clean up by removing one path.
        cop_temp = os.path.join(args.temp_dir, "bc-linux-cops")
        analyzer_paths.extend(resolve_custom_cops(custom_entries, cop_temp))

    ruleset_temp = os.path.join(args.temp_dir, "bc-linux-rulesets")
    ruleset_input = apply_project_root(args.ruleset_file, args.project_root)
    ruleset_path = resolve_ruleset(ruleset_input, ruleset_temp)

    print(f"resolved {len(analyzer_paths)} analyzer DLL(s)", file=sys.stderr)
    if ruleset_path:
        print(f"ruleset: {ruleset_path}", file=sys.stderr)
    if args.enable_external_rulesets:
        print("external rulesets: enabled", file=sys.stderr)

    # Output: one flag per line. Empty when nothing is configured.
    if analyzer_paths:
        print("/analyzer:" + ",".join(analyzer_paths))
    if ruleset_path:
        print(f"/ruleset:{ruleset_path}")
    if args.enable_external_rulesets:
        print("/enableexternalrulesets")


if __name__ == "__main__":
    main()
