#!/bin/bash
# Download BC artifacts (platform + country) to a target directory.
# Supports both public and insider artifact URLs.
#
# Performance design:
#   - App and platform zips are downloaded IN PARALLEL to a fast temp dir
#     (host tmpfs / runner /tmp) rather than directly to the destination
#     volume.  This avoids writing the raw zip into the (slower) Docker
#     named volume and cuts the effective I/O to the volume by ~50%.
#   - Timing is logged for each phase so you can see exactly where time
#     goes: version resolution, download, and extraction.
#
# Usage:
#   With full URL:  download-artifacts.sh <url> <dest>
#   With parts:     download-artifacts.sh <type> <version> <country> <dest>
set -e

_ms() { date +%s%3N; }

# Parse arguments: either (url, dest) or (type, version, country, dest)
if [ $# -eq 2 ]; then
    APP_URL="$1"
    DEST="$2"
    # Derive platform URL: replace country segment with "platform"
    PLATFORM_URL=$(echo "$APP_URL" | sed 's|/[^/]*$|/platform|')
elif [ $# -eq 4 ]; then
    BC_TYPE="$1"; BC_VERSION="$2"; BC_COUNTRY="$3"; DEST="$4"
    BASE_URL="https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net"

    # Resolve short version (e.g. "27.5") to full version (e.g. "27.5.46862.48612)
    # using the per-country JSON index file that Microsoft maintains for
    # navcontainerhelper:
    #
    #   https://bcartifacts.blob.core.windows.net/<type>/indexes/<country>.json
    #
    # This is the canonical approach used by BcContainerHelper's
    # QueryArtifactsFromIndex (HelperFunctions.ps1:1721) — it's a static
    # JSON object, NOT the list-blobs API. Avoids the AFD list-blobs cache
    # poisoning that plagued earlier versions of this script
    # (microsoft/navcontainerhelper#4119), which would intermittently return
    # stale 27.0/27.1/27.2 entries when asked for prefix=27.5.
    #
    # To skip the resolver entirely, pass a fully-qualified version like
    # "27.5.46862.48612" via BC_VERSION — the regex below sees three parts
    # and goes straight to the download.
    if ! echo "$BC_VERSION" | grep -qP '^\d+\.\d+\.\d+'; then
        echo "[artifacts] Resolving version $BC_VERSION via Microsoft's index file..."
        T_RESOLVE=$(_ms)
        REQUESTED_PREFIX="$BC_VERSION"
        INDEX_URL="$BASE_URL/${BC_TYPE}/indexes/${BC_COUNTRY}.json"
        RESOLVED=""
        # Three attempts in case of transient network errors. The index
        # file is a regular cached blob, so it doesn't suffer the
        # list-blobs API's stale-cache problem; one retry is usually
        # plenty.
        for attempt in 1 2 3; do
            RESOLVED=$(curl -sf --retry 2 --retry-delay 2 "$INDEX_URL" 2>/dev/null | \
                BC_PREFIX="$REQUESTED_PREFIX" python3 -c "
import json, os, sys
prefix = os.environ['BC_PREFIX'] + '.'
try:
    data = json.load(sys.stdin)
except Exception as e:
    sys.exit(1)
versions = [d['Version'] for d in data if d.get('Version', '').startswith(prefix)]
if not versions:
    sys.exit(0)
def vkey(v):
    return tuple(int(x) for x in v.split('.'))
versions.sort(key=vkey)
print(versions[-1])
" 2>/dev/null || true)
            if [ -n "$RESOLVED" ] && echo "$RESOLVED" | grep -q "^${REQUESTED_PREFIX}\."; then
                break
            fi
            RESOLVED=""
            echo "[artifacts] WARN: attempt $attempt — index file unreachable or no '$REQUESTED_PREFIX.x' versions found; retrying..."
            sleep 3
        done
        if [ -z "$RESOLVED" ]; then
            echo "[artifacts] ERROR: Could not resolve version $REQUESTED_PREFIX from $INDEX_URL"
            echo "[artifacts] Workaround: pin BC_VERSION to a fully-qualified version, e.g.:"
            echo "[artifacts]   BC_VERSION=27.5.46862.48612 docker compose up -d --wait"
            exit 1
        fi
        echo "[artifacts] Resolved: $REQUESTED_PREFIX → $RESOLVED ($(( $(_ms) - T_RESOLVE ))ms)"
        BC_VERSION="$RESOLVED"
    fi

    APP_URL="$BASE_URL/$BC_TYPE/$BC_VERSION/$BC_COUNTRY"
    PLATFORM_URL="$BASE_URL/$BC_TYPE/$BC_VERSION/platform"
else
    echo "Usage: $0 <artifact-url> <dest>"
    echo "   or: $0 <type> <version> <country> <dest>"
    exit 1
fi

echo "[artifacts] App URL:      $APP_URL"
echo "[artifacts] Platform URL: $PLATFORM_URL"

# Download zips to a temp dir (host /tmp is fast tmpfs/SSD, not a Docker volume).
# This avoids writing ~1-3 GB of zip data into the destination volume just to
# immediately delete them after extraction — halving the volume write load.
TMPDIR_DL=$(mktemp -d)
trap 'rm -rf "$TMPDIR_DL"' EXIT

mkdir -p "$DEST/app" "$DEST/platform"

# ── Parallel download ──────────────────────────────────────────────────────
echo "[artifacts] Downloading app + platform in parallel..."
T0=$(_ms)
curl -sSL --retry 3 --retry-all-errors --http1.1 "$APP_URL"      -o "$TMPDIR_DL/app.zip"      &
APP_PID=$!
curl -sSL --retry 3 --retry-all-errors --http1.1 "$PLATFORM_URL" -o "$TMPDIR_DL/platform.zip" &
PLATFORM_PID=$!

wait $APP_PID      || { echo "[artifacts] ERROR: app artifact download failed";      exit 1; }
wait $PLATFORM_PID || { echo "[artifacts] ERROR: platform artifact download failed"; exit 1; }

T_DOWNLOADED=$(_ms)
APP_BYTES=$(stat -c%s "$TMPDIR_DL/app.zip")
PLAT_BYTES=$(stat -c%s "$TMPDIR_DL/platform.zip")
TOTAL_MB=$(( (APP_BYTES + PLAT_BYTES) / 1024 / 1024 ))
DOWNLOAD_MS=$(( T_DOWNLOADED - T0 ))
# Avoid divide-by-zero if somehow instantaneous
SPEED_MBS=$(( DOWNLOAD_MS > 0 ? TOTAL_MB * 1000 / DOWNLOAD_MS : 0 ))
echo "[artifacts] Downloaded: app=$(du -h "$TMPDIR_DL/app.zip" | cut -f1) platform=$(du -h "$TMPDIR_DL/platform.zip" | cut -f1) in ${DOWNLOAD_MS}ms (~${SPEED_MBS} MB/s)"

# ── Extract ────────────────────────────────────────────────────────────────
echo "[artifacts] Extracting app..."
T_EXTRACT=$(_ms)
unzip -qo "$TMPDIR_DL/app.zip" -d "$DEST/app"

PLATFORM_VERSION=$(python3 -c "import json; print(json.load(open('$DEST/app/manifest.json'))['platform'])" 2>/dev/null)
echo "[artifacts] Platform version: $PLATFORM_VERSION"

echo "[artifacts] Extracting platform (ServiceTier, ModernDev, WebClient, applications, Test Assemblies)..."
# Selective extraction keeps only what the service tier needs (~50% of the zip)
# WebClient is needed for TestPage client DLLs (page testability in tests)
unzip -qo "$TMPDIR_DL/platform.zip" 'ServiceTier/*' 'ModernDev/*' 'WebClient/*' 'applications/*' 'Test Assemblies/*' -d "$DEST/platform" 2>/dev/null || \
    unzip -qo "$TMPDIR_DL/platform.zip" -d "$DEST/platform"

T_DONE=$(_ms)
EXTRACT_MS=$(( T_DONE - T_EXTRACT ))
TOTAL_MS=$(( T_DONE - T0 ))
echo "[artifacts] Extracted in ${EXTRACT_MS}ms | Total: ${TOTAL_MS}ms | Disk: $(du -sh "$DEST" | cut -f1)"

echo "[artifacts] Done. Artifacts at $DEST"
