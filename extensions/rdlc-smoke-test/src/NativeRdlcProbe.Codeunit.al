namespace BCLinux.Rendering.Tests;

using System.Utilities;

codeunit 70103 "BC Linux RDLC Probe"
{
    procedure CreatePdf(var TempBlob: Codeunit "Temp Blob")
    var
        PdfOutput: OutStream;
    begin
        Clear(TempBlob);
        TempBlob.CreateOutStream(PdfOutput);
        if not Report.SaveAs(Report::"BC Linux Native RDLC", '', ReportFormat::Pdf, PdfOutput) then
            Error('RDLC rendering failed: %1', GetLastErrorText());
    end;
}
