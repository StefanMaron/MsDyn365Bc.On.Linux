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

    # Resolve short version (e.g. "27.5") to full version (e.g. "27.5.46862.48004")
    # by listing available blobs in the Azure CDN storage container
    if ! echo "$BC_VERSION" | grep -qP '^\d+\.\d+\.\d+'; then
        echo "[artifacts] Resolving version $BC_VERSION..."
        T_RESOLVE=$(_ms)
        RESOLVED=$(curl -sf "$BASE_URL/${BC_TYPE}?restype=container&comp=list&prefix=${BC_VERSION}" 2>/dev/null | \
            grep -oP '<Name>\K[^<]+' | grep "/${BC_COUNTRY}$" | sort -V | tail -1 | cut -d/ -f1)
        if [ -z "$RESOLVED" ]; then
            echo "[artifacts] ERROR: Could not resolve version $BC_VERSION"
            echo "[artifacts] Listing available versions..."
            curl -sf "$BASE_URL/${BC_TYPE}?restype=container&comp=list&prefix=${BC_VERSION}" 2>/dev/null | \
                grep -oP '<Name>\K[^<]+' | head -10
            exit 1
        fi
        echo "[artifacts] Resolved: $BC_VERSION → $RESOLVED ($(( $(_ms) - T_RESOLVE ))ms)"
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
curl -sSL --retry 3 "$APP_URL"      -o "$TMPDIR_DL/app.zip"      &
APP_PID=$!
curl -sSL --retry 3 "$PLATFORM_URL" -o "$TMPDIR_DL/platform.zip" &
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

echo "[artifacts] Extracting platform (ServiceTier, ModernDev, applications, Test Assemblies)..."
# Selective extraction keeps only what the service tier needs (~50% of the zip)
unzip -qo "$TMPDIR_DL/platform.zip" 'ServiceTier/*' 'ModernDev/*' 'applications/*' 'Test Assemblies/*' -d "$DEST/platform" 2>/dev/null || \
    unzip -qo "$TMPDIR_DL/platform.zip" -d "$DEST/platform"

T_DONE=$(_ms)
EXTRACT_MS=$(( T_DONE - T_EXTRACT ))
TOTAL_MS=$(( T_DONE - T0 ))
echo "[artifacts] Extracted in ${EXTRACT_MS}ms | Total: ${TOTAL_MS}ms | Disk: $(du -sh "$DEST" | cut -f1)"

echo "[artifacts] Done. Artifacts at $DEST"
