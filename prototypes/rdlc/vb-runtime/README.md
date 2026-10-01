# Full Visual Basic runtime for the native RDLC probe

The missing VB runtime blocker and the two Turkish comparison failures are
resolved by the **new ICU candidate**. The original ReportViewer renders the
Turkish wildcard filter correctly under Mono, without Wine or global BCL
changes. The older unmodified/reference candidates and their failure evidence
are preserved below.

## Current candidate: exact native ICU comparisons

Deploy these **three adjacent files**, not just the managed assembly:

| File | SHA-256 |
| --- | --- |
| `icu/Microsoft.VisualBasic.dll` | `8bf4f6dc338ea57eec62c9ebffb8b14d7ddb0ebf75674188eaa181bbbf169808` |
| `icu/libBCRdlc.IcuBridge.so` | `b52c3f3cba12971296ed56e47193ee40b961809e7b1723a6ba8b47c364057920` |
| `icu/libSystem.Globalization.Native.so` | `7da0130279f621ace5cabfc256657947cf4498e7733dc85883ad7f930a8783bc` |

The managed assembly retains version `10.0.0.0`, token
`b03f5f7f11d50a3a`, and the same Mono delay-signing status described below.
This is a Linux x64 candidate. The Mono process needs system ICU
(the isolated noble image has ICU 74) and the native libraries must remain
resolvable if the caller uses assembly shadow-copying. The tiny bridge uses
`$ORIGIN` to locate the adjacent official globalization library.

`NativeIcuCompareInfo.vb` replaces only the comparison/search objects used by
the official legacy `StringType` adaptation. `Compare` and `LastIndexOf`
delegate to **.NET 8's real native ICU implementation**, not Mono's broken
managed collator. The `.NET` native shim implements the comparison-option
tailoring for `IgnoreCase`, `IgnoreWidth`, and `IgnoreKanaType`. No strings
are lowercased, rewritten, normalized by hand, or stripped of accents here.
Literal `cafe` versus `cafe-acute` remains unequal; the reference algorithm's
explicit `IgnoreNonSpace` in range/search operations is passed through only
where the original algorithm requests it.

The native library is extracted unmodified from official NuGet
`Microsoft.NETCore.App.Runtime.linux-x64` **8.0.30**, hash-checked by
`prepare-icu.sh`. Its source tag resolves to commit
`a83db3e0eb2defb6220e15dae2f1a0462fdbf99f` in `dotnet/runtime`; the exact
native ABI sources are retained under `icu-source/`. This redistributable
.NET native library is MIT-licensed, not a proprietary .NET Framework VB
binary. Its package license and third-party notices are in `licenses/`.
No .NET managed runtime is needed in the deployed Mono process.

`icu-bridge.c` uses `pthread_once` because Mono AppDomains have separate
managed statics but share the native library: initialization must happen only
once per process. Handles are `SafeHandle`-owned and cached by culture with a
`ConditionalWeakTable`, so native collators can be released with their culture
or AppDomain.

### Reproduce the ICU candidate

```bash
bash prepare-dependencies.sh
bash prepare-icu.sh
bash build.sh --icu-stringtype
bash validate-icu.sh ../original-native-bridge
```

Preparation and builds do not alter either older candidate. Native compilation
and all Mono execution stay inside the isolated container; the `.NET 8.0.30`
host runtime is used only as a comparison oracle. `icu-artifacts.sha256`
records deployment hashes.

### Evidence

* Original runtime API probe: **22/22**, including both formerly failing
  Turkish cases.
* Additional actual Like cases: **18/18**, including Turkish positive and
  negative pairs, multi-asterisk search, Azerbaijani, accents, canonical
  combining marks, width, kana, voiced kana, and Swedish versus English
  range ordering.
* Comparison matrix: **1,701 rows / 3,402 operations** across nine locale
  names, 27 string pairs, and seven option sets. Every `Compare` sign and
  `LastIndexOf` result matches real **.NET 8.0.30** on both the Linux host and
  the noble container. Includes legacy `zh-CHS`/`zh-CHT` names and Han
  ordering. This is an **ICU/.NET-on-Linux oracle, not a Windows NLS oracle**.
* **16,000 Like assertions in eight concurrent AppDomains**, followed by
  successful domain unloads.
