#!/usr/bin/env bash
# Build every artifact the Mono RDLC renderer needs, from source, into $OUT.
#
# Runs inside a container that has mono-devel, gcc, pkg-config, the
# pango/fontconfig/freetype/harfbuzz (incl. subset) dev packages, git, curl,
# unzip and patch. src/Dockerfile calls this in its builder stage; it is also
# runnable by hand for iteration:
#
#   docker run --rm -v "$PWD:/src" -w /src <image> scripts/build-rdlc.sh /out
#
# Nothing Microsoft-owned is fetched here. The ReportViewer assemblies are
# patched at container start from the BC artifact, not at image build time —
# see the RDLC step in scripts/entrypoint.sh.
set -euo pipefail

SRC=${SRC:-$(cd -- "$(dirname -- "$0")/.." && pwd)}
OUT=${1:?Usage: build-rdlc.sh <output-directory>}
WORK=${WORK:-$(mktemp -d)}
mkdir -p "$OUT" "$WORK"

MONO_BASIC_REV=bdb5276f7d85100e8e9ddd7e5ba2360a792644a9
REFERENCESOURCE_REV=ec9fa9ae770d522a5b5f0607898044b7478574a3
COMPILER_SHA256=37333f4f1e2ce55e621355d6da651dc23d4cb5f94a8f76b9478816e87f110ad9
RUNTIME_SHA512=3c99a46f90b1ddfaeaf37547a27dbbcaa8d9f22ce4403dbaa5db751115376588debd4d2a837f823851573c40ea917ec58f8c770ba026e4da518dce01d97a6354
GLOBALIZATION_SHA256=7da0130279f621ace5cabfc256657947cf4498e7733dc85883ad7f930a8783bc
GRPC_VERSION=2.46.6
GRPC_NATIVE_SHA256=776dc19edd2649800a93bd35b74b61d0b81ae840edc659cffef504ee28b5d581

log() { echo "[build-rdlc] $*" >&2; }

# ---------------------------------------------------------------- dependencies
log "fetching pinned sources"
git clone --quiet https://github.com/mono/mono-basic.git "$WORK/mono-basic"
git -C "$WORK/mono-basic" checkout --quiet --detach "$MONO_BASIC_REV"
test "$(git -C "$WORK/mono-basic" rev-parse HEAD)" = "$MONO_BASIC_REV"

curl --fail --location --silent --retry 3 \
    "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.8.0/microsoft.net.compilers.toolset.4.8.0.nupkg" \
    --output "$WORK/compiler.nupkg"
printf '%s  %s\n' "$COMPILER_SHA256" "$WORK/compiler.nupkg" | sha256sum --check --quiet
unzip -q -o "$WORK/compiler.nupkg" -d "$WORK/compiler"

# The official .NET 8 native ICU implementation. This is what makes VB's Like
# and string comparisons match real .NET on locale-sensitive input (the two
# Turkish dotted/dotless-I cases Mono's own CompareInfo gets wrong).
curl --fail --location --silent --retry 3 \
    "https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.linux-x64/8.0.30/microsoft.netcore.app.runtime.linux-x64.8.0.30.nupkg" \
    --output "$WORK/runtime.nupkg"
printf '%s  %s\n' "$RUNTIME_SHA512" "$WORK/runtime.nupkg" | sha512sum --check --quiet
unzip -p "$WORK/runtime.nupkg" runtimes/linux-x64/native/libSystem.Globalization.Native.so \
    > "$OUT/libSystem.Globalization.Native.so"
printf '%s  %s\n' "$GLOBALIZATION_SHA256" "$OUT/libSystem.Globalization.Native.so" | sha256sum --check --quiet

# Grpc.Core 2.46.6 is the exact managed version the BC artifact carries, and it
# is the only place a matching Linux native exists. Do not substitute another.
curl --fail --location --silent --retry 3 \
    "https://api.nuget.org/v3-flatcontainer/grpc.core/$GRPC_VERSION/grpc.core.$GRPC_VERSION.nupkg" \
    --output "$WORK/grpc.nupkg"
unzip -p "$WORK/grpc.nupkg" "runtimes/linux-x64/native/libgrpc_csharp_ext.x64.so" \
    > "$OUT/libgrpc_csharp_ext.x64.so"
printf '%s  %s\n' "$GRPC_NATIVE_SHA256" "$OUT/libgrpc_csharp_ext.x64.so" | sha256sum --check --quiet

VB=$SRC/prototypes/rdlc/vb-runtime
mkdir -p "$WORK/referencesource"
curl --fail --location --silent --retry 3 \
    "https://raw.githubusercontent.com/microsoft/referencesource/$REFERENCESOURCE_REV/Microsoft.VisualBasic/runtime/msvbalib/Helpers/StringType.vb" \
    --output "$WORK/referencesource/StringType.original.vb"
cp "$WORK/referencesource/StringType.original.vb" "$WORK/referencesource/StringType.vb"
patch --silent "$WORK/referencesource/StringType.vb" "$VB/referencesource/stringtype-mono.patch"
cp "$WORK/referencesource/StringType.vb" "$WORK/referencesource/StringType.Icu.vb"
patch --silent "$WORK/referencesource/StringType.Icu.vb" "$VB/referencesource/stringtype-icu.patch"

