#!/usr/bin/env bash
# run-tests.sh — Run AL tests on a BC Linux container
#
# Mirrors BcContainerHelper's Run-TestsInBcContainer interface.
# Extensions must be published BEFORE calling this script.
#
# Usage:
#   ./scripts/run-tests.sh [options]
#
# Options:
#   --extension-id <guid>      Filter tests by extension ID
#   --codeunit-range <range>   Codeunit ID range (e.g. "70000" or "70000..70010")
#   --company <name>           Company name (default: auto-detect)
#   --host <host:port>         BC client services host (default: localhost:7085)
#   --test-runner <id>         Test runner codeunit ID (default: 130451)
#   --suite-name <name>        Test suite name (default: DEFAULT)
#   --timeout <minutes>        Overall timeout (default: 30)
#   --disabled-tests <file>    Path to disabled tests JSON
#   --sql-password <pw>        SA password for company auto-detect

set -uo pipefail

# Defaults
BC_HOST="localhost:7085"
COMPANY=""
TEST_RUNNER_ID=130451
CODEUNIT_RANGE=""
EXTENSION_ID=""
TIMEOUT_MIN=30
SUITE_NAME="DEFAULT"
DISABLED_TESTS=""
SQL_PASSWORD="${SA_PASSWORD:-Passw0rd123!}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

APP_FILE=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --app) APP_FILE="$2"; shift 2;;
        --extension-id) EXTENSION_ID="$2"; shift 2;;
        --codeunit-range) CODEUNIT_RANGE="$2"; shift 2;;
        --company) COMPANY="$2"; shift 2;;
        --host) BC_HOST="$2"; shift 2;;
        --test-runner) TEST_RUNNER_ID="$2"; shift 2;;
        --suite-name) SUITE_NAME="$2"; shift 2;;
        --timeout) TIMEOUT_MIN="$2"; shift 2;;
        --disabled-tests) DISABLED_TESTS="$2"; shift 2;;
        --sql-password) SQL_PASSWORD="$2"; shift 2;;
        *) echo "Unknown option: $1"; exit 1;;
    esac
done

echo "=== BC Test Runner ==="

# --- Detect SQL container ---
SQL_CONTAINER=$(cd "$REPO_DIR" && docker compose ps -q sql 2>/dev/null | head -1)

# --- Auto-detect company if not specified ---
if [ -z "$COMPANY" ]; then
    if [ -n "$SQL_CONTAINER" ]; then
        COMPANY=$(docker exec -e "_QB=$(echo 'USE [CRONUS]; SELECT TOP 1 RTRIM([Name]) FROM [Company] ORDER BY [Name]' | base64 -w0)" \
            "$SQL_CONTAINER" bash -c \
            'echo "$_QB" | base64 -d | /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "'"$SQL_PASSWORD"'" -C -No -i /dev/stdin' \
            2>/dev/null | grep -v "^-" | grep -v "^Changed" | grep -v "^$" | grep -v "^(" | grep -v "^\s*$" | head -1 | sed 's/ *$//')
    fi
    [ -z "$COMPANY" ] && COMPANY="CRONUS International Ltd."
fi
echo "Company: $COMPANY"

# --- Ensure DEFAULT test suite exists ---
# The WebSocket protocol does not support SaveValue for page variables,
# so we create the suite via SQL. Page 130455 reads it on open.
run_sql() {
    docker exec -e "_QB=$(echo "$1" | base64 -w0)" "$SQL_CONTAINER" bash -c \
        'echo "$_QB" | base64 -d | /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "'"$SQL_PASSWORD"'" -C -No -i /dev/stdin' 2>&1
}

TABLE_PREFIX="$(echo "${COMPANY}" | sed 's/\.//g')_\$"
SUITE_TABLE="${TABLE_PREFIX}AL Test Suite\$23de40a6-dfe8-4f80-80db-d70f83ce8caf"
METHOD_TABLE="${TABLE_PREFIX}Test Method Line\$23de40a6-dfe8-4f80-80db-d70f83ce8caf"

echo "Ensuring DEFAULT test suite..."
run_sql "
USE [CRONUS];
IF NOT EXISTS (SELECT 1 FROM [$SUITE_TABLE] WHERE [Name] = N'$SUITE_NAME')
    INSERT INTO [$SUITE_TABLE]
    ([Name],[Description],[Last Run],[Run Type],[Test Runner Id],[Stability Run],
     [CC Tracking Type],[CC Track All Sessions],[CC Exporter ID],[CC Coverage Map],
     [\$systemId],[\$systemCreatedAt],[\$systemCreatedBy],[\$systemModifiedAt],[\$systemModifiedBy])
    VALUES (N'$SUITE_NAME',N'','1753-01-01',0,$TEST_RUNNER_ID,0,0,0,0,0,
            NEWID(),GETUTCDATE(),'00000000-0000-0000-0000-000000000001',
            GETUTCDATE(),'00000000-0000-0000-0000-000000000001');
