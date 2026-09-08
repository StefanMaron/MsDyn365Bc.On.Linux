# Original ReportViewer font bridge prototype

This directory is isolated session work. It contains independently written
adapter code, not a renderer implementation. Do not ship it as Windows-equivalent.
The actual Mono/original-engine integration is owned by the main agent.

## Build and link

`make all check` builds the native library and runs native contract tests.
Requires a C11 compiler, pthreads, Fontconfig, FreeType, and HarfBuzz including
the subset library (HarfBuzz >= 4.0 recommended).

`make check-managed` uses an installed .NET 8-compatible SDK and isolated
contract doubles to exercise reflection, ref/out writeback, native ABI layout,
SafeHandle release, EM scaling, and packaging. It is not an original-engine
render or a substitute for a Mono/net472 integration run. Never deploy the
test assembly, which deliberately impersonates the original assembly name
only inside the test executable.

Mono build:

```
mcs -langversion:7.2 -target:library -out:FontBridge.dll FontBridge.cs
```

Artifacts for the supplied `bc-rdlc-native-probe:21b74105` image are under
`mono-build/FontBridge.dll` and `mono-build/librdlc_native.so`. The helper is
built with `mcs -sdk:4.7.2 -langversion:7.2 -target:library -r:System.Drawing`.
These are the container-targeted artifacts; the root library from `make` is
host-targeted. Link the source with the text bridge for the combined library.

There is no compile-time dependency on ReportViewer or System.Drawing.
`FontBridge.csproj` also targets net472 for a build environment with those
reference assemblies. The source deliberately uses no APIs newer than net472.

The standalone `librdlc_native.so` is for font development. In the combined
bridge, compile `rdlc_font.c` into the main agent's SAME `librdlc_native.so`:
do not put independent copies of this registry in multiple loaded libraries.
All exports are prefixed `rdlc_font_`.

## Text bridge contract (no shaping implementation here)

* `FontBridge.GetFontToken(object cachedFont)` -> `IntPtr` provider ID.
* `FontBridge.GetSelectedFontToken(object hdc)` -> provider ID.
* `FontBridge.GetSelectedFont(object hdc)` is the same API under the main text
  bridge's preferred name. Its IntPtr is still a TOKEN, not a native structure.
* `FontBridge.GetMetrics(token)` -> the public sequential `Metrics` struct.
  Initialize `rdlc_font_metrics.size = sizeof(rdlc_font_metrics)` in C callers;
  the provider rejects mismatched ABI sizes.
* `FontBridge.AcquireHarfBuzzFont(token)` -> OWNED `hb_font_t*` reference.
* `FontBridge.ReleaseHarfBuzzFont(pointer)` releases that reference.
* Native equivalents are declared in `rdlc_font.h`.
* Text bridge convenience export:
  `hb_font_t *rdlc_font_hb_font(void *providerFontToken)`.
  It acquires an OWNED reference, or returns NULL and sets
  `rdlc_font_last_error()`. Release using `rdlc_font_release_hb_font`.
  A script-cache structure can own this HB reference together with shape
  results. Do not mistake the provider token for the HB pointer, and release
  the previous HB reference when replacing/reinitializing a cache.
* The HB font is immutable, uses OpenType font functions, and is scaled to
  `64 * logical_em` in both axes. Divide returned positions by 64 to obtain
  ReportViewer's logical pixel units. In `UseEmSquare` mode a logical pixel
  is one design unit because the logical em equals UPEM. Do not divide by
  CachedFont.ScaleFactor again at the PDF glyph-width boundary.
* `hb_font_get_face()` returns a face borrowed from the acquired font reference.
  The HB reference outlives registry deletion; release it even on exceptions.
* `rdlc_font_get_glyph` accepts a Unicode SCALAR, including supplementary
  characters. Success with glyph zero means missing/.notdef, not a successful
  fallback. Surrogate code points are rejected. No ASCII-only path exists.
  The initial ScriptShape integration must fail on missing glyphs instead of
  returning a successful run with an unannounced fallback face.
