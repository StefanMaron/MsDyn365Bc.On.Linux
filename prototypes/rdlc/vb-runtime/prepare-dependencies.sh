#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
MONO_REV=bdb5276f7d85100e8e9ddd7e5ba2360a792644a9
REFERENCE_REV=ec9fa9ae770d522a5b5f0607898044b7478574a3
COMPILER_SHA256=37333f4f1e2ce55e621355d6da651dc23d4cb5f94a8f76b9478816e87f110ad9

if [[ ! -d "$ROOT/mono-basic/.git" ]]; then
    git clone https://github.com/mono/mono-basic.git "$ROOT/mono-basic"
    git -C "$ROOT/mono-basic" checkout --detach "$MONO_REV"
fi
test "$(git -C "$ROOT/mono-basic" rev-parse HEAD)" = "$MONO_REV"

if [[ ! -f "$ROOT/compiler.nupkg" ]]; then
    curl --fail --location --retry 3 \
        https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.8.0/microsoft.net.compilers.toolset.4.8.0.nupkg \
        --output "$ROOT/compiler.nupkg"
fi
printf '%s  %s\n' "$COMPILER_SHA256" "$ROOT/compiler.nupkg" | sha256sum --check
unzip -q -o "$ROOT/compiler.nupkg" -d "$ROOT/compiler"

mkdir -p "$ROOT/referencesource" "$ROOT/licenses"
curl --fail --location --retry 3 \
    "https://raw.githubusercontent.com/microsoft/referencesource/$REFERENCE_REV/Microsoft.VisualBasic/runtime/msvbalib/Helpers/StringType.vb" \
    --output "$ROOT/referencesource/StringType.original.vb"
curl --fail --location --retry 3 \
    "https://raw.githubusercontent.com/microsoft/referencesource/$REFERENCE_REV/LICENSE.txt" \
    --output "$ROOT/referencesource/LICENSE.txt"
cp "$ROOT/referencesource/StringType.original.vb" "$ROOT/referencesource/StringType.vb"
patch "$ROOT/referencesource/StringType.vb" "$ROOT/referencesource/stringtype-mono.patch"
cp "$ROOT/referencesource/LICENSE.txt" "$ROOT/licenses/Microsoft-referencesource-MIT.txt"
cp "$ROOT/mono-basic/LICENSE" "$ROOT/licenses/mono-basic-LICENSE.txt"
cp "$ROOT/mono-basic/vbruntime/Microsoft.VisualBasic/AssemblyInfo.vb" "$ROOT/licenses/Mono-library-copyright-and-license.vb"
