#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
BRIDGE=${1:?Usage: validate-icu.sh original-native-bridge-directory}
IMAGE=${IMAGE:-bc-rdlc-native-probe:21b74105}
cd "$ROOT"
bash probe-runtime.sh icu > icu-api.log
docker run --rm --network none --user "$(id -u):$(id -g)" \
    --mount "type=bind,src=$ROOT,dst=/work" -w /work "$IMAGE" bash -c '
    set -euo pipefail
    mcs -out:CollationMatrix.exe CollationMatrix.cs
    mono CollationMatrix.exe /work/icu/Microsoft.VisualBasic.dll > matrix-mono-icu.tsv
    dotnet exec --runtimeconfig net8.runtimeconfig.json CollationMatrix.exe --oracle > matrix-net8-container.tsv
    cd icu
    mcs -r:Microsoft.VisualBasic.dll -out:IcuLikeProbe.exe ../IcuLikeProbe.cs
    mono IcuLikeProbe.exe > ../icu-like.log
    mcs -r:Microsoft.VisualBasic.dll -out:IcuDomainProbe.exe ../IcuDomainProbe.cs
    mono IcuDomainProbe.exe > ../icu-domains.log'
dotnet exec --runtimeconfig net8.runtimeconfig.json CollationMatrix.exe --oracle > matrix-net8-host.tsv
diff -u matrix-net8-container.tsv matrix-mono-icu.tsv
diff -u matrix-net8-host.tsv matrix-mono-icu.tsv
printf 'PASS: %s matrix rows (Compare + LastIndexOf) match .NET 8 on host and container\n' "$(wc -l < matrix-mono-icu.tsv)"
tail -1 icu-api.log
tail -1 icu-like.log
tail -1 icu-domains.log

bash render-probes.sh "$BRIDGE" icu > filters-icu.log
render() {
    local directory=$1 layout=$2 output=$3
    docker run --rm --network none --ulimit core=0 \
        --env PATH=/work/compiler:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
        --mount "type=bind,src=$ROOT,dst=/work" -w "/work/$directory" "$IMAGE" \
        mono Render.exe "/work/$layout" "$output" 120
}
render run filter-turkish.rdlc filter-turkish-icu.pdf > filter-turkish-icu.log 2>&1
pdftotext run/filter-turkish-icu.pdf filter-turkish-after.txt
grep -F 'ROWS=2; SUM=3; AMOUNT=3,75' filter-turkish-after.txt

mkdir -p "$ROOT/run-fail-closed"
cp -a "$ROOT/run/." "$ROOT/run-fail-closed/"
rm -f "$ROOT/run-fail-closed/libSystem.Globalization.Native.so"
test ! -e "$ROOT/run-fail-closed/must-not-exist.pdf"
if render run-fail-closed filter-like.rdlc must-not-exist.pdf > fail-closed-render.log 2>&1; then
    echo "ERROR: renderer accepted a Like filter without its ICU dependency" >&2
    exit 1
else
    status=$?
    if [[ $status != 1 ]]; then
        echo "Unexpected renderer failure status: $status" >&2
        exit "$status"
    fi
fi
test ! -e "$ROOT/run-fail-closed/must-not-exist.pdf"
grep -q 'DllNotFoundException: libBCRdlc.IcuBridge.so' fail-closed-render.log
echo "PASS: missing native dependency aborts original ReportViewer rendering; no PDF emitted"
sha256sum --check binaries.sha256
