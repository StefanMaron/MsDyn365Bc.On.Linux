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
if [ "$SETUP_HTTP" = "200" ] || [ "$SETUP_HTTP" = "204" ]; then
    echo "OK"
else
    echo "FAIL (HTTP $SETUP_HTTP)"
    exit 1
fi

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

# The TestRunner already reads and prints results via OData.
# Its exit code: 0 = all pass, 1 = failures or no tests.
exit $EXIT_CODE
