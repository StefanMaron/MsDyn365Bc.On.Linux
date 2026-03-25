#!/bin/bash
# Download BC artifacts (platform + country) to a target directory.
# Supports both public and insider artifact URLs.
#
# Usage:
#   With full URL:  download-artifacts.sh <url> <dest>
#   With parts:     download-artifacts.sh <type> <version> <country> <dest>
set -e

# Parse arguments: either (url, dest) or (type, version, country, dest)
if [ $# -eq 2 ]; then
    APP_URL="$1"
    DEST="$2"
    # Derive platform URL: replace country segment with "platform"
    PLATFORM_URL=$(echo "$APP_URL" | sed 's|/[^/]*$|/platform|')
elif [ $# -eq 4 ]; then
    BC_TYPE="$1"; BC_VERSION="$2"; BC_COUNTRY="$3"; DEST="$4"
    BASE_URL="https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net"
    APP_URL="$BASE_URL/$BC_TYPE/$BC_VERSION/$BC_COUNTRY"
    PLATFORM_URL="$BASE_URL/$BC_TYPE/$BC_VERSION/platform"
else
    echo "Usage: $0 <artifact-url> <dest>"
    echo "   or: $0 <type> <version> <country> <dest>"
    exit 1
fi

echo "[artifacts] App URL: $APP_URL"
echo "[artifacts] Platform URL: $PLATFORM_URL"

# Download and extract app artifact
mkdir -p "$DEST/app"
echo "[artifacts] Downloading app artifact..."
curl -sSL "$APP_URL" -o "$DEST/bc-app.zip"
echo "[artifacts] Extracting app ($(du -h "$DEST/bc-app.zip" | cut -f1))..."
unzip -qo "$DEST/bc-app.zip" -d "$DEST/app"
rm -f "$DEST/bc-app.zip"

# Read platform version from manifest
PLATFORM_VERSION=$(python3 -c "import json; print(json.load(open('$DEST/app/manifest.json'))['platform'])" 2>/dev/null)
echo "[artifacts] Platform version: $PLATFORM_VERSION"

# Download and extract platform artifact
mkdir -p "$DEST/platform"
echo "[artifacts] Downloading platform artifact..."
curl -sSL "$PLATFORM_URL" -o "$DEST/bc-platform.zip"
ZIPSIZE=$(du -h "$DEST/bc-platform.zip" | cut -f1)
echo "[artifacts] Extracting platform ($ZIPSIZE) — ServiceTier + ModernDev only..."
# Extract only what we need (ServiceTier + ModernDev) to save disk space
unzip -qo "$DEST/bc-platform.zip" 'ServiceTier/*' 'ModernDev/*' -d "$DEST/platform" 2>/dev/null || \
unzip -qo "$DEST/bc-platform.zip" -d "$DEST/platform"
rm -f "$DEST/bc-platform.zip"
echo "[artifacts] Disk usage: $(du -sh "$DEST" | cut -f1)"

echo "[artifacts] Done. Artifacts at $DEST"