mkdir -p "$OUT/licenses"
curl --fail --location --silent --retry 3 \
    "https://raw.githubusercontent.com/microsoft/referencesource/$REFERENCESOURCE_REV/LICENSE.txt" \
    --output "$OUT/licenses/Microsoft-referencesource-MIT.txt"
cp "$WORK/mono-basic/LICENSE" "$OUT/licenses/mono-basic-LICENSE.txt"
unzip -p "$WORK/runtime.nupkg" LICENSE.TXT > "$OUT/licenses/dotnet-runtime-LICENSE.TXT"

# ------------------------------------------------------------- ICU shim + VB
log "building the ICU collation bridge"
gcc -shared -fPIC -O2 -Wall -Wextra -Werror -pthread \
    "$VB/icu-bridge.c" -L"$OUT" -l:libSystem.Globalization.Native.so \
    -Wl,-z,defs -Wl,-rpath,'$ORIGIN' -o "$OUT/libBCRdlc.IcuBridge.so"

# Microsoft.VisualBasic for RDLC expressions: the complete MIT Mono runtime with
# Microsoft's own MIT referencesource StringType swapped in (Mono's is an
# invariant regex and gets four real Like edge cases wrong), over native ICU.
# Delay-signed as 10.0.0.0/b03f5f7f11d50a3a — NOT Microsoft-signed.
log "building Microsoft.VisualBasic.dll"
(
    cd "$WORK/mono-basic/vbruntime/Microsoft.VisualBasic"
    mapfile -t sources < Microsoft.VisualBasic.dll.sources
    filtered=()
    for source in "${sources[@]}"; do
        [ "$source" = "Microsoft.VisualBasic.CompilerServices/StringType.vb" ] || filtered+=("$source")
    done
    mono "$WORK/compiler/tasks/net472/vbc.exe" \
        /noconfig /sdkpath:/usr/lib/mono/4.5-api /vbruntime- \
        /define:NET_VER=4.5 '/define:_MYTYPE="Empty"' \
        /target:library /optionstrict+ /deterministic+ /optimize+ \
        /imports:System,System.Collections,System.Data,System.Diagnostics,System.Collections.Generic \
        /reference:System.dll,System.Windows.Forms.dll,System.Data.dll,System.Drawing.dll,System.Web.dll,System.Xml.dll \
        /resource:strings2.resources,strings.resources \
        "/out:$OUT/Microsoft.VisualBasic.dll" \
        "${filtered[@]}" "$WORK/referencesource/StringType.Icu.vb" "$VB/NativeIcuCompareInfo.vb"
)

# ------------------------------------------------------------- the bridge
# Managed half: Uniscribe and the GDI font/metric calls, reimplemented over
# Pango/HarfBuzz/FreeType. Compiled against net472 because that is what the
# original ReportViewer assemblies target.
log "building RdlcNativeBridge.dll"
mcs -langversion:7.2 -target:library \
    -r:System.Security -r:System.Drawing -r:"$OUT/Microsoft.VisualBasic.dll" \
    -r:/usr/lib/mono/4.5/Facades/netstandard.dll \
    -out:"$OUT/RdlcNativeBridge.dll" \
    "$SRC/src/RdlcNative/Bridge.cs" \
    "$SRC/src/RdlcNative/DrawingBridge.cs" \
    "$SRC/src/RdlcNative/font/FontBridge.cs"

# Native half. text.c and rdlc_font.c MUST land in one shared object: the font
# registry is a process-global and two copies of it in two loaded libraries do
# not see each other's tokens.
log "building librdlc_native.so"
gcc -std=c11 -D_POSIX_C_SOURCE=200809L -shared -fPIC -pthread -O2 \
    -Wall -Wextra -Werror -Wl,-z,defs \
    "$SRC/src/RdlcNative/native/text.c" "$SRC/src/RdlcNative/font/rdlc_font.c" \
    -o "$OUT/librdlc_native.so" \
    $(pkg-config --cflags --libs pangocairo fontconfig freetype2 harfbuzz harfbuzz-subset) -lm

# ------------------------------------------- side-service compatibility patcher
# Applied at container start to BC's own reporting assemblies: removes the two
# Windows-ETW exporter registrations, redirects the printer-dependent
# PageSettings getters, drops the AppDomain monitoring counters Mono lacks, and
# rehydrates CultureInfo across the reporting AppDomain boundary.
log "building the side-service compatibility patcher"
CECIL=$(find /usr/lib/mono/gac/Mono.Cecil -name Mono.Cecil.dll | head -1)
test -n "$CECIL"
SVC=$SRC/prototypes/rdlc/service
mcs -target:library -r:System.Drawing -out:"$OUT/HeadlessPageSettings.dll" \
    "$SVC/HeadlessPageSettings.cs" "$SVC/MonoRenderingContext.cs"
mcs -out:"$OUT/PatchServiceCompat.exe" \
    -r:"$OUT/HeadlessPageSettings.dll" -r:"$CECIL" "$SVC/PatchServiceCompat.cs"
cp "$CECIL" "$OUT/"

# Roslyn's net472 VB compiler, plus the `vbnc` shim Mono's CodeDOM provider
# actually invokes. Put $OUT/compiler on PATH for the renderer process.
mkdir -p "$OUT/compiler/tasks"
cp -a "$WORK/compiler/tasks/net472" "$OUT/compiler/tasks/net472"
install -m 755 "$SRC/prototypes/rdlc/bridge/vbnc" "$OUT/compiler/vbnc"

log "done:"
ls -la "$OUT" >&2
