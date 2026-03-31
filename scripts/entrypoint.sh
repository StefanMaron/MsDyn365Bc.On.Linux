#!/bin/bash
# Self-contained BC service tier entrypoint.
# Downloads artifacts, restores DB, configures BC, publishes test runner, starts server.
set -e
# Merge stdout into stderr so Docker captures all output immediately
# (stdout is pipe-buffered when PID 1 has no TTY; stderr is unbuffered)
exec 1>&2
echo "[entrypoint] Script started at $(date)"

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
    [ $RESTORE_COUNT -gt 0 ] && echo "[entrypoint] Restored $RESTORE_COUNT runtime DLLs from .bak (restart recovery)"
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
if [ ! -f "$ARTIFACTS/app/manifest.json" ]; then
    if [ "$BC_ARTIFACT_URL" = "skip" ]; then
        echo "[entrypoint] Waiting for artifacts to be provided externally..."
        # Wait for BOTH app manifest AND platform ServiceTier to be present
        for i in $(seq 1 120); do
            [ -f "$ARTIFACTS/app/manifest.json" ] && \
            [ -d "$ARTIFACTS/platform/ServiceTier" ] && break
            sleep 2
        done
        [ -f "$ARTIFACTS/app/manifest.json" ] || { echo "[entrypoint] ERROR: App artifacts not provided"; exit 1; }
        [ -d "$ARTIFACTS/platform/ServiceTier" ] || { echo "[entrypoint] ERROR: Platform artifacts not provided"; ls -la "$ARTIFACTS/platform/" 2>/dev/null; exit 1; }
    elif [ -n "$BC_ARTIFACT_URL" ]; then
        echo "[entrypoint] Downloading BC from $BC_ARTIFACT_URL..."
        /bc/scripts/download-artifacts.sh "$BC_ARTIFACT_URL" "$ARTIFACTS"
    else
        echo "[entrypoint] Downloading BC $BC_TYPE $BC_VERSION ($BC_COUNTRY)..."
        /bc/scripts/download-artifacts.sh "$BC_TYPE" "$BC_VERSION" "$BC_COUNTRY" "$ARTIFACTS"
    fi
else
    echo "[entrypoint] Artifacts already cached."
fi

# Read manifest
echo "[entrypoint] Disk: $(df -h /bc/artifacts | tail -1 | awk '{print $4 " free"}')"
echo "[entrypoint] Reading manifest..."
MANIFEST="$ARTIFACTS/app/manifest.json"
ls -la "$MANIFEST" || { echo "[entrypoint] FATAL: manifest.json not found at $MANIFEST"; exit 1; }
DB_FILE=$(python3 -c "import json; print(json.load(open('$MANIFEST')).get('database',''))")
LICENSE_FILE=$(python3 -c "import json; print(json.load(open('$MANIFEST')).get('licenseFile',''))")
PLATFORM_VERSION=$(python3 -c "import json; print(json.load(open('$MANIFEST'))['platform'])")
MAJOR_VERSION=$(echo "$PLATFORM_VERSION" | cut -d. -f1)
NAV_DIR="${MAJOR_VERSION}0"

echo "[entrypoint] Platform: $PLATFORM_VERSION, NAV dir: $NAV_DIR, DB: $DB_FILE"

# =============================================================================
# Step 2: Copy service tier to working directory (if not already set up)
# =============================================================================
if [ ! -f "$SERVICE_DIR/Microsoft.Dynamics.Nav.Server.dll" ]; then
    echo "[entrypoint] Setting up service tier..."
    # Auto-detect service tier path (differs between versions: PFiles64 vs "program files")
    SRC=$(find "$ARTIFACTS/platform/ServiceTier" -name "Microsoft.Dynamics.Nav.Server.dll" -printf "%h\n" 2>/dev/null | head -1)
    if [ -z "$SRC" ] || [ ! -d "$SRC" ]; then
        echo "[entrypoint] ERROR: Service tier not found in $ARTIFACTS/platform/ServiceTier/"
        find "$ARTIFACTS/platform/ServiceTier" -maxdepth 4 -type d 2>/dev/null
        exit 1
    fi
    echo "[entrypoint] Found service tier at: $SRC"
    cp -r "$SRC/." "$SERVICE_DIR/"

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
        "$CONFIG"

    # Add settings if missing
    if ! grep -q "TenantEnvironmentType" "$CONFIG"; then
        sed -i '/<add key="TestAutomationEnabled"/a\  <add key="TenantEnvironmentType" value="Sandbox" />' "$CONFIG"
    fi
    if ! grep -q "TestAutomationEnabled" "$CONFIG"; then
        sed -i '/<\/appSettings>/i\  <add key="TestAutomationEnabled" value="true"/>' "$CONFIG"
    fi

    echo "[entrypoint] Service tier configured."
