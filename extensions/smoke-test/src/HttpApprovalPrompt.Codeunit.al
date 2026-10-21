// Regression guard for issue #165: the HttpClient approval prompt.
//
// A Sandbox tenant asks "Allow Always,Allow Once,Block Always,Block Once" before an app's
// first HttpClient request when the app has no setting row. A Windows container publishes
// with the setting on, so a request to a host that never resolves is sent and fails with the
// socket error. Without Patch #43 the hub raised "Unhandled UI: StrMenu ..." or, inside a
// try scope, answered Get with false and a default response (status 200).
codeunit 70073 "BC Linux Http Approval"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";
        SocketErrorTxt: Label 'There was an error while executing the HTTP request. Make sure you are connecting to a valid endpoint.', Locked = true;

    [Test]
    procedure GetToUnresolvableHostFailsWithSocketError()
    var
        Client: HttpClient;
        Response: HttpResponseMessage;
        Ok: Boolean;
    begin
        ClearLastError();
        Ok := Client.Get('https://bc-linux.invalid/get', Response);
        LibraryAssert.IsFalse(Ok, 'Get result');
        LibraryAssert.AreEqual(0, Response.HttpStatusCode(), 'status code');
        LibraryAssert.AreEqual(SocketErrorTxt, GetLastErrorText(), 'error text');
        LibraryAssert.AreEqual('HttpRequest:Socket', GetLastErrorCode(), 'error code');
    end;

    [Test]
    procedure GetAsStatementRaisesSocketError()
    var
        Client: HttpClient;
        Response: HttpResponseMessage;
    begin
        asserterror Client.Get('https://bc-linux.invalid/statement', Response);
        LibraryAssert.AreEqual(SocketErrorTxt, GetLastErrorText(), 'error text');
        LibraryAssert.AreEqual('HttpRequest:Socket', GetLastErrorCode(), 'error code');
    end;
}
