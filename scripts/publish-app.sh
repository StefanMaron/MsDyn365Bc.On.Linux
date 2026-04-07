#!/usr/bin/env bash
# publish-app.sh — Shared "publish a .app to a running BC instance" helper.
#
# Designed to be SOURCED, not executed:
#
#   . "$REPO_DIR/scripts/publish-app.sh"
#   bc_publish_app /path/to/myapp.app
#   bc_publish_app /path/to/myapp.app "http://localhost:7049/BC/dev" "BCRUNNER:Admin123!"
#
# Returns 0 on success — meaning either:
#   - HTTP 200 (fresh publish completed), or
#   - HTTP 422 with "already" in the body (same version already installed,
#     which BC reports as 422 even though it isn't a real error).
#
# Returns 1 (and prints a diagnostic dump) for everything else, including
# any other 4xx/5xx and 422s whose body does NOT contain "already". The
# most common 422 failure is "missing dependency" — when the .app declares
# a dependency on another extension that isn't installed in the BC
# database. The pre-2026 version of this code treated ALL 422s as
# "already installed" and silently swallowed missing-dep failures, which
# made for many wasted debugging cycles in downstream consumers like
# bc-copilot-blueprint. Now those failures are loud and have a clear
# error body printed.

bc_publish_app() {
    local app="$1"
    local dev_url="${2:-http://localhost:7049/BC/dev}"
    local auth="${3:-BCRUNNER:Admin123!}"

    if [ -z "$app" ]; then
        echo "bc_publish_app: missing required argument: <app-path>" >&2
        return 1
    fi
    if [ ! -f "$app" ]; then
        echo "bc_publish_app: file not found: $app" >&2
        return 1
    fi

    local body
    body=$(mktemp)
    local code
    code=$(curl -s -o "$body" -w "%{http_code}" --max-time 180 \
        -u "$auth" -X POST \
        -F "file=@${app};type=application/octet-stream" \
        "${dev_url}/apps?SchemaUpdateMode=forcesync" 2>/dev/null)

    if [ "$code" = "200" ]; then
        rm -f "$body"
        return 0
    fi

    if [ "$code" = "422" ] && grep -qi "already" "$body"; then
        rm -f "$body"
        return 0
    fi

    echo ""
    echo "ERROR: dev endpoint returned HTTP $code for $(basename "$app")"
    echo "       URL: ${dev_url}/apps?SchemaUpdateMode=forcesync"
    echo "       Body:"
    sed 's/^/         /' "$body"
    echo ""
    echo "       Common causes:"
    echo "         - Missing dependency that isn't installed in BC. (Putting"
    echo "           the symbol file in .alpackages makes it visible at"
    echo "           compile time, but the dependency itself still needs to"
    echo "           be installed in the BC database. If this is a Microsoft"
    echo "           test framework app, it may need to be added to the"
    echo "           republish set in entrypoint.sh.)"
    echo "         - Schema sync failure (forcesync detected a destructive"
    echo "           change in a published version)."
    echo "         - Version conflict with a previously-published variant."
    rm -f "$body"
    return 1
}