* Pango unknown-glyph flags are not TrueType GIDs and MUST NOT be truncated into
  the ushort glyph array.
* A font-resolved run must identify the actual face used. Do not return GIDs
  from a Pango fallback face under a different CachedFont. `CreateFace(path,
  faceIndex, bold, italic, charset, logicalEm, dpi)` creates a provider token
  for an explicitly resolved face; `NewFontHandle(token, true)` transfers
  its ownership to a SafeHandle. This does NOT automatically update a run's
  System.Drawing Font or PDF font identity; the main text/run bridge owns that.
* Resizing to the PDF's EM-sized face retains the original immutable font blob;
  it does not reopen the font path, so a file replacement cannot change GIDs.
* `FontBridge.DescribeFont(token)` reports the selected file/index/family/size.

Tokens are high-range opaque IDs, not libgdiplus, FreeType, or HB pointers.
An HDC is only a side-table key. The original GDI+ acquire/release operation
remains real. Do not call GdipDeleteFont on a provider ID.

## Dispatcher

Public entry:

```
object FontBridge.Dispatch(string method, object instance, object[] args)
```

Instance is null for static methods. Pass arguments in original order. For
ref/out parameters, the dispatcher changes `args[index]`; the patcher must
unbox/cast and write them back. Arrays are changed in place. Cast/unbox the
returned object to the original return type, or pop it for void.

Bare hexadecimal tokens are accepted directly, case-insensitively:
`FontBridge.Dispatch("06006855", instance, args)`. The `font:` and `0x`
prefixes are optional. Symbolic dispatcher strings below remain supported.

All original IDs below have prefix `5b437ccb94874e41a96f8f621f811037:` and
suffix `:M`. Check MVID before applying these token-specific patches.
`RT` = Microsoft.ReportingServices.Rendering.RichText.
`IR` = Microsoft.ReportingServices.Rendering.ImageRenderer.

| Token | Original method | Dispatcher string | Result/writeback |
|---|---|---|---|
| 060066D0 | RT.FontCache.CreateGdiFont(WritingModes,int,bool,bool,bool,byte,bool,string) | FontCache.CreateGdiFont | SafeHandle |
| 060066B9 | RT.CachedFont.Initialize(Win32DCSafeHandle,FontCache) | CachedFont.Initialize | void |
| 06006855 | RT.Win32.SelectObject(Win32DCSafeHandle,Win32ObjectSafeHandle) | Win32.SelectObject.Safe | borrowed SafeHandle |
| 06006854 | RT.Win32.SelectObject(IntPtr,IntPtr) | Win32.SelectObject.Raw | IntPtr; only if needed |
| 0600685B | RT.Win32.DeleteObject(IntPtr) | Win32.DeleteObject | int |
| 0600685C | RT.Win32.DeleteObject(Win32ObjectSafeHandle) | Win32.DeleteObject | int; only if needed |
| 06006A31 | IR.GraphicsBase.ReleaseHdc() | GraphicsBase.ReleaseHdc | void |
| 06006A64 | IR.FontPackage.CheckSimulatedFontStyles(Win32DCSafeHandle,TEXTMETRIC,ref bool,ref bool) | FontPackage.CheckSimulatedFontStyles | args[2],args[3] |
| 06006A65 | IR.FontPackage.CheckEmbeddingRights(Win32DCSafeHandle) | FontPackage.CheckEmbeddingRights | bool |
| 06006A66 | IR.FontPackage.Generate(Win32DCSafeHandle,string,ushort[]) | FontPackage.Generate | byte[] |
| 06006875 | RT.Win32.GetCharABCWidthsFloat(Win32DCSafeHandle,uint,uint,ABCFloat[]) | Win32.GetCharABCWidthsFloat | int; args[3] array |
| 0600687B | RT.Win32.GetOutlineTextMetrics(Win32DCSafeHandle,uint,ref OutlineTextMetric) | Win32.GetOutlineTextMetrics | uint; args[2] |
| 06006871 | RT.Win32.GetTextMetrics(Win32DCSafeHandle,out TEXTMETRIC) | Win32.GetTextMetrics | bool; args[1]; alternative to replacing CachedFont.Initialize |

