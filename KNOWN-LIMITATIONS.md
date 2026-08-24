# Known Test Limitations on BC Linux

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
BC_SQL_IMAGE=ghcr.io/stefanmaron/msdyn365bc.on.linux/mssql:2022-fts docker compose up -d --wait
```

or in the reusable workflows:

```yaml
with:
  sql_image: ghcr.io/stefanmaron/msdyn365bc.on.linux/mssql:2022-fts
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

## "NullReferenceException in NSClientCallback.CreateDotNetHandle" (~29+ failures)

**Root cause**: Tests that use .NET controls requiring a UI context (Camera,
Barcode Scanner, etc.) crash because the headless test runner has no client UI
to create .NET control handles on. `NSClientCallback.CreateDotNetHandle` throws
NullReferenceException when there's no UI session.

**Example**: `Camera Page Impl.` (CU 1908) `.IsAvailable` → crashes any test
that opens a page with a Camera control.

**Potential fix**: Patch `NSClientCallback.CreateDotNetHandle` in Nav.Service to
return a dummy handle (or null) instead of crashing. Similar approach to the
existing `NavOpenTaskPageAction.ShowForm` no-op (Patch #21). Would turn crashes
into graceful no-ops where the DotNet control simply isn't available.

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

## Data Encryption Mgmt. tests (Tests-Misc CU 132569) — 7 failures, by design

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
