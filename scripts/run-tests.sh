#!/usr/bin/env bash
# run-tests.sh — Run AL tests on a BC Linux container
#
# Usage:
#   ./scripts/run-tests.sh [options]
#
# Options:
#   --codeunit-range <range>   Codeunit ID range (e.g. "90000" or "90000..90010")
#   --company <name>           Company name (default: first company in DB)
#   --host <host:port>         BC client services host (default: localhost:7085)
#   --sql <host>               SQL server host (default: localhost via docker exec)
#   --sql-password <pw>        SA password (default: from SA_PASSWORD env or Passw0rd123!)
#   --test-runner <id>         Test runner codeunit ID (default: 130451)
#   --app <path>               .app file to publish before running tests
#   --timeout <minutes>        Test execution timeout (default: 10)
#
# The script:
#   1. Publishes test framework apps (if not already installed)
#   2. Populates the DEFAULT test suite with the specified codeunit range via SQL
#   3. Triggers test execution via BC client services (WebSocket)
#   4. Reads results from SQL and outputs them
#
# Prerequisites:
#   - BC container running with client services on port 7085
#   - SQL Server accessible (either via docker exec or direct connection)
#   - .NET 8 SDK installed (for building the TestRunner tool)

set -euo pipefail

# Defaults
BC_HOST="localhost:7085"
COMPANY=""
SQL_HOST=""
SQL_PASSWORD="${SA_PASSWORD:-Passw0rd123!}"
SQL_CONTAINER="bc-linux-sql-1"
BC_CONTAINER="bc-linux-bc-1"
TEST_RUNNER_ID=130451
CODEUNIT_RANGE=""
APP_FILE=""
TIMEOUT_MIN=3
AUTH="admin:Admin123!"
INSTANCE=""
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

while [[ $# -gt 0 ]]; do
    case $1 in
        --codeunit-range) CODEUNIT_RANGE="$2"; shift 2;;
        --company) COMPANY="$2"; shift 2;;
        --host) BC_HOST="$2"; shift 2;;
        --sql) SQL_HOST="$2"; shift 2;;
        --sql-password) SQL_PASSWORD="$2"; shift 2;;
        --test-runner) TEST_RUNNER_ID="$2"; shift 2;;
        --app) APP_FILE="$2"; shift 2;;
        --timeout) TIMEOUT_MIN="$2"; shift 2;;
        --sql-container) SQL_CONTAINER="$2"; shift 2;;
        --bc-container) BC_CONTAINER="$2"; shift 2;;
        *) echo "Unknown option: $1"; exit 1;;
    esac
done

# Helper: run SQL query (uses stdin to avoid quoting issues with $ in table names)
run_sql() {
    local query="$1"
    if [ -n "$SQL_HOST" ]; then
        echo "$query" | sqlcmd -S "$SQL_HOST" -U sa -P "$SQL_PASSWORD" -C -No -i /dev/stdin 2>&1
    else
        echo "$query" | docker exec -i "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
            -S localhost -U sa -P "$SQL_PASSWORD" -C -No -i /dev/stdin 2>&1
    fi
}

# Step 0: Verify SQL access
echo "=== BC Test Runner ==="
echo "Verifying SQL access..."
run_sql "SELECT 1" > /dev/null || { echo "ERROR: Cannot connect to SQL Server"; exit 1; }

# Detect company name if not specified
if [ -z "$COMPANY" ]; then
    COMPANY=$(run_sql "USE [CRONUS]; SELECT TOP 1 RTRIM([Name]) FROM [Company] WHERE [Name] != 'My Company' ORDER BY [Name]" \
        | grep -v "^-" | grep -v "^Changed" | grep -v "^$" | grep -v "^(" | grep -v "^\s*$" | head -1 | sed 's/ *$//')
    [ -z "$COMPANY" ] && COMPANY="CRONUS International Ltd."
    echo "Using company: $COMPANY"
fi

# Table name prefix (BC uses company name in table names, replacing . with _)
TABLE_PREFIX="$(echo "${COMPANY}" | sed 's/\.//g')_\$"

# App IDs for the test framework tables
TEST_SUITE_TABLE="${TABLE_PREFIX}AL Test Suite\$23de40a6-dfe8-4f80-80db-d70f83ce8caf"
TEST_METHOD_TABLE="${TABLE_PREFIX}Test Method Line\$23de40a6-dfe8-4f80-80db-d70f83ce8caf"

# Step 1: Publish test app if specified
if [ -n "$APP_FILE" ]; then
    echo "Publishing $APP_FILE..."
    # Detect dev endpoint port (Kestrel strips paths)
    HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 30 -u "$AUTH" \
        "http://localhost:7049/apps" -X POST \
        -F "file=@$APP_FILE;type=application/octet-stream" 2>/dev/null || echo "000")
    if [ "$HTTP" = "200" ]; then
        echo "  Published: HTTP $HTTP"
    elif [ "$HTTP" = "422" ]; then
        echo "  Already installed or dependency error (HTTP 422)"
    else
        echo "  WARNING: Publish returned HTTP $HTTP"
    fi
fi

# Step 2: Ensure DEFAULT test suite exists
echo "Setting up test suite..."
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

# Step 3: Populate test method lines from installed test codeunits
echo "Populating test codeunits..."

# Clear existing method lines
run_sql "USE [CRONUS]; DELETE FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT'" > /dev/null

