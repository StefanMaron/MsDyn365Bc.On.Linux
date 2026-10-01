#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
docker run --rm --network none --ulimit core=0 \
    --env PATH=/work/compiler:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    --mount "type=bind,src=$ROOT,dst=/work" \
    -w /work/run bc-rdlc-native-probe:21b74105 bash -c '
set -euo pipefail
if [ ! -f Microsoft.BusinessCentral.Telemetry.OpenTelemetry.dll.original ]; then
    cp Microsoft.BusinessCentral.Telemetry.OpenTelemetry.dll Microsoft.BusinessCentral.Telemetry.OpenTelemetry.dll.original
fi
if [ ! -f Microsoft.BusinessCentral.Reporting.Server.dll.original ]; then
    cp Microsoft.BusinessCentral.Reporting.Server.dll Microsoft.BusinessCentral.Reporting.Server.dll.original
fi
mcs -target:library -r:System.Drawing -out:HeadlessPageSettings.dll /work/HeadlessPageSettings.cs /work/MonoRenderingContext.cs
mcs -out:PatchServiceCompat.exe \
    -r:HeadlessPageSettings.dll \
    -r:/usr/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll \
    /work/PatchServiceCompat.cs
mono PatchServiceCompat.exe Microsoft.BusinessCentral.Telemetry.OpenTelemetry.dll.original \
    Microsoft.BusinessCentral.Telemetry.OpenTelemetry.dll \
    Microsoft.BusinessCentral.Reporting.Server.dll.original Microsoft.BusinessCentral.Reporting.Server.dll
mcs -sdk:4.7.2 -out:ServiceRender.exe -r:Microsoft.BusinessCentral.Reporting.Common.dll \
    -r:Microsoft.Dynamics.Nav.Types.dll -r:Google.Protobuf.dll \
    -r:Grpc.Core.dll -r:Grpc.Core.Api.dll \
    -r:System.Runtime.Serialization -r:System.Data -r:System.Drawing -r:System.Memory.dll \
    -r:/usr/lib/mono/4.5/Facades/netstandard.dll /work/ServiceRender.cs
MONO_TRACE=E:all bash /work/start-service.sh \
    > /work/service.log 2>&1 &
service_pid=$!
trap '\''kill "$service_pid" 2>/dev/null || true; wait "$service_pid" 2>/dev/null || true'\'' EXIT
mono ServiceRender.exe /work/service-test.rdlc /work/service-result.pdf
mono ServiceRender.exe /work/service-test.rdlc /work/service-compact-dedup.pdf 10000 true true
mono ServiceRender.exe /work/service-test.rdlc /work/service-repeat.pdf 7 false false
mono ServiceRender.exe /work/fixtures/invoice.rdlc /work/service-invoice.pdf 120 true true invoice
mono ServiceRender.exe /work/fixtures/chart.rdlc /work/service-chart.pdf 120 true true invoice
'
for pdf in service-result service-compact-dedup service-repeat service-invoice service-chart; do
    pdfinfo "$ROOT/$pdf.pdf" > "$ROOT/$pdf.info"
    pdftotext -layout "$ROOT/$pdf.pdf" "$ROOT/$pdf.txt"
    pdffonts "$ROOT/$pdf.pdf" > "$ROOT/$pdf.fonts"
done
grep -Fq 'rows=3 | total=7.50' "$ROOT/service-result.txt"
grep -Fq 'rows=10000 |' "$ROOT/service-compact-dedup.txt"
grep -Fq 'total=62506250.00' "$ROOT/service-compact-dedup.txt"
grep -Fq 'rows=7 | total=35.00' "$ROOT/service-repeat.txt"
grep -Eq '^Pages:[[:space:]]+4$' "$ROOT/service-invoice.info"
grep -Fq 'Invoice line 120' "$ROOT/service-invoice.txt"
grep -Fq '9075.00' "$ROOT/service-invoice.txt"
grep -Fq 'Embedded VB executed' "$ROOT/service-invoice.txt"
test "$(grep -Ec 'Invoice line [0-9]{3}' "$ROOT/service-invoice.txt")" -eq 120
pdfimages -list "$ROOT/service-chart.pdf" > "$ROOT/service-chart.images"
awk '$3 == "image" && $4 == 1800 && $5 == 1200 && $13 == 300 && $14 == 300 { found=1 } END { exit !found }' \
    "$ROOT/service-chart.images"
echo "Original-service PDFs contain the expected aggregates and all 120 invoice rows across four pages."
