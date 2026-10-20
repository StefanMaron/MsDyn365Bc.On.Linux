// Regression guard for issue #136: an Error() in a DotNet Timer's Elapsed trigger.
//
// The shape Microsoft's code coverage backup uses: a SingleInstance codeunit holds a
// [WithEvents] Microsoft.Dynamics.Nav.Timer. On Windows (BC 27.5 and 29.0) the test runs to
// the end: Elapsed fired, ExceptionOccurred did not. On Linux the test session ended.
// The DotNet assembly resolves through test-versions.yml's assembly_probing_paths.
dotnet
{
    assembly("mscorlib")
    {
        type("System.EventArgs"; BcLinuxEventArgs) { }
    }

    assembly("Microsoft.Dynamics.Nav.Timer")
    {
        type("Microsoft.Dynamics.Nav.Timer"; BcLinuxTimer) { }
        type("Microsoft.Dynamics.Nav.ExceptionOccurredEventArgs"; BcLinuxExceptionOccurredEventArgs) { }
    }
}

codeunit 70054 "BC Linux Timer Host"
{
    SingleInstance = true;

    var
        [WithEvents]
        Timer: DotNet BcLinuxTimer;
        ElapsedCount: Integer;
        ExceptionCount: Integer;
        ExceptionMessage: Text;
        FailInElapsed: Boolean;

    procedure StartTimer(IntervalMs: Integer; Fail: Boolean)
    begin
        ElapsedCount := 0;
        ExceptionCount := 0;
        ExceptionMessage := '';
        FailInElapsed := Fail;
        if IsNull(Timer) then
            Timer := Timer.Timer();
        Timer.Stop();
        Timer.Interval := IntervalMs;
        Timer.Start();
    end;

    procedure StopTimer()
    begin
        if not IsNull(Timer) then
            Timer.Stop();
    end;

    procedure GetElapsedCount(): Integer
    begin
        exit(ElapsedCount);
    end;

    procedure GetExceptionCount(): Integer
    begin
        exit(ExceptionCount);
    end;

    procedure GetExceptionMessage(): Text
    begin
        exit(ExceptionMessage);
    end;

    trigger Timer::Elapsed(sender: Variant; e: DotNet BcLinuxEventArgs)
    begin
        ElapsedCount += 1;
        if FailInElapsed then
            Error('bc-linux timer handler failed');
    end;

    trigger Timer::ExceptionOccurred(sender: Variant; e: DotNet BcLinuxExceptionOccurredEventArgs)
    begin
        ExceptionCount += 1;
        ExceptionMessage := e.Exception.Message;
    end;
}

codeunit 70055 "BC Linux Timer Error"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure ErrorInElapsedDoesNotEndTheTest()
    var
        Host: Codeunit "BC Linux Timer Host";
    begin
        Host.StartTimer(100, true);
        Sleep(1500);
        Host.StopTimer();
        LibraryAssert.IsTrue(Host.GetElapsedCount() > 0, 'Elapsed fired');
        LibraryAssert.AreEqual(0, Host.GetExceptionCount(), 'ExceptionOccurred fired');
        LibraryAssert.AreEqual('', Host.GetExceptionMessage(), 'ExceptionOccurred message');
    end;

    [Test]
    procedure SessionStillRunsTheNextTest()
    begin
        // Runs after the test above in the same session on both runners.
        LibraryAssert.IsTrue(true, 'session alive');
    end;
}
