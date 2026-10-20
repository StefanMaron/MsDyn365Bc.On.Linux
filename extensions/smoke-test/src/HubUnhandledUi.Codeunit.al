// Regression guard for issue #139: unhandled UI on the hub.
//
// A Windows test session refuses a Confirm, Message, StrMenu or page open that no handler
// answers, with "Unhandled UI: <kind> <text>" and the error code MissingUIHandler. The
// codeunit in PartConfirm.Codeunit.al checks this with the error text in the test body,
// which makes scripts/classify-handler-codeunits.py route it to the websocket runner. This
// codeunit checks the same results without the text in any test body, so under
// test_runner=auto it runs on the hub on BC 28 and later. The expected texts are labels
// at codeunit level for that reason.
codeunit 70027 "BC Linux Hub Unhandled UI"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";
        PartConfirmTxt: Label 'Unhandled UI: Confirm bc-linux part asks', Locked = true;
        ConfirmTxt: Label 'Unhandled UI: Confirm bc-linux asks', Locked = true;
        MessageTxt: Label 'Unhandled UI: Message bc-linux says', Locked = true;
        StrMenuTxt: Label 'Unhandled UI: StrMenu One,Two', Locked = true;

    [Test]
    procedure PartConfirmFailsOpenViewOnEveryRunner()
    var
        Host: TestPage "BC Linux Confirm Host";
    begin
        asserterror Host.OpenView();
        LibraryAssert.AreEqual(PartConfirmTxt, GetLastErrorText(), 'error text');
        LibraryAssert.AreEqual('MissingUIHandler', GetLastErrorCode(), 'error code');
    end;

    [Test]
    procedure ConfirmFailsOnEveryRunner()
    var
        Answer: Boolean;
    begin
        asserterror Answer := Confirm('bc-linux asks');
        LibraryAssert.AreEqual(ConfirmTxt, GetLastErrorText(), 'error text');
        LibraryAssert.AreEqual('MissingUIHandler', GetLastErrorCode(), 'error code');
    end;

    [Test]
    procedure MessageFailsOnEveryRunner()
    begin
        asserterror Message('bc-linux says');
        LibraryAssert.AreEqual(MessageTxt, GetLastErrorText(), 'error text');
        LibraryAssert.AreEqual('MissingUIHandler', GetLastErrorCode(), 'error code');
    end;

    [Test]
    procedure StrMenuFailsOnEveryRunner()
    var
        Choice: Integer;
    begin
        asserterror Choice := StrMenu('One,Two');
        LibraryAssert.AreEqual(StrMenuTxt, GetLastErrorText(), 'error text');
        LibraryAssert.AreEqual('MissingUIHandler', GetLastErrorCode(), 'error code');
    end;
}
