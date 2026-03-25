#!/usr/bin/env bash
# run-tests.sh — Run AL tests on a BC Linux container (AL-Go compatible)
#
# Usage:
#   ./scripts/run-tests.sh [options]
#
# Options:
#   --app <path>               .app file to publish and test
#   --codeunit-range <range>   Codeunit ID range filter (e.g. "90000" or "90000..90010")
#   --extension-id <guid>      Filter tests by extension ID (from app.json)
#   --company <name>           Company name (default: auto-detect from DB)
#   --host <host:port>         BC client services host (default: localhost:7085)
#   --sql-password <pw>        SA password (default: $SA_PASSWORD or Passw0rd123!)
#   --test-runner <id>         Test runner codeunit ID (default: 130451)
#   --timeout <minutes>        Per-codeunit test timeout (default: 3)
#   --sql-container <name>     SQL container name (default: bc-linux-sql-1)
#   --bc-container <name>      BC container name (default: bc-linux-bc-1)
#
# The script follows the same flow as AL-Go for GitHub / BcContainerHelper:
#   1. Discovers test codeunits and methods from the .app file
#   2. Populates the DEFAULT test suite via SQL
#   3. Opens page 130455 via WebSocket client services
#   4. Calls RunNextTest in a loop, reading TestResultJson after each call
#   5. Outputs per-method pass/fail results
#
# Prerequisites:
#   - BC container running with client services on port 7085
#   - SQL Server accessible via docker exec
#   - .NET 8 SDK (for building the TestRunner tool)

set -euo pipefail

# Defaults
BC_HOST="localhost:7085"
COMPANY=""
SQL_PASSWORD="${SA_PASSWORD:-Passw0rd123!}"
SQL_CONTAINER="bc-linux-sql-1"
BC_CONTAINER="bc-linux-bc-1"
TEST_RUNNER_ID=130451
CODEUNIT_RANGE=""
EXTENSION_ID=""
APP_FILE=""
TIMEOUT_MIN=3
AUTH="admin:Admin123!"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

while [[ $# -gt 0 ]]; do
    case $1 in
        --codeunit-range) CODEUNIT_RANGE="$2"; shift 2;;
        --extension-id) EXTENSION_ID="$2"; shift 2;;
        --company) COMPANY="$2"; shift 2;;
        --host) BC_HOST="$2"; shift 2;;
        --sql-password) SQL_PASSWORD="$2"; shift 2;;
        --test-runner) TEST_RUNNER_ID="$2"; shift 2;;
        --app) APP_FILE="$2"; shift 2;;
        --timeout) TIMEOUT_MIN="$2"; shift 2;;
        --sql-container) SQL_CONTAINER="$2"; shift 2;;
        --bc-container) BC_CONTAINER="$2"; shift 2;;
        *) echo "Unknown option: $1"; exit 1;;
    esac
done

# Helper: run SQL query via stdin (avoids quoting issues with $ in table names)
run_sql() {
    echo "$1" | docker exec -i "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$SQL_PASSWORD" -C -No -i /dev/stdin 2>&1
}

sql_count() {
    run_sql "$1" | grep -oP '^\s+\d+' | tr -d ' ' | tail -1
}

echo "=== BC Test Runner ==="

# --- Step 0: Verify SQL access ---
echo "Verifying SQL access..."
run_sql "SELECT 1" > /dev/null || { echo "ERROR: Cannot connect to SQL Server"; exit 1; }

# --- Step 1: Detect company ---
if [ -z "$COMPANY" ]; then
    COMPANY=$(run_sql "USE [CRONUS]; SELECT TOP 1 RTRIM([Name]) FROM [Company] WHERE [Name] != 'My Company' ORDER BY [Name]" \
        | grep -v "^-" | grep -v "^Changed" | grep -v "^$" | grep -v "^(" | grep -v "^\s*$" | head -1 | sed 's/ *$//')
    [ -z "$COMPANY" ] && COMPANY="CRONUS International Ltd."
fi
echo "Company: $COMPANY"

# Table name prefix (BC removes . from company name in table names)
TABLE_PREFIX="$(echo "${COMPANY}" | sed 's/\.//g')_\$"
TEST_SUITE_TABLE="${TABLE_PREFIX}AL Test Suite\$23de40a6-dfe8-4f80-80db-d70f83ce8caf"
TEST_METHOD_TABLE="${TABLE_PREFIX}Test Method Line\$23de40a6-dfe8-4f80-80db-d70f83ce8caf"

