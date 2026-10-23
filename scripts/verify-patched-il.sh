#!/usr/bin/env bash
# verify-patched-il.sh — Check that PatchNclTestPage leaves BC's assemblies valid IL.
#
# Usage:
#   scripts/verify-patched-il.sh <bc-service-dir> [patcher-dll]
#
#   <bc-service-dir>  a directory holding BC's service tier DLLs (Nav.Ncl, Nav.Types,
#                     YamlDotNet), e.g. <artifact>/platform/ServiceTier/PFiles64/
#                     Microsoft Dynamics NAV/<n>/Service
#   [patcher-dll]     default src/tools/PatchNclTestPage/bin/Release/net8.0/PatchNclTestPage.dll
#
# Why this exists: the patches insert instructions into compiled methods. A mistake there
# does not fail at patch time or at build time. It fails when the JIT first compiles the
# method, as an InvalidProgramException deep inside BC's startup, with a stack that names
# nothing of ours. The case that prompted this script was a short `br.s` pushed out of range
# by an insertion: Cecil wrote the truncated offset, the branch landed on the wrong
# instruction, and the NST died in WindowsLanguageHelper's static constructor.
#
# What it does: copies the service DLLs to a scratch directory, applies the same patches the
# entrypoint applies, runs `ilverify` on the original and the patched copy, and fails if the
# patched one reports an error the original does not. ilverify already reports a few hundred
# errors on Microsoft's own assemblies (unmanaged pointers, missing optional references), so
# only the difference is meaningful.
#
# Needs: dotnet SDK (ilverify: `dotnet tool install -g dotnet-ilverify`), python3.
set -uo pipefail
unset CDPATH

SVC="${1:?usage: verify-patched-il.sh <bc-service-dir> [patcher-dll]}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PATCHER="${2:-$SCRIPT_DIR/../src/tools/PatchNclTestPage/bin/Release/net8.0/PatchNclTestPage.dll}"

for need in ilverify dotnet python3; do
    command -v "$need" >/dev/null || { echo "ERROR: $need not found" >&2; exit 2; }
done
[ -f "$PATCHER" ] || { echo "ERROR: $PATCHER not found (dotnet build -c Release in src/tools/PatchNclTestPage)" >&2; exit 2; }
[ -f "$SVC/Microsoft.Dynamics.Nav.Ncl.dll" ] || { echo "ERROR: $SVC has no Microsoft.Dynamics.Nav.Ncl.dll" >&2; exit 2; }

RT=$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/8* /usr/lib/dotnet/shared/Microsoft.NETCore.App/8* \
        "$HOME"/.dotnet/shared/Microsoft.NETCore.App/8* 2>/dev/null | head -1)
[ -n "$RT" ] || { echo "ERROR: no .NET 8 shared runtime found for ilverify's references" >&2; exit 2; }

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK:?}"' EXIT
mkdir -p "$WORK/patched" "$WORK/out"
cp "$SVC"/*.dll "$WORK/patched/"

echo "[verify] patching a copy"
for asm in Microsoft.Dynamics.Nav.Ncl Microsoft.Dynamics.Nav.Types; do
    for p in newline encoding uri culture; do
        dotnet "$PATCHER" "$p" "$WORK/patched/$asm.dll" 2>&1 | grep -E "^(Rewrote|Redirected|Windows|Framework|ERROR)" | sed "s/^/[verify]   /"
    done
done
dotnet "$PATCHER" temppath "$WORK/patched/Microsoft.Dynamics.Nav.Ncl.dll" 2>&1 | grep -E "^(Patched|ERROR)" | sed "s/^/[verify]   /"
dotnet "$PATCHER" filelisting "$WORK/patched/Microsoft.Dynamics.Nav.Ncl.dll" 2>&1 | grep -E "^(Routed|ERROR)" | sed "s/^/[verify]   /"
[ -f "$WORK/patched/YamlDotNet.dll" ] && \
    dotnet "$PATCHER" newline "$WORK/patched/YamlDotNet.dll" 2>&1 | grep -E "^(Rewrote|ERROR)" | sed "s/^/[verify]   /"

status=0
for asm in Microsoft.Dynamics.Nav.Ncl Microsoft.Dynamics.Nav.Types YamlDotNet; do
    [ -f "$SVC/$asm.dll" ] || continue
    echo "[verify] ilverify $asm (original, then patched)"
    ilverify "$SVC/$asm.dll"            -r "$RT/*.dll" -r "$SVC/*.dll"            --system-module System.Private.CoreLib > "$WORK/out/$asm.orig" 2>&1
    ilverify "$WORK/patched/$asm.dll"   -r "$RT/*.dll" -r "$WORK/patched/*.dll"   --system-module System.Private.CoreLib > "$WORK/out/$asm.patched" 2>&1
    python3 -I - "$WORK/out/$asm.orig" "$WORK/out/$asm.patched" <<'PY' || status=1
import re, sys
def errors(path):
    out = set()
    for line in open(path, errors="replace"):
        line = line.strip()
        line = re.sub(r"\[/[^\]]*?\.dll : ", "[", line)   # the file path differs between runs
        line = re.sub(r"Verifying .*$", "Verifying", line)
        out.add(line)
    return out
before, after = errors(sys.argv[1]), errors(sys.argv[2])
new = sorted(after - before)
print(f"[verify]   {len(new)} new error(s)")
for line in new[:10]:
    print("[verify]     " + line[:300])
sys.exit(1 if new else 0)
PY
done

if [ "$status" -eq 0 ]; then
    echo "[verify] OK: the patched assemblies verify as well as Microsoft's own"
else
    echo "[verify] FAILED: a patch produced invalid IL" >&2
fi
exit "$status"
