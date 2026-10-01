#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
PACKAGE="$ROOT/dotnet-runtime-8.0.30.nupkg"
PACKAGE_SHA512=3c99a46f90b1ddfaeaf37547a27dbbcaa8d9f22ce4403dbaa5db751115376588debd4d2a837f823851573c40ea917ec58f8c770ba026e4da518dce01d97a6354
NATIVE_SHA256=7da0130279f621ace5cabfc256657947cf4498e7733dc85883ad7f930a8783bc
if [[ ! -f "$PACKAGE" ]]; then
    curl --fail --location --retry 3 \
        https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.linux-x64/8.0.30/microsoft.netcore.app.runtime.linux-x64.8.0.30.nupkg \
        --output "$PACKAGE"
fi
printf '%s  %s\n' "$PACKAGE_SHA512" "$PACKAGE" | sha512sum --check
mkdir -p "$ROOT/icu" "$ROOT/licenses"
unzip -p "$PACKAGE" runtimes/linux-x64/native/libSystem.Globalization.Native.so > "$ROOT/icu/libSystem.Globalization.Native.so"
printf '%s  %s\n' "$NATIVE_SHA256" "$ROOT/icu/libSystem.Globalization.Native.so" | sha256sum --check
unzip -p "$PACKAGE" LICENSE.TXT > "$ROOT/licenses/dotnet-runtime-LICENSE.TXT"
unzip -p "$PACKAGE" THIRD-PARTY-NOTICES.TXT > "$ROOT/licenses/dotnet-runtime-THIRD-PARTY-NOTICES.TXT"
cp "$ROOT/referencesource/StringType.vb" "$ROOT/referencesource/StringType.Icu.vb"
patch "$ROOT/referencesource/StringType.Icu.vb" "$ROOT/referencesource/stringtype-icu.patch"