# --- Step 2: Publish app if specified ---
if [ -n "$APP_FILE" ]; then
    echo "Publishing $(basename "$APP_FILE")..."
    HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 120 -u "$AUTH" -X POST \
        -F "file=@$APP_FILE;type=application/octet-stream" \
        "http://localhost:7049/apps?SchemaUpdateMode=forcesync" 2>/dev/null || echo "000")
    echo "  Publish: HTTP $HTTP"

    # Extract extension ID from app if not specified
    if [ -z "$EXTENSION_ID" ]; then
        EXTENSION_ID=$(unzip -p "$APP_FILE" NavxManifest.xml 2>/dev/null | grep -oP 'App Id="\K[^"]+' || true)
        [ -n "$EXTENSION_ID" ] && echo "  Extension ID: $EXTENSION_ID"
    fi
fi

# --- Step 3: Discover test codeunits and methods from .app or DB ---
echo "Discovering tests..."

# Try to discover from .app file's SymbolReference.json (fastest, most complete)
TEST_JSON=""
if [ -n "$APP_FILE" ] && [ -f "$APP_FILE" ]; then
    TEST_JSON=$(unzip -p "$APP_FILE" SymbolReference.json 2>/dev/null || true)
fi

# Build codeunit range filter for SQL
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

# --- Step 4: Populate test suite ---
echo "Setting up test suite..."

