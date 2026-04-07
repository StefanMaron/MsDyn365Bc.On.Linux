#!/usr/bin/env bash
# workflow-summary.sh — Per-phase timing + counter capture for bc-linux
# test workflows. Emits a markdown summary to the CI surface AND posts
# anonymous telemetry to the bc-linux Application Insights instance so
# we can collect real-world performance benchmarks across consumers.
#
# Usage from inside a workflow step:
#
#   # At workflow start, before any timed phase:
#   bash bc-linux/scripts/workflow-summary.sh init
#
#   # Around each timed phase:
#   bash bc-linux/scripts/workflow-summary.sh begin <phase-id> "<phase-label>"
#   ... do work ...
#   bash bc-linux/scripts/workflow-summary.sh end <phase-id>
#
#   # Capture informational counters at any point:
#   bash bc-linux/scripts/workflow-summary.sh count apps_compiled 5
#   bash bc-linux/scripts/workflow-summary.sh count tests_passed 142
#
#   # At the very end (with `if: always()` so it runs on failure too):
#   bash bc-linux/scripts/workflow-summary.sh emit
#
# The summary is written to:
#   - $GITHUB_STEP_SUMMARY when running under GitHub Actions
#   - via ##vso[task.uploadsummary] when running under Azure Pipelines
#   - stdout otherwise (CI-agnostic fallback)
#
# Telemetry:
#
# At `emit` time, a single batch of events is POSTed to bc-linux's
# Application Insights ingestion endpoint (Poland Central). The data is
# anonymous-ish: repo owner/name from $GITHUB_REPOSITORY, BC version,
# runner OS, per-phase durations, and the informational counters.
# **No source, app payloads, test names, or credentials are sent.**
#
# **Opt out** by setting BC_TELEMETRY_OPT_OUT=true in the workflow env
# (or anywhere in the parent shell). The script will skip the POST and
# print "[telemetry] opted out" instead.
#
# Why telemetry: the goal is to collect real-world wall-time benchmarks
# from many downstream consumers so we can build a credible
# "Linux is faster than Windows BC" case for Microsoft. Without data
# from real pipelines this remains anecdotal.
#
# Phase ordering in the final table is the order in which 'begin' was
# called. Phases not closed by 'end' show as "(in progress)" — useful
# when the workflow fails mid-phase and the summary still gets emitted.

set -e

# ── Telemetry endpoint (App Insights — Poland Central) ──────────────────
# These are public telemetry credentials. The instrumentation key is the
# unique identifier for the bc-linux App Insights instance and is meant
# to be embedded in client code (just like a Google Analytics tracking
# ID). It does NOT grant any read access to the data.
TELEMETRY_IKEY="d4bb1fdf-d36d-4582-859d-1ab9be8d6f29"
TELEMETRY_INGESTION="https://polandcentral-0.in.applicationinsights.azure.com/v2/track"

# Shared scratch dir survives across job steps within the same runner
# (each GH Actions step gets a fresh shell but the filesystem persists).
TIMINGS_DIR="${BC_LINUX_TIMINGS_DIR:-/tmp/bc-linux-timings}"

cmd="${1:-}"; shift || true

