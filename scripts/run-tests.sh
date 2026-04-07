#!/usr/bin/env bash
# run-tests.sh — Run AL tests on a BC Linux container
#
# Hybrid execution: OData for setup + WebSocket for execution + OData for results.
# The WebSocket path creates a proper client session (serviceConnection) required
# for TestPage support. OData handles suite population and result reading.
#
# Usage:
#   ./scripts/run-tests.sh [options]
#
# Options:
#   --app <path>               Test app file (auto-published + codeunit discovery)
#   --codeunit-range <range>   Codeunit ID range (e.g. "70000" or "70000..70001")
#   --company <name>           Company name (default: auto-detect)
#   --base-url <url>           BC base URL (default: http://localhost:7048/BC)
#   --dev-url <url>            BC Dev endpoint (default: http://localhost:7049/BC/dev)
#   --auth <user:pass>         Authentication (default: BCRUNNER:Admin123!)
#   --timeout <minutes>        Overall timeout (default: 30)
#   --test-runner-app <path>   TestRunnerExtension .app (auto-detected)

set -uo pipefail

# === Configuration & CLI Parsing ===
BASE_URL="http://localhost:7048/BC"
DEV_URL="http://localhost:7049/BC/dev"
AUTH="BCRUNNER:Admin123!"
COMPANY=""
CODEUNIT_RANGE=""
APP_FILE=""
TIMEOUT_MIN=30
DISABLED_TESTS_DIR=""
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_RUNNER_APP=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --app) APP_FILE="$2"; shift 2;;
        --codeunit-range) CODEUNIT_RANGE="$2"; shift 2;;
        --company) COMPANY="$2"; shift 2;;
        --base-url) BASE_URL="$2"; shift 2;;
        --dev-url) DEV_URL="$2"; shift 2;;
        --auth) AUTH="$2"; shift 2;;
        --timeout) TIMEOUT_MIN="$2"; shift 2;;
        --test-runner-app) TEST_RUNNER_APP="$2"; shift 2;;
        --disabled-tests) DISABLED_TESTS_DIR="$2"; shift 2;;
        --host|--test-runner|--suite-name|--codeunit-timeout|--extension-id|--sql-password) shift 2;;
        *) echo "Unknown option: $1"; exit 1;;
    esac
done

echo "=== BC Test Runner ==="

# --- Helper ---
py3() { env -u PYTHONHOME -u PYTHONPATH python3 "$@"; }

# --- Find TestRunnerExtension .app ---
if [ -z "$TEST_RUNNER_APP" ]; then
    TEST_RUNNER_APP="$REPO_DIR/extensions/TestRunnerExtension/TestRunnerExtension.app"
fi
if [ ! -f "$TEST_RUNNER_APP" ]; then
    echo "ERROR: TestRunnerExtension .app not found at $TEST_RUNNER_APP"
    exit 1
fi

# === Company Auto-Detection ===
COMPANIES_JSON=""
for url in "${BASE_URL}/api/v2.0/companies" "http://localhost:7052/BC/api/v2.0/companies"; do
    COMPANIES_JSON=$(curl -sf --max-time 10 -u "$AUTH" "$url" 2>/dev/null || true)
    [ -n "$COMPANIES_JSON" ] && break
done
if [ -z "$COMPANIES_JSON" ]; then
    COMPANIES_JSON=$(curl -sf --max-time 10 -u "$AUTH" "${BASE_URL}/ODataV4/Company" 2>/dev/null || true)
fi
if [ -z "$COMPANIES_JSON" ]; then
    echo "ERROR: Cannot reach BC. Is it running?"
    exit 1
fi

COMPANY_AUTO=$(echo "$COMPANIES_JSON" | py3 -c "import sys,json; c=json.load(sys.stdin)['value'][0]; print(c.get('name',c.get('Name','')))" 2>/dev/null || true)
COMPANY_ID=$(echo "$COMPANIES_JSON" | py3 -c "import sys,json; c=json.load(sys.stdin)['value'][0]; print(c.get('id',c.get('SystemId','')))" 2>/dev/null || true)
[ -z "$COMPANY" ] && COMPANY="${COMPANY_AUTO:-CRONUS International Ltd.}"

