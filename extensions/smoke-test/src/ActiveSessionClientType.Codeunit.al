// Regression guard for issue #86.
//
// The Active Session row for the test's own session must name a real client
// type, and it must agree with CurrentClientType() read in the same session.
//
// On BC 27.x, where tests run through the websocket runner, the row read
// "Unknown" while CurrentClientType() answered Windows. The runner never told
// the server what kind of client it is: the OpenConnection request carries a
// ClientConnectionType, and tools/TestRunner/Program.cs left it out, so the
// session defaulted to ConnectionType.UnknownClient (0). The row maps that to
// Unknown, and CurrentClientType() falls through to the server's DefaultClient
// setting, which is Windows. A real client-services session (BcContainerHelper
// on Windows) sends ClientService, which gives "Client Service" and Web.
//
// This test must pass on both runners. The altool hub runs tests in a Web
// Client session ("Web Client" and Web), the websocket runner in a client
// services session ("Client Service" and Web). It asserts only what both agree
// on, which is the same correspondence the corpus test uses.
codeunit 70008 "BC Linux Active Session Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure ActiveSessionClientTypeIsKnownAndAgreesWithCurrentClientType()
    var
        ActiveSession: Record "Active Session";
    begin
        ActiveSession.SetRange("Session ID", SessionId());
        LibraryAssert.IsTrue(ActiveSession.FindFirst(), 'The test session has no Active Session row.');
        LibraryAssert.IsTrue(ActiveSession."Client Type" <> ActiveSession."Client Type"::Unknown,
            'Active Session."Client Type" is Unknown.');
        LibraryAssert.IsTrue(CurrentClientType() = ClientType::Web,
            'CurrentClientType() is not Web in a client-services test session.');
        LibraryAssert.IsTrue(ActiveSession."Client Type" in [ActiveSession."Client Type"::"Client Service", ActiveSession."Client Type"::"Web Client"],
            'Active Session."Client Type" does not describe a web client.');
    end;
}
