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
# The image ships more than one shared framework (net8.0 for BC 27/28, net10.0
# for BC 29+), so sweep all of them — we don't know yet which one this BC needs.
RESTORE_COUNT=0
for RUNTIME_DIR in /usr/share/dotnet/shared/Microsoft.NETCore.App/*/; do
    [ -d "$RUNTIME_DIR" ] || continue
    for bak in "$RUNTIME_DIR"/*.dll.bak; do
        [ -f "$bak" ] || continue
        mv "$bak" "${bak%.bak}"
        RESTORE_COUNT=$((RESTORE_COUNT + 1))
    done
done
[ $RESTORE_COUNT -gt 0 ] && log_step "Restored $RESTORE_COUNT runtime DLLs from .bak (restart recovery)"

BC_TYPE="${BC_TYPE:-sandbox}"
BC_VERSION="${BC_VERSION:-27.5.46862.48004}"
BC_COUNTRY="${BC_COUNTRY:-w1}"
SA_PASSWORD="${SA_PASSWORD:-Passw0rd123!}"
BC_DB_PASSWORD="${BC_DB_PASSWORD:-Test1234}"
BC_DB_USER="${BC_DB_USER:-bctest}"
SQL_SERVER="${SQL_SERVER:-sql}"
ARTIFACTS="/bc/artifacts"
SERVICE_DIR="/bc/service"

# Same shape as the .bak restore above, for the one file BC's own boot rewrites
# inside the volume. The Reporting Service .exe is swapped for a sleep stub
# AFTER the dev endpoint answers, because NST's startup probes the real PE's
# assembly metadata and has to find it there. `/bc/service` outlives the
# container, so without this the next boot hands that probe a /bin/sh script
# instead — it happens to survive, but a warm boot then differs from a cold one
# in a way nothing reports. Restoring here makes the post-start swap re-run
# every boot, which is what the swap's own `[ ! -f .win ]` guard assumes.
REPORT_EXE_AT_BOOT="$SERVICE_DIR/SideServices/Microsoft.BusinessCentral.Reporting.Service.exe"
if [ -f "${REPORT_EXE_AT_BOOT}.win" ]; then
    mv -f "${REPORT_EXE_AT_BOOT}.win" "$REPORT_EXE_AT_BOOT"
    echo "[entrypoint] Restored the real Reporting Service .exe (stub is re-applied once NST is up)"
fi

# =============================================================================
# Step 1: Download artifacts if not already present
# =============================================================================
STEP1_START=$(date +%s)

# Is what's on disk the artifact set THIS container was asked for?
#
# `/bc/artifacts` outlives the container in both deployments that matter: the
# `bc-artifacts` named volume locally, and a workspace bind mount that a
# self-hosted runner reuses between jobs. Until this check existed the guard
# was "does app/manifest.json exist" — so changing BC_VERSION without
# `down -v` silently booted the PREVIOUS version's artifacts, and the only
# symptom was BC reporting a version nobody asked for.
#
# Deliberately network-free. download-artifacts.sh re-resolves a short version
# against Microsoft's index to pick up hotfixes, which is right on the host but
# wrong here: in CI the host already resolved and downloaded, so a hotfix
# published in the seconds since would make the container wipe the host's
# artifacts and re-fetch ~2 GB in the middle of BC boot. The container's job is
# to use what it was given, and only fetch when it was given nothing usable.
ARTIFACT_STAMP="$ARTIFACTS/.bc-artifact-cache"
if [ -n "$BC_ARTIFACT_URL" ] && [ "$BC_ARTIFACT_URL" != "skip" ]; then
    WANT_ARTIFACTS="url|$BC_ARTIFACT_URL"
else
    WANT_ARTIFACTS="args|$BC_TYPE|$BC_VERSION|${BC_COUNTRY,,}"
fi

artifacts_intact() {
    [ -f "$ARTIFACTS/app/manifest.json" ] && [ -d "$ARTIFACTS/platform/ServiceTier" ]
}

# "hit" | "stale" | "miss"
artifact_cache_state() {
    artifacts_intact || { echo miss; return; }
    local have
    have=$(sed -n 's|^request=||p' "$ARTIFACT_STAMP" 2>/dev/null | head -1)
    # Only compare stamps of the SAME KIND. download-artifacts.sh records the
    # request as `url|<app url>` or `args|<type>|<version>|<country>`, and the
    # two are not comparable as strings: a host that downloaded by URL and a
    # container told the version/country describe the same artifacts and would
    # never match. Every insider/preview leg is exactly that shape — the
    # workflow passes bc_artifact_url to the download step, the container gets
    # BC_VERSION/BC_COUNTRY — so a string compare declared the host's freshly
    # downloaded artifacts stale, wiped them, and re-fetched from the RELEASED
    # storage account, where an insider build does not exist. What came back
    # was a 4 KB error page, reported as `BadZipFile`. Worse, the wipe happens
    # in a directory the host shares, so it also destroyed the artifacts the
    # workflow's own symbol-staging step reads, and the leg died much later on
    # an unrelated-looking AL1022.
    #
    # Different kinds therefore fall through to the manifest comparison below,
    # which asks the only question that can be answered from what is on disk:
    # is this the version and country we were asked for?
    if [ -n "$have" ] && [ "${have%%|*}" = "${WANT_ARTIFACTS%%|*}" ]; then
        [ "$have" = "$WANT_ARTIFACTS" ] && echo hit || echo "stale:downloaded for $have"
        return
    fi
    # No stamp: either artifacts staged by hand (BC_ARTIFACT_URL=skip, the
    # macOS overlay) or a volume that predates the stamp. Fall back to the
    # manifest, which carries the version/country for the app half. Only
    # contradiction counts as stale — a manifest without those fields is not
    # evidence of anything, so trust the dir as this script always used to.
    local mismatch
    mismatch=$(BC_WANT_VERSION="$BC_VERSION" BC_WANT_COUNTRY="${BC_COUNTRY,,}" \
        python3 - "$ARTIFACTS/app/manifest.json" <<'PYEOF' 2>/dev/null || true
import json, os, sys
try:
    m = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(0)
ver, country = str(m.get('version', '')), str(m.get('country', '')).lower()
want_ver, want_country = os.environ['BC_WANT_VERSION'], os.environ['BC_WANT_COUNTRY']
# Requested version is a prefix of the full one ("28.3" vs "28.3.12345.67").
if ver and want_ver and not (ver == want_ver or ver.startswith(want_ver + '.')):
    print(f"version {ver} != requested {want_ver}")
elif country and want_country and country != want_country:
    print(f"country {country} != requested {want_country}")
PYEOF
)
    [ -n "$mismatch" ] && echo "stale:$mismatch" || echo hit
}

if [ "$BC_ARTIFACT_URL" = "skip" ]; then
    if ! artifacts_intact; then
        log_step "Waiting for artifacts to be provided externally..."
        # Wait for BOTH app manifest AND platform ServiceTier to be present
        for i in $(seq 1 120); do
            artifacts_intact && break
            sleep 2
        done
        [ -f "$ARTIFACTS/app/manifest.json" ] || { log_step "ERROR: App artifacts not provided"; exit 1; }
        [ -d "$ARTIFACTS/platform/ServiceTier" ] || { log_step "ERROR: Platform artifacts not provided"; ls -la "$ARTIFACTS/platform/" 2>/dev/null; exit 1; }
    fi
else
    ARTIFACT_STATE=$(artifact_cache_state)
    case "$ARTIFACT_STATE" in
        hit)
            log_step "Artifacts already cached ($WANT_ARTIFACTS) — skipping download."
            ;;
        *)
            [ "$ARTIFACT_STATE" != "miss" ] && \
                log_step "Cached artifacts do not match this container (${ARTIFACT_STATE#stale:}) — refetching"
            if [ -n "$BC_ARTIFACT_URL" ]; then
                log_step "Downloading BC from $BC_ARTIFACT_URL..."
                /bc/scripts/download-artifacts.sh "$BC_ARTIFACT_URL" "$ARTIFACTS"
            else
                log_step "Downloading BC $BC_TYPE $BC_VERSION ($BC_COUNTRY)..."
                /bc/scripts/download-artifacts.sh "$BC_TYPE" "$BC_VERSION" "$BC_COUNTRY" "$ARTIFACTS"
            fi
            ;;
    esac
fi
log_step "Step 1 (artifacts): $(($(date +%s) - STEP1_START))s"

# Read manifest
log_step "Disk: $(df -h /bc/artifacts | tail -1 | awk '{print $4 " free"}')"
log_step "Reading manifest..."
MANIFEST="$ARTIFACTS/app/manifest.json"
ls -la "$MANIFEST" || { log_step "FATAL: manifest.json not found at $MANIFEST"; exit 1; }
DB_FILE=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1])).get("database", "").replace("\\", "/"))' "$MANIFEST")
LICENSE_FILE=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1])).get("licenseFile", "").replace("\\", "/"))' "$MANIFEST")
PLATFORM_VERSION=$(python3 -c "import json; print(json.load(open('$MANIFEST'))['platform'])")
MAJOR_VERSION=$(echo "$PLATFORM_VERSION" | cut -d. -f1)
NAV_DIR="${MAJOR_VERSION}0"

log_step "Platform: $PLATFORM_VERSION, NAV dir: $NAV_DIR, DB: $DB_FILE"

# =============================================================================
# Step 2: Copy service tier to working directory (if not already set up)
# =============================================================================
STEP2_START=$(date +%s)

# `/bc/service` is a volume, so a patched service tier survives the container.
# Reusing it is the point — Step 2 + 2b are ~6s — but only when it was built
# from THIS platform version by THIS image. The guard used to be "does
# Nav.Server.dll exist", which is true for any previous version's leftovers,
# so switching BC_VERSION or rebuilding the image with a changed StartupHook
# silently kept the old, differently-patched tier. That's why CLAUDE.md tells
# you to run `docker compose down -v` after editing the hook; with the stamp
# the rebuild is automatic and that step is no longer a footgun.
#
# The image half of the key is StartupHook.dll's size+mtime: docker gives every
# file in a rebuilt layer a fresh mtime, so it changes exactly when the image
# does, and reading it costs one stat.
# The third component is the config fingerprint. Step 2 seds SQL_SERVER,
# BC_DB_USER and BC_DB_PASSWORD into CustomSettings.config, so a volume that
# skips Step 2 keeps whatever credentials the FIRST boot was given — change
# BC_DB_PASSWORD and the warm boot authenticates with the old one while Step 3
# creates the login with the new one. Hashed, not stored plaintext: the config
# in this volume already holds the password, but a stamp file is no place to
# copy it to. Everything else Step 2 writes is a constant.
SERVICE_STAMP_FILE="$SERVICE_DIR/.bc-service-stamp"
SERVICE_CONFIG_FP=$(printf '%s|%s|%s' "$SQL_SERVER" "$BC_DB_USER" "$BC_DB_PASSWORD" | md5sum | cut -c1-12)
SERVICE_STAMP="v1|platform=$PLATFORM_VERSION|image=$(stat -c '%s-%Y' /bc/hook/StartupHook.dll 2>/dev/null || echo unknown)|config=$SERVICE_CONFIG_FP"
if [ -f "$SERVICE_DIR/Microsoft.Dynamics.Nav.Server.dll" ] && \
   [ "$(cat "$SERVICE_STAMP_FILE" 2>/dev/null || true)" != "$SERVICE_STAMP" ]; then
    log_step "Service tier in the volume was built by a different platform/image — rebuilding it"
    log_step "  found:  $(cat "$SERVICE_STAMP_FILE" 2>/dev/null || echo '(no stamp)')"
    log_step "  want:   $SERVICE_STAMP"
    # Clear the CONTENTS, not the directory: $SERVICE_DIR is a volume mount
    # point and removing it would break the mount.
    find "$SERVICE_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} + 2>/dev/null || true
fi

# Set BEFORE Step 2 runs, so it means "the volume was already fully prepared",
# not "we just prepared it".
SERVICE_STAMP_MATCH=0
[ "$(cat "$SERVICE_STAMP_FILE" 2>/dev/null || true)" = "$SERVICE_STAMP" ] && SERVICE_STAMP_MATCH=1

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
    # ManagementServicesEnabled is forced off: with it on, NST crashes at startup
    # (Microsoft.IdentityModel.S2S.Configuration.ConfigurationException: S2S40011 -
    # "At least one inbound policy should be provided") because the Admin API's AAD
    # S2S auth has no configured policy. The dev/OData/SOAP/WebSocket endpoints this
    # image actually needs don't depend on the Admin API.
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
        -e "s|ManagementServicesEnabled\" value=\"[^\"]*\"|ManagementServicesEnabled\" value=\"false\"|" \
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

# =============================================================================
# .NET runtime selection (must run every container start)
# =============================================================================
# BC 27/28 are net8.0 apps, BC 29 is net10.0. The image ships BOTH shared
# frameworks, so the only thing that has to be version-aware is where our
# framework-impersonating stubs get copied and which reference assemblies the
# server-side AL compiler sees. Read the answer straight off BC's own
# runtimeconfig instead of mapping it from the BC major — that file IS the
# contract the host uses to pick a framework, so it cannot drift.
BC_FX_VERSION=$(python3 - "$SERVICE_DIR/Microsoft.Dynamics.Nav.Server.runtimeconfig.json" <<'PYEOF' 2>/dev/null || true
import json, sys
try:
    rc = json.load(open(sys.argv[1]))["runtimeOptions"]
except Exception:
    sys.exit(1)
fws = rc.get("frameworks") or ([rc["framework"]] if "framework" in rc else [])
for fw in fws:
    if fw.get("name") == "Microsoft.NETCore.App":
        print(fw.get("version", ""))
        break
PYEOF
)
BC_FX_MAJOR="${BC_FX_VERSION%%.*}"
case "$BC_FX_MAJOR" in
    8|10) : ;;
    *)
        log_step "WARN: could not read a supported framework version from Microsoft.Dynamics.Nav.Server.runtimeconfig.json (got '${BC_FX_VERSION:-<none>}') — assuming .NET 8"
        BC_FX_MAJOR=8
        ;;
esac

NETCORE_RUNTIME_DIR=$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/${BC_FX_MAJOR}.0.* 2>/dev/null | sort -V | tail -1)
ASPNET_RUNTIME_DIR=$(ls -d /usr/share/dotnet/shared/Microsoft.AspNetCore.App/${BC_FX_MAJOR}.0.* 2>/dev/null | sort -V | tail -1)
if [ -z "$NETCORE_RUNTIME_DIR" ] || [ -z "$ASPNET_RUNTIME_DIR" ]; then
    log_step "FATAL: BC needs .NET ${BC_FX_MAJOR}.0 but that shared framework is not installed in this image"
    ls -d /usr/share/dotnet/shared/*/* 2>/dev/null
    exit 1
