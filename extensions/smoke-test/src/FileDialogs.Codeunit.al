// Regression guard for issue #122, items 2 and 4: what a test run's file dialogs answer.
//
// A Windows container runs tests through the web server's client-services session. Its
// file dialog provider hands a download to the caller as a link and refuses an upload.
// So DownloadFromStream returns true and leaves the file name as it was, and
// DataFileInformation (an upload of a .navdata file) fails with
// NotSupportedFileDialogProvider. Before the fix the websocket runner answered with a null
// result (a NullReferenceException in AL) and the hub said false to both, without an error.
//
// Both runners are covered: tools/TestRunner's FileActionDialog answers as that client
// does, and Patch #36 makes the hub's TestRunnerClientCallback do the same. The ExportData
// return value (issue #138) and the refused xmlport upload (issue #141) are hub answers
// that Patch #36 did not cover at first.
codeunit 70012 "BC Linux File Dialog Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure DownloadFromStreamReturnsTrueAndKeepsTheFileName()
    var
        Blob: Record "BC Linux Windows Text Blob" temporary;
        OutS: OutStream;
        InS: InStream;
        ToFile: Text;
    begin
        Blob.Data.CreateOutStream(OutS);
        OutS.WriteText('download content');
        Blob.Data.CreateInStream(InS);
        ToFile := 'bclinux-download.txt';
        LibraryAssert.IsTrue(DownloadFromStream(InS, 'Title', '', '', ToFile), 'DownloadFromStream');
        LibraryAssert.AreEqual('bclinux-download.txt', ToFile, 'ToFile');
    end;

    [Test]
    procedure DataFileInformationIsNotSupported()
    var
        Company: Record Company;
        FileName: Text;
        Description: Text;
        TenantId: Text;
        HasApplication: Boolean;
        HasApplicationData: Boolean;
        HasGlobalData: Boolean;
        ExportDate: DateTime;
    begin
        FileName := TemporaryPath() + 'bclinux-missing.navdata';
        asserterror Database.DataFileInformation(false, FileName, Description, HasApplication, HasApplicationData, HasGlobalData, TenantId, ExportDate, Company);
        LibraryAssert.ExpectedError('The operation cannot complete because it uses a feature that is not supported (AL File function.)');
        LibraryAssert.AreEqual('NotSupportedFileDialogProvider:NotSupportedFileDialogProvider', GetLastErrorCode(), 'error code');
    end;

    // Issue #125: ExportData writes its export on a background thread into a pipe that blocks
    // until the client reads the download. Accepting the download without reading it made
    // every ExportData wait for the pipe's 60-second timeout and end in an uncatchable
    // TimeoutException. The runner now reads the stream, as Microsoft's client does.
    [Test]
    procedure ExportDataDoesNotWaitForATimeout()
    var
        Started: DateTime;
    begin
        Started := CurrentDateTime();
        if TryExportGlobalData(TemporaryPath() + 'bclinux-export.navdata') then;
        LibraryAssert.IsTrue(CurrentDateTime() - Started < 30000, 'ExportData took 30 s or more');
    end;

    [TryFunction]
    local procedure TryExportGlobalData(FileName: Text)
    begin
        Database.ExportData(false, FileName, 'bc-linux smoke test', false, false, true);
    end;

    // Issue #138: on a Windows container ExportData returns true and leaves the file name
    // as it was (the export goes to the client as a download). The hub's client callback
    // said false; Patch #36 now takes the export the way the websocket runner does.
    [Test]
    procedure ExportDataReturnsTrueAndKeepsTheFileName()
    var
        FileName: Text;
        Result: Boolean;
    begin
        FileName := TemporaryPath() + 'bclinux-export-result.navdata';
        if not TryExportNothing(FileName, Result) then
            Error(GetLastErrorText());
        LibraryAssert.IsTrue(Result, 'ExportData return value');
        LibraryAssert.AreEqual(TemporaryPath() + 'bclinux-export-result.navdata', FileName, 'FileName');
    end;

    [TryFunction]
    local procedure TryExportNothing(var FileName: Text; var Result: Boolean)
    begin
        Result := Database.ExportData(false, FileName, 'bc-linux smoke test', false, false, false);
    end;

    // Issue #141: an import xmlport with no source asks the client for an upload. A
    // Windows container's test client refuses every upload; the websocket runner does too
    // (issue #122). The hub's client callback returned no stream, and the import ran
    // without an error. Patch #36 now refuses it on the hub as well.
    [Test]
    procedure XmlportImportWithoutSourceIsRefused()
    begin
        Commit();
        LibraryAssert.IsFalse(Codeunit.Run(Codeunit::"BC Linux Run Import Xmlport"), 'Codeunit.Run');
        LibraryAssert.AreEqual('The operation cannot complete because it uses a feature that is not supported (AL File function.)', GetLastErrorText(), 'error text');
        LibraryAssert.AreEqual('NotSupportedFileDialogProvider:NotSupportedFileDialogProvider', GetLastErrorCode(), 'error code');
    end;
}

xmlport 70025 "BC Linux Import Xmlport"
{
    Format = Xml;
    Direction = Import;
    UseRequestPage = false;

    schema
    {
        textelement(Root)
        {
            textelement(Entry) { MinOccurs = Zero; }
        }
    }
}

codeunit 70026 "BC Linux Run Import Xmlport"
{
    trigger OnRun()
    begin
        Xmlport.Run(Xmlport::"BC Linux Import Xmlport", false, true);
    end;
}
