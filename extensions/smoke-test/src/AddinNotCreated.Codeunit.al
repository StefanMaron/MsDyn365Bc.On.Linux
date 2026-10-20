// Regression guard for issue #133: a control add-in procedure called from a TestPage action.
//
// A TestPage has no client form, so the add-in on it was never created. BC asks the client
// for the add-in instance (GetDotNetObject) and, when the answer is null, raises "The control
// add-in on control <control> on page <page> has not been instantiated." That is the result
// on Windows (BC 27.5 and 29.0). The websocket runner's client answered with an empty
// acknowledgement instead, which BC read as a null response and dereferenced: "A call to Say
// failed with this message: Object reference not set to an instance of an object."
// (DotNetInvoke). tools/TestRunner now answers as the web client does.
codeunit 70051 "BC Linux Addin Not Created"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";
        NotCreatedTxt: Label 'The control add-in on control Ctl on page BC Linux Addin Card has not been instantiated.', Locked = true;

    [Test]
    procedure AddinProcedureFailsAsOnWindows()
    var
        Card: TestPage "BC Linux Addin Card";
    begin
        Card.OpenView();
        asserterror Card.SayHello.Invoke();
        LibraryAssert.AreEqual(NotCreatedTxt, GetLastErrorText(), 'error text');
        LibraryAssert.AreEqual('TestWrapped:DotNetControlAddInNotCreated', GetLastErrorCode(), 'error code');
        Card.Close();
    end;
}
