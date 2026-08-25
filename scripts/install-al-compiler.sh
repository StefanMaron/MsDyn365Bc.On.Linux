#!/usr/bin/env bash
# install-al-compiler.sh — Resolve and install the Linux AL compiler for a
# given BC version, then print the two facts a caller needs as KEY=VALUE
# lines on stdout:
#
#   AL_TOOL_DIR=<dir containing alc (or alc.dll) and the Microsoft cop DLLs>
#   AL_BIN_DIR=<dir to prepend to PATH; contains an `AL` wrapper script>
#
# Usage:
#   bash scripts/install-al-compiler.sh <bc_version> [al_tool_version_or_policy]
#
# al_tool_version_or_policy is either an exact AL nupkg version, or one of
# the policy keywords resolve-al-tool-version.py understands (matching /
# latest / prerelease); empty defaults to "matching".
#
# All human-readable progress goes to stderr so a caller can safely do:
#   eval "$(bash scripts/install-al-compiler.sh "$BC_VERSION" "$AL_TOOL")"
#
# Why this exists as a shared script rather than inline per-workflow bash:
# this exact logic used to be duplicated three times (the reusable GitHub
# workflow, the GitHub Actions example, the Azure Pipelines example) and
# scripts/check-example-drift.py exists specifically because that kind of
# duplication rots — a fix landing in one copy and not the other two.
#
# Why the extra fallback stage exists: Microsoft has, without warning,
# emptied the `Microsoft.Dynamics.BusinessCentral.Development.Tools.Linux`
# nupkg of its `alc` binary for AL 18 betas newer than 18.0.39.10160-beta —
# the package now ships only the four analyzer cop DLLs and nothing else.
# Confirmed by downloading both 18.0.39.10160-beta (has lib/net10.0/alc,
# self-contained) and 18.0.40.43394-beta (lib/net10.0 and lib/net8.0 hold
# only *Cop.dll files) from nuget.org directly. The actual alc/aldoc/altool/
# almcp binaries moved to the OS-agnostic base package
# `Microsoft.Dynamics.BusinessCentral.Development.Tools` (no `.Linux`
# suffix), under tools/net{8,10}.0/any/ — and there they are
# framework-dependent (no bundled runtime), not self-contained, unlike
# every previous package shape this script has had to handle.
#
# This is an active preview build reshuffling itself, so BC 29 is the only
# thing affected today (a non-blocking preview leg — see test-versions.yml).
# The point of handling it now is that when Microsoft ships this same
# restructuring in a STABLE release, the fallback below is already in
# place and nothing here needs to change.
set -uo pipefail

BC_VERSION="${1:?usage: install-al-compiler.sh <bc_version> [al_tool_version_or_policy]}"
AL_TOOL="${2:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

case "$AL_TOOL" in
  ""|matching|latest|prerelease)
    AL_POLICY="${AL_TOOL:-matching}"
    AL_TOOL=""
    ;;
  *)
    AL_POLICY=""
    ;;
esac

if [ -z "$AL_TOOL" ]; then
  if ! AL_TOOL=$(python3 "$SCRIPT_DIR/resolve-al-tool-version.py" "$BC_VERSION" --policy "$AL_POLICY"); then
    echo "::error::could not resolve an AL compiler version for BC $BC_VERSION" >&2
    exit 1
  fi
fi
echo "Installing AL compiler $AL_TOOL" >&2

LINUX_PKG="microsoft.dynamics.businesscentral.development.tools.linux"
BASE_PKG="microsoft.dynamics.businesscentral.development.tools"

AL_BIN_DIR="$HOME/.al-bin"
mkdir -p "$AL_BIN_DIR"

# Every install path below converges on one of these two wrapper shapes,
# so downstream steps (`AL compile ...`) never need to know whether alc
# came from a dotnet global tool cache, a directly-extracted .Linux nupkg,
# or the base-package fallback. The v16 dotnet tool ships AL.dll which
# routes the `compile` subcommand; raw alc doesn't accept it — both
# wrappers strip it.
write_native_wrapper() {
  # $1 = dir containing a self-contained, executable `alc`
  printf '#!/bin/bash\n[ "$1" = compile ] && shift\nexec "%s/alc" "$@"\n' "$1" > "$AL_BIN_DIR/AL"
  chmod +x "$AL_BIN_DIR/AL"
}

write_dotnet_wrapper() {
  # $1 = full path to a framework-dependent alc.dll
  printf '#!/bin/bash\n[ "$1" = compile ] && shift\nexec dotnet "%s" "$@"\n' "$1" > "$AL_BIN_DIR/AL"
  chmod +x "$AL_BIN_DIR/AL"
}

