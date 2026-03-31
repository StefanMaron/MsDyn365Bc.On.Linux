#!/usr/bin/env bash
# run-tests.sh — Run AL tests on a BC Linux container via REST API
#
# Uses the TestRunnerExtension's OData API to execute tests. The extension
# is auto-published if not already available.
#
# Usage:
#   ./scripts/run-tests.sh [options]
#
# Options:
#   --app <path>               Test app file (for codeunit discovery via SymbolReference.json)
#   --codeunit-range <range>   Codeunit ID range (e.g. "70000" or "70000..70001")
#   --company <name>           Company name (default: auto-detect via OData)
#   --base-url <url>           BC OData base URL (default: http://localhost:7048/BC)
#   --dev-url <url>            BC Dev endpoint URL (default: http://localhost:7049/BC/dev)
#   --auth <user:pass>         Authentication (default: admin:Admin123!)
#   --timeout <minutes>        Overall timeout (default: 30)
#   --test-runner-app <path>   Path to TestRunnerExtension .app (auto-detected from repo)

set -uo pipefail

# Defaults
BASE_URL="http://localhost:7048/BC"
DEV_URL="http://localhost:7049/BC/dev"
AUTH="admin:Admin123!"
COMPANY=""
CODEUNIT_RANGE=""
APP_FILE=""
TIMEOUT_MIN=30
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
        # Legacy options (accepted but ignored for backward compat)
        --host|--test-runner|--suite-name|--codeunit-timeout|--extension-id|--disabled-tests|--sql-password) shift 2;;
        *) echo "Unknown option: $1"; exit 1;;
    esac
done

echo "=== BC Test Runner (REST API) ==="

# --- Find TestRunnerExtension .app ---
if [ -z "$TEST_RUNNER_APP" ]; then
    TEST_RUNNER_APP="$REPO_DIR/extensions/TestRunnerExtension/TestRunnerExtension.app"
fi
if [ ! -f "$TEST_RUNNER_APP" ]; then
    echo "ERROR: TestRunnerExtension .app not found at $TEST_RUNNER_APP"
    echo "  Provide --test-runner-app or place it in extensions/TestRunnerExtension/"
    exit 1
fi
echo "TestRunner app: $(basename "$TEST_RUNNER_APP")"

# --- Helper: run python3 safely (unset PYTHONHOME for AppImage compat) ---
py3() { env -u PYTHONHOME -u PYTHONPATH python3 "$@"; }

# --- Auto-detect company via OData ---
if [ -z "$COMPANY" ]; then
    COMPANY=$(curl -sf --max-time 10 -u "$AUTH" "${BASE_URL}/api/v2.0/companies" 2>/dev/null \
        | py3 -c "import sys,json; print(json.load(sys.stdin)['value'][0]['name'])" 2>/dev/null || true)
    [ -z "$COMPANY" ] && COMPANY="CRONUS International Ltd."
fi
echo "Company: $COMPANY"

# --- Get company ID ---
COMPANY_ID=$(curl -sf --max-time 10 -u "$AUTH" "${BASE_URL}/api/v2.0/companies" 2>/dev/null \
    | py3 -c "
import sys, json
name = sys.argv[1]
for c in json.load(sys.stdin)['value']:
    if c['name'] == name:
        print(c['id']); break
" "$COMPANY" 2>/dev/null || true)

if [ -z "$COMPANY_ID" ]; then
    echo "ERROR: Could not get company ID for '$COMPANY'"
    echo "  Is BC running? Check: curl -u $AUTH ${BASE_URL}/api/v2.0/companies"
    exit 1
fi

# --- Ensure TestRunnerExtension is published ---
API_BASE="${BASE_URL}/api/custom/automation/v1.0/companies(${COMPANY_ID})"
API_URL="${API_BASE}/codeunitRunRequests"

echo -n "Checking TestRunner API... "
HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 -u "$AUTH" "$API_URL" 2>/dev/null || echo "000")
if [ "$HTTP" = "200" ]; then
    echo "available"