* Original ReportViewer Turkish wildcard report:
  before `ROWS=1; SUM=2; AMOUNT=2,50`; after
  **`ROWS=2; SUM=3; AMOUNT=3,75`**, 7,966-byte PDF, zero warnings.
  `filter-turkish.rdlc` filters four values `I`, dotless-i, `i`, dotted-I
  using `*dotless-i*` in `tr-TR`, so both Turkish equivalence classes are
  exercised through the real filter and native search paths.
* Existing numeric, Like and filtered-chart reports still produce their
  original correct totals and PDF sizes.

`validate-icu.log`, the three `matrix-*.tsv` files, `icu-api.log`,
`icu-like.log`, `icu-domains.log`, and the before/after Turkish PDF/text
artifacts retain the evidence. The initial portable/Mono failure records
remain unchanged.

### Fail-closed behavior and limits

Removing the official native dependency causes an actual original
ReportViewer Like-filter render to exit **1**, with a propagated
`TypeInitializationException` / `DllNotFoundException` and **no PDF**.
It does not fall back to Mono collation or return an empty/wrong filter result.
`fail-closed-render.log` records this deliberate failure.

The ICU adapter also rejects unsupported option flags and a failed ICU/sort
handle initialization explicitly. The reference matcher's blanket exception
catch in recursive wildcard matching has been removed for this candidate,
so dependency/collation failures cannot silently become `False`.
Named Windows alternate-sort cultures such as `de-DE_phoneb` and
`es-ES_tradnl` are already rejected by this Mono runtime, before native
collation is reached; no approximate locale-name substitution is added.

This solves the demonstrated Linux comparison defects, not universal
Windows NLS equivalence or all of Mono's other partial VB APIs. Real AL
`SaveAs` and the parent's reporting-service deployment are outside this
scope. No changes were made to the repository or the parent's bridge.

## Preserved earlier outputs and exact provenance

All paths below are relative to this directory.

| Output | Contents | SHA-256 |
| --- | --- | --- |
| `Microsoft.VisualBasic.dll` | Unmodified complete Mono Basic runtime | `dc4ecbcba30f30fb212321734ae7f38def614887e1a37363634b0e7207c52281` |
| `compatible/Microsoft.VisualBasic.dll` | Complete Mono runtime with Microsoft's official legacy `StringType` source, adapted to Mono's internal helpers | `6b742ce1c7a6844ed51b6eac5ab82453ab6c5dbf87552613a8b4c678268d7e07` |

The second historical candidate removes four demonstrated upstream Mono
Like-matching defects, but retains the two Turkish failures. The current
ICU candidate above supersedes it for integration.

* Mono source: https://github.com/mono/mono-basic at
  `bdb5276f7d85100e8e9ddd7e5ba2360a792644a9`.
* Microsoft legacy StringType source: https://github.com/microsoft/referencesource
  at `ec9fa9ae770d522a5b5f0607898044b7478574a3`, path
  `Microsoft.VisualBasic/runtime/msvbalib/Helpers/StringType.vb`.
* Compiler: official NuGet `Microsoft.Net.Compilers.Toolset` **4.8.0**,
  `tasks/net472/vbc.exe`, running under Mono. Package SHA-256:
  `37333f4f1e2ce55e621355d6da651dc23d4cb5f94a8f76b9478816e87f110ad9`.
* Container: `bc-rdlc-native-probe:21b74105`, observed image ID
  `sha256:5da754645b7cb4f430a3c0ac7d1d12c0ff20fba5606c9c0e0d375bb29a46928a`.
  `IMAGE` can override the tag, including with that image ID.

Both binaries expose:

```
Microsoft.VisualBasic, Version=10.0.0.0, Culture=neutral,
PublicKeyToken=b03f5f7f11d50a3a
```

This is the upstream Mono **delay-signed** compatibility identity using its
`msfinal.pub`, not a genuine Microsoft private-key signature. Mono loads it
without a GAC install or system-wide signature-policy change. Do not describe
this assembly as Microsoft-signed. The full public key is in
`mono-basic/vbruntime/Microsoft.VisualBasic/msfinal.pub`.