if [ -z "$COMPANY_ID" ]; then
    for url in "${BASE_URL}/api/v2.0/companies" "http://localhost:7052/BC/api/v2.0/companies"; do
        COMPANY_ID=$(curl -sf --max-time 10 -u "$AUTH" "$url" 2>/dev/null \
            | py3 -c "import sys,json; [print(c.get('id',c.get('SystemId',''))) for c in json.load(sys.stdin)['value'] if c.get('name',c.get('Name',''))==sys.argv[1]]" "$COMPANY" 2>/dev/null || true)
        [ -n "$COMPANY_ID" ] && break
    done
fi
if [ -z "$COMPANY_ID" ]; then
    echo "ERROR: Could not get company ID for '$COMPANY'"
    exit 1
fi
echo "Company: $COMPANY ($COMPANY_ID)"

# === Determine API Base URL ===
API_PORT_BASE=""
for base in "${BASE_URL}" "http://localhost:7052/BC"; do
    HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 -u "$AUTH" \
        "${base}/api/custom/automation/v1.0/companies(${COMPANY_ID})/codeunitRunRequests" 2>/dev/null || echo "000")
    [ "$HTTP" = "200" ] && API_PORT_BASE="$base" && break
done
if [ -z "$API_PORT_BASE" ]; then
    for base in "${BASE_URL}" "http://localhost:7052/BC"; do
        T=$(curl -s -o /dev/null -w "%{http_code}" --max-time 5 -u "$AUTH" "${base}/api/v2.0/companies" 2>/dev/null || echo "000")
        [ "$T" = "200" ] && API_PORT_BASE="$base" && break
    done
    [ -z "$API_PORT_BASE" ] && API_PORT_BASE="$BASE_URL"
fi
API_BASE="${API_PORT_BASE}/api/custom/automation/v1.0/companies(${COMPANY_ID})"

# === Ensure TestRunnerExtension Published ===
echo -n "Checking TestRunner API... "
HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 -u "$AUTH" "${API_BASE}/codeunitRunRequests" 2>/dev/null || echo "000")
if [ "$HTTP" = "200" ]; then
    echo "available"
else
    echo "not found, publishing..."
    PUB_HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 120 -u "$AUTH" -X POST \
        -F "file=@${TEST_RUNNER_APP};type=application/octet-stream" \
        "${DEV_URL}/apps?SchemaUpdateMode=forcesync" 2>/dev/null)
    if [ "$PUB_HTTP" != "200" ] && [ "$PUB_HTTP" != "422" ]; then
        echo "ERROR: Failed to publish TestRunnerExtension (HTTP $PUB_HTTP)"
        exit 1
    fi
    echo -n "  Waiting for API..."
    for i in $(seq 1 30); do
        sleep 2
        HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 -u "$AUTH" "${API_BASE}/codeunitRunRequests" 2>/dev/null || echo "000")
        [ "$HTTP" = "200" ] && break
        echo -n "."
    done
    echo ""
    [ "$HTTP" != "200" ] && echo "ERROR: TestRunner API not available" && exit 1
    echo "  API ready"
fi

# === Publish Test App (if provided) ===
if [ -n "$APP_FILE" ] && [ -f "$APP_FILE" ]; then
    echo -n "Publishing $(basename "$APP_FILE")... "
    PUB_HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 120 -u "$AUTH" -X POST \
        -F "file=@${APP_FILE};type=application/octet-stream" \
        "${DEV_URL}/apps?SchemaUpdateMode=forcesync" 2>/dev/null)
    if [ "$PUB_HTTP" = "200" ]; then
        echo "OK"
    elif [ "$PUB_HTTP" = "422" ]; then
        echo "already installed (same version)"
    else
        echo "WARN: HTTP $PUB_HTTP (continuing anyway)"
    fi
fi

# === Discover Test Codeunit IDs ===
#
# Strategy: when an --app is provided, ALWAYS read SymbolReference.json from
# the .app and extract the actual Test codeunit IDs. If --codeunit-range is
# *also* provided, intersect the discovered IDs with that range. This avoids
# the SetupSuite iterating tens of thousands of nonexistent IDs (each
# AddTestCodeunit call costs ~3-5 SQL ops, so a literal "50000-99999" range
# expands to ~250k SQL ops, which times out long before completing).
#
# When only --codeunit-range is provided (no .app to discover from), fall
# back to the literal range — this is for ad-hoc usage and large
# Microsoft-shipped test apps where the .app may not be on the host.
CODEUNIT_IDS=""