The 060066D0 interception leaves the original CreateFont (060066CF) intact:
it still creates the System.Drawing Font, establishes its cache key and
ScaleFactor, and installs the provider SafeHandle. Initialize additionally
checks that System.Drawing and the bound native face agree on UPEM.

Native P/Invoke methods must have PInvokeInfo/PinvokeImpl cleared and managed
IL bodies installed. Retain original parameter metadata. Do not invoke the
old native entry after forwarding.

DeleteObject and SelectObject dispatchers handle FONTS ONLY. If the main
adapter handles pens/brushes/regions too, dispatch by owned object type.
`FontBridge.IsFontToken` / `rdlc_font_is_token` distinguish live font tokens.
Do not turn an unknown object into a successful no-op. Calls of other object
types intentionally fail with diagnostics in this font-only prototype.

## Two PDF call-site changes

Original `IR.PDFWriter.WriteFont(PDFFont)`, token 06006974:

1. Preferred with the main patcher's ToHfont-only interception:
   `Bridge.PdfFontToHfont(object drawingFont, object pdfFont) -> IntPtr`
   should call `FontBridge.PdfFontToHfont(drawingFont, pdfFont)`.
   Push pdfFont after the existing temporary Font receiver, replace the instance
   ToHfont call with this static two-argument call, and retain the original
   owning SafeHandle constructor. The helper disposes that otherwise unused
   temporary Drawing Font, then returns a new owned EM-sized provider token
   for the CachedFont's actual face. It rejects accidentally receiving the
   cached Drawing Font itself. It never reads a libgdiplus pointer.

   Alternative: replace the entire expression which constructs an EM-sized System.Drawing
   Font, calls ToHfont, then constructs an owning Win32ObjectSafeHandle.
   Instead load pdfFont, call `FontBridge.CreatePdfFontHandle(object)`,
   castclass RT.Win32ObjectSafeHandle, and store in the same local.
   A helper accepting ONLY the temporary Drawing Font is insufficient; pdfFont
   is what supplies the bound face identity. Under Mono ToHfont returns borrowed
   GpFont*, so the original owning SafeHandle lifetime is not suitable.
2. Replace the italic-angle scalar passed to the PDF writer (the original
   rounds `otmItalicAngle * EMGridConversion`) with
   `FontBridge.GetPdfItalicAngle(pdfFont)`, returning int degrees.
   The normal OTM provider correctly returns tenths of a degree; the original
   PDF expression incorrectly treats them as font units. This second patch
   is required for correct italic descriptors.

Retain the rest of WriteFont, font streams, compression, ToUnicode, font-object
allocation, and all report processing/pagination.

## Deliberate limits and errors

Supported: horizontal static monochrome glyf/loca TrueType, real regular/bold/
italic faces, Unicode cmap, CP1252 PDF width slots, duplicate glyph lists,
TrueType TTC faces when subsetting is permitted, retained-GID subsetting.

Rejected: CFF/CFF2, variable/color/SVG fonts, vertical/rotated requests, symbol
encoding, synthetic styles, unknown substituted family names, invalid GIDs,
unrecognized handles, and no-subsetting TTC extraction. Exact-face creation
bypasses family matching, not format/style restrictions.

Fontconfig matching accepts actual family names and explicit generic families.
For example Arial silently becoming Liberation Sans is rejected if "Arial"
reaches the provider; the original System.Drawing path normally supplies its
resolved actual family. Explicit fallback binding is the escape hatch, not an
unconditional successful substitute.

OS/2 restrictions are respected including version differences. A denied
CheckEmbeddingRights returns false with a diagnostic, preserving the original
non-embedding decision; Generate also checks permissions independently and
throws if called despite denial. For no-subsetting single-face TrueType the
complete original font is returned. The output is uncompressed SFNT; original
PDFWriter writes /FontFile2 and performs Flate compression.