else
    echo "not found (HTTP $HTTP), publishing extension..."
    RESP=$(curl -s -w "\n%{http_code}" --max-time 120 -u "$AUTH" -X POST \
        -F "file=@${TEST_RUNNER_APP};type=application/octet-stream" \
        "${DEV_URL}/apps?SchemaUpdateMode=forcesync" 2>/dev/null)
    PUB_HTTP=$(echo "$RESP" | tail -1)
    PUB_BODY=$(echo "$RESP" | head -n -1)
    if [ "$PUB_HTTP" != "200" ] && [ "$PUB_HTTP" != "422" ]; then
        echo "ERROR: Failed to publish TestRunnerExtension (HTTP $PUB_HTTP)"
        echo "  $PUB_BODY"
        exit 1
    fi
    echo "  Published (HTTP $PUB_HTTP)"

    # Wait for BC to register the API endpoint
    echo -n "  Waiting for API..."
    for i in $(seq 1 30); do
        sleep 2
        HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 -u "$AUTH" "$API_URL" 2>/dev/null || echo "000")
        [ "$HTTP" = "200" ] && break
        echo -n "."
    done
    echo ""
    if [ "$HTTP" != "200" ]; then
        echo "ERROR: TestRunner API not available after 60s"
        exit 1
    fi
    echo "  API ready"
fi

# --- Discover test codeunit IDs ---
CODEUNIT_IDS=""

# Priority 1: explicit --codeunit-range
if [ -n "$CODEUNIT_RANGE" ]; then
    if [[ "$CODEUNIT_RANGE" == *".."* ]]; then
        # Convert AL-style "70000..70001" to API-style "70000-70001"
        CODEUNIT_IDS=$(echo "$CODEUNIT_RANGE" | sed 's/\.\.\([0-9]\)/-\1/')
    else
        CODEUNIT_IDS="$CODEUNIT_RANGE"
    fi
fi