# Parse --codeunit-range into a normalized "lo-hi" or "lo,lo,..." form.
NORMALIZED_RANGE=""
if [ -n "$CODEUNIT_RANGE" ]; then
    if [[ "$CODEUNIT_RANGE" == *".."* ]]; then
        NORMALIZED_RANGE=$(echo "$CODEUNIT_RANGE" | sed 's/\.\.\([0-9]\)/-\1/')
    else
        NORMALIZED_RANGE="$CODEUNIT_RANGE"
    fi
fi

if [ -n "$APP_FILE" ] && [ -f "$APP_FILE" ]; then
    echo -n "Discovering test codeunits from $(basename "$APP_FILE")"
    [ -n "$NORMALIZED_RANGE" ] && echo -n " (filter: $NORMALIZED_RANGE)"
    echo -n "... "
    CODEUNIT_IDS=$(unzip -p "$APP_FILE" SymbolReference.json 2>/dev/null | RANGE="$NORMALIZED_RANGE" py3 -c "
import os, sys, json
raw = sys.stdin.read()
if not raw.strip(): sys.exit(0)
data = json.loads(raw.lstrip('\ufeff'))

# Parse the optional range filter into a set/range list of allowed IDs.
filt = os.environ.get('RANGE', '').strip()
allowed = None  # None = no filter
if filt:
    allowed = set()
    for part in filt.split(','):
        part = part.strip()
        if not part:
            continue
        if '-' in part:
            lo, hi = part.split('-', 1)
            try:
                allowed.update(range(int(lo), int(hi) + 1))
            except ValueError:
                pass
        else:
            try:
                allowed.add(int(part))
            except ValueError:
                pass

ids = []
def collect(node):
    for cu in node.get('Codeunits', []):
        props = {p['Name']: p['Value'] for p in cu.get('Properties', [])}
        if props.get('Subtype') != 'Test':
            continue
        cuid = cu.get('Id')
        if allowed is not None and cuid not in allowed:
            continue
        ids.append(str(cuid))
    for ns in node.get('Namespaces', []):
        collect(ns)
collect(data)
print(','.join(ids))
" 2>/dev/null || true)
    echo "${CODEUNIT_IDS:-none found}"
fi

# Fall back to literal range expansion if no .app was provided.
if [ -z "$CODEUNIT_IDS" ] && [ -n "$NORMALIZED_RANGE" ] && [ -z "$APP_FILE" ]; then
    CODEUNIT_IDS="$NORMALIZED_RANGE"
fi

if [ -z "$CODEUNIT_IDS" ]; then
    echo "ERROR: No test codeunits found. Provide --app (with test codeunits in the symbol) or --codeunit-range"
    exit 1
fi
echo "Test codeunits: $CODEUNIT_IDS"

# === Setup Test Suite via OData ===
echo -n "Setting up test suite... "
CREATE_RESP=$(curl -s --max-time 15 -u "$AUTH" -X POST \
    -H "Content-Type: application/json" \
    -d "{\"CodeunitIds\": \"$CODEUNIT_IDS\"}" \
    "${API_BASE}/codeunitRunRequests" 2>/dev/null)
REQUEST_ID=$(echo "$CREATE_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin)['Id'])" 2>/dev/null || true)

if [ -z "$REQUEST_ID" ]; then
    echo "FAIL (could not create request)"
    exit 1
fi

SETUP_HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 60 -u "$AUTH" -X POST \
    "${API_BASE}/codeunitRunRequests(${REQUEST_ID})/Microsoft.NAV.setupSuite" 2>/dev/null)
if [ "$SETUP_HTTP" != "200" ] && [ "$SETUP_HTTP" != "204" ]; then
    echo "FAIL (HTTP $SETUP_HTTP)"
    exit 1
fi