Mono's runtime library is MIT/X11; its separate vbnc compiler is LGPLv2 and is
not used or shipped as this runtime dependency. Microsoft Reference Source is
MIT. Copyright/license files are in `licenses/`; complete Mono source and the
original Microsoft source are retained. No .NET Framework proprietary VB
runtime binary was obtained or redistributed.

## Reproduce the historical candidates

From this directory, with the existing probe image available:

```bash
bash prepare-dependencies.sh
bash build.sh
bash build.sh --reference-stringtype

# These deliberately return 1 for the known compatibility failures below.
bash probe-runtime.sh mono
bash probe-runtime.sh reference

# Existing host pdftotext is used only to inspect generated PDF output.
bash render-probes.sh ../original-native-bridge mono
bash render-probes.sh ../original-native-bridge reference
```

Dependency preparation downloads only pinned official source and the
hash-checked official Roslyn package. Compilation is network-disabled inside
the container, uses `/vbruntime-` to bootstrap the runtime from source, uses
the upstream complete source manifest/resources, and does not modify Mono's
source tree. Builds use `/deterministic+`; fixed container paths keep output
independent of the host directory.

The reference variant substitutes the entire official `StringType` class.
The small, reviewable `referencesource/stringtype-mono.patch` only adapts
imports, culture/constants, exception helpers and diagnostic resources; the
matching algorithms are unchanged. Mono's existing `Operators.LikeString`
and `LikeOperator.LikeString` already forward into `StringType`, so they use
the same replacement rather than competing matchers.

Rendering copies the parent's probe dependencies into this owned `run/`
directory and replaces **only that copy** of the VB runtime. It never writes
the parent's bridge or repository. Rendering uses the probe image's default
root user because its DPAPI bridge creates `/usr/share/.mono/keypairs`;
running the unchanged image as an arbitrary uid fails there before VB
execution. This is a probe-image permission prerequisite, not a VB change.

## Historical empirical results

With **each** runtime variant, the original ReportViewer produced:

| Fixture | Result |
| --- | --- |
| Numeric dataset filter, Quantity <= 6, 120 input rows | `ROWS=6; SUM=21; AMOUNT=26.25`; 7,951-byte PDF; zero warnings |
| Case-insensitive Like filter, `invoice line 00[1-3]`, 120 input rows | `ROWS=3; SUM=6; AMOUNT=7.50`; 8,102-byte PDF; zero warnings |
| Parent's original filtered `fixtures/chart.rdlc`, 120 input rows | 65,015-byte PDF; zero warnings |

Artifacts: `run/filter-{numeric,like}-{mono,reference}.pdf`, corresponding
extracted `.txt` files, `run/chart-{mono,reference}.pdf`.
Logs: `filters-mono.log`, `filters-reference.log`, `chart.log`.
This proves real filter selection and computed decimal aggregation, not just
successful JIT or a nonempty PDF.

The API probe covers `*`, `?`, `#`, bracket sets/ranges, negated sets,
case-insensitive and binary comparison, `LikeOperator`, decimal
`Operators.MultiplyObject`, `NewLateBinding.LateGet`, `Strings.Mid`, and
decimal/integer conversions.

Unmodified Mono: **16 / 22** assertions pass. Its invariant regex implementation
fails empty-string `*`, literal `[!]`, the culture-sensitive accented range
`e-acute Like [a-z]`, an unwanted trailing-newline match, and both Turkish
I comparisons.

Reference StringType candidate: **20 / 22** assertions pass. The first four
defects are fixed by Microsoft's actual legacy matcher; only the two Turkish
I comparisons remain.

## Historical Turkish failure analysis

Under `tr-TR`, Mono's own
`CompareInfo.Compare(..., CompareOptions.IgnoreCase)` reports nonzero for
both `U+0130` versus `i`, and `I` versus `U+0131`. Microsoft's matcher calls
that API, so replacing only the VB matching algorithm cannot fix this.
`MONO_DISABLE_MANAGED_COLLATION=yes` was also tried: it fixes only the first
case and regresses the accented-range case. It is **not recommended**.
Logs preserve the direct `CompareInfo` evidence.

Mono contains other unimplemented/partial APIs; compiling its complete
library is not proof that every Microsoft VB feature is equivalent. This
work resolves the observed ReportViewer runtime and filter failures, not
universal application compatibility. No edits were made to the original
native bridge, fonts, reporting service or repository.
