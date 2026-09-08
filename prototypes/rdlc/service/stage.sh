#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
SIDE=$(realpath "${1:?Usage: stage.sh original-service-directory original-native-bridge-directory [full-VB-runtime.dll]}")
BRIDGE=$(realpath "${2:?Original native bridge directory required}")
if [[ "$SIDE" == "$ROOT" || "$BRIDGE" == "$ROOT" ]]; then
    echo "Source paths must not be the integration output directory." >&2
    exit 2
fi
for file in Microsoft.BusinessCentral.Reporting.Service.exe \
    Microsoft.BusinessCentral.Reporting.Service.exe.config \
    Microsoft.BusinessCentral.Reporting.Common.dll \
    Microsoft.BusinessCentral.Reporting.Server.dll \
    Microsoft.Dynamics.Nav.Types.dll libgrpc_csharp_ext.x64.so; do
    test -f "$SIDE/$file"
done
for file in Microsoft.ReportViewer.Common.dll RdlcNativeBridge.dll librdlc_native.so Microsoft.VisualBasic.dll; do
    test -f "$BRIDGE/run/$file"
done
mkdir -p "$ROOT/run" "$ROOT/compiler" "$ROOT/fixtures"
cp "$SIDE"/*.dll "$SIDE/Microsoft.BusinessCentral.Reporting.Service.exe" \
    "$SIDE/Microsoft.BusinessCentral.Reporting.Service.exe.config" \
    "$SIDE/libgrpc_csharp_ext.x64.so" "$ROOT/run/"
cp "$BRIDGE/run"/*.dll "$BRIDGE/run/librdlc_native.so" "$ROOT/run/"
if [[ -n "${3:-}" ]]; then
    VB_RUNTIME=$(realpath "$3")
    test -f "$VB_RUNTIME"
    cp "$VB_RUNTIME" "$ROOT/run/Microsoft.VisualBasic.dll"
fi
cp -a "$BRIDGE/compiler/." "$ROOT/compiler/"
cp "$BRIDGE/fixtures/invoice.rdlc" "$BRIDGE/fixtures/chart.rdlc" "$ROOT/fixtures/"
cp "$SIDE/Microsoft.BusinessCentral.Telemetry.OpenTelemetry.dll" \
    "$ROOT/run/Microsoft.BusinessCentral.Telemetry.OpenTelemetry.dll.original"
cp "$SIDE/Microsoft.BusinessCentral.Reporting.Server.dll" \
    "$ROOT/run/Microsoft.BusinessCentral.Reporting.Server.dll.original"
echo "Staged original service dependencies and a private snapshot of the native engine."