# === Verify the suite was populated ===
#
# setupSuite returns 200 even when it ended up populating the suite with
# zero codeunits. That happens when the test app was published just before
# this script ran but BC's metadata cache hasn't propagated the new
# codeunits to the test framework yet — a race we've observed when
# run-tests.sh is invoked back-to-back from a fast inner loop like
# bc-copilot-blueprint's iterate.sh.
#
# Diagnostic signature of the race: TestRunner.dll runs and prints
# "0 total, 0 passed, 0 failed" in 0 seconds, then exits 1.
#
# Fix: query the testResults endpoint (which exposes Test Method Line
# rows from the suite). If empty, re-call setupSuite up to a handful of
# times with a small delay — usually the metadata catches up within a
# second or two.
verify_suite_populated() {
    local req_id="$1"
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
        local resp
        resp=$(curl -sf --max-time 10 -u "$AUTH" \
            "${API_BASE}/testResults?\$filter=testSuite%20eq%20'DEFAULT'&\$top=1" 2>/dev/null)
        if [ -n "$resp" ] && echo "$resp" | py3 -c "
import sys, json
try:
    data = json.load(sys.stdin)
    sys.exit(0 if len(data.get('value', [])) > 0 else 1)
except Exception:
    sys.exit(1)
" 2>/dev/null; then
            [ "$attempt" -gt 1 ] && echo "  suite populated after ${attempt} setupSuite attempts"
            return 0
        fi
        # Suite still empty — re-call setupSuite. The metadata may have
        # synced since the previous attempt.
        curl -s -o /dev/null --max-time 60 -u "$AUTH" -X POST \
            "${API_BASE}/codeunitRunRequests(${req_id})/Microsoft.NAV.setupSuite" 2>/dev/null
        sleep 1
    done
    return 1
}

if ! verify_suite_populated "$REQUEST_ID"; then
    echo "FAIL"
    echo "ERROR: setupSuite returned 200 but the test suite is empty after 10 retries."
    echo "       This usually means the test app was published but BC's metadata"
    echo "       cache has not propagated its codeunits to the test framework yet."
    echo "       Either:"
    echo "         - Wait longer between publishing the test app and running tests,"
    echo "         - Or check that the test app installed successfully for the tenant."
    exit 1
fi
echo "OK"

