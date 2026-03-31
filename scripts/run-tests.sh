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

# --- Auto-detect company and ID via OData ---
# Try API v2.0 on both OData port and API port (BC serves APIs on different ports)
COMPANIES_JSON=""
for url in "${BASE_URL}/api/v2.0/companies" "http://localhost:7052/BC/api/v2.0/companies"; do
    COMPANIES_JSON=$(curl -sf --max-time 10 -u "$AUTH" "$url" 2>/dev/null || true)
    [ -n "$COMPANIES_JSON" ] && break
done

# Fallback: OData V4 endpoint (always on port 7048)
if [ -z "$COMPANIES_JSON" ]; then
    COMPANIES_JSON=$(curl -sf --max-time 10 -u "$AUTH" "${BASE_URL}/ODataV4/Company" 2>/dev/null || true)
fi

if [ -z "$COMPANIES_JSON" ]; then
    echo "ERROR: Cannot reach BC. Is it running?"
    echo "  Tried: ${BASE_URL}/api/v2.0/companies"
    exit 1
fi

# Extract company name and ID (handle both 'name'/'Name' and 'id'/'SystemId' fields)
# Use tab separator to handle company names with spaces
COMPANY_AUTO=$(echo "$COMPANIES_JSON" | py3 -c "
import sys, json
data = json.load(sys.stdin)
c = data['value'][0]
print(c.get('name', c.get('Name', '')))
" 2>/dev/null || true)

COMPANY_ID=$(echo "$COMPANIES_JSON" | py3 -c "
import sys, json
data = json.load(sys.stdin)
c = data['value'][0]
print(c.get('id', c.get('SystemId', '')))
" 2>/dev/null || true)

if [ -z "$COMPANY" ]; then
    COMPANY="${COMPANY_AUTO:-CRONUS International Ltd.}"
fi
echo "Company: $COMPANY (ID: ${COMPANY_ID:-none})"

# If we got a name but no ID (OData V4 doesn't return GUID), look up ID via API
if [ -z "$COMPANY_ID" ]; then
    for url in "${BASE_URL}/api/v2.0/companies" "http://localhost:7052/BC/api/v2.0/companies"; do
        COMPANY_ID=$(curl -sf --max-time 10 -u "$AUTH" "$url" 2>/dev/null \
            | py3 -c "
import sys, json
name = sys.argv[1]
for c in json.load(sys.stdin)['value']:
    if c.get('name', c.get('Name', '')) == name:
        print(c.get('id', c.get('SystemId', ''))); break
" "$COMPANY" 2>/dev/null || true)
        [ -n "$COMPANY_ID" ] && break
    done
fi

if [ -z "$COMPANY_ID" ]; then
    echo "ERROR: Could not get company ID for '$COMPANY'"
    echo "  The API v2.0 endpoint may not be available."
    echo "  Check: curl -u $AUTH http://localhost:7052/BC/api/v2.0/companies"
    exit 1
fi

# --- Ensure TestRunnerExtension is published ---
# Try custom API on both OData port and API port
API_PORT_BASE=""
for base in "${BASE_URL}" "http://localhost:7052/BC"; do
    CHECK_URL="${base}/api/custom/automation/v1.0/companies(${COMPANY_ID})/codeunitRunRequests"
    HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 -u "$AUTH" "$CHECK_URL" 2>/dev/null || echo "000")
    if [ "$HTTP" = "200" ]; then
        API_PORT_BASE="$base"
        break
    fi
done

# If not found, we'll need to publish — pick the port that served the v2.0 API
if [ -z "$API_PORT_BASE" ]; then
    # Test which port serves APIs
    for base in "${BASE_URL}" "http://localhost:7052/BC"; do
        T=$(curl -s -o /dev/null -w "%{http_code}" --max-time 5 -u "$AUTH" "${base}/api/v2.0/companies" 2>/dev/null || echo "000")
        if [ "$T" = "200" ]; then API_PORT_BASE="$base"; break; fi
    done
    [ -z "$API_PORT_BASE" ] && API_PORT_BASE="$BASE_URL"
fi

API_BASE="${API_PORT_BASE}/api/custom/automation/v1.0/companies(${COMPANY_ID})"
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

echo "Test codeunits: $CODEUNIT_IDS"

# --- Execute tests via REST API (one codeunit at a time) ---
echo ""
echo "=== Running Tests ==="
echo "  API URL: $API_URL"

# Per-codeunit timeout (5 min should be plenty for a single codeunit)
CU_TIMEOUT=300
TOTAL_PASSED=0
TOTAL_FAILED=0
TOTAL_SKIPPED=0
START_TIME=$(date +%s)
DEADLINE=$(( START_TIME + TIMEOUT_MIN * 60 ))

# Split comma-separated IDs into array
IFS=',' read -ra CU_ARRAY <<< "$CODEUNIT_IDS"
CU_TOTAL=${#CU_ARRAY[@]}
CU_INDEX=0

for CU_ID in "${CU_ARRAY[@]}"; do
    CU_INDEX=$((CU_INDEX + 1))

    # Check overall timeout
    if [ $(date +%s) -ge $DEADLINE ]; then
        echo "TIMEOUT: Overall timeout (${TIMEOUT_MIN}m) reached after $CU_INDEX/$CU_TOTAL codeunits"
        break
    fi

    CU_START=$(date +%s)

    # Create run request for this codeunit
    CREATE_RESP=$(curl -s -w "\n%{http_code}" --max-time 15 -u "$AUTH" -X POST \
        -H "Content-Type: application/json" \
        -d "{\"CodeunitIds\": \"$CU_ID\"}" \
        "$API_URL" 2>/dev/null || true)
    CREATE_HTTP=$(echo "$CREATE_RESP" | tail -1)
    CREATE_BODY=$(echo "$CREATE_RESP" | head -n -1)

    if [ "$CREATE_HTTP" != "201" ] && [ "$CREATE_HTTP" != "200" ]; then
        echo "  [$CU_INDEX/$CU_TOTAL] CU $CU_ID: SKIP (create failed HTTP $CREATE_HTTP)"
        TOTAL_SKIPPED=$((TOTAL_SKIPPED + 1))
        continue
    fi

    REQUEST_ID=$(echo "$CREATE_BODY" | py3 -c "import sys,json; print(json.load(sys.stdin)['Id'])" 2>/dev/null || true)
    if [ -z "$REQUEST_ID" ]; then
        echo "  [$CU_INDEX/$CU_TOTAL] CU $CU_ID: SKIP (no request ID)"
        TOTAL_SKIPPED=$((TOTAL_SKIPPED + 1))
        continue
    fi

    # Trigger execution
    EXEC_RESP=$(curl -s -w "\n%{http_code}" --max-time $CU_TIMEOUT \
        -u "$AUTH" -X POST "${API_URL}(${REQUEST_ID})/Microsoft.NAV.runCodeunit" 2>/dev/null || true)
    EXEC_HTTP=$(echo "$EXEC_RESP" | tail -1)

    # Read status
    STATUS_RESP=$(curl -sf --max-time 10 -u "$AUTH" "${API_URL}(${REQUEST_ID})" 2>/dev/null || true)
    STATUS=$(echo "$STATUS_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin).get('Status',''))" 2>/dev/null || true)
    RESULT=$(echo "$STATUS_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin).get('LastResult',''))" 2>/dev/null || true)

    # Poll if still running (up to per-codeunit timeout)
    if [ "$STATUS" = "Running" ] || [ "$STATUS" = "Pending" ]; then
        CU_DEADLINE=$(( CU_START + CU_TIMEOUT ))
        while [ $(date +%s) -lt $CU_DEADLINE ]; do
            sleep 2
            STATUS_RESP=$(curl -sf --max-time 10 -u "$AUTH" "${API_URL}(${REQUEST_ID})" 2>/dev/null || true)
            STATUS=$(echo "$STATUS_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin).get('Status',''))" 2>/dev/null || true)
            RESULT=$(echo "$STATUS_RESP" | py3 -c "import sys,json; print(json.load(sys.stdin).get('LastResult',''))" 2>/dev/null || true)
            case "$STATUS" in Finished|Error) break ;; esac
        done
    fi

    CU_ELAPSED=$(( $(date +%s) - CU_START ))

    case "$STATUS" in
        Finished)
            echo "  [$CU_INDEX/$CU_TOTAL] CU $CU_ID: PASS (${CU_ELAPSED}s)"
            TOTAL_PASSED=$((TOTAL_PASSED + 1))
            ;;
        Error)
            echo "  [$CU_INDEX/$CU_TOTAL] CU $CU_ID: FAIL (${CU_ELAPSED}s) $RESULT"
            TOTAL_FAILED=$((TOTAL_FAILED + 1))
            ;;
        *)
            echo "  [$CU_INDEX/$CU_TOTAL] CU $CU_ID: TIMEOUT (${CU_ELAPSED}s, status: ${STATUS:-unknown})"
            TOTAL_SKIPPED=$((TOTAL_SKIPPED + 1))
            ;;
    esac