Native failures become the original ReportRenderingException(string) when
ReportViewer is loaded, so PDFWriter's RSException branch propagates them
instead of quietly dropping an embedded font. Failure diagnostics also go
to stderr. Original SafeHandle.ReleaseHandle catches disposal errors; the
diagnostic is particularly important on that path.

Metric policy is explicit and UNHINTED: OS/2 Windows cell metrics, or typo
metrics when OS/2 v4+ requests USE_TYPO_METRICS, with hhea/OS2 spacing; PDF
typographic extents use OS/2 and bbox/post/hmtx data. This is not a claim of
Windows GDI rounding, font fallback, or pagination equivalence.

Qualification still needs the original engine's full mixed-bidi report,
fallback run association, resource lifetime stress, restricted/no-subset
fonts, and visual/PDF resource inspection. Full bidi and text shaping are
not implemented by this directory.

## Read-only inspection helpers and current fixture font requirements

`python3 tests/inspect_pdf_fonts.py ../run/simple.pdf` inspects PDF font
dictionaries through qpdf, validates the embedded SFNT/table checksums, and
compares descriptor metrics and nominal CID advances with the embedded font.
Its nominal-width assertion is for simple runs, not a general GPOS assertion.

`tests/UnicodeFontAudit.cs` is a standalone Mono audit executable. Compile
with System.Drawing, System.Xml.Linq and the combined run/RdlcNativeBridge.dll
as references (do not compile another FontBridge into it). Pass the RDLC
path, optionally an additional candidate family and a textbox name filter.
It reports both direct requested-family resolution and the actual family
passed on by Mono System.Drawing, including missing Unicode scalars.
It neither renders nor changes any run or font binding.

The supplied image observed on 2026-09-08 has these fixture properties:

| Fixture family/run | Actual availability and nominal coverage |
|---|---|
| Liberation Sans / Latin and Wrap | Static TrueType; all fixture characters covered |
| Noto Sans Arabic / Arabic | Static TrueType; all fixture characters covered |
| Noto Sans Hebrew / Hebrew | Static TrueType; all fixture characters covered |
| Noto Sans Arabic / Mixed | Missing Latin E, I, N, R, U, V; Arabic, digits and decimal characters are covered |
| Noto Sans CJK SC / Cjk | Family absent; direct provider resolution rejects substitution |
| Mono-resolved Cjk | Mono reports Noto Sans while OriginalFontName remains Noto Sans CJK SC; all 16 distinct printable fixture characters are missing |
| Droid Sans Fallback / explicit Cjk candidate | Installed static TrueType; provider accepts it and it covers the Chinese/Japanese characters, but not the six distinct Hangul characters |

No installed image font covered U+D55C in the Fontconfig coverage query.
No installed font covered both Arabic ALEF and Latin E. Mixed Arabic/Latin
therefore requires actual per-run fallback, not just bidi reordering.
The six missing Hangul scalars are U+ACE0, U+AD6D, U+BCF4, U+C11C, U+C5B4,
U+D55C. Supplying a CFF/CFF2 or variable CJK font would still be rejected by
the deliberately TrueType-only provider.

The native nominal-glyph API reports a missing character as successful lookup
with glyph zero. The combined managed Bridge.ScriptShape already detects
zero glyphs for printable characters and returns FontMissing. That gate is
load-bearing; the absence of a native zero-glyph rejection is not, by itself,
a missing combined-bridge guard. These observations required no runtime font
provider change, and no shared render invocation.

## Concrete static-font choices for the next integration stage

The installed Noto fonts are script-specific. Noto Sans Arabic covers
`123.45` but lacks the six Latin letters in INV/EUR. Noto Sans Hebrew also
lacks the decimal point and ASCII digits used by the fixture; the full mixed
Hebrew string additionally lacks the colon and six Latin letters.

Two font-controlled fixtures use only installed families:

* `fixtures/noto-arabic-decimal.rdlc`: one Arabic-plus-123.45 run, completely
  covered by Noto Sans Arabic. This separates decimal ordering from fallback.
* `fixtures/noto-explicit-mixed-rtl.rdlc`: Arabic and Hebrew text in Noto,
  with Latin words and numbers explicitly in Liberation Sans. All individual
  runs have complete nominal coverage. This separates cross-run ordering
  from missing-font fallback. It is not an assertion that bidi output is right.

Additional official Ubuntu noble font packages were downloaded and EXTRACTED
under `candidate-fonts/`, not installed in the image or on the host:

| Package/version | Exact face | Provider result for the target fixture |
|---|---|---|
| fonts-dejavu-core 2.37-8 | `usr/share/fonts/truetype/dejavu/DejaVuSans.ttf`, face 0, family DejaVu Sans | Static TrueType, UPEM 2048, fsType 0; complete Arabic/Latin and Hebrew/Latin mixed-string coverage |
| fonts-wqy-zenhei 0.9.45-8 | `usr/share/fonts/truetype/wqy/wqy-zenhei.ttc`, face 0, family WenQuanYi Zen Hei | Static TrueType TTC, UPEM 1024, fsType 0x0008 (editable embedding); complete Chinese/Japanese/Korean fixture coverage |
| fonts-nanum 20200506-1 | `usr/share/fonts/truetype/nanum/NanumGothic.ttf`, face 0, family NanumGothic | Static TrueType, UPEM 1000, fsType 0x0008; missing U+62A5, so not a complete replacement for this CJK fixture |

DejaVu Sans and WenQuanYi Zen Hei both produce standalone TrueType subsets
through the current combined native provider using the fixture's nominal
glyphs. This is face/packaging evidence, not a mixed-bidi rendering claim.
The extracted package copyright documents are retained with the assets.

For a no-fallback mixed-script control, use DejaVu Sans explicitly after the
main agent installs/registers it. For a one-face CJK control, explicitly use
WenQuanYi Zen Hei face 0; do not rename it to Noto Sans CJK SC or allow the
reported family to disagree with the embedded face. No CFF renderer support
or variable-font instantiation is needed for these two choices.

`UnicodeFontAudit` accepts an absolute candidate font file as its second
argument and opens face 0 directly, so extracted fonts can be inspected
without changing Fontconfig or System.Drawing's installed families.

### Isolated original-engine RTL control outputs

The two installed-font controls were subsequently rendered directly through
the main run/Render.exe, mounting the main work directory read-only and
writing only under `font/rtl-controls/`. The shared render.sh and run/ outputs
were not changed. Both controls completed with warnings=0.

* `rtl-controls/noto-arabic-decimal.pdf` and `.png`: visually connected Arabic
  and `123.45` in the correct numeric order, not `54.321`. One embedded Noto
  Sans Arabic CID TrueType subset. Poppler text extraction contains the
  decimal but omits the Arabic.
* `rtl-controls/noto-explicit-mixed-rtl.pdf` and `.png`: Arabic/Hebrew text
  with visually correct `INV-123` and `123.45`, using three embedded subsets
  (Liberation Sans, Noto Sans Arabic, Noto Sans Hebrew). Extracted RTL text
  is garbled despite the correct visual digit order.

This extraction limitation has an exact original-engine boundary:
PDFWriter.MapGlyphToUnicodeChar, token 06006958, only assigns a Unicode
character when both fLayoutRTL and fRTL are zero and the text/glyph/cluster
lengths match. The Arabic control's ToUnicode stream contains mappings for
the six numeric characters and no mappings for the Arabic run. This is
separate from font coverage or retained-GID correctness. The original
method was not changed; identical behavior of Windows PDF readers has not
been established from this observation.

The successful renders used the same root container identity as the main
render.sh. An initial non-root attempt failed before font rendering with
WindowsIdentity.Impersonate / "Couldn't impersonate token". That unrelated
original-engine identity limitation was not patched here.