# === Disable Known-Failing Tests ===
if [ -n "$DISABLED_TESTS_DIR" ] && [ -d "$DISABLED_TESTS_DIR" ]; then
    # Read each DisabledTests JSON file individually (concatenating produces invalid JSON)
    # BCApps format per file: [{"codeunitId": 132920, "method": "TestName"}, ...]
    DISABLED_ENTRIES=$(py3 -c "
import json, glob, os, sys

pairs = []
for f in sorted(glob.glob(os.path.join(sys.argv[1], '*.json'))):
    try:
        with open(f) as fh:
            entries = json.load(fh)
        if not isinstance(entries, list):
            entries = [entries]
        for e in entries:
            cu = e.get('codeunitId', 0)
            method = e.get('method', e.get('Method', ''))
            if cu and method:
                pairs.append(f'{cu}:{method}')
    except Exception as ex:
        pass

# Split into chunks of max 2000 chars (API field limit)
chunks, current = [], ''
for p in pairs:
    if len(current) + len(p) + 1 > 2000:
        chunks.append(current)
        current = p
    else:
        current = f'{current},{p}' if current else p
if current:
    chunks.append(current)
for c in chunks:
    print(c)
" "$DISABLED_TESTS_DIR" 2>/dev/null)
    echo "Parsed disabled tests from $(find "$DISABLED_TESTS_DIR" -name '*.json' | wc -l) files"

    if [ -n "$DISABLED_ENTRIES" ]; then
        DISABLED_COUNT=0
        while IFS= read -r CHUNK; do
            # Create a request and call DisableTests
            DIS_RESP=$(curl -s --max-time 15 -u "$AUTH" -X POST \
                -H "Content-Type: application/json" \
                -d "{\"CodeunitIds\": \"$CHUNK\"}" \
                "${API_BASE}/codeunitRunRequests" 2>/dev/null)
            DIS_ID=$(echo "$DIS_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin)['Id'])" 2>/dev/null || true)
            if [ -n "$DIS_ID" ]; then
                curl -s -o /dev/null --max-time 30 -u "$AUTH" -X POST \
                    "${API_BASE}/codeunitRunRequests(${DIS_ID})/Microsoft.NAV.disableTests" 2>/dev/null
                DISABLED_COUNT=$((DISABLED_COUNT + 1))
            fi
        done <<< "$DISABLED_ENTRIES"
        echo "Disabled tests: $DISABLED_COUNT chunk(s) from $(find "$DISABLED_TESTS_DIR" -name "*.json" | wc -l) file(s)"
    fi
fi

# === Execute Tests via WebSocket ===
echo ""
echo "=== Running Tests ==="

# Extract host from BASE_URL for WebSocket connection
BC_HOST=$(echo "$BASE_URL" | sed 's|http[s]*://||' | sed 's|/.*||' | sed 's|:.*||')
[ -z "$BC_HOST" ] && BC_HOST="localhost"
WS_HOST="${BC_HOST}:7085"
ODATA_HOST="${BC_HOST}:7052"

# Parse auth components
AUTH_USER="${AUTH%%:*}"
AUTH_PASS="${AUTH#*:}"

# Calculate max iterations: each codeunit needs ~2 iterations (run + reconnect after isolation)
IFS=',' read -ra CU_ARRAY <<< "$CODEUNIT_IDS"
NUM_CODEUNITS=${#CU_ARRAY[@]}
MAX_ITER=$(( NUM_CODEUNITS * 3 + 20 ))
echo "Executing $NUM_CODEUNITS codeunits via WebSocket (max $MAX_ITER iterations)..."

# Note: do NOT pass --codeunit-filter here — the suite is already set up via OData.
# Passing it would re-trigger SetupSuite which clears test results.
# We pass --num-codeunits for correct progress display only.
#
# TestRunner execution strategy:
#   1. If BC is in a local docker compose stack, run it INSIDE the bc container
#      via `docker compose exec`. This requires NO host-side .NET 8 SDK because
#      the container already has the .NET 8 runtime (and we pre-publish the
#      TestRunner.dll into the image at /bc/tools/TestRunner/).
#   2. Otherwise (remote BC, or no docker), fall back to `dotnet run` against
#      the source project — requires .NET 8 SDK on the host.
USE_DOCKER_EXEC=false
DOCKER_BC_CONTAINER=""
if [ "$BC_HOST" = "localhost" ] || [ "$BC_HOST" = "127.0.0.1" ]; then
    if command -v docker >/dev/null 2>&1; then
        # Try to find a running bc container in the bc-linux compose project.
        DOCKER_BC_CONTAINER=$(cd "$REPO_DIR" 2>/dev/null && docker compose ps -q bc 2>/dev/null | head -1)
        if [ -n "$DOCKER_BC_CONTAINER" ]; then
            # Verify TestRunner.dll is bundled in the image. Older bc-runner
            # images (built before this change) don't have it; in that case
            # we fall back to host dotnet run so the script keeps working.
            if (cd "$REPO_DIR" 2>/dev/null && docker compose exec -T bc test -f /bc/tools/TestRunner/TestRunner.dll 2>/dev/null); then
                USE_DOCKER_EXEC=true
            else
                echo "[run-tests] bc-runner image does not bundle TestRunner.dll — falling back to host dotnet run."
                echo "[run-tests]   (rebuild with 'docker compose build bc' to drop the host SDK requirement.)"
            fi
        fi
    fi
fi

if [ "$USE_DOCKER_EXEC" = "true" ]; then
    # Inside the container, BC's WebSocket and API ports are local to the
    # container itself, so always use localhost regardless of how the host
    # has them mapped. The TestRunner.dll path is fixed by the Dockerfile.
    ( cd "$REPO_DIR" && docker compose exec -T bc dotnet /bc/tools/TestRunner/TestRunner.dll \
        --host "localhost:7085" \
        --odata-host "localhost:7052" \
        --company "$COMPANY" \
        --user "$AUTH_USER" \
        --password "$AUTH_PASS" \
        --suite "DEFAULT" \
        --num-codeunits "$NUM_CODEUNITS" \
        --timeout "$TIMEOUT_MIN" \
        --codeunit-timeout 10 \
        --max-iterations "$MAX_ITER" )
    EXIT_CODE=$?
elif command -v dotnet >/dev/null 2>&1; then
    dotnet run --project "$REPO_DIR/tools/TestRunner" -v q -- \
        --host "$WS_HOST" \
        --odata-host "$ODATA_HOST" \
        --company "$COMPANY" \
        --user "$AUTH_USER" \
        --password "$AUTH_PASS" \
        --suite "DEFAULT" \
        --num-codeunits "$NUM_CODEUNITS" \
        --timeout "$TIMEOUT_MIN" \
        --codeunit-timeout 10 \
        --max-iterations "$MAX_ITER"
    EXIT_CODE=$?
else
    echo "ERROR: cannot run TestRunner — neither a local BC docker container nor a host-side dotnet SDK is available."
    echo "  Either: start BC via 'docker compose up -d --wait' from the bc-linux directory, or install .NET 8 SDK on the host."
    exit 1
fi

# The TestRunner already reads and prints results via OData.
# Its exit code: 0 = all pass, 1 = failures or no tests.
exit $EXIT_CODE
