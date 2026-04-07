#!/bin/bash
# Self-contained BC service tier entrypoint.
# Downloads artifacts, restores DB, configures BC, publishes test runner, starts server.
set -e
# Merge stdout into stderr so Docker captures all output immediately
# (stdout is pipe-buffered when PID 1 has no TTY; stderr is unbuffered)
exec 1>&2

ENTRYPOINT_START=$(date +%s)
echo "[entrypoint] Script started at $(date)"

# Helper: print a message prefixed with elapsed seconds since script start.
log_step() {
    local elapsed=$(( $(date +%s) - ENTRYPOINT_START ))
    echo "[entrypoint] [${elapsed}s] $*"
}

# Restore runtime DLLs from .bak if they exist (container restart recovery).
# Patch #15 renames runtime DLLs AFTER BC loads them into memory.
# On restart, BC needs the real DLLs to boot, so we restore first.
RUNTIME_DIR=$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.* 2>/dev/null | head -1)
if [ -n "$RUNTIME_DIR" ]; then
    RESTORE_COUNT=0
    for bak in "$RUNTIME_DIR"/*.dll.bak; do
        [ -f "$bak" ] || continue
        mv "$bak" "${bak%.bak}"
        RESTORE_COUNT=$((RESTORE_COUNT + 1))
    done
    [ $RESTORE_COUNT -gt 0 ] && log_step "Restored $RESTORE_COUNT runtime DLLs from .bak (restart recovery)"
fi

BC_TYPE="${BC_TYPE:-sandbox}"
BC_VERSION="${BC_VERSION:-27.5.46862.48004}"
BC_COUNTRY="${BC_COUNTRY:-w1}"
SA_PASSWORD="${SA_PASSWORD:-Passw0rd123!}"
BC_DB_PASSWORD="${BC_DB_PASSWORD:-Test1234}"
BC_DB_USER="${BC_DB_USER:-bctest}"
SQL_SERVER="${SQL_SERVER:-sql}"
ARTIFACTS="/bc/artifacts"
SERVICE_DIR="/bc/service"

# =============================================================================
# Step 1: Download artifacts if not already present
# =============================================================================
STEP1_START=$(date +%s)
if [ ! -f "$ARTIFACTS/app/manifest.json" ]; then
    if [ "$BC_ARTIFACT_URL" = "skip" ]; then
        log_step "Waiting for artifacts to be provided externally..."
        # Wait for BOTH app manifest AND platform ServiceTier to be present
        for i in $(seq 1 120); do
            [ -f "$ARTIFACTS/app/manifest.json" ] && \
            [ -d "$ARTIFACTS/platform/ServiceTier" ] && break
            sleep 2
        done
        [ -f "$ARTIFACTS/app/manifest.json" ] || { log_step "ERROR: App artifacts not provided"; exit 1; }
        [ -d "$ARTIFACTS/platform/ServiceTier" ] || { log_step "ERROR: Platform artifacts not provided"; ls -la "$ARTIFACTS/platform/" 2>/dev/null; exit 1; }
    elif [ -n "$BC_ARTIFACT_URL" ]; then
        log_step "Downloading BC from $BC_ARTIFACT_URL..."
        /bc/scripts/download-artifacts.sh "$BC_ARTIFACT_URL" "$ARTIFACTS"
    else
        log_step "Downloading BC $BC_TYPE $BC_VERSION ($BC_COUNTRY)..."
        /bc/scripts/download-artifacts.sh "$BC_TYPE" "$BC_VERSION" "$BC_COUNTRY" "$ARTIFACTS"
    fi
else
    log_step "Artifacts already cached."
fi
log_step "Step 1 (artifacts): $(($(date +%s) - STEP1_START))s"

# Read manifest
log_step "Disk: $(df -h /bc/artifacts | tail -1 | awk '{print $4 " free"}')"
log_step "Reading manifest..."
MANIFEST="$ARTIFACTS/app/manifest.json"
ls -la "$MANIFEST" || { log_step "FATAL: manifest.json not found at $MANIFEST"; exit 1; }
DB_FILE=$(python3 -c "import json; print(json.load(open('$MANIFEST')).get('database',''))")
LICENSE_FILE=$(python3 -c "import json; print(json.load(open('$MANIFEST')).get('licenseFile',''))")
PLATFORM_VERSION=$(python3 -c "import json; print(json.load(open('$MANIFEST'))['platform'])")
MAJOR_VERSION=$(echo "$PLATFORM_VERSION" | cut -d. -f1)
NAV_DIR="${MAJOR_VERSION}0"

log_step "Platform: $PLATFORM_VERSION, NAV dir: $NAV_DIR, DB: $DB_FILE"

# =============================================================================
# Step 2: Copy service tier to working directory (if not already set up)
# =============================================================================
STEP2_START=$(date +%s)
if [ ! -f "$SERVICE_DIR/Microsoft.Dynamics.Nav.Server.dll" ]; then
    log_step "Setting up service tier..."
    # Auto-detect service tier path (differs between versions: PFiles64 vs "program files")
    SRC=$(find "$ARTIFACTS/platform/ServiceTier" -name "Microsoft.Dynamics.Nav.Server.dll" -printf "%h\n" 2>/dev/null | head -1)
    if [ -z "$SRC" ] || [ ! -d "$SRC" ]; then
        log_step "ERROR: Service tier not found in $ARTIFACTS/platform/ServiceTier/"
        find "$ARTIFACTS/platform/ServiceTier" -maxdepth 4 -type d 2>/dev/null
        exit 1
    fi
    log_step "Found service tier at: $SRC"
    cp -r "$SRC/." "$SERVICE_DIR/"

    # Replace Reporting Service Windows PE with a Linux .NET stub.
    # The original is a Windows-only self-contained .NET app. Without a stub, BC gets
    # "Exec format error" which crashes test codeunits that use reports, causing hundreds
    # of test methods to be skipped. The stub is a minimal .NET app that starts and sleeps
    # — BC can start the process without crashing, and report tests fail gracefully.
    # Keep the Reporting Service .exe for BC startup (constructor probes assembly metadata).
    # After BC starts, replace it with a sleep stub so the watchdog sees a live process.
    # This is done in the background subshell that waits for BC to start (see below).

    # Create temp directory BC expects (detect NAV_DIR from actual path)
    NAV_DIR=$(echo "$SRC" | grep -oP '\d{3}(?=/Service)')
    [ -z "$NAV_DIR" ] && NAV_DIR="${MAJOR_VERSION}0"
    mkdir -p "/usr/share/Microsoft/Microsoft Dynamics NAV/$NAV_DIR/Server"

    # Patch CustomSettings.config
    CONFIG="$SERVICE_DIR/CustomSettings.config"
    sed -i \
        -e "s|DatabaseServer\" value=\"[^\"]*\"|DatabaseServer\" value=\"$SQL_SERVER\"|" \
        -e "s|DatabaseName\" value=\"[^\"]*\"|DatabaseName\" value=\"CRONUS\"|" \
        -e "s|DatabaseUserName\" value=\"[^\"]*\"|DatabaseUserName\" value=\"$BC_DB_USER\"|" \
        -e "s|ProtectedDatabasePassword\" value=\"[^\"]*\"|ProtectedDatabasePassword\" value=\"$BC_DB_PASSWORD\"|" \
        -e "s|ClientServicesCredentialType\" value=\"[^\"]*\"|ClientServicesCredentialType\" value=\"NavUserPassword\"|" \
        -e "s|DeveloperServicesEnabled\" value=\"[^\"]*\"|DeveloperServicesEnabled\" value=\"true\"|" \
        -e "s|TrustSQLServerCertificate\" value=\"[^\"]*\"|TrustSQLServerCertificate\" value=\"true\"|" \
        -e "s|ReportingServiceIsSideService\" value=\"[^\"]*\"|ReportingServiceIsSideService\" value=\"false\"|" \
        -e "s|ClientServicesPort\" value=\"[^\"]*\"|ClientServicesPort\" value=\"7085\"|" \
        -e "s|SOAPServicesPort\" value=\"[^\"]*\"|SOAPServicesPort\" value=\"7047\"|" \
        -e "s|ODataServicesPort\" value=\"[^\"]*\"|ODataServicesPort\" value=\"7048\"|" \
        -e "s|ManagementServicesPort\" value=\"[^\"]*\"|ManagementServicesPort\" value=\"7045\"|" \
        -e "s|ManagementApiServicesPort\" value=\"[^\"]*\"|ManagementApiServicesPort\" value=\"7086\"|" \
        -e "s|DeveloperServicesPort\" value=\"[^\"]*\"|DeveloperServicesPort\" value=\"7049\"|" \
        -e "s|ServerInstance\" value=\"[^\"]*\"|ServerInstance\" value=\"BC\"|" \
        -e "s|ExtensionAllowedTargetLevel\" value=\"[^\"]*\"|ExtensionAllowedTargetLevel\" value=\"OnPrem\"|" \
        "$CONFIG"

    # Ensure TenantEnvironmentType=Sandbox (required for test automation at platform level)
    if grep -q "TenantEnvironmentType" "$CONFIG"; then
        sed -i 's|TenantEnvironmentType" value="[^"]*"|TenantEnvironmentType" value="Sandbox"|' "$CONFIG"
    else
        sed -i '/<add key="TestAutomationEnabled"/a\  <add key="TenantEnvironmentType" value="Sandbox" />' "$CONFIG"
    fi
    if ! grep -q "TestAutomationEnabled" "$CONFIG"; then
        sed -i '/<\/appSettings>/i\  <add key="TestAutomationEnabled" value="true"/>' "$CONFIG"
    fi

    log_step "Service tier configured."
else
    log_step "Service tier already set up."
fi

# Copy WebClient DLLs needed for TestPage (page testability in tests).
# TestPageClient.dll depends on client framework DLLs that are only in WebClient.
WC_DIR=$(find "$ARTIFACTS/platform/WebClient" -name "Microsoft.Dynamics.Nav.Client.Actions.dll" -printf "%h\n" 2>/dev/null | head -1)
if [ -n "$WC_DIR" ] && [ ! -f "$SERVICE_DIR/Microsoft.Dynamics.Nav.Client.Actions.dll" ]; then
    for dll in Microsoft.Dynamics.Nav.Client.Actions.dll \
               Microsoft.Dynamics.Nav.Client.Controls.dll \
               Microsoft.Dynamics.Nav.Client.DataBinder.dll \
               Microsoft.Dynamics.Nav.Client.FormBuilder.dll \
               Microsoft.Dynamics.Nav.Client.Formatters.Decorators.dll; do
        [ -f "$WC_DIR/$dll" ] && cp "$WC_DIR/$dll" "$SERVICE_DIR/$dll"
    done
    echo "[entrypoint] Copied WebClient DLLs for TestPage support"
fi

# Override framework DLLs (must run every container start, not just first setup)
cp /bc/hook/System.Security.Principal.Windows.dll /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.*/
cp /bc/hook/Microsoft.AspNetCore.Server.HttpSys.dll /usr/share/dotnet/shared/Microsoft.AspNetCore.App/8.0.*/
# Replace stub DLLs in service dir
for stub in OpenTelemetry.Exporter.Geneva.dll Microsoft.Data.SqlClient.dll; do
    if [ -f "/bc/hook/$stub" ]; then
        [ -f "$SERVICE_DIR/$stub" ] && [ ! -f "$SERVICE_DIR/${stub}.orig" ] && cp "$SERVICE_DIR/$stub" "$SERVICE_DIR/${stub}.orig"
        cp "/bc/hook/$stub" "$SERVICE_DIR/$stub"
        log_step "Replaced $stub with stub/unix version"
    fi
