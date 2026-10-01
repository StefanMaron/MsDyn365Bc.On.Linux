#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
BRIDGE=${1:?Usage: render-probes.sh original-native-bridge-directory [mono|reference|icu]}
VARIANT=${2:-mono}
if [[ "$VARIANT" == mono ]]; then
    RUNTIME="$ROOT/Microsoft.VisualBasic.dll"
elif [[ "$VARIANT" == reference ]]; then
    RUNTIME="$ROOT/compatible/Microsoft.VisualBasic.dll"
elif [[ "$VARIANT" == icu ]]; then
    RUNTIME="$ROOT/icu/Microsoft.VisualBasic.dll"
else
    echo "Unknown runtime variant: $VARIANT" >&2
    exit 2
fi
if [[ ! -d "$ROOT/run" ]]; then
    cp -a "$BRIDGE/run" "$ROOT/run"
fi
cp "$BRIDGE/compiler/vbnc" "$ROOT/compiler/vbnc"
cp "$RUNTIME" "$ROOT/run/Microsoft.VisualBasic.dll"
if [[ "$VARIANT" == icu ]]; then
    cp "$ROOT/icu/libBCRdlc.IcuBridge.so" "$ROOT/icu/libSystem.Globalization.Native.so" "$ROOT/run/"
fi
for layout in filter-numeric filter-like; do
    docker run --rm --network none --ulimit core=0 \
        --env PATH=/work/compiler:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
        --mount "type=bind,src=$ROOT,dst=/work" \
        -w /work/run "${IMAGE:-bc-rdlc-native-probe:21b74105}" \
        mono Render.exe "/work/$layout.rdlc" "$layout-$VARIANT.pdf" 120
    pdftotext "$ROOT/run/$layout-$VARIANT.pdf" "$ROOT/run/$layout-$VARIANT.txt"
done
grep -F 'ROWS=6; SUM=21; AMOUNT=26.25' "$ROOT/run/filter-numeric-$VARIANT.txt"
grep -F 'ROWS=3; SUM=6; AMOUNT=7.50' "$ROOT/run/filter-like-$VARIANT.txt"
docker run --rm --network none --ulimit core=0 \
    --env PATH=/work/compiler:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    --mount "type=bind,src=$ROOT,dst=/work" \
    --mount "type=bind,src=$BRIDGE/fixtures,dst=/fixtures,readonly" \
    -w /work/run "${IMAGE:-bc-rdlc-native-probe:21b74105}" \
    mono Render.exe /fixtures/chart.rdlc "chart-$VARIANT.pdf" 120
