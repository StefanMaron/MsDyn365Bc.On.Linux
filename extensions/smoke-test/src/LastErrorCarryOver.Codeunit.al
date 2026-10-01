// Regression guard for issue #97.
//
// The last error a test method trapped must not be visible to the next test
// method in the same codeunit. A real tier clears it between methods: Microsoft's
// ALTestRunnerResetEnvironment (130453) calls ClearLastError() from
// OnBeforeTestMethodRun. The altool/TestRunnerHub runner has no AL test runner
// codeunit, so that subscriber never ran and GetLastErrorText() still returned
// the previous method's error. Patch #34 clears it in the hub's own runner.
//
// Method order matters: A traps an error, B runs next and must see none. Both
// run in this order on every runner.
codeunit 70009 "BC Linux Last Error Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure A_TrapsAnError()
    begin
        asserterror Error('BCLINUX-LE-CARRY-A');
        LibraryAssert.ExpectedError('BCLINUX-LE-CARRY-A');
    end;

    [Test]
    procedure B_NextTest_LastErrorIsEmpty()
    begin
        LibraryAssert.AreEqual('', GetLastErrorText(), 'The last error text carried over from the previous test method.');
        LibraryAssert.AreEqual('', GetLastErrorCode(), 'The last error code carried over from the previous test method.');
        LibraryAssert.AreEqual('', GetLastErrorCallStack(), 'The last error call stack carried over from the previous test method.');
    end;
}
