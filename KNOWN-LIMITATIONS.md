# Known Test Limitations on BC Linux

## Where a green Linux leg is not evidence about BC

A test can pass here for the wrong reason. The corpus
(StefanMaron/BusinessCentral.AL.Language.Tests) started running its own nightly
against an official Microsoft BC container on Windows, and the disagreements it
found are listed below with what each one actually turned out to be. Anything on
this list is a surface where a green result on this tier says something about the
image or the harness, not about Business Central.

**This is a worklist, not a list of things we have decided to live with.** A
divergence is a defect from an AL developer's point of view whichever tier is
"cleaner", so every row says either what the fix is or why there is no fix. Only
one row below is genuinely closed as unfixable. Add to it whenever a new
divergence is settled, and take rows off it when they are fixed.

All rows measured on BC 28.4 unless stated. The Windows side is corpus run
34068376154 (BC 28.4, onprem, w1); the Linux side is a local BC 28.4 sandbox w1
container plus, where noted, corpus run 34079926224.

| Surface | Linux says | Real BC says | What it actually is |
|---|---|---|---|
| ~~`IsolatedStorage(Encrypted)`, `ENCRYPT`/`DECRYPT`~~ (issue #66) | ~~encrypted write reads back as plaintext~~ | "An encryption key is required to complete the request." | **FIXED — this tier now does real encryption.** Patch #26's premise was wrong: nothing in `CreateKey()` is Windows-only. It failed because the CRONUS demo backup ships its single `[$ndo$tenantproperty]` row with a BLANK `tenantid` while the NST runs tenant `default`, so the `UPDATE ... WHERE tenantid=@p` that persists the key file name matched zero rows — and the next line's `RequireKeyCreatedAndPresent()` threw. The key file was on disk the whole time; the tier could not remember its name. `entrypoint.sh` sets that tenantid, Patch #26 is now opt-in (`BC_FAKE_ENCRYPTION=1`), and the keys directory is symlinked onto the service volume so a container recreate does not orphan a key BC still remembers. Measured on a clean BC 28.4 boot: `EnableEncryption` succeeds and `EncryptText` returns 344 bytes of real ciphertext where the proxy returned 16. **Follow-on (issue #75): the tier now also creates the key.** BC never creates one by itself — on Windows it is an explicit setup step and online it comes from the service — so with Patch #26 off the corpus's `IsolatedStorage_SetEncrypted_GetRoundTripsPlaintext` still failed, correctly, with "An encryption key is required to complete the request." The test runner extension's install codeunit now calls BC's own `CreateEncryptionKey()`, which runs on every boot because the SQL data lives on a tmpfs and the extension is installed against a fresh database each time. Verified on a clean BC 28.4 boot: cu 60378 passes all four tests, and the probe reports `enabled=Yes keyexists=Yes cipherlen=344`. |
| ~~`Media.ExportStream()` byte length~~ (issue #67) | ~~exactly the imported bytes (68 in, 68 out)~~ **now 68 in, 93 out** | 31 bytes more (68 in, 99 out) | **FIXED — the round-trip re-encodes.** BC's `NavMediaImage.Bytes()` copies the original stream when it still holds one and only calls `Image.Save()` where it deliberately re-encodes, so `Save()` is reached exactly where a real tier produces different bytes. `DrawingStub` wrote the source bytes back there; it now decodes and re-encodes through SkiaSharp, which is already in the service dir for the report renderer. Reflection-only and optional — a hard reference would break the Add-Ins compile-time copy, and the entrypoint links `libSkiaSharp.so` only when it can version-match, so every failure path falls back to the old pass-through. **Superseded for PNG by issue #135 below:** the corpus image now comes out as the same 99 bytes as on Windows. |
| ~~`Media`/`MediaSet` bytes of an imported PNG~~ (issue #135) | ~~BC 27/28: re-encoded by Skia, still grayscale, an sBIT chunk; BC 29: the imported bytes~~ **now the Windows bytes** | 8-bit RGBA with sRGB and gAMA chunks | **FIXED for the sources GDI+ is known to treat this way.** `DrawingStub`'s `GdiPlusPng` decodes an 8-bit, non-interlaced gray, gray+alpha, RGB or RGBA PNG to RGBA and writes it as GDI+ does: IHDR color type 6, sRGB (0), gAMA (45455), libpng 1.6's adaptive filter choice, classic zlib (`libz.so.1`) with libpng's window sizing. BC 29 does not reach `DrawingStub` at all: when `OperatingSystem.IsWindows()` is false it builds Microsoft's own `NavMediaImageSkia`, whose `Bytes()` returns the imported bytes. **Patch #40** hands those bytes to the same `GdiPlusPng`. Measured against Windows for one image, the corpus's 1x1 grayscale PNG, which matches byte for byte (`extensions/smoke-test` codeunit 70053). **Not known:** the zlib level GDI+ uses (2 to 5 all give that output; 5 is used), so a larger image may compress to different bytes; palette, 16-bit, interlaced and colour-managed sources (gAMA other than 45455, cHRM, iCCP) still go through Skia. |
| ~~A PNG whose image data ends early~~ (issue #161) | ~~BC 29: refused with "not a valid image type"; BC 27/28: the imported 69 bytes~~ **now 99 bytes** | accepted, 99 bytes | **FIXED.** The corpus's 1x1 RGBA PNG one byte short holds 4 of the 5 bytes its image needs. BC 29 decodes an imported image with Skia to build the thumbnail, and Skia gives no bitmap for it; **Patch #42** then decodes `GdiPlusPng`'s re-encoding instead. `GdiPlusPng` refused short image data, which also left BC 27/28 with the source bytes; it now leaves the rest of the image zero. Only the length was compared with Windows (`extensions/smoke-test` codeunit 70053); the pixel values GDI+ fills in are not known. |
| ~~Control add-in procedure called from a TestPage action~~ (issue #133) | ~~"A call to Say failed with this message: Object reference not set to an instance of an object." (`DotNetInvoke`)~~ **now the Windows error** | "The control add-in on control Ctl on page WC3 Addin Card has not been instantiated." (`DotNetControlAddInNotCreated`) | **FIXED — the websocket runner's client answer.** BC asks the client for the add-in instance (`GetDotNetObject`); the web client answers with an `AutomationMethodResponse` whose `ReturnValue` is null, and `NavDotNet.CheckClientControlAddIn` then raises the not-instantiated error. `tools/TestRunner` acknowledged the call with an empty result, which `NSClientCallback` dereferenced. It now answers as the web client does. The hub's `TestRunnerClientCallback` already returned null and gave the Windows error. Guarded by `extensions/smoke-test` codeunit 70051, measured on BC 28.5 with both runners. |
| ~~`Error()` in a DotNet `Microsoft.Dynamics.Nav.Timer` Elapsed trigger~~ (issue #136) | ~~the whole NST process ends (rc=134), so the test session and the hub are lost~~ **now the test runs to the end** | the test runs to the end; Elapsed fired, ExceptionOccurred did not | **FIXED — Patch #39.** The Timer add-in raises Elapsed on a thread-pool thread and, with `RedirectExceptions` false, rethrows the trigger's error there; an exception that leaves a thread-pool callback ends a .NET process. The patch keeps the add-in's body without that rethrow. Why the same rethrow does not end the process on Windows was not determined. Windows BC 27.5 also failed to start the next test session after this test; BC 29.0 did not, and neither does this tier. Guarded by `extensions/smoke-test` codeunit 70055 (BC 28.5, both runners). |
| ~~A `?` in an in-memory filter, and the license's date filter, on BC 27~~ (issue #153) | ~~matches nothing: a temporary record filtered with `??12*` is empty, and every date typed into a page is refused with "the date you've entered is outside the allowed interval"~~ **now matches** | matches; both December dates are stored | **FIXED — Patch #41.** BC 27's `WildcardEvaluatorHelper` matches the parts of a pattern around a `?` with kernel32 `FindNLSString`, and the stub in `kernel32_stubs.c` answered "not found" for every input. The BC 27.5 Cronus license carries the date filter `??11*\|??12*\|??01*\|??02*`, which BC checks against the typed date as `ddMMyy` in `RecordImplementation.ValidateNonFlowFieldAsync`. The patch answers with the `CompareInfo` span calls BC 28 makes. BC 28 and later have no such helper and never had the problem. Guarded by `extensions/smoke-test` codeunit 70064. |
| ~~`Report.SaveAs` Pdf of a Word layout with section breaks and `WordMergeDataItem`~~ (issue #155) | ~~returns true and writes a PDF, but leaves "Unable to merge pdf streams due to internal error. Retry without compliance" (`InvalidOperation:TypeLoad`) as the last error~~ **now no last error** | no last error | **FIXED — `DrawingStub`.** BC renders each record to its own PDF and merges them with Aspose.PDF (`Microsoft.BusinessCentral.DocumentProcessor.PdfMerger`), which uses `System.Drawing.Drawing2D.Matrix` for image placement while it makes the PDF compliant. The stub had no `Matrix`, the compliant merge failed with a `TypeLoadException`, and BC retried without compliance. The stub now has GDI+'s 3x2 matrix in float math (row-vector convention, `MatrixOrder.Prepend` by default). Measured on BC 28.4 and 29.0, where both failed before. Aspose.PDF references about 60 more `Drawing2D`/`Imaging` types the stub does not have (`GraphicsPath`, the gradient brushes, `ImageAttributes`, `Metafile` ...); other PDF paths may still reach one. Guarded by `extensions/smoke-test` codeunit 70069. |
| ~~Report FormatRegion dates, times and numbers~~ (issue #157) | ~~ICU data: da-DK `14.30.15`, es-ES `31/1/2025`, nl-NL `31-01-2025`, en-GB `5 March 2024`, sv-SE `5 March 2024`, sv-SE/nb-NO minus U+2212~~ **now the Windows values** | `14:30:15`, `31/01/2025`, `31-1-2025`, `05 March 2024`, `den 5 March 2024`, `-` | **FIXED — `BcLinuxCulture.ApplyWindowsData`**, through the `culture` IL patch, so `Format()` with these cultures gets the same data. Measured on a Windows BC 28.5 container with the corpus's codeunit 81070 (`Run-TestsInBcContainer`); fr-FR, de-DE, de-CH, it-IT and ja-JP already matched. **BC 29 only, issue #167:** a Windows BC 29.0 container groups fr-FR thousands with U+00A0, CLDR has U+202F; `ApplyWindowsData` sets U+00A0 when the runtime is .NET 10 or later (guard: codeunit 70071 `FrenchGroupSeparatorIsNoBreakSpace`, BC 29+ legs). **Unknown region (`FormatRegion = 'zz-ZZ'`), issue #167:** Windows formats the dataset's typed dates zero-padded (`01/31/2025`), here they were `1/31/2025`. `ApplyWindowsData` sets the short date of a custom culture with region ZZ to `MM/dd/yyyy`. The method `CurrReport.FormatRegion('zz-ZZ')` is a separate path: it is refused with "An unsupported language was selected" because BC looks the culture up by LCID. Guarded by `extensions/smoke-test` codeunit 70071. |
| Outbound-HTTP consent prompt (issue #68) | prompt fires; a `[StrMenuHandler]` answers it | no prompt; a declared handler goes unexecuted | **Not a divergence in this image — the two tiers were not comparable.** The prompt is a sandbox protection: BC raises it in sandbox environments and never in production, gated on `TenantEnvironmentType`. This image sets that to `Sandbox`, and must — measured: with `Production` the platform test framework returns no results at all, on BOTH runners ("no test results returned for a Subtype=Test codeunit" on altool, "TestRunner API not available" on websocket). A sandbox prompts, we prompt, and **a Windows sandbox container would prompt too.** The Windows nightly does not prompt because it runs `onprem` artifacts. So the fix is on the comparison, not the image: the nightly needs to run `sandbox` artifacts (or run both and label them) before any difference between the two tiers means something about the operating system. Anything else BC gates on `TenantEnvironmentType` will show up the same way. |
| `Published Application`: `Package ID` vs `Runtime Package ID` (issue #69) | one GUID in both columns | two different GUIDs | **Neither — it is the publish route, and it is BC's own rule.** `NavAppPackageCompiler.CreateRuntimePackageId` (Nav.Ncl.dll) reads, in full: `new RuntimePackageId((isDeveloperExtension && !forceUniqueRuntimePackageId) ? packageId.Value.Value : Guid.NewGuid())`. bc-linux publishes through the dev endpoint, i.e. as a developer extension, so the package id is reused. BcContainerHelper publishes with `Publish-NAVApp`, which does not. Measured on a live container: of 141 rows in `[Published Application]`, the 9 with identical ids are exactly the 9 this image published through the dev endpoint. Publishing the same app the same way on Windows would give the same answer. |
| `Report.SaveAs(Pdf)` with an RDLC layout (issue #70) | returns false — RDLC rendering is not implemented | returns true | **The image. Re-measured 2026-09-07 on BC 28.4: still false**, so the claim is current, not stale. Note this does NOT generalise to "reports don't render on Linux": the Aspose.Words path (Word and Excel layouts) does render, since commit 9679545 linked Linux `libSkiaSharp.so` + harfbuzz into the service dir. RDLC is separate — it goes to the Windows Reporting Service PE binary, which the entrypoint replaces with a stub and Patch #19 makes throw `NavReportException` from `RenderAsync`. Fixing it means running Microsoft's own renderer, which is .NET Framework, under Mono — investigated and parked at a `TypeLoadException` inside ReportViewer's intermediate-format serializer; the CAS blocker before it IS solved. See `docs/RDLC-ON-LINUX.md` and issue #73. Until then the assertion belongs here (e.g. `extensions/smoke-test`), not in a corpus whose premise is "validated against a real service tier". |
| ~~Opening a card on an empty linked part: part page `OnNewRecord` firing count~~ (issue #78) | ~~6 firings~~ **now 3, matching Windows** | 3 firings | **FIXED — this tier now coalesces notifications the way a real BC tier does.** A diagnostic arm (`Card.Lines.First()` returns false, then `Card.Lines.Next()` immediately returns false) proved it was one draft row notified repeatedly, not extra rows — confirmed identical in shape on both tiers (an online sandbox run gave the exact same `afterFirst=3 \| next1=No,count=3`). Traced with `dotnet-trace` into `Microsoft.Dynamics.Nav.Client.UI.dll` and `Microsoft.Dynamics.Framework.UI.dll`: `DraftLinePattern.MakeDraftLines()` is invoked from three binding-manager notifications, and BC normally coalesces rapid-fire ones — `CommunicationChannel.EnsureQueueLength()` evicts queued messages past `ChannelOptions.QueueLength`, and `BindingManagerConsumerPort.FillStarting()` pulls only one survivor per channel per fill — but only when `CommunicationBroker.DefaultChannelOptions.Async` is `true`. This image's own `PatchTestPageClient` forced it to `false` (`src/tools/PatchNclTestPage/PatchTestPageClient.cs`, wired from `scripts/entrypoint.sh` Step 2b), which sends every notification inline with nothing to evict — twice as many `OnNewRecord` firings for the same page, on this tier only. The patch is now opt-in (`BC_TESTPAGE_ASYNC_PATCH=1`); default is unpatched (`Async=true`), matching Windows. The deadlock the patch was written against (commit 29d2bdf) was never isolated from that commit's OTHER fix in the same change (a `ToUnicodeEx` bug causing its own infinite loop) and was measured before TestPage could even run end-to-end on this tier — re-tested against a RunObject → `[ModalPageHandler]` StandardDialog target (the shape most likely to need a real pump) with `Async` left at `true`: 7/7 pass, no hang. Corpus PR #284 landed and rewrote `TestPagePartOnNewRecordCount.al` as delta assertions, which pass on either tier by construction — so nothing downstream checks the absolute count any more. `extensions/smoke-test`'s `BcLinuxOnrcTests` codeunit now carries that check directly, pinning 3 on this image's own version matrix. |
| Text and number formatting: line breaks, encodings, culture data (issue #113) | LF line breaks; UTF-8 for `TextEncoding::Windows`, `MSDos` and the default; an identity OEM table; a narrow no-break space before AM/PM; `/path` accepted as a request URI | CRLF; windows-1252 and code page 437; a plain space; `/path` refused | **Mostly FIXED — 212 of the 219 corpus tests that differed now fail on this tier with the message the Windows container gave** (a corpus test pins the Linux value, so "fails the way Windows fails" is what a fix looks like; the 219 are listed in issue #113). Measured on BC 29.0 against the Windows run's raw xunit messages. One root, several faces: .NET bakes `Environment.NewLine` and `Encoding.GetEncoding(0)` into CoreLib's precompiled code as constants, so no runtime hook can change them, and BC's own call sites are ordinary IL. `PatchNclTestPage` rewrites them on disk (Step 2b): `newline` (CRLF in `Environment.NewLine`, `StringBuilder.AppendLine`, `XmlWriterSettings`, `StreamWriter`/`StringWriter`, `XmlTextWriter(path)`, `XDocument.Save(Stream)`; also `YamlDotNet.dll`), `encoding` (`GetEncoding(0)` is 1252), `uri` (HTTP objects refuse a leading `/`) and `culture` (a plain space where ICU 72+ writes U+202F, and the da-DK short date). The OEM tables behind `MSDos` are real in `kernel32_stubs.c` now: .NET's windows-437/1252 best-fit data plus three pairs Microsoft's own table has (`0x14`↔`0xB6`, `0x15`↔`0xA7`, `0x0F`↔`0xA4`) — not a guess: with them BC's own pairing fix-up reproduces all 16 table entries the Windows results reveal (euro sign → `0xB1`), and without them it misses most of the ones outside both code pages. `BC_WINDOWS_TEXT=0` leaves the assemblies as shipped. `extensions/smoke-test` codeunit 70010 guards it, and `scripts/verify-patched-il.sh` checks the patched assemblies with `ilverify`. **Not fixable here, with reasons (issue #114):** collation and case mapping (4 tests): ICU orders `ä` before `å` and punctuation differently and upper-cases `ſ` to `S`, NLS does none of that, and ICU is the only collation .NET offers on Linux. The `SystemId` version nibbles (1 test): they come from SQL Server's `NEWSEQUENTIALID`, which differs between SQL Server on Linux and Windows. Default decimal places (2 tests, one is `Format` of a decimal): a field with no `DecimalPlaces` asks the UI helper system codeunit for its default, and Microsoft's Base Application answers that call (`<Precision,2:5>`, which is `#,##0.00###`); this tier does not install the Base Application, so BC falls back to `#,##0.00` — an environment difference, not a Linux one. One auto-increment number: the test prints the identity value, which depends on how many rows earlier tests put in that table, so it is order dependent on any platform. The 10 tests that compared the framework's `ArgumentOutOfRangeException` text were a regression of this fix and are fixed: `MapNativeException` now returns CR LF. 2 tests in codeunit 60285 threw a `TargetException` in a local run that sent every codeunit through one websocket session; they pass on the corpus CI and could not be reproduced there. All of it is tracked in issue #114 |
| ~~The virtual table "Time Zone" (2000000164)~~ (issue #132) | ~~419 IANA zones: No. 1 `Pacific/Niue`, `Europe/Berlin` at No. 195, no `W. Europe Standard Time`~~ | 144 Windows zones: No. 1 `Dateline Standard Time`, No. 50 `UTC`, No. 55 `W. Europe Standard Time` with display name `(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna`, No. 144 `Line Islands Standard Time` | **FIXED.** BC fills the table from `TimeZoneInfo.GetSystemTimeZones()` and reads only `Id` and `DisplayName`. ICU knows IANA zones only. `PatchNclTestPage culture` swaps that one call in `TimeZoneDataProvider` for `BcLinuxTimeZones.WindowsTimeZones()`, a fixed list of the 144 Windows zones in Windows' order, taken from .NET in `mcr.microsoft.com/windows/servercore:ltsc2025` build 10.0.26100.33650 — the OS build of the official BC containers the issue was measured on. Nothing else changes: `FindSystemTimeZoneById` already resolves a Windows id on Linux, and Patch #24's `ZoneForOffset` is untouched. Windows adds a zone now and then in a cumulative update; when the Windows corpus run reports a count other than 144, regenerate the list. `extensions/smoke-test` codeunit 70041 guards it |
| ~~`Format(Date, 0, 7)` month abbreviations, German and Spanish~~ (issue #134) | ~~`Jan.`, `März`, `Juni`, `Sept.`; `ene`, `sept`~~ | `Jan`, `Mrz`, `Jun`, `Sep`; `ene.`, `sep.` | **FIXED for de-DE and es-ES.** Format 7 is the region's long date with `MMMM` shortened to `MMM`, and .NET takes the `MMM` names from the session language's culture: ICU data here, NLS data on Windows. `BcLinuxCulture` sets `AbbreviatedMonthNames` and `AbbreviatedMonthGenitiveNames` to the Windows values for those two cultures only. Other languages are left alone: the BC Windows runs measured only these four, and English and French already agree. Windows' NLS names for 26 cultures, read with .NET Framework on Windows Server 2025, show that more of them differ from ICU (de-AT `Jän` against `Jän.`, da-DK `jan` against `jan.`); fixing those needs a Windows BC run that confirms them. `extensions/smoke-test` codeunit 70040 guards it |
| ~~`SecurityIdentifier` (DotNet, System.Security.Principal.Windows)~~ (issue #137) | ~~`BinaryLength` 28 and `IsAccountSid()` true for every SID; any text accepted as a SID~~ | `BinaryLength` 16 and `IsAccountSid()` false for `S-1-5-32-544`; `'not a sid'` raises "Value was invalid. (Parameter 'sddlForm')", error code `DotNetInvoke:Argument` | **FIXED.** This assembly is a stub here (`src/stubs/WindowsPrincipalStub`). Its `SecurityIdentifier` now parses, measures and prints a SID the way .NET does on Windows: the SID grammar including hex, leading zeros, clamped sub-authorities and the two-letter SDDL aliases; `BinaryLength` = 8 + 4 × sub-authorities; `IsAccountSid()` and `AccountDomainSid` for `S-1-5-21-x-y-z...`; the binary form; `CompareTo`; the well-known SID types. 119 inputs were compared against .NET on a Windows Server 2025 runner and all but one agree: a hex revision (`S-0x1-5-18`), which Windows reads as `S-1-5-24`. The aliases `LA` and `LG` (the Windows machine's own Administrator and Guest) are refused here. `extensions/smoke-test` codeunit 70042 guards it on the BC 29+ legs (the AL 16/17 compilers of the BC 27/28 legs cannot resolve a DotNet framework type on Linux) |
| ~~File dialogs and temporary paths in a test run~~ (issue #122) | ~~`File.IsPathTemporary` true for every path; `File.Download` / `DownloadFromStream` false (hub) or a `NullReferenceException` (websocket, uncatchable on BC 27); `Database.DataFileInformation` / `ImportData` empty with no error; on BC 27 a TestPage rename that asks "Your change might update related records" ends the session~~ | false outside the temporary folder (`C:\`, `Z:/`, UNC, `/`); download true with the file name unchanged; `NotSupportedFileDialogProvider` for the import; the rename is declined and the session goes on | **FIXED — all four, measured against the probes of corpus runs 39125184978 (Windows 27.5) and 39125190236 (Windows 29.0).** `IsPathTemporary` is BC's user-folder check, which Patch #28 had turned off; Patch #35 answers it again. The rest is the test client. A Windows container runs tests through the web server's client-services session, whose file provider hands a download to the caller and refuses an upload. The websocket runner (`tools/TestRunner`) answered every file dialog and every Confirm with a null result, which `NSClientCallback` casts or unboxes: the `NullReferenceException`s. It now answers a download with `EndClientFileActionCall` (accessed, request echoed), an upload with `SendClientException` carrying `NavNotSupportedFileDialogProviderException`, and a Confirm with its default. The hub's `TestRunnerClientCallback` said false to both file dialogs; Patch #36 makes it give the same answers. Verified on BC 27.5 (websocket) and 28.5 (websocket and hub) with the corpus probes and `extensions/smoke-test` codeunits 70012, 70013 and 70018. On the websocket runner every upload is now refused that way, `UploadIntoStream` included, as the Windows client's provider does; not measured on Windows for anything but `ImportData` and `DataFileInformation`. Not covered: `File.View`, and the hub's `UploadFileAction` (still no file, no error). Accepting a download without reading its stream broke `Database.ExportData`, which writes the export into a pipe that blocks until the client reads it: every call waited 60 s and ended in an uncatchable `TimeoutException` (found in issue #125). The runner now fetches the stream from `uploadDownload/download` with the session's `server-session-id`, as Microsoft's `UploadDownloadClient` does; codeunit 70012 guards it. |

Four entries that were on this list and are now closed, all harness rather than
tier:

- **`TestPermissions` was not enforced on the fast path** (issue #64). The
  altool/TestRunnerHub runner runs no AL test runner codeunit, and that is what
  the platform switches the permission execution context through — so every test
  on that leg ran as SUPER regardless of what it declared. Measured on one
  container in one minute: a test codeunit with no `TestPermissions` declaration
  inserting into its own table is refused by the websocket runner ("Sorry, the
  current permissions prevented the action.") and succeeds on the hub. At corpus
  scale the websocket runner reports 652 permission refusals where the hybrid
  runner's fast path reported none, against 659 on the Windows container — the
  same defect, the same size. `classify-handler-codeunits.py` now routes anything
  not declaring `TestPermissions = Disabled` to the websocket leg.
- **`SingleInstance` codeunit state was torn down between test codeunits**
  (issue #65). That state lives for the lifetime of the session, and the
  websocket runner opened a new one before every codeunit. Real BC does not:
  BcContainerHelper defaults `RenewClientContextBetweenTests` to `$false`.
  `tools/TestRunner/Program.cs` no longer renews by default
  (`--renew-client-context` restores the old behaviour). Note the reason recorded
  for the old behaviour — "BC kills the session after each codeunit under test
  isolation" — was wrong: `TestIsolation = Codeunit` rolls back database changes
  and says nothing about the session.
- **An unhandled `Confirm` in a part's `OnOpenPage` was not refused on BC 28.x**
  (issue #93). Windows 28.4 and this image's 27.x fail `Host.OpenView()` with
  `Unhandled UI: Confirm`. This image's 28.0 to 28.5 opened the host and raised
  nothing. No UI patch is involved. On BC 28+ the `test_runner=auto` workflow
  runs tests on the altool hub, and the hub has no test runner codeunit to
  refuse an unhandled Confirm. 27.x has no hub, so it uses the websocket runner,
  which refuses it. Measured on one BC 28.4 container: the same four tests
  (a Confirm in a part's `OnOpenPage`, in the host's own `OnOpenPage`, on an
  empty table and on a table with a row) pass on the websocket runner and fail
  on both altool transports (`cli` and `hub`). So the problem is not specific to
  parts. `classify-handler-codeunits.py` did not catch the test, because rule (b)
  needs `.RunModal(` or `.Invoke(` next to `asserterror` and this test only calls
  `OpenView()`. It now also routes any test whose body contains the text
  `Unhandled UI` to the websocket runner (rule (d)), and
  `scripts/test-classify-handler-codeunits.py` covers it. **Residual:** a test
  that depends on the refusal without naming the text, for example one with a
  looser `ExpectedError`, still runs on the hub and passes silently. Only
  making the hub refuse unhandled UI itself would close that.
- **`Active Session."Client Type"` read `Unknown` on BC 27.x** (issue #86). The
  row said `Unknown` while `CurrentClientType()` answered `Windows` in the same
  session. A Windows container passes. It looked like a 27.x versus 28.x
  difference, but it is a runner difference: on BC 28+ `test_runner=auto` uses the
  altool hub, which runs tests in a Web Client session (`Web Client` and `Web`),
  and 27.x has no hub, so it uses the websocket runner. Measured on one BC 28.4
  container, the same test reads `Unknown` and `Windows` on the websocket runner,
  and `Web Client` and `Web` on both altool transports. The server takes the
  session's connection type from `ConnectionRequest.ClientConnectionType` in the
  client's `OpenConnection` call. `tools/TestRunner/Program.cs` did not send it, so
  the session kept the default `UnknownClient`. The row maps that to `Unknown`,
  and `CurrentClientType()` falls through to the server's `DefaultClient` setting,
  which is `Windows`. The runner now sends `ClientService` (115), which is what a
  real client-services session sends, giving `Client Service` and `Web`.
  `extensions/smoke-test` carries a guard (codeunit 70008) that passes on both
  runners. **Side effect:** a `ClientService` session is interactive for
  `DisableUpdateUserPersonalization`, so it now writes user personalization the
  way a real client does, and its number-format symbols follow the same path as
  a real test session. The image must be rebuilt (`docker compose build bc`) for
  `run-tests.sh` to use the new `TestRunner.dll`.

## The hub runner skips the Test Runner's per-test reset (issue #97)

The altool/TestRunnerHub runner has no AL test runner codeunit (130450/130451), so
the "Test Runner - Mgt" events never fire and Microsoft's
`ALTestRunnerResetEnvironment` (130453) never runs. On a real tier its
`OnBeforeTestMethodRun` calls `ClearLastError()` before every test method. On the hub
nothing did, so the last error a test trapped was still there when the next method in
the same codeunit started. Measured on BC 28.4 with a test that traps
`Error('X')` followed by one that asserts `GetLastErrorText()` is empty: the websocket
runner passes and the hub failed with `Actual:<X>`. The next codeunit was not
affected, because each codeunit gets its own session.

**Fixed for the last error: Patch #34** re-implements
`DevTestRunnerCodeunit.OnBeforeTestRunAsync` (the hub's own C# runner, so the
websocket runner and a real tier are untouched) and calls `ClearLastError()` first.
`extensions/smoke-test` carries the guard (codeunit 70009).

**Not fixed, and not measured:** the rest of 130453 is also skipped on the hub. From
reading its source: `OnBeforeTestMethodRun` also resets the application areas and runs
codeunits 130301, 130302 and 132553 when they are present, and `OnAfterCodeunitRun`
restores WorkDate and the application areas. A test that depends on those carrying
over, or being reset, between methods can still differ on the hub. Routing by AL source
cannot find those tests, so the options are to raise the same events from the hub's
runner or to accept the gap. Measure a case before choosing.

## BC 30 opened no service port (issue #91)

BC 30 hosts several API endpoints on one port, separated by URL path. HTTP.SYS routes
those by prefix, but BC 30's hosts use Kestrel directly and Kestrel cannot bind a port
twice. The second `Open()` threw `AddressInUseException`, the service start failed, and
the container sat unhealthy with nothing on 7045, 7047, 7048, 7049 or 7085. **Patch
#33** gives auxiliary endpoints their own ports and moves a colliding primary endpoint
(see the header in `StartupHook.cs`), and `entrypoint.sh` forces
`ManagementApiServicesEnabled` off. Reported and verified on BC 30.0.55227.0 by
@ernestasjuska: healthy in 91 seconds, Role Center reached.

**Still broken on BC 30:** the test framework apps (`Library Assert`, `Test Runner` and
the rest) fail to publish with HTTP 422, so metadata they define is missing. The cause
has not been determined. BC 30 is therefore a boot-only target for now.

## Failure triage: bcapps-gate run 2026-08-06 (BC 28.1, hub runner, TC=0 legs)

Full classification of the 787 Tests-Misc + 165 Tests-Workflow failures from
gate run 31090966927 (per-method JUnit in the run's `result-*-tc0` artifacts).
Every failure fell into one of the buckets below. "Hub runner" caveat applies
throughout: `run-tests-altool.py --transport hub` runs WITHOUT an AL test-runner
codeunit, so isolation-dependent buckets are inflated versus `run-tests.sh`.

| Bucket | Count | Class |
|---|---|---|
| ~~`NavClientHandle.Dispose` NullRef~~ (form/handle teardown without client UI) | 189 | **FIXED — Patch #27** (null-guarded Dispose; disposal-side sibling of the CreateDotNetHandle hole). Validation rerun: signature fully eliminated |
| TestPage metadata: "field/action/part with ID not found", "TestPage is not open" | ~165 | needs comparison vs websocket runner before judging — may be hub-runner artifact, may be TestPageClient patch gap |
| ~~Report rendering pipeline~~: "Value too large/small for Int32" in `Report Selections.SaveReportAsHTML/PDFInTempBlob` (70), uninstantiated `XmlDocument.Load` (43), empty PDF attachments | ~143 | **MOSTLY FIXED — report rendering now actually works on Linux.** The renderer text-shapes via native P/Invoke `harfbuzz` and rasterizes via SkiaSharp; the artifact only ships Windows natives. The image now bundles Linux `libSkiaSharp.so` (2.88.9 + 3.119.0) and the entrypoint links a version-matched pair (system harfbuzz + Skia) into the service dir — BOTH or NEITHER (Skia-missing-but-harfbuzz-present kills the NST via SkiaSharp's SKObject finalizer). Validated on 28.1: Report Selections Tests 54→14 failures, Document Attachment Tests 91→38, zero NST crashes |
| Missing-isolation cascades: "General Posting Setup does not exist" (39), "Commit with AutoRollback" (25), empty-table/filter asserts | ~90 | hub-runner artifact (no test-runner codeunit → no RequiredTestIsolation); expected to shrink under run-tests.sh |
| ~~Empty generated images~~: "selected file 'x.jpeg' has no content" | ~17 | **FIXED — DrawingStub** now emits real image bytes (source-byte passthrough on round-trips, valid synthesized PNG/JPEG otherwise). CU 134776: 91 → 45 failures; the rest are empty *PDF* attachments (reporting-stub bucket above) |
| License limit ("new users do not meet the terms", SUPER assignment) | ~14 | Cronus demo license constraint — use `BC_LICENSE_FILE` (ISV license) to clear |
| User cannot be deleted (BCRUNNER logged on) | 9 | documented below |
| Word merger `TransformContentElement` NullRef (TC=0 residual, non-fatal) | 6 | Patch #23 neighborhood; benign leftovers |
| ~~Backslash paths~~: "Access is denied to file '/bc/service\\..\\..\\App\\Test\\Files\\...'" | 5 | **FIXED — Patch #28** (ExpandFileName normalizes `\`→`/`, materializes user-folder parents). Access-denied signature eliminated; the ImageAnalysis tests still fail honestly because the artifact doesn't ship `App/Test/Files` |
| `MemoryMappedFile.CreateOrOpen`: "Named maps are not supported" | 3 | genuine .NET-on-Linux platform limitation |
| Long tail (assert diffs, localized-collation artifacts, item tracking data) | rest | mostly data/cascade noise; re-triage after the isolation bucket is sized |

Remaining next steps: (1) rerun one suite under `run-tests.sh` to size the
isolation + TestPage buckets honestly; (2) `NSClientCallback.CreateDotNetHandle`
("A call to IsAvailable failed": the creation-side hole, still open, see below).

## Apps using `OptimizeForTextSearch` need the FTS SQL image (issue #20)

No official SQL Server **Linux** image ships the Full-Text Search
component — it's a separate `mssql-server-fts` package — so an app that
declares `OptimizeForTextSearch = true` on any field cannot be installed
on the default stack. The dev endpoint returns HTTP 422:

```
Text optimized index cannot be created/queried because the SQL Server
Full-Text Search component is not installed.
```

**The follow-on error is the one you'll actually see first**, and it
points somewhere completely wrong — every app depending on the one that
failed to install then fails to compile with:

```
error AL1024: A package with publisher '<P>', name '<A>', and a version
compatible with '27.0.0.0' could not be loaded. Symbols for the requested
app ... could not be found in the database.
```

That reads like a symbol-staging problem and sends you into `.alpackages`
and `stage-symbols.py`. It isn't: the app simply never installed.
`scripts/publish-app.sh` now detects the FTS 422 and says so explicitly.

**Fix** — switch to the FTS-capable SQL image:

```bash
BC_SQL_IMAGE=ghcr.io/aldevtools/msdyn365bc.on.linux/mssql:2022-fts docker compose up -d --wait
```

or in the reusable workflows:

```yaml
with:
  sql_image: ghcr.io/aldevtools/msdyn365bc.on.linux/mssql:2022-fts
```

It is **opt-in, not the default**: FTS adds ~550 MB (1.68 GB → 2.23 GB
uncompressed, measured), and the SQL pull sits on the critical path of
every CI run. The tag is built and verified
(`SERVERPROPERTY('IsFullTextInstalled') = 1`) by
`.github/workflows/mirror-sql-image.yml` alongside the other mirrors.

Reported by @ChristianHovenbitzer.

## "User cannot be deleted because logged on" (~142 failures in SINGLESERVER)

**Root cause**: Microsoft's test cleanup code does broad `User.DeleteAll()` or
`User.FindFirst(); User.Delete()` without filtering out the session user. BC's
platform rejects the delete before it even reaches the transaction layer, so
codeunit isolation rollback can't help.

**Biggest contributors**:
- `DocumentApprovalUsers.TestCleanup()` — calls `DeleteAllUsers()` which deletes
  `FindFirst()` result (60+ calls)
- `UserCardTest.EnsureNoUsers()` — `User.DeleteAll()` unfiltered
- `UserAccessinSaaSTests.Initialize()` — `User.DeleteAll(true)` unfiltered
- `DocumentApprovalDocuments` teardown — explicitly targets `UserId()` for cleanup

**Why this works on Windows**: Microsoft containers use Windows Auth where the OS
identity is separate from the BC User table. Tests can delete the BC "ADMIN" user
because the Windows service account keeps the session alive independently.

**On Linux**: Our BCRUNNER user is the session user AND the User table record.
The platform blocks deletion of any user with an active session.

**Impact on benchmarks**: These failures happen during setup/teardown, not during
the actual test logic. Tests that fail early (in setup) run faster than they would
on Windows, slightly skewing timing comparisons for affected codeunits.

**Potential fix**: Patch the .NET platform check that validates "user is logged on"
to skip the constraint. Not implemented — would require finding the exact method
in Nav.Ncl or Nav.Server that performs this check.

## ~~"NullReferenceException in NSClientCallback.CreateDotNetHandle" (~29+ failures)~~ — FIXED (issue #125)

**Root cause**: not a missing UI session in BC, but the answer the test client gave.
`if Camera.IsAvailable()`, `UserTours.IsAvailable()`, `PageNotifier.IsAvailable()` and the
other client capabilities are RunOnClient DotNet: BC asks the client to create a handle for
the type (`CreateDotNetHandle`), then calls the static `IsAvailable` on it
(`InvokeAutomationMethod`). The websocket runner acknowledged both with a null result, and
`NSClientCallback` dereferenced the missing `AutomationMethodResponse`. The hub's
`TestRunnerClientCallback` returns null from both, with the same effect. That is what broke a
TestPage of the Business Manager Role Center (`"O365 Activities"` `OnInit`), and on BC 27 an
exception in a client callback ends the session.

**Fix**: answer as a Windows container's test client does. That client is the web server's
client-services session, whose `ObservingAutomationHandler`
(`Microsoft.Dynamics.Nav.Client.WebCommon`) creates a handle for the eleven client extension
types it knows when no instance is asked for, says false to `IsAvailable` and `#IsNull` on
it in a session without a browser, and refuses every other RunOnClient DotNet with
`NavDotNetFromAlNotSupportedException`. `tools/TestRunner` now gives those answers (and
acknowledges `DisposeAutomationObject`, which BC waits for and which nothing reached
before); Patch #37 gives them on the hub. Verified on BC 28.5 with both runners, and guarded
by `extensions/smoke-test` codeunit 70019. The IsAvailable answer was read from Microsoft's
code, not measured on Windows; the Role Center opening on Windows was measured (corpus run
39162785239).

Note that Patch #21 (`NavOpenTaskPageAction.ShowForm`) is no longer a model for
that: it started as a no-op and was rewritten to do the real work, because
skipping the call made the container silently unable to answer any question
about opening a page through an action. A no-op that swallows a call whose
*absence* the test can observe buys a live session at the price of a wrong
answer. Prefer doing the real work and containing the failure.

## ~~Container crash after Tests-Misc in sequential Bucket 4 runs~~ (FIXED — Patch #23)

**Status**: Fixed in Patch #23 (`OfficeWordDocumentPictureMerger.ReplaceMissingImageWithTransparentImage`).

**Symptom (was)**: When running Bucket 4 sequentially
(ERM → SCM → Misc → Workflow → SCM-Service → SINGLESERVER), the BC container
became unhealthy after Tests-Misc completed. The remaining 3 apps (Workflow,
SCM-Service, SINGLESERVER) all failed with "Failed to create run request"
because the API was dead.

**Root cause**: Infinite recursion in Microsoft's
`Microsoft.Dynamics.Nav.OpenXml.Word.DocumentMerger.OfficeWordDocumentPictureMerger.ReplaceMissingImageWithTransparentImage`.
When a Word report references a missing image, the method calls
`MergePictureElements` with the transparent placeholder, which re-enters
`ReplaceMissingImageWithTransparentImage` unconditionally → ~37,390 frames
deep → stack overflow → fatal session crash → container goes unhealthy.
Triggered by `TestSendToEMailAndPDFVendor` in Tests-Misc; two earlier
`NavNCLStackOverflowException` events were also visible during ERM and SCM but
were recoverable until the deeper Misc invocation killed the worker.

**Fix**: Patch #23 in `StartupHook.cs` no-ops
`ReplaceMissingImageWithTransparentImage` via JMP hook (the type is in
`Microsoft.Dynamics.Nav.OpenXml.dll`, JIT-compiled BC code → patchable).
The missing image XElement is left in place — reports render with a broken
image marker but the session survives and report generation completes.
The Misc tests do not validate rendered image content.

**Diagnostic logs (historical)**:
- Local benchmark run 2026-04-04 stack trace:
  `PipelinePerformanceComparison/benchmark-results/local-20260404/bc-container.log`
- GitHub Actions run 23974655275 (same crash pattern, same offending test)

## ~~Data Encryption Mgmt. tests (Tests-Misc CU 132569) — 7 failures, by design~~ (SUPERSEDED — real encryption works, see issue #66)

**Everything below described the pass-through proxy, which is no longer applied by
default.** The reason it existed — "CreateKey() depends on Windows-only key storage"
— was wrong. `RsaEncryptionProviderBase.CreateKey()` is `RSACryptoServiceProvider`
+ `ToXmlString` + `File.Create(name, 1024, FileOptions.Encrypted)` + a hash; all of
that works on .NET 8 Linux (`FileOptions.Encrypted` is Windows EFS and .NET ignores
it on Unix). It failed because the CRONUS demo backup ships its single
`[$ndo$tenantproperty]` row with a blank `tenantid` while the NST runs tenant
`default`, so the UPDATE that persists the key file name matched zero rows. See the
tenantid fix in `scripts/entrypoint.sh` Step 3.

With that fixed, `EnableEncryption` succeeds and `EncryptText` returns real
ciphertext, so the 7 residual failures listed at the end of this section should no
longer be expected — they were properties of the fake. **That has not been
re-measured against CU 132569 itself**; the check was done with a purpose-built
probe on BC 28.4. Re-run the BCApps sweep before trusting the old numbers either
way. `BC_FAKE_ENCRYPTION=1` restores the proxy and everything below with it.


**Root cause (was)**: `TenantEncryptionProviderFactory.GetTenantEncryptionProvider`
(Nav.Ncl.dll) — the factory AL's `ENCRYPT`/`DECRYPT`, `IsolatedStorage(Encrypted=true)`,
and Data Encryption Management (System App codeunit 1266/1279) all resolve their key
through — is a completely separate path from Patch #7's
`DefaultServerInstanceRsaEncryptionProviderFactory` (SQL connection-string password
only). Left unpatched it returned a real `TenantRsaEncryptionProvider` whose
`CreateKey()` threw `NavEncryptionNotCreatedException`
("An encryption key is required to complete the request."), failing any AL code
that touches encryption — not just this test codeunit.

**Fix**: Patch #26 hooks the factory to always return the same pass-through
`ISystemEncryptionProvider` proxy Patch #7 builds (`IsKeyPresent`/`IsKeyCreated`
always `true`, `Encrypt`/`Decrypt` pass the value through unchanged,
`CreateKey`/`DeleteKey`/`ImportKey`/`ExportKey` no-op). This is a deliberate
"good enough to not crash" fake, not real cryptography — see the Patch #26 header
comment in `StartupHook.cs`.

**Caveat — applied on a 20s delay, not at Nav.Ncl load time**: at assembly-load
time the target method has never been called, so its precode still points at a
shared not-yet-JIT-compiled stub; JMP-hooking that address hijacked every *other*
method resolving through the same shared stub on first call, observed as an
unrelated NST boot crash (`PlatformNotSupportedException` in
`NavDirectorySecurity.CreateSecurityForDomainDirectory`, reached via
`NavSystemTenant..ctor` during system tenant bootstrap). Deferring the hook a few
seconds avoids the collision. If you see boot crashes with an ACL/`TempPathHelper`
stack trace, suspect this class of hook timing issue before anything else.

**Residual failures (expected, not regressions)**: because "is a key present"
always reports `true` and `Encrypt`/`Decrypt` don't actually transform data, 7 of
21 subtests in CU 132569 still fail — they specifically assert the
enabled/disabled toggle and that ciphertext differs from plaintext, which a
pass-through fake structurally cannot satisfy:
`EncryptThrowsErrorWhenEncryptionIsNotEnabled`,
`DecryptThrowsErrorWhenEncryptionIsNotEnabled`, `EncryptDecryptText`,
`TestEncryptionMgmtPageOpenWhenEncryptionIsDisabled`,
`TestEncryptionMgmtPageOpenWhenEncryptionIsEnabled`,
`TestEnableEncryptionInEncryptionMgmtPage`,
`TestDisableEncryptionInEncryptionMgmtPage`. The other 14 subtests (hashing,
blob content hash, etc.) pass. Real encryption semantics (actual AES/RSA,
genuine key provisioning, toggle support) would need Patch #7's original
"real work" scope — flagged as infra work, not attempted here.

## ~~ISV extension installs fail with "You must assign at least one user the SUPER permission set..."~~ (FIXED — entrypoint.sh + Patch #22b)

**Symptom**: publishing/installing a third-party extension whose install
codeunit performs any transaction-committing operation (e.g. Continia
OPplus's install codeunit, which calls Microsoft's `IsPlanAssignedToUser`)
fails on BC Linux with:

```
You must assign at least one user the SUPER permission set and configure
that user to log in with authentication type 'NavUserPassword', which is
supported by the current server instance.
```

...even when a NavUserPassword SUPER user obviously exists and is
reachable via OData/dev-endpoint with the same credentials. This message
is BC's own generic fallback for ANY failed install-transaction commit,
not specifically about a missing/misconfigured SUPER user — so it hid
**two independent, unrelated bugs** that both happened to surface through
it. Neither showed a real exception in BC's normal logs: install-time
exceptions are reported via `NavCSideException` with the message text
redacted ("Message not shown because the NavBaseException constructor
was used without privacy classification"); both needed
`BC_DEBUG_FIRSTCHANCE=1` plus decompiling `Microsoft.Dynamics.Nav.Ncl.dll`
to actually find.

**Bug 1 (entrypoint.sh)**: the bootstrap `BC_SERVER_USERNAME` and
`YOURBC-SERVICEUSER` accounts had `[User].[Expiry Date]` set to
`2099-12-31`, on the assumption that "a date far enough in the future"
means "never expires". BC's own
`SystemTableTriggers.CheckForExistenceOfSuperUserIfNecessaryAsync` query
(decompiled from `Nav.Ncl.dll`) filters candidate SUPER users to
`expiryDateField.Equal(NavDateTime.Undefined)` — the platform's actual
"never expires" sentinel, `1753-01-01` — so both bootstrap users were
silently excluded from every SUPER-user check. Fixed by using
`1753-01-01` for both, matching the convention BC itself already used two
lines below for `[User Property].[WebServices Key Expiry Date]`.

**Bug 2 (StartupHook.cs, Patch #22b)**: even with Bug 1 fixed, AL's
`GraphQuery` DotNet variable's `GetTenantDetail()` call (reached via
`IsPlanAssignedToUser`) threw a `NullReferenceException` on a null
`currentSession`, unrelated to SUPER/user config at all — see the
Patch #22b header comment in `StartupHook.cs` for the full root cause
(GraphQuery's own ctor always passes a null session to its inner
`AzureADGraphQuery`, regardless of what's actually current).

**Verified live end-to-end** with both fixes applied: Continia OPplus
(and its dependencies — Continia System Application, Continia Core,
Continia Connector App — plus its own Trial Balance VAT DACH add-on)
all install successfully against a freshly created container.

## ~~Platform-table captions come back in Traditional Chinese inside English error messages~~ (FIXED — Patch #31, issue #52)

Symptom: `標題 must have a value in 所有設定檔: 範圍=租用戶, 應用程式識別碼=…, 設定檔識別碼=…. It cannot be zero or empty.`
The message template is English — the session really is running with
`GlobalLanguage()=1033` — but every caption belonging to a **platform/virtual
table** (All Profile, User, Company, their fields and option values) is
rendered in zh-TW. Base Application tables are unaffected, and forcing
`GLOBALLANGUAGE(1033)` changes nothing, which is why this looked like a
session leak and wasn't one.

Root cause, verified against a live 27.0 container by calling Microsoft's own
code: platform table metadata (embedded System package in
`Microsoft.BusinessCentral.SystemApp.dll`) carries alphabetically ordered
CaptionML strings keyed by Windows three-letter names
(`…;CHT=所有設定檔;…;ENU=All Profile;…`). `LanguageHelper` maps those
abbreviations to LCIDs from `CultureInfo.GetCultures(AllCultures)`, and on
Linux (.NET on ICU) Chinese is only enumerated as `zh-Hant-TW`/`zh-Hans-CN`
with LCID 4096, which BC filters out — plain `zh-TW` (1028, `CHT`) is never
seen. Unknown abbreviations fall back to 1033, so the Chinese text is stored
under the English LCID, first in the list, and every first-match lookup for
1033 returns it. `GetLanguageIdByAbbreviatedName("CHT")` really returned 1033
and `MultiLanguage.Parse("…CHT=x;…ENU=y").GetText(1033)` really returned `x`.

Patch #31 hooks the `LanguageHelper` constructor and rebuilds its table from
`WindowsLanguageHelper.AllCultures` plus the cultures ICU hides
(`zh-TW`, `zh-HK`, `zh-MO`, `zh-SG`, …). All 49 abbreviations used by the
platform's CaptionML resolve to their Windows LCIDs afterwards; on Windows the
augmentation is a no-op. Session language selection is untouched, so a test
that deliberately switches to zh-TW still gets Chinese captions.

## ~~Random "Unexpected CLR exception thrown." NullReferenceException on any AL statement~~ (FIXED — Patch #32/#32b)

Symptom: one test in one codeunit, in an otherwise green run, fails with

```
Unexpected CLR exception thrown.: System.NullReferenceException
  at BclLicenseDataReader.ILicenseDataReader.TryGetPermissionMask(...)
  at NavLicense.GetLicensePermissionMask(...)
  at NavDatabaseSecurityAndLicense.<>c.<.ctor>b__13_0(ApplicationObjectId)
  at NavDatabaseSecurityAndLicense.GetLicensePermissions(...)
  at LicensePermissionSet.FetchPermissions()
  ...
  at RecordImplementation.VerifyPermissions(...)
```

The AL frame at the bottom is arbitrary — whatever statement happened to need
a permission check. Observed on `Record.DeleteAll` inside a fixture-cleanup
`Initialize`. Rerunning passes. Sibling BC versions on the same commit pass.

Root cause is a use-after-dispose race in Microsoft's own code, not anything
Linux-specific — there is no Win32 API anywhere in the chain:

```csharp
// BclLicenseDataReader — the ENTIRE Dispose body
void IDisposable.Dispose() { reader = null; }

// ...and every accessor, unguarded
bool TryGetPermissionMask(ObjectType t, int n, out PermissionMask m) {
    m = PermissionMask.None;
    var p = reader.GetObjectRangePermission(...);   // NRE
```

`NavDatabase.Dispose` → `NavDatabaseSecurityAndLicense.Dispose` →
`NavLicense.Dispose` → that. Meanwhile the reader side is unsynchronized:
`GetLicensePermissions` holds `licenseLock` only around its memo-cache reads
and writes, and calls the actual license lookup
(`NavCurrentThread.Session.License.GetLicensePermissionMask`) between them
holding nothing. So a database teardown concurrent with a running session
gives that session an NRE.

bc-linux does not cause the race but is unusually good at provoking it:
`run-tests-altool.py`'s default `cli` transport starts a fresh BC session per
codeunit, so session start and database teardown overlap continuously for the
length of a run. One bad landing in a 300-codeunit run reddens the leg.

`run-tests-hybrid.py` used to add a second, concurrent websocket leg on top of
that; it no longer does — the legs are serialised (see `run_legs` there, and
StefanMaron/BusinessCentral.AL.Language.Tests#158). That removes one source of
overlap, not the race: the per-codeunit session churn inside the altool leg is
by itself enough to provoke it, so Patches #32 and #32b below stay load-bearing.

**Patch #32** no-ops `NavLicense.Dispose(bool)` and **Patch #32b** no-ops
`NavDatabaseSecurityAndLicense.Dispose(bool)`. Both bodies are teardown
bookkeeping: `isDisposed` is read nowhere but inside the Dispose that writes
it, and the owner nulls its own reference regardless, so the object graph
still becomes unreachable at the same instant. #32b additionally skips
`ReaderWriterLockSlim.Dispose` — deliberate, because that lock is the other
half of the same window (a race landing on `EnterReadLock` gets
`ObjectDisposedException` instead of the NRE), and an undisposed RWLS costs
at most three finalizer-reclaimed wait events per NavDatabase.

Both patches shape-check the fields they reasoned about and skip themselves
with a log line if Microsoft changes the type, so a future BC that gives these
types a real resource to release will not silently leak it.