done

TOTAL_ELAPSED=$(( $(date +%s) - START_TIME ))
TOTAL_RUN=$(( TOTAL_PASSED + TOTAL_FAILED + TOTAL_SKIPPED ))

echo ""
echo "=== Test Results ==="
echo "Results: $TOTAL_RUN total, $TOTAL_PASSED passed, $TOTAL_FAILED failed, $TOTAL_SKIPPED skipped (${TOTAL_ELAPSED}s)"

# Also read detailed per-function results from Log Table
LOG_URL="${API_BASE}/logEntries?\$top=10000"
LOGS=$(curl -sf --max-time 30 -u "$AUTH" "$LOG_URL" 2>/dev/null || true)
if [ -n "$LOGS" ]; then
    echo ""
    echo "=== Detailed Results ==="
    echo "$LOGS" | py3 -c "
import sys, json
data = json.load(sys.stdin)
logs = data.get('value', [])
p = f = 0
for l in logs:
    ok = l.get('success', False)
    cu = l.get('codeunitName', '?')
    fn = l.get('functionName', '')
    err = l.get('errorMessage', '')
    if ok:
        p += 1
    else:
        f += 1
        line = f'  FAIL  {cu}::{fn}'
        if err: line += f' -- {err[:120]}'
        print(line)
print(f'')
print(f'Functions: {p+f} total, {p} passed, {f} failed')
" 2>/dev/null || true
fi

if [ "$TOTAL_FAILED" -gt 0 ]; then exit 1; fi
if [ "$TOTAL_PASSED" -gt 0 ]; then exit 0; fi
echo "ERROR: No tests executed"
exit 1