fi

# Per-runtime asset selection. Defaults are the .NET 8 set (unchanged for BC 27/28).
REFASM_DIR=/bc/refasm
ADDINS_DRAWING_STUB=/bc/addins-overlay/System.Drawing.Common.dll
if [ "$BC_FX_MAJOR" = "10" ]; then
    REFASM_DIR=/bc/refasm-net10
    ADDINS_DRAWING_STUB=/bc/addins-overlay/System.Drawing.Common.net10.dll
    # The stubs the StartupHook serves through its resolver are read from the
    # hook's own directory (SetupStubWithResolver uses the hook assembly's path),
    # so the net10 flavours have to land there rather than being pointed at.
    if [ -d /bc/hook-net10 ]; then
        cp /bc/hook-net10/*.dll /bc/hook/
        log_step "Overlaid net10.0 stub assemblies onto /bc/hook"
    fi
fi
log_step "BC targets .NET ${BC_FX_VERSION:-unknown} — runtime $NETCORE_RUNTIME_DIR, refasm $REFASM_DIR"

# Override framework DLLs (must run every container start, not just first setup)
cp /bc/hook/System.Security.Principal.Windows.dll "$NETCORE_RUNTIME_DIR/"
cp /bc/hook/Microsoft.AspNetCore.Server.HttpSys.dll "$ASPNET_RUNTIME_DIR/"
# BC 29's Nav.Ncl references OpenTelemetry exporter assemblies that the artifact
# doesn't ship, so NavEnvironment's ctor dies with FileNotFoundException before
# the NST comes up. Stage the real package assemblies, but only the ones Ncl
# actually references and the service dir actually lacks — BC 27/28 reference
# none of them, so this is a no-op there.
for otel in /bc/natives/otel/*.dll; do
    [ -f "$otel" ] || continue
    otel_name=$(basename "$otel" .dll)
    [ -f "$SERVICE_DIR/$otel_name.dll" ] && continue
    grep -aq "$otel_name" "$SERVICE_DIR/Microsoft.Dynamics.Nav.Ncl.dll" 2>/dev/null || continue
    cp "$otel" "$SERVICE_DIR/"
    log_step "Staged $otel_name.dll (referenced by Nav.Ncl, missing from artifact)"
done

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

# The rest of Step 2b writes patched assemblies INTO the service dir, so a
# stamp hit means every one of them is already there — including the three
# PatchNclTestPage passes, which are three `dotnet` process starts against
# DLLs that are already patched. Skipping is what makes a warm volume cheap;
# the wipe above is what makes it safe.
if [ "$SERVICE_STAMP_MATCH" = "1" ]; then
    log_step "Service tier already patched by this image ($SERVICE_STAMP) — skipping binary patch pass"
else

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
if [ ! -f "$ADDINS_DIR/System.Runtime.dll" ] && [ -d "$REFASM_DIR" ]; then
    # Layer 1: base reference assemblies (matched to the runtime BC targets)
    cp "$REFASM_DIR"/*.dll "$ADDINS_DIR/" 2>/dev/null || true
    log_step "Copied .NET reference assemblies to Add-Ins ($(ls "$REFASM_DIR"/*.dll 2>/dev/null | wc -l) files from $REFASM_DIR)"

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
    if [ -f "$ADDINS_DRAWING_STUB" ]; then
        cp "$ADDINS_DRAWING_STUB" "$ADDINS_DIR/System.Drawing.Common.dll"
        log_step "Applied DrawingStub to Add-Ins (compile-time)"
    fi

    # Report rendering natives: BC's renderer text-shapes via P/Invoke "harfbuzz"
    # (Aspose.Words) and rasterizes via SkiaSharp. The artifact only ships the
    # Windows natives (harfbuzz.dll / libSkiaSharp.dll). Link the system harfbuzz
    # and a version-matched Linux libSkiaSharp.so into the service dir (the
    # P/Invoke probe path) — BOTH or NEITHER: with harfbuzz resolvable but Skia
    # missing, the first render kills the whole NST via SkiaSharp's SKObject
    # finalizer, which is strictly worse than the graceful per-test shaper error.
    SKIA_VER=$(python3 - "$SERVICE_DIR/SkiaSharp.dll" <<'PYEOF' 2>/dev/null || true
import re, sys
try:
    data = open(sys.argv[1], 'rb').read()
    m = re.search(rb'i(\d+\.\d+\.\d+)\+', data)
    print(m.group(1).decode() if m else '')
except Exception:
    print('')
PYEOF
)
    if [ -n "$SKIA_VER" ] && [ -f "/bc/natives/skia/$SKIA_VER/libSkiaSharp.so" ]; then
        cp "/bc/natives/skia/$SKIA_VER/libSkiaSharp.so" "$SERVICE_DIR/libSkiaSharp.so"
        ln -sf /usr/lib/x86_64-linux-gnu/libharfbuzz.so.0 "$SERVICE_DIR/libharfbuzz.so"
        log_step "Report rendering natives linked (SkiaSharp $SKIA_VER + system harfbuzz)"
    else
        log_step "WARN: no bundled libSkiaSharp.so for SkiaSharp '$SKIA_VER' — report rendering stays disabled (harfbuzz not linked either)"
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

# Stamp only after every patch above has been applied. A crash partway through
# leaves the dir unstamped, so the next boot rebuilds it instead of trusting a
# half-patched tier.
printf '%s\n' "$SERVICE_STAMP" > "$SERVICE_STAMP_FILE"

fi  # SERVICE_STAMP_MATCH
log_step "Step 2b (patched assemblies): $(($(date +%s) - STEP2B_START))s"


# =============================================================================
# Step 3: Wait for SQL Server and set up database
# =============================================================================
export PATH="$PATH:/opt/mssql-tools18/bin"

log_step "Waiting for SQL Server..."
# Bounded. This loop is now the ONLY thing waiting for SQL — docker-compose.yml
# gates bc on service_started so the two boot in parallel — and an unbounded
# wait would turn "sql never came up" into "bc was unhealthy 30 minutes later",
# pointing nowhere near the cause. Generous enough for a loaded runner.
SQL_WAIT_DEADLINE=$(( $(date +%s) + ${BC_SQL_WAIT_TIMEOUT:-600} ))
until sqlcmd -S "$SQL_SERVER" -U sa -P "$SA_PASSWORD" -C -No -Q "SELECT 1" &>/dev/null; do
    if [ "$(date +%s)" -ge "$SQL_WAIT_DEADLINE" ]; then
        log_step "ERROR: SQL Server at '$SQL_SERVER' did not accept connections within ${BC_SQL_WAIT_TIMEOUT:-600}s."
        log_step "       Check the sql container: docker compose logs sql"
        exit 1
    fi
    sleep 2
done
log_step "SQL Server ready after $(( $(date +%s) - ENTRYPOINT_START ))s of container uptime."
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

# License import.
#
# By default the entrypoint imports the Cronus.bclicense that ships with
# Microsoft's BC artifact. ISVs typically need their own developer or
# partner license — historically that meant booting BC, importing the
# license via SQL/PowerShell, and restarting NST so the new license takes
# effect. That round-trip costs a full extra NST cold boot.
#
# BC_LICENSE_FILE override: if set and points to a regular file (typically
# a path inside the container, mounted via the optional license volume in
# docker-compose.yml — see BC_LICENSE_HOST_PATH there), import THAT file
# instead of the artifact's default. NST sees the right license at first
# boot, no restart needed.
#
# Workflow integration: the reusable bc-test-* workflows accept a
# `bc_license` secret (base64-encoded license bytes), decode it to a
# tempfile in the runner workspace, mount that into the container, and
# set BC_LICENSE_FILE for the entrypoint. See bc-test-from-source.yml.
LICENSE_TO_IMPORT=""
if [ -n "${BC_LICENSE_FILE:-}" ]; then
    if [ -f "$BC_LICENSE_FILE" ]; then
        LICENSE_TO_IMPORT="$BC_LICENSE_FILE"
        log_step "BC_LICENSE_FILE override: $BC_LICENSE_FILE"
    else
        log_step "WARN: BC_LICENSE_FILE=$BC_LICENSE_FILE not found or not a regular file — falling back to artifact default"
    fi
fi
if [ -z "$LICENSE_TO_IMPORT" ] && [ -n "$LICENSE_FILE" ] && [ -f "$ARTIFACTS/app/$LICENSE_FILE" ]; then
    LICENSE_TO_IMPORT="$ARTIFACTS/app/$LICENSE_FILE"
fi
if [ -n "$LICENSE_TO_IMPORT" ]; then
    $SQLCMD_DB -Q "
    UPDATE [\$ndo\$dbproperty]
    SET [license] = (SELECT BulkColumn FROM OPENROWSET(BULK '$LICENSE_TO_IMPORT', SINGLE_BLOB) AS f);
    " 2>/dev/null
    log_step "License imported: $(basename "$LICENSE_TO_IMPORT")"
fi

# Sandbox tenant type
$SQLCMD_DB -Q "UPDATE [\$ndo\$tenantproperty] SET tenanttype = 1;" 2>/dev/null

# Normalize demo-DB user time zones to UTC. The CRONUS backup ships
# [User Personalization].[Time Zone] = 'Europe/Amsterdam' for the default
# user SID; on Linux, BC's TimeZoneInfo serialize→deserialize round-trip
# throws for such ICU zones and kills every client login at OpenConnection.
# Patch #24 in the StartupHook also guards this at runtime — this is the
# data-side half so the shipped demo data is clean to begin with.
$SQLCMD_DB -Q "UPDATE [User Personalization] SET [Time Zone] = N'UTC' WHERE [Time Zone] <> N'UTC';" 2>/dev/null

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

# Service user for scripting/OData/dev endpoint/web client sign-in (SUPER access).
# Username defaults to BCRUNNER (not ADMIN) so tests can freely create/delete/disable
# an "ADMIN" user; override via BC_SERVER_USERNAME/BC_SERVER_PASSWORD (docker-compose.yml
# passes these through — see the comment there). GUID 00000000-0000-0000-0000-000000000001.
#
# [User].[Expiry Date] MUST be '1753-01-01' (BC's "never expires" sentinel/NavDateTime.Undefined),
# NOT a real future date like '2099-12-31' -- confirmed by decompiling
# SystemTableTriggers.CheckForExistenceOfSuperUserIfNecessaryAsync (Nav.Ncl.dll): its
# GenerateSuperUserAuthenticationMethodQuery join filters candidate SUPER users to
# expiryDateField.Equal(NavDateTime.Undefined), so a real Expiry Date -- even one far in the
# future and clearly intended to mean "does not expire" -- silently excludes the user from
# that query, making CheckSuperUserAuthenticationMethodQueryAsync see zero qualifying rows and
# throw "You must assign at least one user the SUPER permission set...". This fires on ANY
# operation that commits a transaction while requiring a live SUPER user check -- reproduced
# via extension installs (e.g. publishing Continia OPplus), not something specific to that app.
# 2099-12-31 was previously used here on the mistaken assumption that BC just wants "a date far
# enough in the future"; the platform's own convention (see [User Property].[WebServices Key
# Expiry Date] two lines below, which already correctly used 1753-01-01) says otherwise.
#
# The stored password hash is a fixed placeholder, NOT a hash of BC_SERVER_PASSWORD —
# NavUser.TryAuthenticate's hash check doesn't verify on Linux (the Windows hash format
# doesn't port; StartupHook Patch #16b bypasses verification and always returns true),
# so whatever the DB row holds is never compared against what the client sends. Any
# password authenticates as this user once it exists with SUPER access; the [Password]
# column only needs to be non-empty for the platform to treat the account as
# NavUserPassword-eligible. That means BC_SERVER_PASSWORD is honored end-to-end (that
# value really does work at every login surface) without needing to reproduce BC's
# hash algorithm, which isn't documented anywhere outside the platform binaries.
# The helper reads the password from stdin and creates Microsoft's V3 representation;
# plaintext is never passed on argv, written to SQL, or logged.
USER_GUID='00000000-0000-0000-0000-000000000001'
BC_SERVER_USERNAME="${BC_SERVER_USERNAME:-BCRUNNER}"
BC_SERVER_PASSWORD="${BC_SERVER_PASSWORD:-Admin123!}"
if [ -z "$BC_SERVER_PASSWORD" ]; then
    log_step "ERROR: BC_SERVER_PASSWORD must not be empty"
    exit 1
fi
PASSWORD_HASH=$(printf '%s' "$BC_SERVER_PASSWORD" | \
    env DOTNET_STARTUP_HOOKS= /bc/tools/NavUserPasswordInspector/NavUserPasswordInspector generate \
        --user-security-id "$USER_GUID")
if [[ ! "$PASSWORD_HASH" =~ ^[A-Za-z0-9+/]{22}==-V3$ ]]; then
    log_step "ERROR: password generator returned an invalid V3 representation"
    exit 1
fi
$SQLCMD_DB -b -Q "
IF NOT EXISTS (SELECT 1 FROM [User] WHERE [User Security ID] = '$USER_GUID')
BEGIN
    INSERT INTO [User] ([User Security ID], [User Name], [Full Name], [State], [Expiry Date],
        [Windows Security ID], [Change Password], [License Type], [Authentication Email],
        [Contact Email], [Exchange Identifier], [Application ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'$BC_SERVER_USERNAME', N'BC Runner', 0, '1753-01-01', N'S-1-5-21-2074085148-119339936-2019613796-1001', 0, 0, N'', N'', N'',
        '00000000-0000-0000-0000-000000000000',
        NEWID(), GETUTCDATE(), '$USER_GUID', GETUTCDATE(), '$USER_GUID');
END
ELSE
BEGIN
    UPDATE [User]
    SET [User Name] = N'$BC_SERVER_USERNAME', [\$systemModifiedAt] = GETUTCDATE(),
        [\$systemModifiedBy] = '$USER_GUID'
    WHERE [User Security ID] = '$USER_GUID';
END;

IF NOT EXISTS (SELECT 1 FROM [User Property] WHERE [User Security ID] = '$USER_GUID')
BEGIN
    INSERT INTO [User Property] ([User Security ID], [Password], [Name Identifier],
        [Authentication Key], [WebServices Key], [WebServices Key Expiry Date],
        [Authentication Object ID], [Directory Role ID], [Telemetry User ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'$PASSWORD_HASH', N'', N'', N'', '1753-01-01', N'', N'', '$USER_GUID',
        NEWID(), GETUTCDATE(), '$USER_GUID', GETUTCDATE(), '$USER_GUID');
END
ELSE
BEGIN
    UPDATE [User Property]
    SET [Password] = N'$PASSWORD_HASH', [\$systemModifiedAt] = GETUTCDATE(),
        [\$systemModifiedBy] = '$USER_GUID'
    WHERE [User Security ID] = '$USER_GUID';
END;

IF NOT EXISTS (SELECT 1 FROM [Access Control]
               WHERE [User Security ID] = '$USER_GUID' AND [Role ID] = N'SUPER'
                 AND [Company Name] = N'' AND [Scope] = 0
                 AND [App ID] = '00000000-0000-0000-0000-000000000000')
BEGIN
    INSERT INTO [Access Control] ([User Security ID], [Role ID], [Company Name], [Scope], [App ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'SUPER', N'', 0, '00000000-0000-0000-0000-000000000000',
        NEWID(), GETUTCDATE(), '$USER_GUID', GETUTCDATE(), '$USER_GUID');
END
"

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
    VALUES ('$SVC_GUID', N'YOURBC-SERVICEUSER', N'BC Service', 0, '1753-01-01', N'S-1-5-21-572246948-1269080603-559786204-1001', 0, 0, N'', N'', N'',
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
log_step "Database ready ($BC_SERVER_USERNAME). Step 3 (DB setup): $(($(date +%s) - STEP3_START))s"

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

# Advertise the web client URL on the dev endpoint (runs every boot — BC_WEBCLIENT
# can be toggled on an existing service volume). The AL debugger's launch mode (F5)
# asks the NST for the web endpoint and opens the browser at
# "<webEndpoint>?page=N&debuggingcontext=<hub-connection>" — with PublicWebBaseUrl
# empty the generated URL is the port-less http://localhost/?page=N, which lands on
# port 80 and the debugger never binds a session. Override the default with
# BC_WEBCLIENT_PUBLIC_URL when the host port differs (parallel instances).
#
# BC_WEBCLIENT_PATHBASE (e.g. "/my-prefix") mirrors the same prefix start-webclient.sh
# gave Kestrel, so a reverse proxy that routes by path (one hostname/port shared by
# several tiers) sees a matching PublicWebBaseUrl instead of one that points back
# outside the prefix. See docs/WEBCLIENT-POC.md.
if [ "${BC_WEBCLIENT:-0}" = "1" ]; then
    WEBCLIENT_PATHBASE="${BC_WEBCLIENT_PATHBASE:-}"
    if [ -n "$WEBCLIENT_PATHBASE" ]; then
        case "$WEBCLIENT_PATHBASE" in
            /*) ;;
            *) WEBCLIENT_PATHBASE="/$WEBCLIENT_PATHBASE" ;;
        esac
        WEBCLIENT_PATHBASE="${WEBCLIENT_PATHBASE%/}"
    fi
    PUBLIC_WEB_URL="${BC_WEBCLIENT_PUBLIC_URL:-http://localhost:${BC_WEBCLIENT_PORT:-8080}${WEBCLIENT_PATHBASE}/}"
    sed -i "s|PublicWebBaseUrl\" value=\"[^\"]*\"|PublicWebBaseUrl\" value=\"$PUBLIC_WEB_URL\"|" "$SERVICE_DIR/CustomSettings.config"
    log_step "PublicWebBaseUrl set to $PUBLIC_WEB_URL (AL debugger F5 / launch-mode browser URL)"
fi

log_step "Config check:"
grep -E "DatabaseServer|DatabaseName|DatabaseUserName|ProtectedDatabase" "$SERVICE_DIR/CustomSettings.config" | head -5
log_step "Pre-seeding R2R extension DLL cache..."
# BC's NST compiles all published extensions from AL source on first startup (~190s without pre-seeding).
# R2R (.app) packages already contain the pre-compiled DLLs under publishedartifacts/.
# By extracting them into the assembly cache before NST starts, the NST finds them and can skip most
# of that work — reducing first-start time dramatically (Base App alone is ~5 chunks × 40-80s of compile).
#
# IMPORTANT — instance name mismatch: CustomSettings.config has ServerInstance="BC",
# but NST itself ignores that for the assembly cache path and writes to the literal
# default name "MicrosoftDynamicsNavServer$MicrosoftDynamicsNavServer/...". Confirmed by
# inspecting a running container: the 5 Base App R2R chunks (hashes 25ABE184, 23B1F2D7,
# 923623F9, 1A31B51E, 65AD4BBF) get compiled by NST into the $MicrosoftDynamicsNavServer
# directory regardless of what the config says. We pre-seed BOTH paths to be safe — if
# a future BC version honors ServerInstance for the cache path, we're already covered.
# Hardlinking between them would be cheaper but feels fragile (different inode under any
# tooling that resolves links); we just write to both. Cost is ~233 MB extra disk on
# tmpfs/btrfs for one boot — negligible vs the wall-clock savings.
PLATFORM_VER=$(python3 -c "import json; print(json.load(open('$ARTIFACTS/app/manifest.json'))['platform'])" 2>/dev/null || true)
if [ -n "$PLATFORM_VER" ]; then
    INSTANCE=$(grep -oP 'ServerInstance" value="\K[^"]+' "$SERVICE_DIR/CustomSettings.config" 2>/dev/null || echo "BC")
    SERVER_BASE="/usr/share/Microsoft/Microsoft Dynamics NAV/$NAV_DIR/Server"

    # Persist the compiled-assembly cache across containers when a volume is
    # mounted at /bc/assembly-cache. It is 1.2 GB on BC 28.1 and lives in the
    # container filesystem, so every boot rebuilds what the previous one
    # compiled — the pre-seed below is the cheap approximation of keeping it.
    #
    # Symlinked at the `apps/assembly` level, NOT at $SERVER_BASE: that
    # directory is also BC's temp dir, and persisting scratch state across runs
    # is how a cache turns into a leak. Stamped like /bc/service, since a
    # compiled assembly is only valid for the platform that compiled it.
    if [ -d /bc/assembly-cache ]; then
        AC_STAMP="/bc/assembly-cache/.bc-assembly-stamp"
        AC_WANT="v1|platform=$PLATFORM_VER|image=$(stat -c '%s-%Y' /bc/hook/StartupHook.dll 2>/dev/null || echo unknown)"
        if [ "$(cat "$AC_STAMP" 2>/dev/null || true)" != "$AC_WANT" ]; then
            [ -e "$AC_STAMP" ] && log_step "Assembly cache was built by a different platform/image — clearing"
            find /bc/assembly-cache -mindepth 1 -maxdepth 1 -exec rm -rf {} + 2>/dev/null || true
            printf '%s\n' "$AC_WANT" > "$AC_STAMP"
        fi
        for _inst in "MicrosoftDynamicsNavServer" "$INSTANCE"; do
            _link="$SERVER_BASE/MicrosoftDynamicsNavServer\$${_inst}/apps/assembly"
            [ -L "$_link" ] && continue
            mkdir -p "$(dirname "$_link")" "/bc/assembly-cache/$_inst"
            rm -rf "$_link"
            ln -s "/bc/assembly-cache/$_inst" "$_link"
        done
        log_step "Assembly cache persisted via /bc/assembly-cache ($(du -sh /bc/assembly-cache 2>/dev/null | cut -f1) present)"
    fi
    # Primary = NST's actual path. Secondary = configured-instance path (in case a future
    # BC build starts honoring it). Dedupe if INSTANCE happens to be MicrosoftDynamicsNavServer.
    ASSEMBLY_CACHE_PRIMARY="$SERVER_BASE/MicrosoftDynamicsNavServer\$MicrosoftDynamicsNavServer/apps/assembly/release/${PLATFORM_VER}_1"
    ASSEMBLY_CACHE_ALT="$SERVER_BASE/MicrosoftDynamicsNavServer\$${INSTANCE}/apps/assembly/release/${PLATFORM_VER}_1"
    ASSEMBLY_CACHES="$ASSEMBLY_CACHE_PRIMARY"
    if [ "$ASSEMBLY_CACHE_ALT" != "$ASSEMBLY_CACHE_PRIMARY" ]; then
        ASSEMBLY_CACHES="$ASSEMBLY_CACHE_PRIMARY|$ASSEMBLY_CACHE_ALT"
    fi
    # mkdir all targets up front so the Python helper doesn't need to.
    IFS='|' read -ra _CACHE_LIST <<< "$ASSEMBLY_CACHES"
    for c in "${_CACHE_LIST[@]}"; do mkdir -p "$c"; done
    # Merkle chunk-map remapping: NST finds a seeded {hash}.dll on its own for
    # single-chunk apps, but a multi-chunk app (Base Application = 5 chunks)
    # needs the app's Merkle json to know the chunk layout — and NST looks it
    # up as {RuntimePackageId}_<N>_Merkle.json, where Runtime Package ID comes
    # from the restored DB's [Published Application] row. The json shipped in
    # the .app is named after Microsoft's build-time package id, which never
    # matches a restored .bak, so NST recompiled all 5 Base App chunks from AL
    # source on every first boot (~2 min wall each, in parallel) despite
    # byte-identical DLLs sitting in the cache. Verified empirically on BC
    # 28.3: cache Merkle names match [Runtime Package ID], Base App's shipped
    # name matches neither Package ID nor Runtime Package ID. So: dump the
    # appid → runtime-package-id map from the DB (restore finished just
    # above) and have the seeder ALSO write each Merkle json under the
    # runtime-package-id name.
    R2R_PKGMAP="/tmp/r2r-pkgmap.csv"
    $SQLCMD -d CRONUS -h -1 -W -Q "SET NOCOUNT ON; SELECT LOWER(CONVERT(varchar(36),[ID])) + ',' + UPPER(REPLACE(CONVERT(varchar(36),[Runtime Package ID]),'-','')) FROM [Published Application]" > "$R2R_PKGMAP" 2>/dev/null || : > "$R2R_PKGMAP"
    R2R_SEEDED=0
    R2R_FILTERED=0
    R2R_FAILED=0
    while IFS= read -r -d '' appfile; do
        python3 - "$appfile" "$ASSEMBLY_CACHES" "${BC_KEEP_APP_IDS_FOR_R2R:-}" "$R2R_PKGMAP" << 'PYEOF' && R2R_SEEDED=$((R2R_SEEDED + 1)) || { [ $? -eq 2 ] && R2R_FILTERED=$((R2R_FILTERED + 1)) || R2R_FAILED=$((R2R_FAILED + 1)); }
import sys, zipfile, os, json, re

app_path = sys.argv[1]
# Pipe-separated list of destination cache dirs (entrypoint passes both the
# NST-actual MicrosoftDynamicsNavServer path and the configured-instance path).
dests = [d for d in sys.argv[2].split('|') if d]
keep_ids = set(sys.argv[3].split(',')) if len(sys.argv) > 3 and sys.argv[3] else set()

# appid (lower) → runtime package id (upper, no dashes) from the DB
pkg_map = {}
if len(sys.argv) > 4 and sys.argv[4]:
    try:
        with open(sys.argv[4]) as f:
            for line in f:
                line = line.strip()
                if ',' in line:
                    aid, rtid = line.split(',', 1)
                    pkg_map[aid.strip().lower()] = rtid.strip()
    except Exception:
        pass

try:
    z = zipfile.ZipFile(app_path)
    names = z.namelist()

    app_id = None
    if 'readytorunappmanifest.json' in names:
        m = json.loads(z.read('readytorunappmanifest.json'))
        app_id = m.get('EmbeddedAppId', '').lower().strip('{}')
    elif 'NavxManifest.xml' in names:
        xml = z.read('NavxManifest.xml').decode('utf-8', errors='replace')
        match = re.search(r'(?:App)?Id\s*=\s*"([^"]+)"', xml, re.IGNORECASE)
        if match:
            app_id = match.group(1).lower().strip('{}')

    # If selective filtering is active, check this app's ID against the keep list
    if keep_ids and app_id and app_id not in keep_ids:
        sys.exit(2)  # filtered out — not an error

    runtime_pkg_id = pkg_map.get(app_id) if app_id else None

    extracted = 0
    for name in names:
        if 'publishedartifacts/' not in name:
            continue
        basename = os.path.basename(name)
        if not basename:
            continue
        # Write the entry under its shipped name, and Merkle jsons additionally
        # under the DB runtime-package-id name NST actually looks up (see the
        # comment above the pkg map dump). Shipped name pattern:
        # {32-hex-build-pkgid}_{N}_Merkle.json → {RuntimePkgId}_{N}_Merkle.json
        target_names = [basename]
        if runtime_pkg_id and basename.endswith('_Merkle.json') and '_' in basename:
            target_names.append(runtime_pkg_id + '_' + basename.split('_', 1)[1])
        # Read the entry once, then write to every destination that doesn't
        # already have it. Reading is the expensive part (zip decompress);
        # extra writes to a same-fs target are nearly free.
        payload = None
        for dest in dests:
            for tname in target_names:
                dest_path = os.path.join(dest, tname)
                if os.path.exists(dest_path):
                    continue
                if payload is None:
                    payload = z.read(name)
                with open(dest_path, 'wb') as f:
                    f.write(payload)
        extracted += 1
    sys.exit(0 if extracted > 0 else 1)
except Exception:
    sys.exit(1)
PYEOF
    done < <(find "$ARTIFACTS/app/Extensions" -name "*.app" -type f -print0 2>/dev/null)
    if [ "$R2R_FILTERED" -gt 0 ]; then
        log_step "R2R DLL cache seeded: $R2R_SEEDED apps extracted, $R2R_FILTERED filtered (selective), $R2R_FAILED skipped — caches: $ASSEMBLY_CACHES"
    else
        log_step "R2R DLL cache seeded: $R2R_SEEDED apps extracted, $R2R_FAILED skipped — caches: $ASSEMBLY_CACHES"
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
# Overridable for the same reason TieredCompilation is: an unconditional export
# makes the docker-compose passthrough silently dead. Server GC remains the
# default and is what every non-experimental path gets.
export DOTNET_gcServer="${DOTNET_gcServer:-1}"
# Default 0 (JMP-hook safety), but honor a caller override so the
# docker-compose passthrough is actually usable for A/B experiments —
# the unconditional export made that knob silently dead.
export DOTNET_TieredCompilation="${DOTNET_TieredCompilation:-0}"

# Diagnostic-only: when BC_PROFILE_NST=1, suspend NST at process startup until
# a diagnostic client (typically dotnet-trace) connects on /tmp/nst-diag.sock.
# This is the canonical way to profile .NET app startup from the very first
# instruction. Attach from the host with:
#   docker exec bc-linux-bc-1 /tmp/dotnet-trace collect \
#     --diagnostic-port /tmp/nst-diag.sock \
#     --duration 00:02:30 --format Speedscope -o /tmp/full-cold.nettrace
# The container must already have /tmp/dotnet-trace staged (curl from
# https://aka.ms/dotnet-trace/linux-x64 — it's a self-contained ELF).
if [ "${BC_PROFILE_NST:-0}" = "1" ]; then
    log_step "BC_PROFILE_NST=1: NST will suspend at startup until /tmp/nst-diag.sock client connects"
    rm -f /tmp/nst-diag.sock
    export DOTNET_DiagnosticPorts="/tmp/nst-diag.sock,suspend"
fi

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
                    -u "$BC_SERVER_USERNAME:$BC_SERVER_PASSWORD" -X POST \
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
            #
            # Tried client-side parallelization within dependency layers
            # (concurrency=4) on 2026-04-08 — measured zero wall-clock gain
            # because NST's dev endpoint serializes publishes server-side
            # (probably for SQL transaction safety / schema sync ordering).
            # 5+2+1 layered POSTs took the same ~27s as 8 serial POSTs.
            # Don't bother trying again unless NST changes behavior upstream.
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
                # SchemaUpdateMode=synchronize: only run schema sync if the
                # schema actually changed. Test framework apps have NO tables
                # (codeunits only), so schema sync is a no-op and synchronize
                # avoids unnecessary work compared to forcesync. The Test
                # Runner Extension publish below also uses synchronize and
                # works fine.
                #
                # Tried &DependencyPublishingOption=Install — BC rejects
                # 'Install' as an invalid value (Default/Strict/Ignore are
                # the only valid values, none of which auto-install missing
                # tenant deps), so we have to wipe stuck-published apps in
                # SQL beforehand instead.
                HTTP=$(curl -s -o /tmp/install-tenant.out -w "%{http_code}" --max-time 120 \
                    -u "$BC_SERVER_USERNAME:$BC_SERVER_PASSWORD" -X POST \
                    -F "file=@$APP_PATH;type=application/octet-stream" \
                    "$DEV_URL/apps?SchemaUpdateMode=synchronize" 2>/dev/null)
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

        # Default flow: republish the test framework apps that were wiped
        # from SQL (lines 573-579) plus test toolkit apps that aren't in
        # the sandbox DB but are needed by most real test apps.
        # The selective/keep-set path above handles this when BC_KEEP_APP_IDS
        # is set; this block covers the interactive / Codespace case.
        if [ -z "${BC_KEEP_APP_IDS:-}" ]; then
            echo "[entrypoint] Publishing test toolkit apps (default flow)..."
            TF_INSTALL_ORDER=$(python3 - "$ARTIFACTS" << 'PYEOF'
import sys
sys.path.insert(0, "/bc/scripts")
from _bcapp import load_artifact_apps
apps = load_artifact_apps(sys.argv[1])
# Core test framework (wiped from SQL, need republish) + test toolkit
# apps that aren't in the sandbox DB but most test apps depend on.
# This list rarely changes — last change was ~5 years ago.
NAMES = {
    "Test Runner", "Library Assert", "Library Variable Storage",
    "Permissions Mock", "Any",
    "System Application Test Library", "Business Foundation Test Libraries",
    "Tests-TestLibraries",
}
by_name = {}
for aid, info in apps.items():
    if info.get("name") in NAMES:
        by_name[aid] = info
visited, ordered = set(), []
def visit(aid):
    if aid in visited:
        return
    visited.add(aid)
    info = apps.get(aid)
    if info is None:
        return
    for dep in info.get("dependencies", []):
        visit(dep["id"])
    if aid in by_name:
        ordered.append(info["path"])
for aid in by_name:
    visit(aid)
for path in ordered:
    print(path)
PYEOF
            )
            while IFS= read -r APP_PATH; do
                [ -z "$APP_PATH" ] && continue
                NAME=$(basename "$APP_PATH")
                HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 120 \
                    -u "$BC_SERVER_USERNAME:$BC_SERVER_PASSWORD" -X POST \
                    -F "file=@$APP_PATH;type=application/octet-stream" \
                    "$DEV_URL/apps?SchemaUpdateMode=synchronize" 2>/dev/null)
                echo "[entrypoint]   $NAME: HTTP $HTTP"
            done <<< "$TF_INSTALL_ORDER"
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
                    -u "$BC_SERVER_USERNAME:$BC_SERVER_PASSWORD" -X POST \
                    -F "file=@$app;type=application/octet-stream" \
                    "$DEV_URL/apps?SchemaUpdateMode=forcesync" 2>/dev/null)
                echo "[entrypoint]   $NAME: HTTP $HTTP"
            done
        fi

        # Publish our TestRunner Extension (custom API for test execution, depends on MS Test Runner)
        if [ -f /bc/testrunner/TestRunner.app ]; then
            echo "[entrypoint] Publishing Test Runner Extension..."
            HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" --max-time 30 \
                -u "$BC_SERVER_USERNAME:$BC_SERVER_PASSWORD" -X POST \
                -F "file=@/bc/testrunner/TestRunner.app;type=application/octet-stream" \
                "$DEV_URL/apps?SchemaUpdateMode=synchronize" 2>&1)
            echo "[entrypoint] Test Runner Extension: HTTP $HTTP_CODE"
        fi
    fi
    TOTAL_ELAPSED=$(( $(date +%s) - ENTRYPOINT_START ))
    echo "[entrypoint] [${TOTAL_ELAPSED}s] Ready for extensions. Total startup: ${TOTAL_ELAPSED}s"
    touch /tmp/bc-ready

    # Opt-in web client PoC: self-host Microsoft's Prod.Client.WebCoreApp on
    # Kestrel, pointed at this NST. EXPERIMENTAL — see docs/WEBCLIENT-POC.md.
    # Supervised with a simple restart loop: this is a dev convenience tool,
    # so a crash should self-heal rather than require a docker exec.
    if [ "${BC_WEBCLIENT:-0}" = "1" ]; then
        echo "[entrypoint] BC_WEBCLIENT=1: starting web client on port ${BC_WEBCLIENT_PORT:-8080} (log: /tmp/webclient.log)"
        (
            while true; do
                /bc/scripts/start-webclient.sh >> /tmp/webclient.log 2>&1
                echo "[entrypoint] web client exited (rc=$?) — restarting in 3s"
                sleep 3
            done
        ) &
    fi
) &

wait $BC_PID