done

# Create Win32 DLL symlinks in the service directory and .NET runtime dir.
# The StartupHook's ResolvingUnmanagedDll only fires on the Default ALC, but
# compiled AL extensions run in tenant ALCs. Native library search needs symlinks
# so the .NET loader finds libwin32_stubs.so for user32/kernel32/etc. directly.
STUB_SO=$(find /bc/hook -name "libwin32_stubs.so" 2>/dev/null | head -1)
if [ -n "$STUB_SO" ]; then
    for winlib in user32 kernel32 advapi32 Wintrust wintrust nclcsrts dhcpcsvc Netapi32 netapi32 ntdsapi rpcrt4 httpapi gdiplus; do
        ln -sf "$STUB_SO" "$SERVICE_DIR/${winlib}.dll" 2>/dev/null
    done
    log_step "Created Win32 DLL symlinks → libwin32_stubs.so"
fi
log_step "Step 2 (service tier setup): $(($(date +%s) - STEP2_START))s"

# =============================================================================
# Step 2b: Generate merged assemblies if not cached (first boot)
# =============================================================================
STEP2B_START=$(date +%s)
if [ ! -f "/bc/patched/netstandard-merged.dll" ] && [ -f /bc/tools/MergeNetstandard.dll ]; then
    log_step "Generating merged assemblies (first boot)..."
    BASE_DIR=/bc PLATFORM_DIR="$ARTIFACTS/platform" \
        dotnet /bc/tools/MergeNetstandard.dll 2>&1 | tail -5
    log_step "Merged assemblies generated in $(($(date +%s) - STEP2B_START))s"
fi

# Apply patched DLLs (Cecil-modified to fix Linux-specific bugs)
# Patch #14: CodeAnalysis.dll — fix IsTypeForwardingCircular NullRef on Linux
#   BC's Cecil type loader crashes following type-forwarding chains in netstandard.dll.
#   The patched DLL returns false for circular check, allowing forwarding to work.
if [ -f /bc/patched/Microsoft.Dynamics.Nav.CodeAnalysis.dll ]; then
    cp /bc/patched/Microsoft.Dynamics.Nav.CodeAnalysis.dll "$SERVICE_DIR/Microsoft.Dynamics.Nav.CodeAnalysis.dll"
    [ -d "$SERVICE_DIR/Admin" ] && cp /bc/patched/Microsoft.Dynamics.Nav.CodeAnalysis.dll "$SERVICE_DIR/Admin/Microsoft.Dynamics.Nav.CodeAnalysis.dll"
    log_step "Applied patched CodeAnalysis.dll (Patch #14: type forwarding fix)"
fi
# Patch Mono.Cecil's CheckFileName to not throw on empty file paths
if [ -f /bc/patched/Mono.Cecil.dll ]; then
    cp /bc/patched/Mono.Cecil.dll "$SERVICE_DIR/Mono.Cecil.dll"
    [ -d "$SERVICE_DIR/Admin" ] && cp /bc/patched/Mono.Cecil.dll "$SERVICE_DIR/Admin/Mono.Cecil.dll"
    log_step "Applied patched Mono.Cecil.dll (CheckFileName empty path fix)"
fi

# Patch TestPage support: fix assembly loading and async deadlock
# Nav.Ncl.dll: Assembly.Load (version-qualified) → Assembly.LoadFrom (file path)
# TestPageClient.dll: CommunicationBroker Async=true → false (prevents dispatcher deadlock)
if [ -f /bc/tools/patcher/PatchNclTestPage.dll ]; then
    PATCHER="dotnet /bc/tools/patcher/PatchNclTestPage.dll"
    if $PATCHER ncl "$SERVICE_DIR/Microsoft.Dynamics.Nav.Ncl.dll" 2>&1 | tail -1; then
        log_step "Patched Nav.Ncl.dll (TestPage Assembly.Load → LoadFrom)"
    fi
    if $PATCHER client "$SERVICE_DIR/Microsoft.Dynamics.Nav.Client.TestPageClient.dll" 2>&1 | tail -1; then
        log_step "Patched TestPageClient.dll (Async=true → false)"
    fi
    if $PATCHER types "$SERVICE_DIR/Microsoft.Dynamics.Nav.Types.dll" 2>&1 | tail -1; then
        log_step "Patched Nav.Types.dll (TestClientProxy Assembly.Load → LoadFrom)"
    fi
