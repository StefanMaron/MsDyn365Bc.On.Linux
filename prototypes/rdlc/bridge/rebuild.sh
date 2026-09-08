#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "$0")" && pwd)
SOURCE=${1:?Usage: rebuild.sh original-Microsoft.ReportViewer.Common.dll}
cd "$ROOT"
docker run --rm --network none --mount "type=bind,src=$ROOT,dst=/work" \
    bc-rdlc-native-probe:21b74105 sh -ec '
    mcs -langversion:7.2 -r:System.Security -r:System.Drawing -r:run/Microsoft.VisualBasic.dll \
        -r:/usr/lib/mono/4.5/Facades/netstandard.dll -target:library \
        -out:RdlcNativeBridge.dll Bridge.cs DrawingBridge.cs font/FontBridge.cs
    cp RdlcNativeBridge.dll run/
    gcc -std=c11 -D_POSIX_C_SOURCE=200809L -shared -fPIC -pthread -O2 \
        -Wall -Wextra -Werror -Wl,-z,defs text.c font/rdlc_font.c \
        -o run/librdlc_native.so \
        $(pkg-config --cflags --libs pangocairo fontconfig freetype2 harfbuzz harfbuzz-subset) -lm
    mcs -langversion:7.2 -out:run/Render.exe -r:run/Microsoft.ReportViewer.WebForms.dll \
        -r:System.Data -r:System.Drawing -r:System.Web Render.cs
    '
dotnet run --project patcher/Patcher.csproj -c Release -- "$SOURCE" \
    run/Microsoft.ReportViewer.Common.dll RdlcNativeBridge.dll framework \
    060066D0,060066B9,06006855,0600685B,06006A31,06006A64,06006A65,06006A66,06006875,0600687B \
    > patch.log
dotnet run --project patcher/Patcher.csproj -c Release -- \
    "$(dirname "$SOURCE")/Microsoft.ReportViewer.DataVisualization.dll" \
    run/Microsoft.ReportViewer.DataVisualization.dll RdlcNativeBridge.dll framework \
    >> patch.log