else
    echo "[entrypoint] Service tier already set up."
fi

# Override framework DLLs (must run every container start, not just first setup)
cp /bc/hook/System.Security.Principal.Windows.dll /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.*/
cp /bc/hook/Microsoft.AspNetCore.Server.HttpSys.dll /usr/share/dotnet/shared/Microsoft.AspNetCore.App/8.0.*/
# Replace stub DLLs in service dir
for stub in OpenTelemetry.Exporter.Geneva.dll Microsoft.Data.SqlClient.dll; do
    if [ -f "/bc/hook/$stub" ]; then
        [ -f "$SERVICE_DIR/$stub" ] && [ ! -f "$SERVICE_DIR/${stub}.orig" ] && cp "$SERVICE_DIR/$stub" "$SERVICE_DIR/${stub}.orig"
        cp "/bc/hook/$stub" "$SERVICE_DIR/$stub"
        echo "[entrypoint] Replaced $stub with stub/unix version"
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
    echo "[entrypoint] Created Win32 DLL symlinks → libwin32_stubs.so"
fi

# =============================================================================
# Step 2b: Generate merged assemblies if not cached (first boot)
# =============================================================================
if [ ! -f "/bc/patched/netstandard-merged.dll" ] && [ -f /bc/tools/MergeNetstandard.dll ]; then
    echo "[entrypoint] Generating merged assemblies (first boot)..."
    BASE_DIR=/bc PLATFORM_DIR="$ARTIFACTS/platform" \
        dotnet /bc/tools/MergeNetstandard.dll 2>&1 | tail -5
    echo "[entrypoint] Merged assemblies generated."
fi

# Apply patched DLLs (Cecil-modified to fix Linux-specific bugs)
# Patch #14: CodeAnalysis.dll — fix IsTypeForwardingCircular NullRef on Linux
#   BC's Cecil type loader crashes following type-forwarding chains in netstandard.dll.
#   The patched DLL returns false for circular check, allowing forwarding to work.
if [ -f /bc/patched/Microsoft.Dynamics.Nav.CodeAnalysis.dll ]; then
    cp /bc/patched/Microsoft.Dynamics.Nav.CodeAnalysis.dll "$SERVICE_DIR/Microsoft.Dynamics.Nav.CodeAnalysis.dll"
    [ -d "$SERVICE_DIR/Admin" ] && cp /bc/patched/Microsoft.Dynamics.Nav.CodeAnalysis.dll "$SERVICE_DIR/Admin/Microsoft.Dynamics.Nav.CodeAnalysis.dll"
    echo "[entrypoint] Applied patched CodeAnalysis.dll (Patch #14: type forwarding fix)"
fi
# Patch Mono.Cecil's CheckFileName to not throw on empty file paths
if [ -f /bc/patched/Mono.Cecil.dll ]; then
    cp /bc/patched/Mono.Cecil.dll "$SERVICE_DIR/Mono.Cecil.dll"
    [ -d "$SERVICE_DIR/Admin" ] && cp /bc/patched/Mono.Cecil.dll "$SERVICE_DIR/Admin/Mono.Cecil.dll"
    echo "[entrypoint] Applied patched Mono.Cecil.dll (CheckFileName empty path fix)"
fi