fi

# Fix Add-Ins directory case (Linux is case-sensitive, BC expects "Add-Ins")
if [ -d "$SERVICE_DIR/Add-ins" ] && [ ! -d "$SERVICE_DIR/Add-Ins" ]; then
    mv "$SERVICE_DIR/Add-ins" "$SERVICE_DIR/Add-Ins"
    log_step "Renamed Add-ins → Add-Ins (case-sensitivity fix)"
fi
ADDINS_DIR="$SERVICE_DIR/Add-Ins"

# Patch #16: Deploy assemblies for server-side compiler type resolution.
# Three layers deployed to Add-Ins in order:
#   1. Base refasm: .NET 8 reference assemblies (full type metadata, no R2R)
#   2. Forwarding assemblies: redirect refasm types → netstandard-merged.dll
#      (eliminates type identity duplication between AL code and BC DLL params)
#   3. Merged assemblies: netstandard/OpenXml/Drawing/Core with resolved type-forwards
#   4. DrawingStub: compile-time System.Drawing.Common with framework type refs
if [ ! -f "$ADDINS_DIR/System.Runtime.dll" ] && [ -d /bc/refasm ]; then
    # Layer 1: base reference assemblies
    cp /bc/refasm/*.dll "$ADDINS_DIR/" 2>/dev/null || true
    log_step "Copied .NET reference assemblies to Add-Ins ($(ls /bc/refasm/*.dll 2>/dev/null | wc -l) files)"

    # Layer 2: forwarding assemblies (override refasm with type-forwards to netstandard)
    if [ -d /bc/patched/refasm-forwarding ]; then
        cp /bc/patched/refasm-forwarding/*.dll "$ADDINS_DIR/" 2>/dev/null || true
        log_step "Applied forwarding assemblies ($(ls /bc/patched/refasm-forwarding/*.dll 2>/dev/null | wc -l) files)"
    fi

    # Layer 3: merged assemblies (deploy with original filenames)
    for merged in netstandard:netstandard-merged DocumentFormat.OpenXml:DocumentFormat.OpenXml-merged System.Drawing:System.Drawing-merged System.Core:System.Core-merged; do
        TARGET="${merged%%:*}.dll"
        SRC="${merged##*:}.dll"
        if [ -f "/bc/patched/$SRC" ]; then
            cp "/bc/patched/$SRC" "$ADDINS_DIR/$TARGET"
        fi
    done
    log_step "Applied merged type-forward assemblies"

    # Layer 4: DrawingStub for compile-time (uses framework Color/Rectangle refs)
    if [ -f /bc/addins-overlay/System.Drawing.Common.dll ]; then
        cp /bc/addins-overlay/System.Drawing.Common.dll "$ADDINS_DIR/System.Drawing.Common.dll"
        log_step "Applied DrawingStub to Add-Ins (compile-time)"
    fi

    # Layer 5: MockTest.dll for test framework (required by Test Library)
    # Try from image overlay first, fall back to artifacts
    if [ -f /bc/addins-overlay/MockTest.dll ]; then
        cp /bc/addins-overlay/MockTest.dll "$ADDINS_DIR/MockTest.dll"
        log_step "Copied MockTest.dll to Add-Ins (from image)"
    else
        MOCK_DLL=$(find "$ARTIFACTS/platform" -path "*/Mock Assemblies/MockTest.dll" 2>/dev/null | head -1)
        if [ -n "$MOCK_DLL" ]; then
            cp "$MOCK_DLL" "$ADDINS_DIR/MockTest.dll"
            log_step "Copied MockTest.dll to Add-Ins (from artifacts)"
        fi
    fi
fi


# =============================================================================
# Step 3: Wait for SQL Server and set up database
# =============================================================================
export PATH="$PATH:/opt/mssql-tools18/bin"

log_step "Waiting for SQL Server..."
until sqlcmd -S "$SQL_SERVER" -U sa -P "$SA_PASSWORD" -C -No -Q "SELECT 1" &>/dev/null; do
    sleep 2
done
log_step "SQL Server ready."
STEP3_START=$(date +%s)

SQLCMD="sqlcmd -S $SQL_SERVER -U sa -P $SA_PASSWORD -C -No"

# Create login
$SQLCMD -Q "
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '$BC_DB_USER')
    CREATE LOGIN [$BC_DB_USER] WITH PASSWORD = '$BC_DB_PASSWORD', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
ELSE
    ALTER LOGIN [$BC_DB_USER] WITH PASSWORD = '$BC_DB_PASSWORD', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
ALTER SERVER ROLE sysadmin ADD MEMBER [$BC_DB_USER];
"

# Restore database if needed
DB_EXISTS=$($SQLCMD -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name='CRONUS'" 2>/dev/null | tr -d '[:space:]')
if [ "$DB_EXISTS" != "1" ]; then
    log_step "Restoring CRONUS database..."
    BAK_PATH="$ARTIFACTS/app/$DB_FILE"
    if [ ! -f "$BAK_PATH" ]; then
        log_step "ERROR: Database backup not found at $BAK_PATH"
        exit 1
    fi

    # Get logical file names (may contain spaces, e.g. "Demo Database BC (29-0)_Data")
    # Use tab-separated output to reliably parse
    DATA_NAME=$($SQLCMD -h -1 -s $'\t' -W -Q "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK='$BAK_PATH'" 2>/dev/null | head -1 | cut -f1)
    LOG_NAME=$($SQLCMD -h -1 -s $'\t' -W -Q "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK='$BAK_PATH'" 2>/dev/null | head -2 | tail -1 | cut -f1)
    log_step "DB logical names: data='$DATA_NAME' log='$LOG_NAME'"

    $SQLCMD -Q "
        RESTORE DATABASE [CRONUS] FROM DISK='$BAK_PATH'
        WITH MOVE '$DATA_NAME' TO '/var/opt/mssql/data/CRONUS.mdf',
             MOVE '$LOG_NAME' TO '/var/opt/mssql/data/CRONUS_log.ldf'
    "
    log_step "CRONUS restored."
else
    log_step "CRONUS already exists."
fi

SQLCMD_DB="sqlcmd -S $SQL_SERVER -U $BC_DB_USER -P $BC_DB_PASSWORD -d CRONUS -C -No"

# Encryption key
$SQLCMD_DB -Q "
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '\$ndo\$publicencryptionkey')
    CREATE TABLE [dbo].[\$ndo\$publicencryptionkey] ([id] INT NOT NULL PRIMARY KEY, [publickey] NVARCHAR(1024) NOT NULL);
DELETE FROM [dbo].[\$ndo\$publicencryptionkey] WHERE [id] = 0;
INSERT INTO [dbo].[\$ndo\$publicencryptionkey] ([id], [publickey]) VALUES (0,
N'<RSAKeyValue><Modulus>xbzyD+SGxykyAv82XOEFtDzWEIok0MM5SAc+CS6Mq0W5LwiyXeakWyblq1XgYi3CDu700986ZVRi4KJjruZlzBeZ7IWXD4lEEpTCRuqoxasRTnwVpyVqGuHclJAnUpjeBS6HvaS/iesYWwxZcmlsmzJHvF3hXdDmLj+8GSKgo4IhschPCIpnoH8+FREX++VpwfZH1ejMk5Izds/ZI70Xc/OWfRfaYy3rtCFeZQ1R5T1AhlNJDgpn0a1oP86F8yDGYawB2GJKIewdcWE8usu4QesrFnlS1g/IJcFXe71/TiJjryqRJPk8ze3Jh9+atx57OnI4R3QvuM/lQ7YoN1RVjw==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>');
" 2>/dev/null

# License
if [ -n "$LICENSE_FILE" ] && [ -f "$ARTIFACTS/app/$LICENSE_FILE" ]; then
    $SQLCMD_DB -Q "
    UPDATE [\$ndo\$dbproperty]
    SET [license] = (SELECT BulkColumn FROM OPENROWSET(BULK '$ARTIFACTS/app/$LICENSE_FILE', SINGLE_BLOB) AS f);
    " 2>/dev/null
    log_step "License imported."
