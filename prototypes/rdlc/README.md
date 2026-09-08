# RDLC on Linux — the working prototype

**Status: working, opt-in, on a branch.** `Report.SaveAs(Pdf)` against an RDLC
layout returns a real PDF from inside the container when the image is built
with `--build-arg BC_WITH_RDLC=1` and run with `BC_RDLC_RENDERER=mono` plus
`BC_RDLC_TRUST_LAYOUTS=1`. Without those the image and its behaviour are
exactly what they were: no Mono, no size change, `SaveAs(Pdf)` still returns
`false`.

Verified end to end on a cold boot of BC 28.4.53241.54387:

```
$ ./scripts/run-tests.sh --app extensions/rdlc-smoke-test/RdlcSmokeTest.app \
      --codeunit-range 70101
  [1/1] Codeunit 70101: SaveAsPdfReturnsDocument (1.7s)
    PASS  SaveAsPdfReturnsDocument
1 total, 1 passed, 0 failed
```

The PDF that call produces: 24,898 bytes, 4 US Letter pages, all 120 detail
rows, the embedded VB expression evaluated, embedded subset Liberation Sans
regular and bold. `extensions/rdlc-smoke-test` also exposes an API page that
returns the PDF as base64, which is how it was inspected.

This supersedes `docs/RDLC-ON-LINUX.md`, which opens by saying nothing in it is
implemented and stops at a `TypeLoadException` it could not identify. What is
still accurate there is the architecture (why RDLC is a separate process) and
the CAS analysis.

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
- **Wide document layouts.** Report 101 renders correctly; report 1305 does
  not, on any of its six layouts — see the section above. Simple and
  medium-complexity reports work; real document layouts do not yet.
- **Still untested:** subreports, non-Latin text, concurrent renders, and
  anything out of a real customer app.
- **Any BC version but 28.4.** `src/tools/PatchRdlc/Program.cs` refuses a
  ReportViewer whose MVID it does not know, which disables the renderer. A new
  BC version needs those MVIDs re-pointed and the font tokens re-checked.

## The next bug: wide document layouts lose their trailing dataset columns

Found 2026-09-08 by running real Microsoft reports through the web client on
BC 28.4. This is the thing to fix next, and it is a real defect in this
integration, not a layout problem.

**Report 101 (Customer - List) renders correctly.** Header, column captions,
grouped detail row with a bold company name, footer total, page number — all
correct against a Windows rendering of the same report, once you account for
the demo database differing (CRONUS International vs CRONUS USA gives different
addresses, currency captions, number formats and dates).

**Report 1305 (Sales - Confirmation) fails on every layout**, with two symptoms
that share one cause:

| layout | error from `/run/bc-rdlc/service.log` |
|---|---|
| Sales Order Confirmation for Subscription Billing | `ReportPublishingException: The Value expression for the text box 'GlobalLocationNumber_Lbl' refers to the field 'GlobalLocationNumber_Lbl'` |
| Standard Sales Order Confirmation (all fields) | `ReportProcessingException_FieldError: There is no data for the field at position 102` (and 103) |

The second is the honest one: the row the reporting service hands ReportViewer
has fewer columns than the layout declares. The first is the same shortfall
caught earlier, at compile time, on a label column.

Trailing columns in a BC RDLC dataset are where the **labels** live — the
`*_Lbl` captions BC appends after the data columns. `NST-WIRING.md` lists "BC
report parameters/labels from a real AL report" as never demonstrated, and this
is that gap arriving.

Where to look, in order:

1. Compare the column count the NST puts into `NavDataSet.Serialize` against
   what the service reconstructs from `HybridDataStore`. Both are Microsoft
   code, so a divergence points at the settings they were given, not at them.
2. `ReportingServiceSettings` — particularly `EnableCompactSerialization` and
   the per-`RenderingContext` compression/deduplication flags. `NativeRdlcService`
   configures the process through BC's own
   `ReportingProcessStartup.InitializeReportingServiceConfiguration`, which is
   the right thing to do, but the result has never been diffed against what the
   NST actually serializes with.
3. Whether the failure position tracks the dataset width. Report 101 is narrow
   and works; 1305 is ~100 columns and loses the last two.

Do not chase this in the font or text bridge. Nothing above involves rendering
— the report never reaches layout.

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

## How to run it

```bash
BC_WITH_RDLC=1 docker compose build bc
BC_RDLC_RENDERER=mono BC_RDLC_TRUST_LAYOUTS=1 docker compose up -d --wait
```

`BC_WITH_RDLC` builds the renderer into the image; `BC_RDLC_RENDERER` turns it
on. Both are needed. If the image was built without it, or the ReportViewer in
that BC build is not one the patcher recognises, the entrypoint says so and
reporting behaves exactly as it does today — an opt-in feature must not be able
to fail a boot.

### With the web client, to click through it yourself

The web client PoC (`docs/WEBCLIENT-POC.md`) and the renderer work together:

```bash
BC_WITH_RDLC=1 docker compose build bc
BC_WEBCLIENT=1 BC_RDLC_RENDERER=mono BC_RDLC_TRUST_LAYOUTS=1 \
    docker compose up -d --wait
# publish extensions/rdlc-smoke-test, then browse to
#   http://localhost:8080/?report=70100      (BCRUNNER / Admin123!)
```

Running report 70100 downloads a four-page PDF whose Producer is
`Microsoft Reporting Services PDF Rendering Extension 15.0.0.0` — ReportViewer's
own output. Verified in headless Chromium on BC 28.4: sign in, role center with
live CRONUS data, report runs, 120 rows across 4 pages.

Note the two PDF producers you will see. Through the web client the file is
ReportViewer's raw output; through `Report.SaveAs(Pdf)` from AL, BC
post-processes it with Aspose.PDF, so the Producer differs and the byte count
is slightly different. Both are the same rendered document.

Testing it on a NON-default instance has a trap. `scripts/run-tests.sh` derives
`WS_HOST` and `ODATA_HOST` from `--base-url`'s host but hardcodes `:7085` and
`:7052`, so on a port-shifted instance `--base-url`/`--dev-url` alone still send
the websocket and OData steps to whatever is on the default ports — which, on a
machine already running a bc-linux stack, is somebody else's container. Address
the container on the docker network instead:

```bash
IP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' <project>-bc-1)
./scripts/run-tests.sh --base-url "http://$IP:7048/BC" --dev-url "http://$IP:7049/BC/dev" \
    --app extensions/rdlc-smoke-test/RdlcSmokeTest.app --codeunit-range 70101
```

When a render fails, read `/run/bc-rdlc/service.log` inside the container. BC
maps every render failure to "an internal error while rendering the report",
so that log is the only place the real cause appears. `BC_RDLC_TRACE=1` adds
Mono's exception trace to it.

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