# Ensure DEFAULT suite exists
run_sql "
USE [CRONUS];
IF NOT EXISTS (SELECT 1 FROM [$TEST_SUITE_TABLE] WHERE [Name] = N'DEFAULT')
    INSERT INTO [$TEST_SUITE_TABLE]
    ([Name], [Description], [Last Run], [Run Type], [Test Runner Id], [Stability Run],
     [CC Tracking Type], [CC Track All Sessions], [CC Exporter ID], [CC Coverage Map],
     [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES (N'DEFAULT', N'', '1753-01-01', 0, $TEST_RUNNER_ID, 0, 0, 0, 0, 0,
            NEWID(), GETUTCDATE(), '00000000-0000-0000-0000-000000000001',
            GETUTCDATE(), '00000000-0000-0000-0000-000000000001');
ELSE
    UPDATE [$TEST_SUITE_TABLE] SET [Test Runner Id] = $TEST_RUNNER_ID WHERE [Name] = N'DEFAULT';
" > /dev/null

# Clear existing test lines
run_sql "USE [CRONUS]; DELETE FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT'" > /dev/null

# Insert codeunit + function lines
if [ -n "$TEST_JSON" ]; then
    # Parse SymbolReference.json to discover test codeunits and their [Test] methods
    echo "  Parsing .app SymbolReference.json..."
    LINE_NO=10000
    while IFS='|' read -r CU_ID CU_NAME; do
        [ -z "$CU_ID" ] && continue
        # Apply range filter
        if [ -n "$CODEUNIT_RANGE" ] && [[ "$CODEUNIT_RANGE" != *".."* ]] && [ "$CU_ID" != "$CODEUNIT_RANGE" ]; then
            continue
        fi
        echo "  Codeunit $CU_ID: $CU_NAME"

        # Insert codeunit-level line
        run_sql "
        USE [CRONUS];
        SET IDENTITY_INSERT [$TEST_METHOD_TABLE] ON;
        INSERT INTO [$TEST_METHOD_TABLE]
        ([Test Suite],[Line No_],[Test Codeunit],[Name],[Function],[Run],[Result],[Line Type],
         [Start Time],[Finish Time],[Level],[Error Message Preview],[Error Code],
         [Error Message],[Error Call Stack],[Skip Logging Results],[Data Input Group Code],[Data Input],
         [\$systemId],[\$systemCreatedAt],[\$systemCreatedBy],[\$systemModifiedAt],[\$systemModifiedBy])
        VALUES (N'DEFAULT',$LINE_NO,$CU_ID,N'$CU_NAME',N'',1,0,0,
                '1753-01-01','1753-01-01',0,N'',N'',0x,0x,0,N'',0x,
                NEWID(),GETUTCDATE(),'00000000-0000-0000-0000-000000000001',
                GETUTCDATE(),'00000000-0000-0000-0000-000000000001');
        SET IDENTITY_INSERT [$TEST_METHOD_TABLE] OFF;
        " > /dev/null
        LINE_NO=$((LINE_NO + 10000))

        # Insert function-level lines for each [Test] method
        while IFS='|' read -r METHOD_NAME; do
            [ -z "$METHOD_NAME" ] && continue
            run_sql "
            USE [CRONUS];
            SET IDENTITY_INSERT [$TEST_METHOD_TABLE] ON;
            INSERT INTO [$TEST_METHOD_TABLE]
            ([Test Suite],[Line No_],[Test Codeunit],[Name],[Function],[Run],[Result],[Line Type],
             [Start Time],[Finish Time],[Level],[Error Message Preview],[Error Code],
             [Error Message],[Error Call Stack],[Skip Logging Results],[Data Input Group Code],[Data Input],
             [\$systemId],[\$systemCreatedAt],[\$systemCreatedBy],[\$systemModifiedAt],[\$systemModifiedBy])
            VALUES (N'DEFAULT',$LINE_NO,$CU_ID,N'$METHOD_NAME',N'$METHOD_NAME',1,0,1,
                    '1753-01-01','1753-01-01',1,N'',N'',0x,0x,0,N'',0x,
                    NEWID(),GETUTCDATE(),'00000000-0000-0000-0000-000000000001',
                    GETUTCDATE(),'00000000-0000-0000-0000-000000000001');
            SET IDENTITY_INSERT [$TEST_METHOD_TABLE] OFF;
            " > /dev/null
            LINE_NO=$((LINE_NO + 1))
            echo "    - $METHOD_NAME"
        done < <(echo "$TEST_JSON" | python3 -c "
import sys, json
data = json.loads(sys.stdin.read().lstrip('\ufeff'))
for cu in data.get('Codeunits', []):
    if cu.get('Id') == $CU_ID:
        for m in cu.get('Methods', []):
            attrs = [a.get('Name','') for a in m.get('Attributes',[])]
            if 'Test' in attrs:
                print(m['Name'])
" 2>/dev/null)
    done < <(echo "$TEST_JSON" | python3 -c "
import sys, json
data = json.loads(sys.stdin.read().lstrip('\ufeff'))
for cu in data.get('Codeunits', []):
    props = {p['Name']: p['Value'] for p in cu.get('Properties', [])}
    if props.get('Subtype') == 'Test':
        print(f\"{cu['Id']}|{cu['Name']}\")
" 2>/dev/null)
else
    # Fall back: discover test codeunits from Application Object Metadata in SQL
    echo "  Querying Application Object Metadata..."
    run_sql "
    USE [CRONUS];
    SET IDENTITY_INSERT [$TEST_METHOD_TABLE] ON;
    INSERT INTO [$TEST_METHOD_TABLE]
    ([Test Suite],[Line No_],[Test Codeunit],[Name],[Function],[Run],[Result],[Line Type],
     [Start Time],[Finish Time],[Level],[Error Message Preview],[Error Code],
     [Error Message],[Error Call Stack],[Skip Logging Results],[Data Input Group Code],[Data Input],
     [\$systemId],[\$systemCreatedAt],[\$systemCreatedBy],[\$systemModifiedAt],[\$systemModifiedBy])
    SELECT N'DEFAULT', ROW_NUMBER() OVER (ORDER BY ao.[Object ID]) * 10000,
           ao.[Object ID], CAST(ao.[Object Name] AS nvarchar(250)),
           N'', 1, 0, 0,
           '1753-01-01','1753-01-01',0,N'',N'',0x,0x,0,N'',0x,
           NEWID(),GETUTCDATE(),'00000000-0000-0000-0000-000000000001',
           GETUTCDATE(),'00000000-0000-0000-0000-000000000001'
    FROM [Application Object Metadata] ao
    WHERE ao.[Object Type] = 5 AND ao.[Object Subtype] = 'Test' $RANGE_FILTER;
    SET IDENTITY_INSERT [$TEST_METHOD_TABLE] OFF;
    " > /dev/null
fi

CU_COUNT=$(sql_count "USE [CRONUS]; SELECT COUNT(*) FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT' AND [Line Type] = 0")
FUNC_COUNT=$(sql_count "USE [CRONUS]; SELECT COUNT(*) FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT' AND [Line Type] = 1")
echo "  Test codeunits: ${CU_COUNT:-0}, Test methods: ${FUNC_COUNT:-0}"

if [ "${CU_COUNT:-0}" = "0" ]; then
    echo "ERROR: No test codeunits found"
    exit 1
fi

# --- Step 5: Run tests via WebSocket client services ---
echo ""
echo "=== Running Tests ==="
TESTRUNNER_DIR="$REPO_DIR/tools/TestRunner"

if [ ! -f "$TESTRUNNER_DIR/bin/Release/net8.0/TestRunner.dll" ]; then
    echo "Building TestRunner..."
    dotnet build "$TESTRUNNER_DIR" -c Release 2>&1 | tail -3
fi

# The TestRunner opens page 130455, calls ClearTestResults then RunNextTest.
# The session may die during test execution — results are read from SQL afterward.
timeout "${TIMEOUT_MIN}m" dotnet run --project "$TESTRUNNER_DIR" --no-build -c Release -- \
    --host "$BC_HOST" --company "$COMPANY" 2>&1 || true

sleep 2

# --- Step 6: Display results ---
echo ""
echo "=== Test Results ==="

# Function-level results (per test method)
if [ "${FUNC_COUNT:-0}" -gt 0 ]; then
    RESULTS=$(run_sql "
    USE [CRONUS];
    SELECT
        CASE t.[Result] WHEN 2 THEN '  PASS' WHEN 1 THEN '  FAIL' WHEN 0 THEN '  ----' ELSE '  SKIP' END AS [  ],
        RTRIM(t.[Function]) AS Method,
        t.[Test Codeunit] AS CU,
        RTRIM(CAST(t.[Error Message Preview] AS nvarchar(200))) AS Error
    FROM [$TEST_METHOD_TABLE] t
    WHERE t.[Test Suite] = N'DEFAULT' AND t.[Line Type] = 1
    ORDER BY t.[Test Codeunit], t.[Line No_];
    ")
    echo "$RESULTS"

    # BC result codes: 0=NotExecuted, 1=Failure, 2=Success, 3=Inconclusive
    PASSED=$(sql_count "USE [CRONUS]; SELECT COUNT(*) FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT' AND [Line Type] = 1 AND [Result] = 2")
    FAILED=$(sql_count "USE [CRONUS]; SELECT COUNT(*) FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT' AND [Line Type] = 1 AND [Result] = 1")
    SKIPPED=$(sql_count "USE [CRONUS]; SELECT COUNT(*) FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT' AND [Line Type] = 1 AND [Result] NOT IN (1,2)")
else
    # Codeunit-level only
    RESULTS=$(run_sql "
    USE [CRONUS];
    SELECT
        CASE t.[Result] WHEN 2 THEN '  PASS' WHEN 1 THEN '  FAIL' WHEN 3 THEN '  EXEC' WHEN 0 THEN '  ----' ELSE '  ???' END AS [  ],
        RTRIM(t.[Name]) AS Codeunit,
        t.[Test Codeunit] AS CU
    FROM [$TEST_METHOD_TABLE] t
    WHERE t.[Test Suite] = N'DEFAULT' AND t.[Line Type] = 0
    ORDER BY t.[Test Codeunit];
    ")
    echo "$RESULTS"

    PASSED=$(sql_count "USE [CRONUS]; SELECT COUNT(*) FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT' AND [Line Type] = 0 AND [Result] IN (2,3)")
    FAILED=$(sql_count "USE [CRONUS]; SELECT COUNT(*) FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT' AND [Line Type] = 0 AND [Result] = 1")
    SKIPPED=$(sql_count "USE [CRONUS]; SELECT COUNT(*) FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT' AND [Line Type] = 0 AND [Result] = 0")
fi

TOTAL=$(( ${PASSED:-0} + ${FAILED:-0} + ${SKIPPED:-0} ))
echo ""
echo "Results: $TOTAL total, ${PASSED:-0} passed, ${FAILED:-0} failed, ${SKIPPED:-0} skipped"

# Show failure details
if [ "${FAILED:-0}" -gt 0 ]; then
    echo ""
    echo "=== Failures ==="
    run_sql "
    USE [CRONUS];
    SELECT t.[Test Codeunit] AS CU, RTRIM(t.[Function]) AS Method,
           RTRIM(CAST(t.[Error Message Preview] AS nvarchar(500))) AS Error
    FROM [$TEST_METHOD_TABLE] t
    WHERE t.[Test Suite] = N'DEFAULT' AND t.[Result] = 1 AND t.[Function] != N''
    ORDER BY t.[Test Codeunit], t.[Line No_];
    "
    exit 1
fi

if [ "${PASSED:-0}" -gt 0 ]; then
    exit 0
elif [ "$TOTAL" -gt 0 ] && [ "${SKIPPED:-0}" -eq "$TOTAL" ]; then
    echo "WARNING: All tests skipped"
    exit 0
else
    echo "ERROR: No tests executed"
    exit 1
fi