# Priority 2: discover from --app SymbolReference.json
if [ -z "$CODEUNIT_IDS" ] && [ -n "$APP_FILE" ] && [ -f "$APP_FILE" ]; then
    echo "Discovering test codeunits from $(basename "$APP_FILE")..."
    CODEUNIT_IDS=$(unzip -p "$APP_FILE" SymbolReference.json 2>/dev/null | py3 -c "
import sys, json
raw = sys.stdin.read()
if not raw.strip(): sys.exit(0)
data = json.loads(raw.lstrip('\ufeff'))
ids = []
def collect(node):
    for cu in node.get('Codeunits', []):
        props = {p['Name']: p['Value'] for p in cu.get('Properties', [])}
        if props.get('Subtype') == 'Test':
            ids.append(str(cu['Id']))
    for ns in node.get('Namespaces', []):
        collect(ns)
collect(data)
print(','.join(ids))
" 2>/dev/null || true)
fi

if [ -z "$CODEUNIT_IDS" ]; then
    echo "ERROR: No test codeunits found."
    echo "  Provide --app <path> or --codeunit-range <range>"
    exit 1
fi

CU_COUNT=$(echo "$CODEUNIT_IDS" | tr ',' '\n' | wc -l)
echo "Test codeunits: $CODEUNIT_IDS ($CU_COUNT)"

# --- Execute tests via REST API ---
echo ""
echo "=== Running Tests ==="

# Create run request
CREATE_RESP=$(curl -sf --max-time 30 -u "$AUTH" -X POST \
    -H "Content-Type: application/json" \
    -d "{\"CodeunitIds\": \"$CODEUNIT_IDS\"}" \
    "$API_URL" 2>/dev/null || true)

if [ -z "$CREATE_RESP" ]; then
    echo "ERROR: Failed to create test run request"
    exit 1
fi

REQUEST_ID=$(echo "$CREATE_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin)['Id'])" 2>/dev/null || true)
if [ -z "$REQUEST_ID" ]; then
    echo "ERROR: Failed to parse run request response"
    echo "  Response: $CREATE_RESP"
    exit 1
fi

# Trigger execution (synchronous — blocks until tests complete or timeout)
echo "Executing (timeout: ${TIMEOUT_MIN}m)..."
START_TIME=$(date +%s)
EXEC_RESP=$(curl -s -w "\n%{http_code}" --max-time $((TIMEOUT_MIN * 60)) \
    -u "$AUTH" -X POST "${API_URL}(${REQUEST_ID})/Microsoft.NAV.runCodeunit" 2>/dev/null || true)
EXEC_HTTP=$(echo "$EXEC_RESP" | tail -1)
ELAPSED=$(( $(date +%s) - START_TIME ))

# Read status
STATUS_RESP=$(curl -sf --max-time 10 -u "$AUTH" "${API_URL}(${REQUEST_ID})" 2>/dev/null || true)
STATUS=$(echo "$STATUS_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin).get('Status',''))" 2>/dev/null || true)
RESULT=$(echo "$STATUS_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin).get('LastResult',''))" 2>/dev/null || true)

# If trigger timed out, poll for completion
if [ "$STATUS" = "Running" ] || [ "$STATUS" = "Pending" ]; then
    echo "  Trigger returned HTTP $EXEC_HTTP after ${ELAPSED}s, polling..."
    DEADLINE=$(( $(date +%s) + TIMEOUT_MIN * 60 ))
    while [ $(date +%s) -lt $DEADLINE ]; do
        sleep 3
        STATUS_RESP=$(curl -sf --max-time 10 -u "$AUTH" "${API_URL}(${REQUEST_ID})" 2>/dev/null || true)
        STATUS=$(echo "$STATUS_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin).get('Status',''))" 2>/dev/null || true)
        RESULT=$(echo "$STATUS_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin).get('LastResult',''))" 2>/dev/null || true)
        case "$STATUS" in
            Finished|Error) break ;;
        esac
        echo -n "."
    done
    echo ""
fi

TOTAL_ELAPSED=$(( $(date +%s) - START_TIME ))

case "$STATUS" in
    Finished) echo "Tests completed in ${TOTAL_ELAPSED}s ($RESULT)" ;;
    Error)    echo "Tests completed in ${TOTAL_ELAPSED}s with failures ($RESULT)" ;;
    *)        echo "ERROR: Tests did not complete (status: ${STATUS:-unknown}, HTTP: $EXEC_HTTP)"; exit 1 ;;
esac

# --- Read results from Log Table API ---
echo ""
echo "=== Test Results ==="
LOG_URL="${API_BASE}/logEntries?\$top=10000"
LOGS=$(curl -sf --max-time 30 -u "$AUTH" "$LOG_URL" 2>/dev/null || true)

if [ -z "$LOGS" ]; then
    echo "WARNING: Could not retrieve test logs from API"
    [ "$STATUS" = "Finished" ] && exit 0 || exit 1
fi

echo "$LOGS" | py3 -c "
import sys, json

data = json.load(sys.stdin)
logs = data.get('value', [])

passed = 0
failed = 0
for l in logs:
    success = l.get('success', False)
    cu_name = l.get('codeunitName', 'Unknown')
    fn_name = l.get('functionName', '')
    error = l.get('errorMessage', '')

    if success:
        passed += 1
        print(f'  PASS  {cu_name}::{fn_name}')
    else:
        failed += 1
        line = f'  FAIL  {cu_name}::{fn_name}'
        if error:
            line += f' -- {error[:120]}'
        print(line)

total = passed + failed
print(f'')
print(f'Results: {total} total, {passed} passed, {failed} failed')

if failed > 0:
    sys.exit(1)
elif passed > 0:
    sys.exit(0)
else:
    print('ERROR: No tests executed')
    sys.exit(1)
"
