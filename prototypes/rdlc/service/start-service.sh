#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
export PATH="$ROOT/compiler:$PATH"
cd "$ROOT/run"
mono_options=()
if [[ -n "${MONO_TRACE:-}" ]]; then
    mono_options+=("--trace=$MONO_TRACE")
fi
echo "Starting original BC Reporting.Service under Mono; Linux compatibility adapters enabled." >&2
echo "AppDomain resource counters and Windows ETW exporters are unavailable; PDF rendering remains original." >&2
exec mono "${mono_options[@]}" Microsoft.BusinessCentral.Reporting.Service.exe \
    -PortNumber "${REPORTING_PORT:-5005}" -Name ReportingService