ELSE
    UPDATE [$SUITE_TABLE] SET [Test Runner Id] = $TEST_RUNNER_ID WHERE [Name] = N'$SUITE_NAME';
" > /dev/null 2>&1

# Populate test suite with codeunit + function lines
echo "Populating test suite..."
run_sql "USE [CRONUS]; DELETE FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME';" > /dev/null 2>&1

LINE_NO=10000
insert_line() {
    local CU_ID="$1" NAME="$2" FUNC="$3" LINE_TYPE="$4" LEVEL="$5"
    run_sql "
    USE [CRONUS];
    SET IDENTITY_INSERT [$METHOD_TABLE] ON;
    INSERT INTO [$METHOD_TABLE]
    ([Test Suite],[Line No_],[Test Codeunit],[Name],[Function],[Run],[Result],[Line Type],
     [Start Time],[Finish Time],[Level],[Error Message Preview],[Error Code],
     [Error Message],[Error Call Stack],[Skip Logging Results],[Data Input Group Code],[Data Input],
     [\$systemId],[\$systemCreatedAt],[\$systemCreatedBy],[\$systemModifiedAt],[\$systemModifiedBy])
    VALUES (N'$SUITE_NAME',$LINE_NO,$CU_ID,N'$NAME',N'$FUNC',1,0,$LINE_TYPE,
            '1753-01-01','1753-01-01',$LEVEL,N'',N'',0x,0x,0,N'',0x,
            NEWID(),GETUTCDATE(),'00000000-0000-0000-0000-000000000001',
            GETUTCDATE(),'00000000-0000-0000-0000-000000000001');
    SET IDENTITY_INSERT [$METHOD_TABLE] OFF;
    " > /dev/null 2>&1
    LINE_NO=$((LINE_NO + 1))
}