case "$cmd" in
    init)
        rm -rf "$TIMINGS_DIR"
        mkdir -p "$TIMINGS_DIR"
        date +%s > "$TIMINGS_DIR/T_WORKFLOW_START"
        # Order tracking: phases are listed in this file in begin-call order.
        : > "$TIMINGS_DIR/PHASE_ORDER"
        ;;

    begin)
        phase_id="$1"; phase_label="${2:-$phase_id}"
        mkdir -p "$TIMINGS_DIR"
        # Append to PHASE_ORDER unless already present.
        if ! grep -qxF "$phase_id" "$TIMINGS_DIR/PHASE_ORDER" 2>/dev/null; then
            echo "$phase_id" >> "$TIMINGS_DIR/PHASE_ORDER"
        fi
        date +%s > "$TIMINGS_DIR/T_${phase_id}_START"
        echo "$phase_label" > "$TIMINGS_DIR/T_${phase_id}_LABEL"
        ;;

    end)
        phase_id="$1"
        date +%s > "$TIMINGS_DIR/T_${phase_id}_END"
        ;;

    count)
        # Record an informational counter (number of apps compiled,
        # tests passed, codeunits run, etc.). Counters are flat
        # name=value pairs in $TIMINGS_DIR/COUNTERS, one per line.
        counter_name="$1"; counter_value="$2"
        mkdir -p "$TIMINGS_DIR"
        : > "$TIMINGS_DIR/COUNTERS.tmp"
        if [ -f "$TIMINGS_DIR/COUNTERS" ]; then
            grep -v "^${counter_name}=" "$TIMINGS_DIR/COUNTERS" > "$TIMINGS_DIR/COUNTERS.tmp" || true
        fi
        echo "${counter_name}=${counter_value}" >> "$TIMINGS_DIR/COUNTERS.tmp"
        mv "$TIMINGS_DIR/COUNTERS.tmp" "$TIMINGS_DIR/COUNTERS"
        ;;

    emit)
        if [ ! -f "$TIMINGS_DIR/T_WORKFLOW_START" ]; then
            echo "workflow-summary: no timings recorded — was 'init' called?" >&2
            exit 0
        fi
        T_START=$(cat "$TIMINGS_DIR/T_WORKFLOW_START")
        T_NOW=$(date +%s)
        TOTAL=$((T_NOW - T_START))
        TOTAL_M=$((TOTAL / 60))
        TOTAL_S=$((TOTAL % 60))

        # Job outcome — passed in via the workflow's `env: JOB_STATUS:
        # ${{ job.status }}`. 'success' | 'failure' | 'cancelled'.
        # Defaults to 'success' if not set, since the script being
        # called at all means the rest of the steps got that far.
        JOB_STATUS_VALUE="${JOB_STATUS:-success}"

        # Identify the in-flight phase (started but not closed) — that's
        # almost certainly the one that crashed if the job failed.
        FAILED_PHASE=""
        if [ -s "$TIMINGS_DIR/PHASE_ORDER" ]; then
            while IFS= read -r p; do
                [ -z "$p" ] && continue
                if [ -f "$TIMINGS_DIR/T_${p}_START" ] && [ ! -f "$TIMINGS_DIR/T_${p}_END" ]; then
                    FAILED_PHASE="$p"
                fi
            done < "$TIMINGS_DIR/PHASE_ORDER"
        fi

        # On failure, capture the tail of the BC container log (if a
        # compose project is reachable) so the telemetry includes a
        # diagnostic snippet — this is what lets Stefan see "the NST
        # crashed on X" without asking the consumer to repro.
        FAILURE_LOG=""
        if [ "$JOB_STATUS_VALUE" != "success" ]; then
            if command -v docker >/dev/null 2>&1; then
                # Try common compose locations: $REPO_DIR/bc-linux is what
                # the templates check out to; ./bc-linux when running from
                # the workspace root; . if we're already inside bc-linux.
                for compose_dir in "${GITHUB_WORKSPACE:-.}/bc-linux" "./bc-linux" "."; do
                    if [ -f "$compose_dir/docker-compose.yml" ]; then
                        FAILURE_LOG=$(cd "$compose_dir" && docker compose logs bc 2>&1 | tail -80 || true)
                        break
                    fi
                done
            fi
        fi

        # Build the markdown body in a heredoc-friendly variable.
        STATUS_ICON="✅"
        STATUS_LABEL="success"
        case "$JOB_STATUS_VALUE" in
            failure)   STATUS_ICON="❌"; STATUS_LABEL="failure" ;;
            cancelled) STATUS_ICON="⚠️";  STATUS_LABEL="cancelled" ;;
        esac
        SUMMARY=$(cat <<EOF
## ${STATUS_ICON} Workflow ${STATUS_LABEL} — ${TOTAL_M}m ${TOTAL_S}s

| Phase | Duration | % of total |
|---|---:|---:|
EOF
)
        if [ -s "$TIMINGS_DIR/PHASE_ORDER" ]; then
            while IFS= read -r phase_id; do
                [ -z "$phase_id" ] && continue
                label=$(cat "$TIMINGS_DIR/T_${phase_id}_LABEL" 2>/dev/null || echo "$phase_id")
                pstart=$(cat "$TIMINGS_DIR/T_${phase_id}_START" 2>/dev/null || echo "")
                pend=$(cat "$TIMINGS_DIR/T_${phase_id}_END" 2>/dev/null || echo "")
                if [ -n "$pstart" ] && [ -n "$pend" ]; then
                    delta=$((pend - pstart))
                    pct=0
                    [ "$TOTAL" -gt 0 ] && pct=$(( (delta * 100) / TOTAL ))
                    SUMMARY="$SUMMARY
| ${label} | ${delta}s | ${pct}% |"
                elif [ -n "$pstart" ]; then
                    delta=$((T_NOW - pstart))
                    SUMMARY="$SUMMARY
| ${label} | ${delta}s (in progress) | — |"
                fi
            done < "$TIMINGS_DIR/PHASE_ORDER"
        fi

        # Counters (informational) — appended to summary if any.
        if [ -s "$TIMINGS_DIR/COUNTERS" ]; then
            SUMMARY="$SUMMARY

