#!/usr/bin/env bash
set -euo pipefail
cd /work/mono-basic/vbruntime/Microsoft.VisualBasic
mapfile -t sources < Microsoft.VisualBasic.dll.sources
output=/work/Microsoft.VisualBasic.dll
if [[ "${1:-}" == --reference-stringtype || "${1:-}" == --icu-stringtype ]]; then
    filtered=()
    for source in "${sources[@]}"; do
        if [[ "$source" != Microsoft.VisualBasic.CompilerServices/StringType.vb ]]; then
            filtered+=("$source")
        fi
    done
    if [[ "$1" == --icu-stringtype ]]; then
        sources=("${filtered[@]}" /work/referencesource/StringType.Icu.vb /work/NativeIcuCompareInfo.vb)
        mkdir -p /work/icu
        output=/work/icu/Microsoft.VisualBasic.dll
        gcc -shared -fPIC -O2 -Wall -Wextra -Werror -pthread \
            /work/icu-bridge.c -L/work/icu -l:libSystem.Globalization.Native.so \
            -Wl,-z,defs -Wl,-rpath,'$ORIGIN' -o /work/icu/libBCRdlc.IcuBridge.so
    else
        sources=("${filtered[@]}" /work/referencesource/StringType.vb)
        mkdir -p /work/compatible
        output=/work/compatible/Microsoft.VisualBasic.dll
    fi
elif [[ $# != 0 ]]; then
    echo "Usage: compile-in-container.sh [--reference-stringtype|--icu-stringtype]" >&2
    exit 2
fi
mono /work/compiler/tasks/net472/vbc.exe \
    /noconfig /sdkpath:/usr/lib/mono/4.5-api /vbruntime- \
    /define:NET_VER=4.5 '/define:_MYTYPE="Empty"' \
    /target:library /optionstrict+ /deterministic+ /optimize+ \
    /imports:System,System.Collections,System.Data,System.Diagnostics,System.Collections.Generic \
    /reference:System.dll,System.Windows.Forms.dll,System.Data.dll,System.Drawing.dll,System.Web.dll,System.Xml.dll \
    /resource:strings2.resources,strings.resources \
    "/out:$output" "${sources[@]}"