# Find test codeunits in the specified range
RANGE_FILTER=""
if [ -n "$CODEUNIT_RANGE" ]; then
    # Parse range like "90000" or "90000..90010"
    if [[ "$CODEUNIT_RANGE" == *".."* ]]; then
        FROM=$(echo "$CODEUNIT_RANGE" | cut -d. -f1)
        TO=$(echo "$CODEUNIT_RANGE" | cut -d. -f3)
        RANGE_FILTER="AND ao.[Object ID] BETWEEN $FROM AND $TO"
    else
        RANGE_FILTER="AND ao.[Object ID] = $CODEUNIT_RANGE"
    fi
fi

# Insert codeunit-level lines for test codeunits from Application Object Metadata
run_sql "
USE [CRONUS];
SET IDENTITY_INSERT [$TEST_METHOD_TABLE] ON;
INSERT INTO [$TEST_METHOD_TABLE]
([Test Suite], [Line No_], [Test Codeunit], [Name], [Function], [Run], [Result],
 [Line Type], [Start Time], [Finish Time], [Level], [Error Message Preview],
 [Error Code], [Error Message], [Error Call Stack], [Skip Logging Results],
 [Data Input Group Code], [Data Input],
 [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
SELECT
    N'DEFAULT',
    ROW_NUMBER() OVER (ORDER BY ao.[Object ID]) * 10000,
    ao.[Object ID],
    CAST(ao.[Object Name] AS nvarchar(250)),
    N'', 1, 0, 0,
    '1753-01-01', '1753-01-01', 0, N'', N'', 0x, 0x, 0, N'', 0x,
    NEWID(), GETUTCDATE(), '00000000-0000-0000-0000-000000000001',
    GETUTCDATE(), '00000000-0000-0000-0000-000000000001'
FROM [Application Object Metadata] ao
WHERE ao.[Object Type] = 5
  AND ao.[Object Subtype] = 'Test'
  $RANGE_FILTER;
SET IDENTITY_INSERT [$TEST_METHOD_TABLE] OFF;
" > /dev/null

# Count how many were inserted
INSERTED=$(run_sql "USE [CRONUS]; SELECT COUNT(*) FROM [$TEST_METHOD_TABLE] WHERE [Test Suite] = N'DEFAULT' AND [Line Type] = 0" \
    | grep -oP '^\s+\d+' | tr -d ' ' | tail -1)

echo "  Inserted $INSERTED test codeunit(s)"

if [ "${INSERTED:-0}" = "0" ]; then
    echo "ERROR: No test codeunits found in the specified range"
    echo "  Make sure your test app is published and contains codeunits with Subtype = Test"
    exit 1
fi

# Step 4: Run tests via WebSocket client services
echo ""
echo "=== Running tests ==="
TESTRUNNER_DIR="$REPO_DIR/tools/TestRunner"

if [ ! -f "$TESTRUNNER_DIR/bin/Release/net8.0/TestRunner.dll" ]; then
    echo "Building TestRunner..."
    dotnet build "$TESTRUNNER_DIR" -c Release 2>&1 | tail -3
fi

# Run test trigger with timeout. The session may die during test execution —
# that's expected with test isolation. Results are read from SQL afterward.
timeout "${TIMEOUT_MIN}m" dotnet run --project "$TESTRUNNER_DIR" --no-build -c Release -- \
    --host "$BC_HOST" --company "$COMPANY" 2>&1 || true

# Give BC a moment to write results to SQL
sleep 2

# Step 5: Read and display results from SQL
echo ""
echo "=== Test Results ==="
RESULTS=$(run_sql "
USE [CRONUS];
SELECT
    t.[Test Codeunit],
    RTRIM(t.[Name]) AS Name,
    t.[Result],
    CASE t.[Result]
        WHEN 0 THEN 'PENDING'
        WHEN 1 THEN 'PASS'
        WHEN 2 THEN 'FAIL'
        WHEN 3 THEN 'EXECUTED'
        ELSE 'UNKNOWN'
    END AS Status,
    CONVERT(varchar, t.[Start Time], 108) AS Started,
    RTRIM(CAST(t.[Error Message Preview] AS nvarchar(200))) AS Error
FROM [$TEST_METHOD_TABLE] t
WHERE t.[Test Suite] = N'DEFAULT'
  AND t.[Line Type] = 0
ORDER BY t.[Test Codeunit];
")
echo "$RESULTS"

# Count results (Result: 0=Pending, 1=Pass, 2=Fail, 3=Executed/Inconclusive)
PASSED=$(echo "$RESULTS" | grep -cE "PASS|EXECUTED" || true)
FAILED=$(echo "$RESULTS" | grep -c "FAIL" || true)
PENDING=$(echo "$RESULTS" | grep -c "PENDING" || true)
TOTAL=$((PASSED + FAILED + PENDING))

echo ""
echo "Total: $TOTAL | Passed: $PASSED | Failed: $FAILED | Pending: $PENDING"

if [ "$FAILED" -gt 0 ]; then
    echo ""
    echo "=== Failed Tests ==="
    run_sql "
    USE [CRONUS];
    SELECT t.[Test Codeunit], RTRIM(t.[Name]) AS Name,
           RTRIM(CAST(t.[Error Message Preview] AS nvarchar(500))) AS Error
    FROM [$TEST_METHOD_TABLE] t
    WHERE t.[Test Suite] = N'DEFAULT' AND t.[Result] = 2 AND t.[Line Type] = 0
    ORDER BY t.[Test Codeunit];
    "
    exit 1
fi

[ "$TOTAL" -gt 0 ] && exit 0 || exit 1
