#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
REV=bdb5276f7d85100e8e9ddd7e5ba2360a792644a9
if [[ ! -d "$ROOT/mono-basic/.git" ]]; then
    git clone https://github.com/mono/mono-basic.git "$ROOT/mono-basic"
    git -C "$ROOT/mono-basic" checkout --detach "$REV"
fi
test "$(git -C "$ROOT/mono-basic" rev-parse HEAD)" = "$REV"
git -C "$ROOT/mono-basic" diff --exit-code "$REV" -- vbruntime/Microsoft.VisualBasic
docker run --rm --network none \
    --user "$(id -u):$(id -g)" \
    --mount "type=bind,src=$ROOT,dst=/work" \
    "${IMAGE:-bc-rdlc-native-probe:21b74105}" \
    bash /work/compile-in-container.sh "$@"