### Counters

| Metric | Value |
|---|---:|"
            while IFS='=' read -r cname cvalue; do
                [ -z "$cname" ] && continue
                # Replace underscores with spaces, capitalize for display.
                pretty=$(echo "$cname" | tr '_' ' ')
                SUMMARY="$SUMMARY
| ${pretty} | ${cvalue} |"
            done < "$TIMINGS_DIR/COUNTERS"
        fi

        # On failure, surface the in-flight phase and a BC log tail in
        # the markdown summary so the GitHub Actions run page itself
        # shows what crashed without anyone digging through logs.
        if [ "$JOB_STATUS_VALUE" != "success" ]; then
            if [ -n "$FAILED_PHASE" ]; then
                FAILED_LABEL=$(cat "$TIMINGS_DIR/T_${FAILED_PHASE}_LABEL" 2>/dev/null || echo "$FAILED_PHASE")
                SUMMARY="$SUMMARY

### ❌ Failed in: ${FAILED_LABEL} (\`${FAILED_PHASE}\`)"
            fi
            if [ -n "$FAILURE_LOG" ]; then
                # Truncate to ~6 KB to stay readable in the GH summary view.
                LOG_SNIPPET=$(echo "$FAILURE_LOG" | tail -c 6000)
                SUMMARY="$SUMMARY

<details><summary>Last 80 lines of <code>docker compose logs bc</code></summary>