# Pick the alc.dll build whose target framework matches an installed
# runtime, rather than assuming a TFM. Needed because the base-package
# fallback ships both net8.0 and net10.0 builds side by side and CI only
# sets up .NET 8 — sorting by name is not a reliable way to prefer a
# framework that is actually installed (net10.0 sorts before net8.0
# lexicographically, the opposite of the version order).
select_dotnet_alc() {
  # $1 = root dir to search
  local candidates candidate tfm major fallback=""
  candidates=$(find "$1" -type f -name alc.dll 2>/dev/null | sort)
  [ -z "$candidates" ] && return 1
  while IFS= read -r candidate; do
    tfm=$(basename "$(dirname "$(dirname "$candidate")")")
    major=${tfm#net}
    major=${major%%.*}
    if dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft\.NETCore\.App ${major}\."; then
      echo "$candidate"
      return 0
    fi
    fallback="$candidate"
  done <<< "$candidates"
  # No installed runtime matched any candidate's TFM — return the last one
  # found so the caller at least gets a clear "dotnet: framework not
  # found" error instead of "no alc binary found anywhere", and note it.
  echo "WARN: found alc.dll but no installed .NET runtime matches its TFM; trying $fallback anyway" >&2
  echo "$fallback"
}

AL_TOOL_DIR=""

# --- Attempt 1: dotnet global tool (BC 27 / v16 packages ship with
# DotnetToolSettings.xml and install correctly this way). BC 28+ dropped
# the tool manifest — the package is now a plain library nupkg and
# `dotnet tool install` rejects it; fall through to extraction below.
if dotnet tool install -g \
    Microsoft.Dynamics.BusinessCentral.Development.Tools.Linux \
    --version "$AL_TOOL" >&2 \
|| dotnet tool update -g \
    Microsoft.Dynamics.BusinessCentral.Development.Tools.Linux \
    --version "$AL_TOOL" >&2; then
  echo "Installed as dotnet global tool" >&2
  ALC_PATH=$(find "$HOME/.dotnet/tools/.store/$LINUX_PKG" -type f -name alc 2>/dev/null | sort | tail -1)
  if [ -n "$ALC_PATH" ]; then
    AL_TOOL_DIR=$(dirname "$ALC_PATH")
    write_native_wrapper "$AL_TOOL_DIR"
  fi
fi

# --- Attempt 2: extract the .Linux nupkg directly and look for a
# self-contained alc, wherever the package puts it. The TFM directory is
# NOT stable across AL majors (v17 lib/net8.0, v18 lib/net10.0, plus a
# lib/net8.0 in v18 that holds only analyzer DLLs) — search by filename,
# never assume a path.
if [ -z "$AL_TOOL_DIR" ]; then
  echo "Looking for alc in the $LINUX_PKG nupkg" >&2
  EXTRACT_DIR="$HOME/.al-tool-cache/$AL_TOOL"
  mkdir -p "$EXTRACT_DIR"
  if curl -fsSL \
      "https://api.nuget.org/v3-flatcontainer/${LINUX_PKG}/${AL_TOOL}/${LINUX_PKG}.${AL_TOOL}.nupkg" \
      -o "$EXTRACT_DIR/pkg.zip"; then
    unzip -q -o "$EXTRACT_DIR/pkg.zip" -d "$EXTRACT_DIR"
    rm -f "$EXTRACT_DIR/pkg.zip"
    ALC_PATH=$(find "$EXTRACT_DIR" -type f -name alc 2>/dev/null | sort | tail -1)
    if [ -n "$ALC_PATH" ]; then
      AL_TOOL_DIR=$(dirname "$ALC_PATH")
      echo "Found alc at $ALC_PATH" >&2
      chmod +x "$ALC_PATH" 2>/dev/null || true
      write_native_wrapper "$AL_TOOL_DIR"
    fi
  fi
fi

# --- Attempt 3: the .Linux package has no compiler at all (see the header
# comment) — fall back to the OS-agnostic base package, which carries a
# framework-dependent alc.dll under tools/net{8,10}.0/any/ alongside the
# same cop DLLs resolve-analyzers.py looks for.
if [ -z "$AL_TOOL_DIR" ]; then
  echo "$LINUX_PKG has no alc binary for $AL_TOOL; trying the base package $BASE_PKG" >&2
  BASE_EXTRACT_DIR="$HOME/.al-tool-cache/${AL_TOOL}-base"
  mkdir -p "$BASE_EXTRACT_DIR"
  if curl -fsSL \
      "https://api.nuget.org/v3-flatcontainer/${BASE_PKG}/${AL_TOOL}/${BASE_PKG}.${AL_TOOL}.nupkg" \
      -o "$BASE_EXTRACT_DIR/pkg.zip"; then
    unzip -q -o "$BASE_EXTRACT_DIR/pkg.zip" -d "$BASE_EXTRACT_DIR"
    rm -f "$BASE_EXTRACT_DIR/pkg.zip"
    ALC_DLL=$(select_dotnet_alc "$BASE_EXTRACT_DIR")
    if [ -n "$ALC_DLL" ]; then
      AL_TOOL_DIR=$(dirname "$ALC_DLL")
      echo "Found framework-dependent alc.dll at $ALC_DLL" >&2
      write_dotnet_wrapper "$ALC_DLL"
    fi
  fi
fi

if [ -z "$AL_TOOL_DIR" ]; then
  echo "::error::no alc binary found in either $LINUX_PKG or $BASE_PKG for AL $AL_TOOL." >&2
  echo "$LINUX_PKG contents:" >&2
  find "$HOME/.al-tool-cache/$AL_TOOL" -maxdepth 3 -type d 2>/dev/null | sed 's/^/  /' >&2
  echo "$BASE_PKG contents:" >&2
  find "$HOME/.al-tool-cache/${AL_TOOL}-base" -maxdepth 3 -type d 2>/dev/null | sed 's/^/  /' >&2
  exit 1
fi

echo "AL_TOOL_DIR=$AL_TOOL_DIR"
echo "AL_BIN_DIR=$AL_BIN_DIR"
