#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
INPUT=$(realpath "${1:?Usage: render.sh layout.rdlc output-name.pdf}")
OUTPUT=${2:?Output filename required}
if [[ "$OUTPUT" == */* ]]; then
    echo "Output must be a filename inside the probe run directory." >&2
    exit 2
fi
docker run --rm --network none --ulimit core=0 \
    --env RDLC_TRACE="${RDLC_TRACE:-0}" \
    --env PATH=/work/compiler:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    --mount "type=bind,src=$ROOT,dst=/work" \
    --mount "type=bind,src=$INPUT,dst=/input.rdlc,readonly" \
    -w /work/run bc-rdlc-native-probe:21b74105 \
    mono Render.exe /input.rdlc "/work/run/$OUTPUT" "${3:-120}"