# Fix Add-Ins directory case (Linux is case-sensitive, BC expects "Add-Ins")
if [ -d "$SERVICE_DIR/Add-ins" ] && [ ! -d "$SERVICE_DIR/Add-Ins" ]; then
    mv "$SERVICE_DIR/Add-ins" "$SERVICE_DIR/Add-Ins"
    echo "[entrypoint] Renamed Add-ins → Add-Ins (case-sensitivity fix)"
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
    echo "[entrypoint] Copied .NET reference assemblies to Add-Ins ($(ls /bc/refasm/*.dll 2>/dev/null | wc -l) files)"

    # Layer 2: forwarding assemblies (override refasm with type-forwards to netstandard)
    if [ -d /bc/patched/refasm-forwarding ]; then
        cp /bc/patched/refasm-forwarding/*.dll "$ADDINS_DIR/" 2>/dev/null || true
        echo "[entrypoint] Applied forwarding assemblies ($(ls /bc/patched/refasm-forwarding/*.dll 2>/dev/null | wc -l) files)"
    fi

    # Layer 3: merged assemblies (deploy with original filenames)
    for merged in netstandard:netstandard-merged DocumentFormat.OpenXml:DocumentFormat.OpenXml-merged System.Drawing:System.Drawing-merged System.Core:System.Core-merged; do
        TARGET="${merged%%:*}.dll"
        SRC="${merged##*:}.dll"
        if [ -f "/bc/patched/$SRC" ]; then
            cp "/bc/patched/$SRC" "$ADDINS_DIR/$TARGET"
        fi
    done
    echo "[entrypoint] Applied merged type-forward assemblies"

    # Layer 4: DrawingStub for compile-time (uses framework Color/Rectangle refs)
    if [ -f /bc/addins-overlay/System.Drawing.Common.dll ]; then
        cp /bc/addins-overlay/System.Drawing.Common.dll "$ADDINS_DIR/System.Drawing.Common.dll"
        echo "[entrypoint] Applied DrawingStub to Add-Ins (compile-time)"
    fi

    # Layer 5: MockTest.dll for test framework (required by Test Library)
    # Try from image overlay first, fall back to artifacts
    if [ -f /bc/addins-overlay/MockTest.dll ]; then
        cp /bc/addins-overlay/MockTest.dll "$ADDINS_DIR/MockTest.dll"
        echo "[entrypoint] Copied MockTest.dll to Add-Ins (from image)"
    else
        MOCK_DLL=$(find "$ARTIFACTS/platform" -path "*/Mock Assemblies/MockTest.dll" 2>/dev/null | head -1)
        if [ -n "$MOCK_DLL" ]; then
            cp "$MOCK_DLL" "$ADDINS_DIR/MockTest.dll"
            echo "[entrypoint] Copied MockTest.dll to Add-Ins (from artifacts)"
        fi
    fi
fi


# =============================================================================
# Step 3: Wait for SQL Server and set up database
# =============================================================================
export PATH="$PATH:/opt/mssql-tools18/bin"

echo "[entrypoint] Waiting for SQL Server..."
until sqlcmd -S "$SQL_SERVER" -U sa -P "$SA_PASSWORD" -C -No -Q "SELECT 1" &>/dev/null; do
    sleep 2
done
echo "[entrypoint] SQL Server ready."

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
    echo "[entrypoint] Restoring CRONUS database..."
    BAK_PATH="$ARTIFACTS/app/$DB_FILE"
    if [ ! -f "$BAK_PATH" ]; then
        echo "[entrypoint] ERROR: Database backup not found at $BAK_PATH"
        exit 1
    fi

    # Get logical file names
    FILELIST=$($SQLCMD -h -1 -Q "RESTORE FILELISTONLY FROM DISK='$BAK_PATH'" 2>/dev/null)
    DATA_NAME=$(echo "$FILELIST" | head -1 | awk '{print $1}')
    LOG_NAME=$(echo "$FILELIST" | head -2 | tail -1 | awk '{print $1}')

    $SQLCMD -Q "
        RESTORE DATABASE [CRONUS] FROM DISK='$BAK_PATH'
        WITH MOVE '$DATA_NAME' TO '/var/opt/mssql/data/CRONUS.mdf',
             MOVE '$LOG_NAME' TO '/var/opt/mssql/data/CRONUS_log.ldf'
    "
    echo "[entrypoint] CRONUS restored."
else
    echo "[entrypoint] CRONUS already exists."
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
    echo "[entrypoint] License imported."
fi

# Sandbox tenant type
$SQLCMD_DB -Q "UPDATE [\$ndo\$tenantproperty] SET tenanttype = 1 WHERE tenantid = 'default';" 2>/dev/null

# Clear pre-installed apps before BC starts.
# BC_CLEAR_ALL_APPS=true: nuclear clear of ALL apps (needed when publishing custom System App)
# Default: only clear test framework apps (Test Runner, Library Assert, etc.)
if [ "${BC_CLEAR_ALL_APPS:-false}" = "true" ]; then
    $SQLCMD_DB -Q "
    DELETE FROM [NAV App Installed App];
    DELETE FROM [NAV App Tenant App];
    DELETE FROM [NAV App Dependencies];
    DELETE FROM [NAV App Published App];
    DELETE FROM [Published Application];
    DELETE FROM [Installed Application];
    DELETE FROM [Inplace Installed Application];
    " 2>/dev/null
    echo "[entrypoint] Cleared ALL pre-installed apps (BC_CLEAR_ALL_APPS=true)"