\`\`\`
${LOG_SNIPPET}
\`\`\`

</details>"
            fi
        fi

        SUMMARY="$SUMMARY

> bc-linux \`workflow-summary.sh\` — see [StefanMaron/MsDyn365Bc.On.Linux](https://github.com/StefanMaron/MsDyn365Bc.On.Linux)"

        # Write to whichever CI summary surface is available.
        if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
            echo "$SUMMARY" >> "$GITHUB_STEP_SUMMARY"
            echo "[workflow-summary] wrote summary to \$GITHUB_STEP_SUMMARY"
        elif [ "${TF_BUILD:-}" = "True" ]; then
            # Azure Pipelines: task.uploadsummary needs a file path.
            tmpfile=$(mktemp --suffix=.md)
            echo "$SUMMARY" > "$tmpfile"
            echo "##vso[task.uploadsummary]$tmpfile"
            echo "[workflow-summary] uploaded summary via task.uploadsummary"
        else
            echo "$SUMMARY"
        fi

        # ─── Telemetry: post to bc-linux Application Insights ────────
        if [ "${BC_TELEMETRY_OPT_OUT:-}" = "true" ] || [ "${BC_TELEMETRY_OPT_OUT:-}" = "1" ]; then
            echo "[telemetry] opted out (BC_TELEMETRY_OPT_OUT is set)"
            exit 0
        fi
        if ! command -v curl >/dev/null 2>&1 || ! command -v python3 >/dev/null 2>&1; then
            echo "[telemetry] skipped (curl or python3 not available)"
            exit 0
        fi

        # Build the telemetry payload via python3 — easier than escaping
        # JSON in shell. Reads phase timings + counters from $TIMINGS_DIR.
        TELEMETRY_PAYLOAD=$(
            BC_LINUX_TIMINGS_DIR="$TIMINGS_DIR" \
            TELEMETRY_IKEY="$TELEMETRY_IKEY" \
            T_WORKFLOW_START="$T_START" \
            T_WORKFLOW_END="$T_NOW" \
            JOB_STATUS_VALUE="$JOB_STATUS_VALUE" \
            FAILED_PHASE="$FAILED_PHASE" \
            FAILURE_LOG="$FAILURE_LOG" \
            python3 - <<'PYEOF'
import json, os, sys, datetime, uuid

timings_dir = os.environ['BC_LINUX_TIMINGS_DIR']
ikey        = os.environ['TELEMETRY_IKEY']
t_start     = int(os.environ['T_WORKFLOW_START'])
t_end       = int(os.environ['T_WORKFLOW_END'])
job_status  = os.environ.get('JOB_STATUS_VALUE', 'success')
failed_phase = os.environ.get('FAILED_PHASE', '')
failure_log  = os.environ.get('FAILURE_LOG', '')

# Common identifying tags (anonymous-ish, never includes secrets/source).
operation_id = str(uuid.uuid4())
tags = {
    "ai.cloud.role": "bc-linux-pipeline",
    "ai.cloud.roleInstance": os.environ.get("RUNNER_NAME") or os.environ.get("AGENT_NAME") or "unknown",
    "ai.operation.id": operation_id,
    "ai.application.ver": os.environ.get("BC_VERSION", "unknown"),
}

# Properties carried on every event so a single dimension query can
# slice all of them by repo / BC version / OS / status.
common_props = {
    "repository":      os.environ.get("GITHUB_REPOSITORY") or os.environ.get("BUILD_REPOSITORY_NAME") or "unknown",
    "ci_provider":     "github" if os.environ.get("GITHUB_ACTIONS") == "true" else
                       "azure_devops" if os.environ.get("TF_BUILD") == "True" else "other",
    "runner_os":       os.environ.get("RUNNER_OS") or os.environ.get("AGENT_OS") or "unknown",
    "bc_version":      os.environ.get("BC_VERSION", "unknown"),
    "bc_country":      os.environ.get("BC_COUNTRY", "unknown"),
    "bc_type":         os.environ.get("BC_TYPE", "unknown"),
    "operation_id":    operation_id,
    "status":          job_status,  # success | failure | cancelled
}

def now_iso():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")

def make_event(name, props=None, measurements=None):
    return {
        "name":  f"Microsoft.ApplicationInsights.{ikey.replace('-', '')}.Event",
        "time":  now_iso(),
        "iKey":  ikey,
        "tags":  tags,
        "data": {
            "baseType": "EventData",
            "baseData": {
                "ver":  2,
                "name": name,
                "properties":  {**common_props, **(props or {})},
                "measurements": measurements or {},
            },
        },
    }

events = []

# 1. Per-phase timing events
phase_order = []
phase_order_file = os.path.join(timings_dir, "PHASE_ORDER")
if os.path.exists(phase_order_file):
    with open(phase_order_file) as f:
        phase_order = [l.strip() for l in f if l.strip()]

for phase_id in phase_order:
    pstart_f = os.path.join(timings_dir, f"T_{phase_id}_START")
    pend_f   = os.path.join(timings_dir, f"T_{phase_id}_END")
    plabel_f = os.path.join(timings_dir, f"T_{phase_id}_LABEL")
    if not (os.path.exists(pstart_f) and os.path.exists(pend_f)):
        continue
    pstart = int(open(pstart_f).read().strip())
    pend   = int(open(pend_f).read().strip())
    label  = open(plabel_f).read().strip() if os.path.exists(plabel_f) else phase_id
    events.append(make_event(
        "PhaseTiming",
        props={"phase_id": phase_id, "phase_label": label},
        measurements={"duration_seconds": pend - pstart},
    ))

# 2. Counter events
counters = {}
counters_file = os.path.join(timings_dir, "COUNTERS")
if os.path.exists(counters_file):
    for line in open(counters_file):
        line = line.strip()
        if "=" not in line:
            continue
        k, v = line.split("=", 1)
        try:
            counters[k] = float(v)
        except ValueError:
            pass

# 3. Final WorkflowComplete event with all measurements rolled up
workflow_measurements = {"total_seconds": t_end - t_start}
workflow_measurements.update(counters)
events.append(make_event(
    "WorkflowComplete",
    measurements=workflow_measurements,
))

# 4. On failure, also send a WorkflowFailure event with the in-flight
#    phase and a truncated docker-compose log tail. App Insights string
#    properties are capped at ~8 KB, so we trim the log to ~6 KB.
if job_status != "success":
    failure_props = {
        "failed_phase": failed_phase or "(unknown)",
    }
    if failure_log:
        # Trim from the END (most recent lines are most useful) and
        # also from very long single lines that would blow the cap.
        snippet = failure_log[-6000:]
        failure_props["failure_log_tail"] = snippet
    events.append(make_event("WorkflowFailure", props=failure_props))

print(json.dumps(events))
PYEOF
        )

        if [ -n "$TELEMETRY_PAYLOAD" ]; then
            HTTP_CODE=$(curl -s -o /tmp/telemetry.out -w "%{http_code}" \
                --max-time 10 \
                -H "Content-Type: application/json" \
                -X POST "$TELEMETRY_INGESTION" \
                -d "$TELEMETRY_PAYLOAD" 2>/dev/null || echo "000")
            if [ "$HTTP_CODE" = "200" ]; then
                echo "[telemetry] sent $(echo "$TELEMETRY_PAYLOAD" | python3 -c 'import sys,json; print(len(json.load(sys.stdin)))') events to bc-linux App Insights (set BC_TELEMETRY_OPT_OUT=true to disable)"
            else
                echo "[telemetry] POST failed (HTTP $HTTP_CODE) — ignored"
            fi
        fi
        ;;

    *)
        echo "Usage: $0 {init|begin <id> <label>|end <id>|emit}" >&2
        exit 2
        ;;
esac
