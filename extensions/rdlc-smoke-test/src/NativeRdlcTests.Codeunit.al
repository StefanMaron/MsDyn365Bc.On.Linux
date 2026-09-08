namespace BCLinux.Rendering.Tests;

using System.Utilities;
using System.TestLibraries.Utilities;

codeunit 70101 "BC Linux Native RDLC Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SaveAsPdfReturnsDocument()
    var
        Probe: Codeunit "BC Linux RDLC Probe";
        TempBlob: Codeunit "Temp Blob";
        LibraryAssert: Codeunit "Library Assert";
        PdfInput: InStream;
        Header: Text[5];
    begin
        Probe.CreatePdf(TempBlob);
        LibraryAssert.IsTrue(TempBlob.Length() > 10000, 'The invoice PDF is unexpectedly small.');
        TempBlob.CreateInStream(PdfInput);
        PdfInput.ReadText(Header, 5);
        LibraryAssert.AreEqual('%PDF-', Header, 'SaveAs did not produce a PDF.');
    end;
}
