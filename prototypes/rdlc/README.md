# RDLC on Linux — the working prototype

**Status: prototype, on a branch, wired into nothing.** No file here is
referenced by `src/Dockerfile`, `scripts/entrypoint.sh`, `docker-compose.yml`
or any workflow. Checking this branch out and building the image gives you the
same image master gives you, with one exception noted under "Before you build"
below. `Report.SaveAs(Pdf)` against an RDLC layout still returns `false` on a
container built from this branch.

What changed is that the thing `docs/RDLC-ON-LINUX.md` said was unknown is now
known. That document opens with "Nothing in this document is implemented" and
stops at a `TypeLoadException` whose cause it could not identify. It is
superseded by this directory; the parts of it that are still accurate are the
architecture (why RDLC is a separate process) and the CAS analysis.

This work came out of one long session on 2026-09-08 that ran out of quota
partway through the NST wiring. It is recorded here so the next person starts
from a working renderer rather than from the blocker.

## What actually renders

Microsoft's own `Microsoft.ReportViewer.Common.dll` — unmodified engine logic,
no reimplementation, no fork of ReportViewerCore or RdlCore — running under
Mono on Linux, with the Windows-only text and font stack replaced by an
independently written Pango/HarfBuzz/FreeType/Fontconfig bridge.

Rendered end to end, zero renderer warnings:

| fixture | result |
|---|---|
| one textbox | 7,262-byte PDF, embedded subset CID TrueType |
| 120-row invoice | 4 US Letter pages, all 120 rows, totals 7,260 / 9,075.00, embedded VB expression, PNG logo, embedded regular + bold |
| filtered chart | 6 bars from 120 input rows, 1800×1200 at the original 300 DPI, 65,015 bytes |
| Arabic / Hebrew RTL | renders with correct visual digit order — `123.45` stays `123.45`, `INV-123` stays `INV-123` |

And through **Microsoft's real `Microsoft.BusinessCentral.Reporting.Service.exe`
over its real gRPC `ConfigureService` + `Render` endpoints**, with datasets
serialized by BC's own `NavDataSet.Serialize`: 3 rows → 9,099 bytes;
10,000 rows compact+deduped+compressed (34,992 wire bytes) → 9,224 bytes;
120-row invoice → 25,281 bytes.

The original pagination, PDF writer, font embedding, dataset serializers,
AppDomain handling and protobuf layer are all Microsoft's, retained.

## What is not done

- **NST wiring.** `src/StartupHook/NativeRdlcService.cs` exists and is not
  called from anywhere. `prototypes/rdlc/service/NST-WIRING.md` has the exact
  client constructor and `CustomReportingServiceClient` delegate contract to
  finish it against.
- **Non-root.** Every render above ran as UID 0. ReportViewer's
  `RevertImpersonationContext.Impersonate(IntPtr.Zero)` fails as a normal user;
  there is no Windows impersonation to revert on Linux, but it has not been
  patched.
- **Unicode font fallback.** No font installed in the toolchain image covers
  both Arabic and Latin, so a genuinely mixed Arabic/Latin run needs real
  per-run fallback that keeps the `CachedFont` and PDF font identity in step.
  The provider currently fails closed on a missing glyph, which is the right
  behaviour and is not a fallback.
- **Searchable RTL text.** See the `MapGlyphToUnicodeChar` note below.
- **Printing.** Out of scope, deliberately. Only PDF was asked for and only
  PDF was tried.
- **Windows-equivalence.** Nothing here has been diffed against a Windows
  container. "Renders correctly" above means the output was inspected and the
  values are right, not that it matches Windows byte for byte or line-breaks
  identically.

## Layout

| path | what |
|---|---|
| `src/RdlcNative/Bridge.cs` | managed text bridge — Uniscribe (`ScriptItemize`, `ScriptShape`, `ScriptPlace`, `ScriptLayout`, …) reimplemented over Pango/HarfBuzz |
| `src/RdlcNative/DrawingBridge.cs` | `System.Drawing` measurement calls, and the libgdiplus DPI correction |
| `src/RdlcNative/native/text.c` | the native half: Pango itemization with full bidi levels, HarfBuzz shaping, Unicode L2 reordering |
| `src/RdlcNative/font/` | the font provider — Fontconfig matching, FreeType metrics, HarfBuzz subsetting, PDF font embedding. `INTEGRATION.md` is the contract between it and the text bridge, and is the most detailed document in this tree |
| `src/tools/PatchRdlc/` | the Cecil patcher that rewrites `Microsoft.ReportViewer.Common.dll` and `Microsoft.ReportViewer.DataVisualization.dll` |
| `src/StartupHook/NativeRdlcService.cs` | supervisor for the Mono renderer process, gated on `BC_RDLC_RENDERER=mono`. Written, never called |
| `extensions/rdlc-smoke-test/` | AL app that calls `Report.SaveAs(Pdf)` — the end-to-end test that has not been run yet |
| `tests/rdlc-native/` | bridge unit tests and the render driver |
| `prototypes/rdlc/bridge/` | toolchain `Dockerfile`, `rebuild.sh` (patch both ReportViewer assemblies), `render.sh`, fixtures |
| `prototypes/rdlc/service/` | making Microsoft's real reporting service run and serve gRPC on Linux — plus `NST-WIRING.md` |
| `prototypes/rdlc/vb-runtime/` | a working `Microsoft.VisualBasic.dll` for RDLC expressions |
| `prototypes/rdlc/rdlcore-evaluation/` | the alternative that was evaluated and rejected |

## Things that will cost you a day if you rediscover them