else
    $SQLCMD_DB -Q "
    DELETE FROM [Installed Application] WHERE [Package ID] IN (SELECT [Package ID] FROM [Published Application] WHERE [Name] IN (N'Test Runner',N'Library Assert',N'Library Variable Storage',N'Permissions Mock',N'Any'));
    DELETE FROM [NAV App Installed App] WHERE [Name] IN (N'Test Runner',N'Library Assert',N'Library Variable Storage',N'Permissions Mock',N'Any');
    DELETE FROM [Published Application] WHERE [Name] IN (N'Test Runner',N'Library Assert',N'Library Variable Storage',N'Permissions Mock',N'Any');
    " 2>/dev/null
    echo "[entrypoint] Cleared test framework global entries (will re-publish via dev endpoint)"
fi

# Admin user (password hash for Admin123! with GUID 00000000-0000-0000-0000-000000000001)
USER_GUID='00000000-0000-0000-0000-000000000001'
PASSWORD_HASH='aXD91GRctWiXaqXeWbXhxQ==-V3'
$SQLCMD_DB -Q "
IF NOT EXISTS (SELECT 1 FROM [User] WHERE [User Name] = 'admin')
BEGIN
    INSERT INTO [User] ([User Security ID], [User Name], [Full Name], [State], [Expiry Date],
        [Windows Security ID], [Change Password], [License Type], [Authentication Email],
        [Contact Email], [Exchange Identifier], [Application ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'admin', N'Admin', 0, '2099-12-31', N'', 0, 0, N'', N'', N'',
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
echo "[entrypoint] Database ready (admin / Admin123!)."

# =============================================================================
# Step 4: Start BC server in background, publish test runner, then wait
# =============================================================================
cd "$SERVICE_DIR"

# Verify SQL is still accessible before starting BC
echo "[entrypoint] Verifying SQL connection..."
if sqlcmd -S "$SQL_SERVER" -U "$BC_DB_USER" -P "$BC_DB_PASSWORD" -d CRONUS -C -No -Q "SELECT 1" &>/dev/null; then
    echo "[entrypoint] SQL connection verified."
else
    echo "[entrypoint] ERROR: SQL connection failed! Retrying..."
    sleep 5
    sqlcmd -S "$SQL_SERVER" -U "$BC_DB_USER" -P "$BC_DB_PASSWORD" -d CRONUS -C -No -Q "SELECT 1" || {
        echo "[entrypoint] FATAL: Cannot connect to SQL"
        exit 1
    }
fi

echo "[entrypoint] Config check:"
grep -E "DatabaseServer|DatabaseName|DatabaseUserName|ProtectedDatabase" "$SERVICE_DIR/CustomSettings.config" | head -5
echo "[entrypoint] Starting BC service tier..."
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

    echo "[entrypoint] Waiting for BC to start..."
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
    echo "[entrypoint] Dev endpoint ready (HTTP $HTTP)"

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
        # BC pre-loads them as "global" apps but doesn't install for the tenant.
        # We cleared the stale global entries at DB setup so we can re-publish here.
        echo "[entrypoint] Publishing test framework..."
        # Use find to handle spaces in filenames (e.g. "Test Runner")
        find "$ARTIFACTS" -name "*.app" -type f \( \
            -name "Microsoft_Test Runner_*" -o \
            -name "Microsoft_Library Assert_*" -o \
            -name "Microsoft_Library Variable Storage_*" -o \
            -name "Microsoft_Permissions Mock_*" -o \
            -name "Microsoft_Any_*" \
        \) 2>/dev/null | sort | while read -r app; do
            NAME=$(basename "$app")
            HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 120 \
                -u "admin:Admin123!" -X POST \
                -F "file=@$app;type=application/octet-stream" \
                "$DEV_URL/apps?SchemaUpdateMode=forcesync" 2>/dev/null)
            echo "[entrypoint]   $NAME: HTTP $HTTP"
        done

        # Publish our TestRunner Extension (custom API for test execution, depends on MS Test Runner)
        if [ -f /bc/testrunner/TestRunner.app ]; then
            echo "[entrypoint] Publishing Test Runner Extension..."
            HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" --max-time 30 \
                -u "admin:Admin123!" -X POST \
                -F "file=@/bc/testrunner/TestRunner.app;type=application/octet-stream" \
                "$DEV_URL/apps?SchemaUpdateMode=synchronize" 2>&1)
            echo "[entrypoint] Test Runner Extension: HTTP $HTTP_CODE"
        fi
    fi
    echo "[entrypoint] Ready for extensions."
    touch /tmp/bc-ready
) &

wait $BC_PID