fi

# Sandbox tenant type
$SQLCMD_DB -Q "UPDATE [\$ndo\$tenantproperty] SET tenanttype = 1;" 2>/dev/null

# SQL performance tuning for CI/CD — disable safety overhead not needed for test runs
# ALTER DATABASE must run from master context, not from within the target database
$SQLCMD -Q "
ALTER DATABASE CRONUS SET QUERY_STORE = OFF;
ALTER DATABASE CRONUS SET AUTO_UPDATE_STATISTICS OFF;
ALTER DATABASE CRONUS SET AUTO_UPDATE_STATISTICS_ASYNC OFF;
ALTER DATABASE CRONUS SET AUTO_CREATE_STATISTICS OFF;
ALTER DATABASE CRONUS SET PAGE_VERIFY NONE;
ALTER DATABASE CRONUS SET DELAYED_DURABILITY = FORCED;
" 2>/dev/null
# Disable change tracking (must disable on tables first, from CRONUS context)
$SQLCMD_DB -Q "
DECLARE @sql NVARCHAR(MAX) = '';
SELECT @sql = @sql + 'ALTER TABLE [' + s.name + '].[' + t.name + '] DISABLE CHANGE_TRACKING;'
FROM sys.change_tracking_tables ct
JOIN sys.tables t ON ct.object_id = t.object_id
JOIN sys.schemas s ON t.schema_id = s.schema_id;
IF LEN(@sql) > 0 EXEC sp_executesql @sql;
" 2>/dev/null
$SQLCMD -Q "ALTER DATABASE CRONUS SET CHANGE_TRACKING = OFF;" 2>/dev/null
log_step "SQL tuned for CI/CD (query store, stats, page verify, change tracking OFF)"

# Clear pre-installed apps before BC starts.
# BC_CLEAR_ALL_APPS=true:      clear ALL apps, republish ALL dynamically after NST starts (~300s)
# BC_CLEAR_ALL_APPS=deps-only: clear ALL apps, republish ONLY test framework after NST starts (~30s)
#   (caller is responsible for publishing the actual dependency chain via dev endpoint)
# BC_CLEAR_ALL_APPS=selective: keep only apps whose App ID is in BC_KEEP_APP_IDS + test framework
#   (BC_KEEP_APP_IDS=comma-separated lowercase app IDs from sort-apps-by-deps.py)
# BC_CLEAR_ALL_APPS=none:      don't clear ANY apps — keep stock extensions intact (~0s)
#   (caller publishes only what it needs to override via forcesync)
# Default (false):             only clear test framework apps (Test Runner, Library Assert, etc.)
if [ "${BC_CLEAR_ALL_APPS:-false}" = "none" ]; then
    log_step "BC_CLEAR_ALL_APPS=none: keeping all pre-installed extensions"