# If --app is provided, parse SymbolReference.json for per-method lines
if [ -n "$APP_FILE" ] && [ -f "$APP_FILE" ]; then
    TEST_LINES=$(unzip -p "$APP_FILE" SymbolReference.json 2>/dev/null | python3 -c "
import sys, json
data = json.loads(sys.stdin.read().lstrip('\ufeff'))
for cu in data.get('Codeunits', []):
    props = {p['Name']: p['Value'] for p in cu.get('Properties', [])}
    if props.get('Subtype') != 'Test': continue
    print(f\"CU|{cu['Id']}|{cu['Name']}\")
    for m in cu.get('Methods', []):
        attrs = [a.get('Name','') for a in m.get('Attributes',[])]
        if 'Test' in attrs:
            print(f\"FN|{cu['Id']}|{m['Name']}\")
" 2>/dev/null || true)

    TMPLINES=$(mktemp)
    echo "$TEST_LINES" > "$TMPLINES"
    while IFS='|' read -r TYPE ID NAME; do
        [ -z "$TYPE" ] && continue
        # Apply codeunit range filter
        if [ -n "$CODEUNIT_RANGE" ] && [[ "$CODEUNIT_RANGE" != *".."* ]] && [ "$ID" != "$CODEUNIT_RANGE" ]; then
            continue
        fi
        if [ "$TYPE" = "CU" ]; then
            echo "  Codeunit $ID: $NAME"
            insert_line "$ID" "$NAME" "" 0 0
        else
            echo "    - $NAME"
            insert_line "$ID" "$NAME" "$NAME" 1 1
        fi
    done < "$TMPLINES"
    rm -f "$TMPLINES"
else
    # Fallback: discover codeunits from Application Object Metadata (no per-method detail)
    RANGE_FILTER=""
    if [ -n "$CODEUNIT_RANGE" ]; then
        if [[ "$CODEUNIT_RANGE" == *".."* ]]; then
            FROM=$(echo "$CODEUNIT_RANGE" | cut -d. -f1)
            TO=$(echo "$CODEUNIT_RANGE" | cut -d. -f3)
            RANGE_FILTER="AND ao.[Object ID] BETWEEN $FROM AND $TO"
        else
            RANGE_FILTER="AND ao.[Object ID] = $CODEUNIT_RANGE"
        fi
    fi
    run_sql "
    USE [CRONUS];
    SET IDENTITY_INSERT [$METHOD_TABLE] ON;
    INSERT INTO [$METHOD_TABLE]
    ([Test Suite],[Line No_],[Test Codeunit],[Name],[Function],[Run],[Result],[Line Type],
     [Start Time],[Finish Time],[Level],[Error Message Preview],[Error Code],
     [Error Message],[Error Call Stack],[Skip Logging Results],[Data Input Group Code],[Data Input],
     [\$systemId],[\$systemCreatedAt],[\$systemCreatedBy],[\$systemModifiedAt],[\$systemModifiedBy])
    SELECT N'$SUITE_NAME', ROW_NUMBER() OVER (ORDER BY ao.[Object ID]) * 10000,
           ao.[Object ID], CAST(ao.[Object Name] AS nvarchar(250)),
           N'', 1, 0, 0, '1753-01-01','1753-01-01', 0, N'', N'', 0x, 0x, 0, N'', 0x,
           NEWID(), GETUTCDATE(), '00000000-0000-0000-0000-000000000001',
           GETUTCDATE(), '00000000-0000-0000-0000-000000000001'
    FROM [Application Object Metadata] ao
    WHERE ao.[Object Type] = 5 AND ao.[Object Subtype] = 'Test' $RANGE_FILTER;
    SET IDENTITY_INSERT [$METHOD_TABLE] OFF;
    " > /dev/null 2>&1
fi

CU_COUNT=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME' AND [Line Type] = 0" \
    | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)
FUNC_COUNT=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME' AND [Line Type] = 1" \
    | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)
echo "  Test codeunits: ${CU_COUNT:-0}, Test methods: ${FUNC_COUNT:-0}"

if [ "${CU_COUNT:-0}" = "0" ]; then
    echo "ERROR: No test codeunits found"
    exit 1
fi

# --- Build TestRunner tool if needed ---
TESTRUNNER_DIR="$REPO_DIR/tools/TestRunner"
if [ ! -f "$TESTRUNNER_DIR/bin/Release/net8.0/TestRunner.dll" ]; then
    echo "Building TestRunner..."
    dotnet build "$TESTRUNNER_DIR" -c Release 2>&1 | tail -3
fi

# --- Run tests via client services (page 130455) ---
# RunNextTest triggers BC test execution. The session typically dies during test
# execution (test isolation), so we ignore the exit code and read results from SQL.
timeout "${TIMEOUT_MIN}m" dotnet run --project "$TESTRUNNER_DIR" --no-build -c Release -- \
    --host "$BC_HOST" --company "$COMPANY" --timeout "$TIMEOUT_MIN" 2>&1 || true

sleep 2

# --- Read results from SQL ---
echo ""
echo "=== Test Results ==="
FUNC_COUNT=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME' AND [Line Type] = 1" \
    | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)

if [ "${FUNC_COUNT:-0}" -gt 0 ]; then
    run_sql "
    USE [CRONUS];
    SELECT CASE t.[Result] WHEN 2 THEN '  PASS' WHEN 1 THEN '  FAIL' WHEN 0 THEN '  ----' ELSE '  SKIP' END,
           RTRIM(t.[Function]), t.[Test Codeunit],
           RTRIM(CAST(t.[Error Message Preview] AS nvarchar(200)))
    FROM [$METHOD_TABLE] t
    WHERE t.[Test Suite] = N'$SUITE_NAME' AND t.[Line Type] = 1
    ORDER BY t.[Test Codeunit], t.[Line No_];
    "
    PASSED=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME' AND [Line Type] = 1 AND [Result] = 2" | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)
    FAILED=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME' AND [Line Type] = 1 AND [Result] = 1" | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)
    SKIPPED=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME' AND [Line Type] = 1 AND [Result] NOT IN (1,2)" | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)
else
    run_sql "
    USE [CRONUS];
    SELECT CASE t.[Result] WHEN 2 THEN '  PASS' WHEN 1 THEN '  FAIL' WHEN 3 THEN '  EXEC' WHEN 0 THEN '  ----' ELSE '  ???' END,
           RTRIM(t.[Name]), t.[Test Codeunit]
    FROM [$METHOD_TABLE] t
    WHERE t.[Test Suite] = N'$SUITE_NAME' AND t.[Line Type] = 0
    ORDER BY t.[Test Codeunit];
    "
    PASSED=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME' AND [Line Type] = 0 AND [Result] IN (2,3)" | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)
    FAILED=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME' AND [Line Type] = 0 AND [Result] = 1" | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)
    SKIPPED=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$METHOD_TABLE] WHERE [Test Suite] = N'$SUITE_NAME' AND [Line Type] = 0 AND [Result] = 0" | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)
fi

TOTAL=$(( ${PASSED:-0} + ${FAILED:-0} + ${SKIPPED:-0} ))
echo ""
echo "Results: $TOTAL total, ${PASSED:-0} passed, ${FAILED:-0} failed, ${SKIPPED:-0} skipped"

if [ "${FAILED:-0}" -gt 0 ]; then exit 1; fi
if [ "${PASSED:-0}" -gt 0 ]; then exit 0; fi
echo "ERROR: No tests executed"
exit 1
