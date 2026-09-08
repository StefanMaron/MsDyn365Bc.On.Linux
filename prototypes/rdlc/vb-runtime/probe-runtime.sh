#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
case "${1:-reference}" in
    mono) DIRECTORY=/work ;;
    reference) DIRECTORY=/work/compatible ;;
    icu) DIRECTORY=/work/icu ;;
    *) echo "Usage: probe-runtime.sh [mono|reference|icu]" >&2; exit 2 ;;
esac
docker run --rm --network none --user "$(id -u):$(id -g)" \
    --mount "type=bind,src=$ROOT,dst=/work" \
    -w "$DIRECTORY" "${IMAGE:-bc-rdlc-native-probe:21b74105}" \
    bash -c 'mcs -r:Microsoft.VisualBasic.dll -out:RuntimeProbe-current.exe /work/RuntimeProbe.cs && mono RuntimeProbe-current.exe'
