#!/bin/sh
# Cleanly restart just the BC service tier (NST) inside an already-running
# container, without tearing the container down. Run it via:
#   docker compose exec bc /bc/scripts/restart-service.sh
#
# Why this exists: entrypoint.sh's main process just `wait`s on the NST pid
# with nothing to relaunch it — that's deliberate for the normal "container
# dies, orchestrator restarts it" case, but it means sending NST SIGTERM
# directly (`docker exec ... kill <pid>`) takes the whole container down
# with it (verified: exits 143). A `docker compose restart bc` avoids that,
# but pays for it by re-running entrypoint.sh's one-time setup (SQL Server
# tuning, clearing/re-publishing the test-framework apps, tenant property
# fixups, ...) against a database that already has all of that done — repeat
# runs of code written for "database is fresh" are the working theory for
# why a full container restart has been observed to leave BC unable to
# resolve a user's default profile (role center stuck on "Getting ready").
#
# This script only touches the NST process itself: it asks entrypoint.sh's
# supervisor loop (see the bottom of entrypoint.sh) to relaunch
# `dotnet Microsoft.Dynamics.Nav.Server.dll` in place, and waits for the new
# process's dev endpoint to answer, mirroring entrypoint.sh's own startup
# wait. Nothing SQL-side reruns. Whether that actually avoids the profile
# problem above is exactly what this script exists to let you test —
# it has NOT been confirmed to fix it, only confirmed to keep the container
# alive across the bounce.
set -eu

PIDFILE=/tmp/bc-nst.pid
DEV_URL="http://localhost:7049"
TIMEOUT="${1:-60}"

if [ ! -f "$PIDFILE" ]; then
    echo "[restart-service] ERROR: $PIDFILE not found — NST is not running under" \
         "entrypoint.sh's supervisor loop (old image? container still booting?)" >&2
    exit 1
fi
OLD_PID=$(cat "$PIDFILE")
if ! kill -0 "$OLD_PID" 2>/dev/null; then
    echo "[restart-service] WARNING: pid $OLD_PID in $PIDFILE is not running already" \
         "— proceeding, the supervisor loop may already be relaunching it" >&2
fi

START=$(date +%s)
echo "[restart-service] requesting restart of NST (current pid $OLD_PID)..."
touch /tmp/bc-restart-requested
kill -TERM "$OLD_PID" 2>/dev/null || true

# Wait for the supervisor loop to record a new pid in the same file.
NEW_PID="$OLD_PID"
while [ "$NEW_PID" = "$OLD_PID" ]; do
    ELAPSED=$(( $(date +%s) - START ))
    if [ "$ELAPSED" -gt "$TIMEOUT" ]; then
        echo "[restart-service] ERROR: NST did not relaunch within ${TIMEOUT}s" \
             "(still pid $OLD_PID, or supervisor loop gave up — check container logs)" >&2
        exit 1
    fi
    sleep 1
    NEW_PID=$(cat "$PIDFILE" 2>/dev/null || echo "$OLD_PID")
done
echo "[restart-service] [$(( $(date +%s) - START ))s] NST relaunched as pid $NEW_PID"

echo "[restart-service] waiting for dev endpoint..."
HTTP=000
while [ "$(( $(date +%s) - START ))" -le "$TIMEOUT" ]; do
    if ! kill -0 "$NEW_PID" 2>/dev/null; then
        echo "[restart-service] ERROR: relaunched NST (pid $NEW_PID) died before" \
             "the dev endpoint came up — check container logs" >&2
        exit 1
    fi
    # curl itself exits non-zero (e.g. 7, connection refused) while NST's
    # listener isn't up yet — under `set -e` a bare assignment to its
    # command substitution would abort the script right here, so give it an
    # `||` fallback (set -e exempts the left side of `||`), same fix as
    # entrypoint.sh's `wait "$BC_PID" || RC=$?`.
    HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 3 "$DEV_URL/packages" 2>&1) || HTTP=000
    [ "$HTTP" != "000" ] && break
    sleep 2
done
ELAPSED=$(( $(date +%s) - START ))
if [ "$HTTP" = "000" ]; then
    echo "[restart-service] ERROR: dev endpoint did not respond within ${TIMEOUT}s" \
         "(pid $NEW_PID, last HTTP=$HTTP)" >&2
    exit 1
fi
echo "[restart-service] [${ELAPSED}s] dev endpoint ready (HTTP $HTTP) — NST restart complete"
