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
