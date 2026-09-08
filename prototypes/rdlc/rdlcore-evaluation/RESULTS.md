# Native RDLC evaluation, 2026-09-08

No Wine was installed or used. This is an isolated feasibility evaluation,
not a change to BC's rendering path.

## Evaluated engine

NicFT/RdlCore revision `13869e9ddde8e58b92bd9a18c8a730f4ac06afbe`,
unmodified, built for Linux x64 and executed with Linux .NET 10.
The candidate uses SkiaSharp/HarfBuzz rather than Windows text APIs.

Release publishing failed in the candidate's ILRepack target because it
attempted to merge a native library. Debug publishing succeeded without the
optional merge. This evaluation does not prove its Release packaging works.

The source checkout remains separate from the bc-linux repository. No engine
source or binaries were added to bc-linux, committed, or uploaded. The upstream
README describes an internal tool; redistribution rights are not established.

## Successful native PDF output

`invoice.rdlc` is an independently authored RDL 2010 fixture. `Program.cs`
provides a typed `DataSet_Result` table with 120 rows and a generated PNG logo.
It invokes the candidate's actual `LocalReport.Render("PDF")` path.

Output: `invoice.pdf`, 121,270 bytes, four US Letter pages.

Observed in the rendered PDF:

- All 120 invoice descriptions, each exactly once.
- Per-row quantity/price expressions.
- Quantity total 7,260 and amount total 9,075.00.
- Embedded VB expression result: `Embedded VB executed`.
- Page footers 1 of 4 through 4 of 4.
- Embedded regular and bold Liberation Sans fonts.
- Embedded 120 x 40 PNG logo.
- A readable, correctly separated Latin-text invoice in rasterized output.

The engine returned zero warnings. A requested repeating tablix heading
appeared only once; this has not been compared against Windows with the same
fixture, so it is an observation, not an established Linux regression.

`simple.pdf` also renders the previous one-textbox fixture. Its literal text
says "under Mono" because that text was authored for the old probe; this
particular execution actually used Linux .NET 10, not Mono.

## Counterexample to Windows-equivalent correctness

`unicode.rdlc` exercises Latin, Arabic, Hebrew, mixed-direction text, CJK,
and a narrow `CanGrow` textbox using fonts available on this machine.

Output: `unicode.pdf`, 1,617,355 bytes, zero renderer warnings.

Latin/CJK text and the numbered wrapping fixture are present. However:

- The mixed Arabic/Latin input contains the literal amount `123.45`. The
  rasterized PDF visibly shows `54.321`.
- The same mixed run substitutes incorrect glyphs for Latin content.
- PDF text extraction of the Arabic/Hebrew runs produces unrelated characters,
  even where the rasterized pure-script run looks plausible.

`unicode-page1.png` preserves the visual counterexample.
These are actual output defects, not inferred from source warnings. A renderer
that changes a numeric amount must not be enabled as a BC-compatible backend.

Source inspection is consistent with the observations: the candidate's
itemizer merges digits into adjacent script runs and does not implement full
Unicode bidi levels; its shaping adapter assumes monotonically increasing
clusters. Those are leads, not a claim that changing one line fixes the full
text pipeline.

## Boundary of the result

Native RDLC-to-PDF is technically possible and was demonstrated beyond a PDF
header or blank page. This does not establish compatibility with BC's report
corpus, Windows-identical pagination, arbitrary languages, external report
assemblies, or BC's compressed dataset protocol.

No BC runtime hook was changed, no renderer was enabled in the shared running
BC container, and no runtime-fix PR was opened on the strength of this probe.
