namespace BCLinux.Rendering.Tests;

using System.Utilities;

codeunit 70103 "BC Linux RDLC Probe"
{
    procedure CreatePdf(var TempBlob: Codeunit "Temp Blob")
    begin
        CreateDocument(TempBlob, ReportFormat::Pdf);
    end;

    procedure CreateDocument(var TempBlob: Codeunit "Temp Blob"; Format: ReportFormat)
    var
        Output: OutStream;
    begin
        Clear(TempBlob);
        TempBlob.CreateOutStream(Output);
        if not Report.SaveAs(Report::"BC Linux Native RDLC", '', Format, Output) then
            Error('RDLC rendering to %1 failed: %2', Format, GetLastErrorText());
    end;
}
