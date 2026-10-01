#!/usr/bin/env bash
# Launch Microsoft's own Reporting Service under Mono. Started and supervised by
# NativeRdlcService in the startup hook, never by hand and never by BC's Windows
# side-service lifecycle (Patch #18 keeps that disabled).
#
# The hook clears the environment before exec'ing this, so report expressions
# cannot read the NST's SQL credentials or inherit DOTNET_STARTUP_HOOKS.
set -euo pipefail

SERVICE_DIR=${BC_RDLC_SERVICE_DIR:-/bc/service/SideServices}
cd "$SERVICE_DIR"

# libgrpc_csharp_ext and the ICU bridge are staged next to the service.
export LD_LIBRARY_PATH="$SERVICE_DIR${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
# `vbnc` here is our Roslyn shim: Mono's VB CodeDOM provider shells out to it to
# compile the report expression assembly.
export PATH="/bc/rdlc/compiler:$PATH"
export MONO_PATH="$SERVICE_DIR"

LOG=${BC_RDLC_LOG:-/run/bc-rdlc/service.log}
mkdir -p "$(dirname "$LOG")"

opts=()
if [ "${RDLC_TRACE:-0}" = "1" ]; then
    # Mono's StackTrace.AddFrames throws inside BC's exception telemetry, which
    # can turn a real render error into a bare gRPC "Unknown". This keeps the
    # original exception visible.
    opts+=("--trace=E:all")
fi

# Always keep the renderer's own output. BC maps a render failure to a bare
# "internal error while rendering" over gRPC, and Mono's StackTrace.AddFrames
# throws inside BC's exception telemetry, so without this the actual cause —
# a missing native, an invalid layout, a font with no glyph — is invisible.
exec >>"$LOG" 2>&1

exec mono "${opts[@]}" Microsoft.BusinessCentral.Reporting.Service.exe \
    -PortNumber "${BC_RDLC_PORT:-5005}" -Name ReportingService