- **Cecil P/Invoke conversion has an ordering trap.** Clear `PInvokeInfo`
  *first*, then set `IsPInvokeImpl = false`. Doing it the other way round, the
  `PInvokeInfo` setter turns the flag back on and the method still binds to the
  missing native entry point.
- **ReportViewer's script caches are per font, and the engine legitimately
  reuses earlier glyph arrays.** Caching only the last positions per font looks
  fine on small fixtures and produces wrong output on the invoice. It was
  caught by the 120-row fixture, not by the unit tests.
- **libgdiplus measures point-size fonts identically at 96 and 300 DPI.** This
  is a real libgdiplus bug, and it is why charts came out at the wrong font
  size. `DrawingBridge` plus the patched `DataVisualization.dll` correct it;
  don't "fix" it by downsampling a raster.
- **RTL text extracts as garbage, and that is Microsoft's code, not the
  bridge.** `PDFWriter.MapGlyphToUnicodeChar` (token `06006958`) only assigns a
  Unicode character when `fLayoutRTL` and `fRTL` are both zero and the
  text/glyph/cluster counts match 1:1. So the `ToUnicode` CMap for an RTL run
  is empty by design. The glyphs on the page are correct; Poppler has nothing
  to extract from. Don't go looking for a subsetting bug.
- **The invoice fixture had a split-row duplication that was not a pagination
  bug.** Mixed `CanGrow` settings on the detail cells cause it, identically on
  the other renderer that was evaluated. `fixtures/invoice.rdlc` sets
  `CanGrow=true` on all three detail cells.
- **`docs/RDLC-ON-LINUX.md` says a full Cecil `Write` of
  `Microsoft.ReportViewer.Common.dll` is not possible without a .NET Framework
  GAC.** That was true at the time and is no longer the constraint: a full
  rewrite works once real Mono 4.5 framework assemblies are on the resolver's
  search path, which is what `rebuild.sh` sets up. The byte-patch approach
  described there still works and is still simpler for the two CAS getters.
- **A missing native dependency makes the renderer abort with no PDF**, rather
  than emit a PDF with wrong filter results. That is deliberate, and it is
  worth keeping.

## Before you build this branch

`src/StartupHook/StartupHook.csproj` has no `<Compile Include>` list, so it
globs every `.cs` beside it — which now includes `NativeRdlcService.cs`. That
file has never been compiled as part of `StartupHook`, and `docker compose
build bc` on this branch will try to. If it does not compile, either fix it or
exclude it; do not conclude the branch is broken elsewhere.

## Licensing, and one thing to decide

Everything under `src/RdlcNative/` and `prototypes/` is written from scratch
for this project. No ReportViewerCore or RdlCore source was copied — that fork
was evaluated (`rdlcore-evaluation/`) and rejected for want of established
redistribution rights, and separately because it silently reordered `123.45`
into `54.321` in mixed Arabic/Latin text.

The VB runtime is built from MIT sources: mono/mono-basic at
`bdb5276f7d85100e8e9ddd7e5ba2360a792644a9`, plus Microsoft's own MIT
referencesource `StringType`, plus the .NET 8.0.30 native ICU implementation
for collation. Licenses and pinned hashes are in
`prototypes/rdlc/vb-runtime/`.

**Open question for a human:** the resulting assembly carries the identity
`Microsoft.VisualBasic, Version=10.0.0.0, PublicKeyToken=b03f5f7f11d50a3a` and
is delay-signed, not Microsoft-signed. The sources are MIT; the assembly
identity is a separate question and has not been answered.

## The scripts here do not run from a fresh checkout

They are the real scripts, not sketches, but half of what they consume is
Microsoft's and is not in this repo by design — the same rule that keeps BC's
service tier out of the image.

- `bridge/rebuild.sh` needs a pristine `Microsoft.ReportViewer.Common.dll` and
  `Microsoft.ReportViewer.DataVisualization.dll` (argument 1 and its sibling), a
  `run/` directory holding the rest of the ReportViewer assemblies plus
  `Microsoft.SqlServer.Types.dll` and `Microsoft.VisualBasic.dll`, and a
  `framework/` directory of real Mono 4.5 assemblies — that last one is what
  makes the full Cecil `Write` resolve.
- `bridge/render.sh` needs the `run/` that `rebuild.sh` produces.
- `service/stage.sh` needs all of the above plus the side-service assemblies
  from a BC artifact and the Linux `libgrpc_csharp_ext.x64.so` from
  `Grpc.Core` 2.46.6.
- `vb-runtime/prepare-dependencies.sh` and `prepare-icu.sh` are the exception:
  they bootstrap themselves, cloning mono-basic at a pinned revision and
  downloading pinned NuGet packages with `sha256sum --check`. Those two run
  from a fresh checkout.

The archive below has all of it already assembled, in the layout the scripts
expect. Restoring `files/original-native-bridge/` and
`files/rdlc-service-integration/` from it, then copying this branch's sources
over the top, is the shortest path back to a working render.

## The full evidence, including the binaries

Only source, scripts and documentation are committed here. The rendered PDFs,
page rasters, patched assemblies, build logs, the mono-basic checkout and the
complete session transcript are ~1.5 GB and live outside the repo, on the
machine this was built on:

```
~/Documents/rdlc-prototype-archive/session-21b74105/     # files/, events.jsonl
~/Documents/rdlc-prototype-archive/bc-rdlc-native-probe-21b74105.tar.gz
```

That tarball is the toolchain image (Ubuntu noble, Mono 6.8, gcc,
pango/fontconfig/freetype/harfbuzz with subsetting, libgdiplus, Liberation and
Noto fonts); `prototypes/rdlc/bridge/Dockerfile` rebuilds it.

Tracking issue: [#73](https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues/73),
originally raised as [#70](https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues/70).