elif [ "${BC_CLEAR_ALL_APPS:-false}" = "selective" ]; then
    # Selective clearing: keep only apps in BC_KEEP_APP_IDS + hardcoded test framework/system apps.
    # This is much faster than clearing everything because BC only compiles ~10 kept apps on startup.
    TOTAL_BEFORE=$($SQLCMD_DB -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM [Published Application];" 2>/dev/null | tr -d '[:space:]')
    log_step "BC_CLEAR_ALL_APPS=selective: $TOTAL_BEFORE apps installed, filtering..."

    # Build the keep list from BC_KEEP_APP_IDS only.
    # Test framework apps are NOT kept — they're deleted and freshly installed
    # via dev endpoint after NST starts (ensures proper installation state).
    KEEP_IDS="${BC_KEEP_APP_IDS:-}"

    # Build SQL IN clause from BC_KEEP_APP_IDS (comma-separated lowercase GUIDs)
    KEEP_SQL=""
    if [ -n "$KEEP_IDS" ]; then
        KEEP_SQL=$(echo "$KEEP_IDS" | tr ',' '\n' | sed "s/^.*$/'\0'/" | paste -sd,)
    fi

    # Delete apps NOT in the keep list
    # Write SQL to a temp file to avoid shell quoting issues with large multi-statement batches
    # Write SQL to temp file — avoids shell quoting issues with large multi-statement batches.
    # Use a temp table to hold the Package IDs to keep (avoids repeating the subquery).
    SELECTIVE_SQL="/tmp/selective-clear.sql"
    if [ -n "$KEEP_SQL" ]; then
        cat > "$SELECTIVE_SQL" << SQLEOF
SET NOCOUNT ON;
SELECT 'KEEP: ' + [Name] FROM [Published Application]
WHERE LOWER(CONVERT(VARCHAR(36), [ID])) IN ($KEEP_SQL);

SELECT [Package ID] INTO #keep FROM [Published Application]
WHERE LOWER(CONVERT(VARCHAR(36), [ID])) IN ($KEEP_SQL);

DELETE FROM [NAV App Installed App] WHERE [Package ID] NOT IN (SELECT [Package ID] FROM #keep);
DELETE FROM [NAV App Tenant App] WHERE [App Package ID] NOT IN (SELECT [Package ID] FROM #keep);
DELETE FROM [NAV App Dependencies] WHERE [Package ID] NOT IN (SELECT [Package ID] FROM #keep);
DELETE FROM [NAV App Published App] WHERE [Package ID] NOT IN (SELECT [Package ID] FROM #keep);
DELETE FROM [Installed Application] WHERE [Package ID] NOT IN (SELECT [Package ID] FROM #keep);
DELETE FROM [Inplace Installed Application] WHERE [Runtime Package ID] NOT IN (SELECT [Package ID] FROM #keep);
DELETE FROM [Published Application] WHERE [Package ID] NOT IN (SELECT [Package ID] FROM #keep);
DROP TABLE #keep;
SQLEOF
    else
        log_step "BC_KEEP_APP_IDS is empty — clearing ALL apps"
        cat > "$SELECTIVE_SQL" << SQLEOF
SET NOCOUNT ON;
DELETE FROM [NAV App Installed App];
DELETE FROM [NAV App Tenant App];
DELETE FROM [NAV App Dependencies];
DELETE FROM [NAV App Published App];
DELETE FROM [Installed Application];
DELETE FROM [Inplace Installed Application];
DELETE FROM [Published Application];
SQLEOF
    fi
    REMOVED=$($SQLCMD_DB -h -1 -W -i "$SELECTIVE_SQL" 2>&1) || true
    # Find any apps that are PUBLISHED but NOT INSTALLED for any tenant
    # and wipe them so the install-for-tenant loop after NST starts can
    # republish them as proper Dev/tenant deployments.
    #
    # Background: BC's sandbox image ships several test framework apps
    # (Test Runner, Library Assert, Library Variable Storage, Permissions
    # Mock, Any) in a "published as Global, not installed for tenant"
    # state. The dev endpoint forcesync POST cannot promote a published-
    # as-Global app to a tenant install (the DependencyPublishingOption
    # parameter rejects 'Install' as a value), so the only way to get
    # these apps tenant-installed via the dev endpoint is to wipe them
    # from [Published Application] first and then re-POST them.
    #
    # This used to be a hand-coded list of 5 names. Discovering the set
    # dynamically by querying [NAV App Installed App] is more robust:
    # if a future BC version ships a different set of "published but
    # not tenant-installed" apps, the query picks them up automatically.
    WIPE_SQL="/tmp/wipe-stuck.sql"
    cat > "$WIPE_SQL" << 'SQLEOF'
SET NOCOUNT ON;
-- Stuck apps = published but never installed for any tenant
SELECT [Package ID] INTO #stuck
FROM [Published Application] pa
WHERE NOT EXISTS (
    SELECT 1 FROM [NAV App Installed App] iaa
    WHERE iaa.[Package ID] = pa.[Package ID]
);

SELECT 'WIPE-STUCK: ' + pa.[Name]
FROM [Published Application] pa
WHERE pa.[Package ID] IN (SELECT [Package ID] FROM #stuck);

DELETE FROM [NAV App Installed App] WHERE [Package ID] IN (SELECT [Package ID] FROM #stuck);
DELETE FROM [NAV App Tenant App] WHERE [App Package ID] IN (SELECT [Package ID] FROM #stuck);
DELETE FROM [NAV App Dependencies] WHERE [Package ID] IN (SELECT [Package ID] FROM #stuck);
DELETE FROM [NAV App Published App] WHERE [Package ID] IN (SELECT [Package ID] FROM #stuck);
DELETE FROM [Installed Application] WHERE [Package ID] IN (SELECT [Package ID] FROM #stuck);
DELETE FROM [Inplace Installed Application] WHERE [Runtime Package ID] IN (SELECT [Package ID] FROM #stuck);
DELETE FROM [Published Application] WHERE [Package ID] IN (SELECT [Package ID] FROM #stuck);
DROP TABLE #stuck;
SQLEOF
    STUCK_OUT=$($SQLCMD_DB -h -1 -W -i "$WIPE_SQL" 2>&1) || true
    log_step "Stuck-publish wipe result:"
    echo "$STUCK_OUT" | while read -r line; do
        [ -n "$line" ] && echo "[entrypoint]   $line" || true
    done
    rm -f "$WIPE_SQL"
    log_step "Selective clear result:"
    echo "$REMOVED" | while read -r line; do
        [ -n "$line" ] && echo "[entrypoint]   $line" || true
    done
    TOTAL_AFTER=$($SQLCMD_DB -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM [Published Application];" 2>/dev/null | tr -d '[:space:]')
    TOTAL_AFTER="${TOTAL_AFTER:-0}"
    log_step "Selective clear: ${TOTAL_BEFORE:-?} → ${TOTAL_AFTER} apps (removed $((${TOTAL_BEFORE:-0} - ${TOTAL_AFTER})))"
    # Export keep list for R2R pre-seed filtering
    export BC_KEEP_APP_IDS_FOR_R2R="$KEEP_IDS"
elif [ "${BC_CLEAR_ALL_APPS:-false}" = "true" ] || [ "${BC_CLEAR_ALL_APPS:-false}" = "deps-only" ]; then
    # Snapshot the full list of published apps + dependency graph BEFORE clearing,
    # so we can republish them after NST starts.
    log_step "BC_CLEAR_ALL_APPS=true: snapshotting installed extensions..."
    APPS_SNAPSHOT="/tmp/bc-apps-to-republish.tsv"
    DEPS_SNAPSHOT="/tmp/bc-app-deps.tsv"

    # Save: PackageID | Name | Publisher | VersionMajor.Minor.Build.Revision
    $SQLCMD_DB -h -1 -s $'\t' -W -Q "
    SET NOCOUNT ON;
    SELECT
        CONVERT(VARCHAR(36), [Package ID]) AS PackageID,
        [Name],
        [Publisher],
        CAST([Version Major] AS VARCHAR) + '.' +
        CAST([Version Minor] AS VARCHAR) + '.' +
        CAST([Version Build] AS VARCHAR) + '.' +
        CAST([Version Revision] AS VARCHAR) AS Version
    FROM [Published Application]
    ORDER BY [Name];
    " 2>/dev/null > "$APPS_SNAPSHOT" || true
    APP_COUNT=$(grep -c $'\t' "$APPS_SNAPSHOT" 2>/dev/null || echo 0)
    log_step "Snapshotted $APP_COUNT published extensions"

    # Save dependency graph: DependentPackageID | DependsOnPackageID
    $SQLCMD_DB -h -1 -s $'\t' -W -Q "
    SET NOCOUNT ON;
    SELECT
        CONVERT(VARCHAR(36), [Package ID]) AS PackageID,
        CONVERT(VARCHAR(36), [Dependency Package ID]) AS DependsOnPackageID
    FROM [NAV App Dependencies];
    " 2>/dev/null > "$DEPS_SNAPSHOT" || true

    $SQLCMD_DB -Q "
    DELETE FROM [NAV App Installed App];
    DELETE FROM [NAV App Tenant App];
    DELETE FROM [NAV App Dependencies];
    DELETE FROM [NAV App Published App];
    DELETE FROM [Published Application];
    DELETE FROM [Installed Application];
    DELETE FROM [Inplace Installed Application];
    " 2>/dev/null
    log_step "Cleared ALL pre-installed apps (BC_CLEAR_ALL_APPS=true)"
else
    $SQLCMD_DB -Q "
    DELETE FROM [Installed Application] WHERE [Package ID] IN (SELECT [Package ID] FROM [Published Application] WHERE [Name] IN (N'Test Runner',N'Library Assert',N'Library Variable Storage',N'Permissions Mock',N'Any'));
    DELETE FROM [NAV App Installed App] WHERE [Name] IN (N'Test Runner',N'Library Assert',N'Library Variable Storage',N'Permissions Mock',N'Any');
    DELETE FROM [Published Application] WHERE [Name] IN (N'Test Runner',N'Library Assert',N'Library Variable Storage',N'Permissions Mock',N'Any');
    " 2>/dev/null
    log_step "Cleared test framework global entries (will re-publish via dev endpoint)"
fi

# Service user for scripting/OData/dev endpoint (password hash for Admin123! with GUID 00000000-0000-0000-0000-000000000001)
# Named BCRUNNER (not ADMIN) so tests can freely create/delete/disable an "ADMIN" user.
USER_GUID='00000000-0000-0000-0000-000000000001'
PASSWORD_HASH='aXD91GRctWiXaqXeWbXhxQ==-V3'
$SQLCMD_DB -Q "
IF NOT EXISTS (SELECT 1 FROM [User] WHERE [User Name] = 'BCRUNNER')
BEGIN
    INSERT INTO [User] ([User Security ID], [User Name], [Full Name], [State], [Expiry Date],
        [Windows Security ID], [Change Password], [License Type], [Authentication Email],
        [Contact Email], [Exchange Identifier], [Application ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'BCRUNNER', N'BC Runner', 0, '2099-12-31', N'S-1-5-21-2074085148-119339936-2019613796-1001', 0, 0, N'', N'', N'',
        '00000000-0000-0000-0000-000000000000',
        NEWID(), GETUTCDATE(), '$USER_GUID', GETUTCDATE(), '$USER_GUID');
    INSERT INTO [User Property] ([User Security ID], [Password], [Name Identifier],
        [Authentication Key], [WebServices Key], [WebServices Key Expiry Date],
        [Authentication Object ID], [Directory Role ID], [Telemetry User ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'$PASSWORD_HASH', N'', N'', N'', '1753-01-01', N'', N'', '$USER_GUID',
        NEWID(), GETUTCDATE(), '$USER_GUID', GETUTCDATE(), '$USER_GUID');
    INSERT INTO [Access Control] ([User Security ID], [Role ID], [Company Name], [Scope], [App ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'SUPER', N'', 0, '00000000-0000-0000-0000-000000000000',
        NEWID(), GETUTCDATE(), '$USER_GUID', GETUTCDATE(), '$USER_GUID');
END
" 2>/dev/null

# Background SUPER user — safety net so tests can freely disable/delete users
# without violating the "at least one enabled SUPER user" platform constraint.
# without violating the "at least one enabled SUPER user" platform constraint.
# This user has no password and is never used for authentication.
SVC_GUID='00000000-0000-0000-0000-000000000002'
$SQLCMD_DB -Q "
IF NOT EXISTS (SELECT 1 FROM [User] WHERE [User Name] = 'YOURBC-SERVICEUSER')
BEGIN
    INSERT INTO [User] ([User Security ID], [User Name], [Full Name], [State], [Expiry Date],
        [Windows Security ID], [Change Password], [License Type], [Authentication Email],
        [Contact Email], [Exchange Identifier], [Application ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$SVC_GUID', N'YOURBC-SERVICEUSER', N'BC Service', 0, '2099-12-31', N'S-1-5-21-572246948-1269080603-559786204-1001', 0, 0, N'', N'', N'',
        '00000000-0000-0000-0000-000000000000',
        NEWID(), GETUTCDATE(), '$SVC_GUID', GETUTCDATE(), '$SVC_GUID');
    INSERT INTO [User Property] ([User Security ID], [Password], [Name Identifier],
        [Authentication Key], [WebServices Key], [WebServices Key Expiry Date],
        [Authentication Object ID], [Directory Role ID], [Telemetry User ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$SVC_GUID', N'', N'', N'', N'', '1753-01-01', N'', N'', '$SVC_GUID',
        NEWID(), GETUTCDATE(), '$SVC_GUID', GETUTCDATE(), '$SVC_GUID');
    INSERT INTO [Access Control] ([User Security ID], [Role ID], [Company Name], [Scope], [App ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$SVC_GUID', N'SUPER', N'', 0, '00000000-0000-0000-0000-000000000000',
        NEWID(), GETUTCDATE(), '$SVC_GUID', GETUTCDATE(), '$SVC_GUID');
END
" 2>/dev/null
log_step "Database ready (BCRUNNER / Admin123!). Step 3 (DB setup): $(($(date +%s) - STEP3_START))s"

# =============================================================================
# Step 4: Start BC server in background, publish test runner, then wait
# =============================================================================
cd "$SERVICE_DIR"
# Verify SQL is still accessible before starting BC
log_step "Verifying SQL connection..."
if sqlcmd -S "$SQL_SERVER" -U "$BC_DB_USER" -P "$BC_DB_PASSWORD" -d CRONUS -C -No -Q "SELECT 1" &>/dev/null; then
    log_step "SQL connection verified."
else
    log_step "ERROR: SQL connection failed! Retrying..."
    sleep 5
    sqlcmd -S "$SQL_SERVER" -U "$BC_DB_USER" -P "$BC_DB_PASSWORD" -d CRONUS -C -No -Q "SELECT 1" || {
        log_step "FATAL: Cannot connect to SQL"
        exit 1
    }
fi

log_step "Config check:"
grep -E "DatabaseServer|DatabaseName|DatabaseUserName|ProtectedDatabase" "$SERVICE_DIR/CustomSettings.config" | head -5
log_step "Pre-seeding R2R extension DLL cache..."
# BC's NST compiles all published extensions from AL source on first startup (~190s without pre-seeding).
# R2R (.app) packages already contain the pre-compiled DLLs under publishedartifacts/.
# By extracting them into the assembly cache before NST starts, the NST finds them and can skip most
# of that work — reducing first-start time from ~190s to ~110s overall (AL compilation sub-phase drops to <10s).
PLATFORM_VER=$(python3 -c "import json; print(json.load(open('$ARTIFACTS/app/manifest.json'))['platform'])" 2>/dev/null || true)
if [ -n "$PLATFORM_VER" ]; then
    INSTANCE=$(grep -oP 'ServerInstance" value="\K[^"]+' "$SERVICE_DIR/CustomSettings.config" 2>/dev/null || echo "BC")
    ASSEMBLY_CACHE="/usr/share/Microsoft/Microsoft Dynamics NAV/$NAV_DIR/Server/MicrosoftDynamicsNavServer\$${INSTANCE}/apps/assembly/release/${PLATFORM_VER}_1"
    mkdir -p "$ASSEMBLY_CACHE"
    R2R_SEEDED=0
    R2R_FILTERED=0
    R2R_FAILED=0
    while IFS= read -r -d '' appfile; do
        python3 - "$appfile" "$ASSEMBLY_CACHE" "${BC_KEEP_APP_IDS_FOR_R2R:-}" << 'PYEOF' && R2R_SEEDED=$((R2R_SEEDED + 1)) || { [ $? -eq 2 ] && R2R_FILTERED=$((R2R_FILTERED + 1)) || R2R_FAILED=$((R2R_FAILED + 1)); }
import sys, zipfile, os, json, re

app_path, dest = sys.argv[1], sys.argv[2]
keep_ids = set(sys.argv[3].split(',')) if len(sys.argv) > 3 and sys.argv[3] else set()

try:
    z = zipfile.ZipFile(app_path)
    names = z.namelist()

    # If selective filtering is active, check this app's ID against the keep list
    if keep_ids:
        app_id = None
        if 'readytorunappmanifest.json' in names:
            m = json.loads(z.read('readytorunappmanifest.json'))
            app_id = m.get('EmbeddedAppId', '').lower().strip('{}')
        elif 'NavxManifest.xml' in names:
            xml = z.read('NavxManifest.xml').decode('utf-8', errors='replace')
            match = re.search(r'(?:App)?Id\s*=\s*"([^"]+)"', xml, re.IGNORECASE)
            if match:
                app_id = match.group(1).lower().strip('{}')
        if app_id and app_id not in keep_ids:
            sys.exit(2)  # filtered out — not an error

    extracted = 0
    for name in names:
        if 'publishedartifacts/' not in name:
            continue
        basename = os.path.basename(name)
        if not basename:
            continue
        dest_path = os.path.join(dest, basename)
        if not os.path.exists(dest_path):
            with open(dest_path, 'wb') as f:
                f.write(z.read(name))
        extracted += 1
    sys.exit(0 if extracted > 0 else 1)
except Exception:
    sys.exit(1)
PYEOF
    done < <(find "$ARTIFACTS/app/Extensions" -name "*.app" -type f -print0 2>/dev/null)
    if [ "$R2R_FILTERED" -gt 0 ]; then
        log_step "R2R DLL cache seeded: $R2R_SEEDED apps extracted, $R2R_FILTERED filtered (selective), $R2R_FAILED skipped"
    else
        log_step "R2R DLL cache seeded: $R2R_SEEDED apps extracted, $R2R_FAILED skipped — cache: $ASSEMBLY_CACHE"
    fi
else
    log_step "WARN: Could not determine platform version; skipping R2R pre-seed"
fi

log_step "Starting BC service tier..."
# Start BC — use a FIFO to keep stdin open for /console mode
mkfifo /tmp/bc-stdin 2>/dev/null || true

# .NET runtime tuning for BC service tier performance:
# - Server GC: better throughput for multi-threaded workloads (extension compilation)
# - Tiered compilation: DISABLED to prevent JMP hooks from being overwritten by Tier 1 recompilation.
#   The Watson crash handler patch relies on JMP hooks staying in place.
export DOTNET_gcServer=1
export DOTNET_TieredCompilation=0

DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll dotnet Microsoft.Dynamics.Nav.Server.dll /console < /tmp/bc-stdin &
BC_PID=$!
# Keep the FIFO writer open in background (prevents EOF)
exec 3>/tmp/bc-stdin

# Wait for dev endpoint to be ready, then publish test runner
(
    # Disable set -e in background subshell — curl returns non-zero when BC
    # hasn't started yet, and inherited set -e would silently kill this process
    # before Patch #15 and test framework publishing can run.
    set +e

    INSTANCE=$(grep -oP 'ServerInstance" value="\K[^"]+' $SERVICE_DIR/CustomSettings.config 2>/dev/null || echo "BC")
    DEV_URL="http://localhost:7049"
    NST_WAIT_START=$(date +%s)

    echo "[entrypoint] [$(( $(date +%s) - ENTRYPOINT_START ))s] Waiting for BC to start..."
    for i in $(seq 1 180); do
        # Check if BC process died
        if ! kill -0 $BC_PID 2>/dev/null; then
            echo "[entrypoint] ERROR: BC process died"
            wait $BC_PID 2>/dev/null
            exit 1
        fi
        HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 3 "$DEV_URL/packages" 2>&1)
        if [ "$HTTP" != "000" ]; then
            break
        fi
        sleep 5
    done
    NST_WAIT_ELAPSED=$(( $(date +%s) - NST_WAIT_START ))
    TOTAL_ELAPSED=$(( $(date +%s) - ENTRYPOINT_START ))
    echo "[entrypoint] [${TOTAL_ELAPSED}s] Dev endpoint ready (HTTP $HTTP) — NST startup: ${NST_WAIT_ELAPSED}s"

    # Replace Reporting Service .exe with sleep stub NOW (after BC startup probed the assembly).
    # The SideServiceWatchdog will call Process.Start on this path and see a live process.
    REPORT_EXE="$SERVICE_DIR/SideServices/Microsoft.BusinessCentral.Reporting.Service.exe"
    if [ -f "$REPORT_EXE" ] && [ ! -f "${REPORT_EXE}.win" ]; then
        mv "$REPORT_EXE" "${REPORT_EXE}.win"
        printf '#!/bin/sh\nexec sleep infinity\n' > "$REPORT_EXE"
        chmod +x "$REPORT_EXE"
        echo "[entrypoint] Replaced Reporting Service .exe with sleep stub (watchdog silenced)"
    fi

    # Patch #15: Disabled — renaming ALL runtime DLLs breaks System.Net.HttpListener
    # and other assemblies that BC needs at request time (not just at startup).
    # The merged assemblies in Add-Ins should handle type-forwarding resolution
    # for server-side compilation without needing to hide the runtime DLLs.
    # TODO: Selectively rename only the DLLs that cause Cecil type-forwarding issues.
    echo "[entrypoint] Patch #15: Skipped (merged assemblies handle type-forwarding)"

    # Publish test framework apps unless caller will handle all publishing.
    # BC_SKIP_APP_PUBLISH=true: skip all publishing (caller manages extensions)
    if [ "${BC_SKIP_APP_PUBLISH:-false}" = "true" ]; then
        echo "[entrypoint] Skipping app publishing (BC_SKIP_APP_PUBLISH=true)"
    else
        # -------------------------------------------------------------------------
        # BC_CLEAR_ALL_APPS: republish all previously-installed extensions in
        # dependency order, then fall through to test framework publishing below.
        # -------------------------------------------------------------------------
        if [ "${BC_CLEAR_ALL_APPS:-false}" = "selective" ]; then
            TOTAL_ELAPSED=$(( $(date +%s) - ENTRYPOINT_START ))
            echo "[entrypoint] [${TOTAL_ELAPSED}s] BC_CLEAR_ALL_APPS=selective: publishing test framework only (caller will publish overrides)"
        elif [ "${BC_CLEAR_ALL_APPS:-false}" = "deps-only" ]; then
            TOTAL_ELAPSED=$(( $(date +%s) - ENTRYPOINT_START ))
            echo "[entrypoint] [${TOTAL_ELAPSED}s] BC_CLEAR_ALL_APPS=deps-only: skipping full republish (caller will publish dependency chain)"
        elif [ "${BC_CLEAR_ALL_APPS:-false}" = "true" ] && [ -f "/tmp/bc-apps-to-republish.tsv" ]; then
            REPUBLISH_START=$(date +%s)
            TOTAL_ELAPSED=$(( $(date +%s) - ENTRYPOINT_START ))
            echo "[entrypoint] [${TOTAL_ELAPSED}s] BC_CLEAR_ALL_APPS: republishing extensions in dependency order..."

            # Build a name→app-file map by scanning all .app files in artifacts.
            APP_INDEX="/tmp/bc-app-index.tsv"
            ARTIFACTS_VAL="$ARTIFACTS"
            python3 << PYEOF > "$APP_INDEX"
import os, zipfile, json, re

artifacts = "$ARTIFACTS_VAL"
for root, dirs, files in os.walk(artifacts):
    for fname in files:
        if not fname.endswith('.app'):
            continue
        path = os.path.join(root, fname)
        try:
            z = zipfile.ZipFile(path)
            names = z.namelist()
            if 'readytorunappmanifest.json' in names:
                d = json.loads(z.read('readytorunappmanifest.json'))
                app_name = d.get('EmbeddedAppName', '')
                app_pub  = d.get('EmbeddedAppPublisher', '')
                app_ver  = d.get('EmbeddedAppVersion', '')
            elif 'NavxManifest.xml' in names:
                xml = z.read('NavxManifest.xml').decode('utf-8', errors='replace')
                m_name = re.search(r'Name="([^"]+)"', xml)
                m_pub  = re.search(r'Publisher="([^"]+)"', xml)
                m_ver  = re.search(r'Version="([^"]+)"', xml)
                app_name = m_name.group(1) if m_name else ''
                app_pub  = m_pub.group(1)  if m_pub  else ''
                app_ver  = m_ver.group(1)  if m_ver  else ''
            else:
                continue
            if app_name:
                print(f"{app_name}\t{app_pub}\t{app_ver}\t{path}")
        except Exception:
            pass
PYEOF

            TOPO_SCRIPT=$(cat <<'PYEOF'
import sys, os

snapshot_file = sys.argv[1]
deps_file     = sys.argv[2]

apps = {}
with open(snapshot_file) as f:
    for line in f:
        line = line.strip()
        if not line or '\t' not in line:
            continue
        parts = line.split('\t')
        if len(parts) < 4:
            continue
        pkg_id, name, pub, ver = parts[0], parts[1], parts[2], parts[3]
        apps[pkg_id] = (name, pub, ver)

deps = {k: [] for k in apps}
if os.path.exists(deps_file):
    with open(deps_file) as f:
        for line in f:
            line = line.strip()
            if not line or '\t' not in line:
                continue
            parts = line.split('\t')
            if len(parts) < 2:
                continue
            pkg_id, dep_id = parts[0], parts[1]
            if pkg_id in deps:
                deps[pkg_id].append(dep_id)

from collections import deque
in_degree = {k: 0 for k in apps}
reverse_deps = {k: [] for k in apps}
for pkg_id, dep_list in deps.items():
    for dep in dep_list:
        if dep in apps:
            in_degree[pkg_id] += 1
            reverse_deps[dep].append(pkg_id)

queue = deque([k for k, v in in_degree.items() if v == 0])
order = []
while queue:
    node = queue.popleft()
    order.append(node)
    for dependent in reverse_deps.get(node, []):
        in_degree[dependent] -= 1
        if in_degree[dependent] == 0:
            queue.append(dependent)

remaining = [k for k in apps if k not in order]
order.extend(remaining)

for pkg_id in order:
    if pkg_id in apps:
        name, pub, ver = apps[pkg_id]
        print(f"{pkg_id}\t{name}\t{pub}\t{ver}")
PYEOF
            )

            ORDERED_LIST=$(python3 -c "$TOPO_SCRIPT" "/tmp/bc-apps-to-republish.tsv" "/tmp/bc-app-deps.tsv" 2>/dev/null)
            APP_COUNT=$(echo "$ORDERED_LIST" | grep -c $'\t' || echo 0)
            echo "[entrypoint] Republishing $APP_COUNT extensions in dependency order..."

            REPUBLISH_OK=0
            REPUBLISH_SKIP=0
            while IFS=$'\t' read -r PKG_ID APP_NAME APP_PUB APP_VER; do
                [ -z "$APP_NAME" ] && continue

                APP_FILE=""
                APP_FILE=$(awk -F'\t' -v n="$APP_NAME" -v p="$APP_PUB" -v v="$APP_VER" \
                    '$1==n && $2==p && $3==v {print $4; exit}' "$APP_INDEX" 2>/dev/null)
                if [ -z "$APP_FILE" ]; then
                    APP_FILE=$(awk -F'\t' -v n="$APP_NAME" -v p="$APP_PUB" \
                        '$1==n && $2==p {print $4; exit}' "$APP_INDEX" 2>/dev/null)
                fi

                if [ -z "$APP_FILE" ] || [ ! -f "$APP_FILE" ]; then
                    echo "[entrypoint]   SKIP (no .app found): $APP_NAME $APP_VER by $APP_PUB"
                    REPUBLISH_SKIP=$((REPUBLISH_SKIP + 1))
                    continue
                fi

                HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 300 \
                    -u "BCRUNNER:Admin123!" -X POST \
                    -F "file=@$APP_FILE;type=application/octet-stream" \
                    "$DEV_URL/apps?SchemaUpdateMode=forcesync" 2>/dev/null)
                echo "[entrypoint]   $APP_NAME $APP_VER: HTTP $HTTP"
                if [ "$HTTP" = "200" ] || [ "$HTTP" = "201" ] || [ "$HTTP" = "204" ]; then
                    REPUBLISH_OK=$((REPUBLISH_OK + 1))
                else
                    echo "[entrypoint]   WARN: failed to republish $APP_NAME $APP_VER (HTTP $HTTP)"
                fi
            done <<< "$ORDERED_LIST"

            REPUBLISH_ELAPSED=$(( $(date +%s) - REPUBLISH_START ))
            TOTAL_ELAPSED=$(( $(date +%s) - ENTRYPOINT_START ))
            echo "[entrypoint] [${TOTAL_ELAPSED}s] Republished $REPUBLISH_OK extensions, skipped $REPUBLISH_SKIP — republish took ${REPUBLISH_ELAPSED}s"
        fi

        # Install-for-tenant pass. The selective filter (lines 391-453)
        # preserves keep-set apps in [Published Application] but BC's
        # default sandbox install leaves test framework apps in a
        # published-but-not-installed-for-tenant state. We need an
        # explicit "install for tenant" step or downstream publishes
        # fail with "the referenced dependencies ... published as Global
        # application are not installed".
        #
        # The dev endpoint POST with SchemaUpdateMode=forcesync does
        # BOTH publish AND install-for-tenant in one step. So we just
        # POST every keep-set app from the artifact tree.
        #
        # This is consumer-driven: the keep set is computed from the
        # consumer's app.json by resolve-keep-app-ids.py. There's no
        # hand-curated array of "test framework apps to republish"
        # anywhere — the loop here just iterates whatever the consumer
        # transitively needs.
        if [ -n "${BC_KEEP_APP_IDS:-}" ]; then
            echo "[entrypoint] Ensuring keep-set apps are installed for tenant..."
            # Topologically sort the keep-set apps by their declared
            # dependencies (deps before dependents). The bash equivalent
            # is fragile; let Python do it via the shared _bcapp helper.
            #
            # The sorter:
            #   1. Loads every .app from /bc/artifacts via _bcapp.py
            #   2. Walks each keep-set id's transitive deps
            #   3. Emits absolute paths in dep-first (post-order) order
            #
            # Skips the application stack baseline ids — they're already
            # installed for the tenant by BC's sandbox setup, re-POSTing
            # them is wasteful and slow.
            INSTALL_ORDER=$(BC_KEEP_APP_IDS="$BC_KEEP_APP_IDS" python3 - "$ARTIFACTS" << 'PYEOF'
import os, sys
sys.path.insert(0, "/bc/scripts")
from _bcapp import load_artifact_apps  # noqa
artifact_dir = sys.argv[1]
keep_ids = {x.strip().lower() for x in os.environ.get("BC_KEEP_APP_IDS","").split(",") if x.strip()}
# BC application stack baseline — already installed for tenant by default.
BASELINE = {
    "8874ed3a-0643-4247-9ced-7a7002f7135d",  # System
    "63ca2fa4-4f03-4f2b-a480-172fef340d3f",  # System Application
    "f3552374-a1f2-4356-848e-196002525837",  # Business Foundation
    "437dbf0e-84ff-417a-965d-ed2bb9650972",  # Base Application
    "c1335042-3002-4257-bf8a-75c898ccb1b8",  # Application umbrella
}
to_install = keep_ids - BASELINE
apps = load_artifact_apps(artifact_dir)
visited, ordered = set(), []
def visit(aid):
    if aid in visited:
        return
    visited.add(aid)
    info = apps.get(aid)
    if info is None:
        return  # not in artifact tree — silently skip
    for dep in info.get("dependencies", []):
        visit(dep["id"])
    if aid in to_install:
        ordered.append(info["path"])
for aid in sorted(to_install):
    visit(aid)
for path in ordered:
    print(path)
PYEOF
            )
            while IFS= read -r APP_PATH; do
                [ -z "$APP_PATH" ] && continue
                NAME=$(basename "$APP_PATH")
                # forcesync alone is enough: the 5 core test framework apps
                # were wiped by the SQL above so this POST publishes them
                # fresh as Dev/tenant-installed. Other apps in the keep set
                # (BaseApp helpers like Tests-TestLibraries) were never in
                # the BC database to begin with, so they also publish fresh.
                # Tried &DependencyPublishingOption=Install — BC rejects
                # 'Install' as an invalid value (the actual valid values are
                # Default/Strict/Ignore, none of which auto-install missing
                # tenant deps), so we have to wipe instead.
                HTTP=$(curl -s -o /tmp/install-tenant.out -w "%{http_code}" --max-time 120 \
                    -u "BCRUNNER:Admin123!" -X POST \
                    -F "file=@$APP_PATH;type=application/octet-stream" \
                    "$DEV_URL/apps?SchemaUpdateMode=forcesync" 2>/dev/null)
                # Treat "already deployed as Global" 422s as benign — they
                # mean the app was pre-installed by BC's sandbox image and
                # doesn't need a republish.
                if [ "$HTTP" = "200" ] || [ "$HTTP" = "204" ]; then
                    echo "[entrypoint]   $NAME: HTTP $HTTP"
                elif [ "$HTTP" = "422" ] && grep -qiE "already (deployed|installed)" /tmp/install-tenant.out; then
                    echo "[entrypoint]   $NAME: HTTP $HTTP (already deployed — skip)"
                else
                    echo "[entrypoint]   $NAME: HTTP $HTTP"
                    sed 's/^/[entrypoint]     /' /tmp/install-tenant.out
                fi
                rm -f /tmp/install-tenant.out
            done <<< "$INSTALL_ORDER"
        fi

        # Publish additional test app dependencies (e.g. System App Test Library, Tests-TestLibraries)
        # and the actual test app (e.g. Tests-SINGLESERVER) if BC_TEST_APPS is set.
        # BC_TEST_APPS is a semicolon-separated list of .app file paths.
        if [ -n "${BC_TEST_APPS:-}" ]; then
            echo "[entrypoint] Publishing test app dependencies..."
            IFS=';' read -ra TEST_APPS <<< "$BC_TEST_APPS"
            for app in "${TEST_APPS[@]}"; do
                app=$(echo "$app" | xargs)  # trim whitespace
                [ -z "$app" ] && continue
                if [ ! -f "$app" ]; then
                    echo "[entrypoint]   SKIP (not found): $app"
                    continue
                fi
                NAME=$(basename "$app")
                HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 600 \
                    -u "BCRUNNER:Admin123!" -X POST \
                    -F "file=@$app;type=application/octet-stream" \
                    "$DEV_URL/apps?SchemaUpdateMode=forcesync" 2>/dev/null)
                echo "[entrypoint]   $NAME: HTTP $HTTP"
            done
        fi

        # Publish our TestRunner Extension (custom API for test execution, depends on MS Test Runner)
        if [ -f /bc/testrunner/TestRunner.app ]; then
            echo "[entrypoint] Publishing Test Runner Extension..."
            HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" --max-time 30 \
                -u "BCRUNNER:Admin123!" -X POST \
                -F "file=@/bc/testrunner/TestRunner.app;type=application/octet-stream" \
                "$DEV_URL/apps?SchemaUpdateMode=synchronize" 2>&1)
            echo "[entrypoint] Test Runner Extension: HTTP $HTTP_CODE"
        fi
    fi
    TOTAL_ELAPSED=$(( $(date +%s) - ENTRYPOINT_START ))
    echo "[entrypoint] [${TOTAL_ELAPSED}s] Ready for extensions. Total startup: ${TOTAL_ELAPSED}s"
    touch /tmp/bc-ready
) &

wait $BC_PID
