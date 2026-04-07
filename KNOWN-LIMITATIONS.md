# Known Test Limitations on BC Linux

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
