#!/usr/bin/env bash
# wait-for-bc-healthy.sh — Block until the BC docker container reports
# the docker healthcheck as `healthy`. Fails fast on `unhealthy`, on the
# container exiting, and on the container never appearing.
#
# Usage (run from a directory containing docker-compose.yml):
#   ./scripts/wait-for-bc-healthy.sh [timeout-minutes]
#
# Defaults to 30 minutes. Polls every 2 seconds and prints a progress
# line from the most recent [entrypoint] log message every 60 seconds
# so you can see what BC is doing without staring at silence.
#
# Exit codes:
#   0  BC reached `healthy` within the timeout
#   1  BC reached `unhealthy`, the container died, or the timeout
#      expired. The last 100 lines of `docker compose logs bc` are
#      printed before exit so the failure context is preserved.
#
# This is a single canonical implementation of the BC-readiness loop
# that the previous codebase had inlined into bc-test-from-source.yml,
# bc-test-prebuilt.yml, test-versions.yml (twice — test job AND
# test-container-download job), AND iterate.sh in the bc-copilot-blueprint
# repo. Five copies in five files, all subtly different. The bc-copilot
# debugging session in 2026 made it clear that this needs to be one
# script — bug fixes here propagate everywhere instead of leaving four
# stale duplicates behind.

set -uo pipefail

TIMEOUT_MIN="${1:-30}"
MAX_ITER=$(( TIMEOUT_MIN * 30 ))   # 2 sec poll interval

START_TIME=$(date +%s)
LAST_PROGRESS=0
STATUS="unknown"

echo "Waiting for BC to be healthy (max ${TIMEOUT_MIN} min)..."

# How long a missing container is tolerated before it counts as a failure. The
# caller runs `docker compose up -d` first, so the container normally exists on
# the first poll; this only covers a caller that starts us a moment early.
CREATE_GRACE_SECONDS=60
MISSING_SINCE=0

for i in $(seq 1 "$MAX_ITER"); do
    # -a: without it `docker compose ps` lists only RUNNING containers. The bc
    # service has no restart policy, so once the tier exits the container stays
    # exited, `ps -q` goes empty, and the loop used to spin to the timeout
    # without printing the unhealthy, not-running or progress lines.
    CID=$(docker compose ps -a -q bc 2>/dev/null | head -1)
    if [ -z "$CID" ]; then
        NOW=$(date +%s)
        [ "$MISSING_SINCE" -eq 0 ] && MISSING_SINCE=$NOW
        if [ $((NOW - MISSING_SINCE)) -ge "$CREATE_GRACE_SECONDS" ]; then
            echo "ERROR: no BC container exists after ${CREATE_GRACE_SECONDS}s"
            docker compose ps -a 2>&1 | tail -20
            exit 1
        fi
        sleep 2
        continue
    fi
    MISSING_SINCE=0

    STATUS=$(docker inspect --format='{{.State.Health.Status}}' "$CID" 2>/dev/null || echo "unknown")
    RUN_STATE=$(docker inspect --format='{{.State.Status}}' "$CID" 2>/dev/null || echo "unknown")
    case "$STATUS" in
        healthy)
            ELAPSED=$(( $(date +%s) - START_TIME ))
            echo "BC healthy after ${ELAPSED}s"
            exit 0
            ;;
        unhealthy)
            echo "ERROR: BC container reached 'unhealthy'"
            docker compose logs bc 2>&1 | tail -100
            exit 1
            ;;
    esac

    # The container stopped (the tier crashed, or the entrypoint exited).
    case "$RUN_STATE" in
        exited|dead)
            EXIT_CODE=$(docker inspect --format='{{.State.ExitCode}}' "$CID" 2>/dev/null || echo "?")
            echo "ERROR: BC container is no longer running (state=${RUN_STATE}, exit code ${EXIT_CODE})"
            docker compose logs bc 2>&1 | tail -100
            exit 1
            ;;
    esac

    # Print a progress line every ~60 seconds so callers can see what
    # BC's entrypoint is currently doing instead of staring at silence.
    NOW=$(date +%s)
    if [ $((NOW - LAST_PROGRESS)) -ge 60 ]; then
        ELAPSED=$((NOW - START_TIME))
        LAST_LOG=$(docker compose logs bc 2>&1 | grep -E '\[entrypoint\]' | tail -1 | sed 's/^bc-1[[:space:]]*|[[:space:]]*//')
        echo "  ${ELAPSED}s — status=${STATUS}${LAST_LOG:+ — $LAST_LOG}"
        LAST_PROGRESS=$NOW
    fi

    sleep 2
done

echo "ERROR: BC did not become healthy within ${TIMEOUT_MIN} minutes (final status: ${STATUS})"
docker compose logs bc 2>&1 | tail -100
exit 1
