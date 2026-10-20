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
    begin
        Probe.CreatePdf(TempBlob);
        LibraryAssert.IsTrue(TempBlob.Length() > 10000, 'The invoice PDF is unexpectedly small.');
        LibraryAssert.AreEqual('%PDF-', ReadHeader(TempBlob, 5), 'SaveAs did not produce a PDF.');
    end;

    // Word and Excel output are Open XML packages, so both start with the ZIP
    // signature "PK". Both failed under Mono with ReportingService:Rpc while
    // PDF worked (issue #144).
    [Test]
    procedure SaveAsWordReturnsDocument()
    begin
        AssertOpenXmlDocument(ReportFormat::Word);
    end;

    [Test]
    procedure SaveAsExcelReturnsDocument()
    begin
        AssertOpenXmlDocument(ReportFormat::Excel);
    end;

    local procedure AssertOpenXmlDocument(Format: ReportFormat)
    var
        Probe: Codeunit "BC Linux RDLC Probe";
        TempBlob: Codeunit "Temp Blob";
        LibraryAssert: Codeunit "Library Assert";
    begin
        Probe.CreateDocument(TempBlob, Format);
        LibraryAssert.IsTrue(TempBlob.Length() > 2000, StrSubstNo('The %1 document is unexpectedly small.', Format));
        LibraryAssert.AreEqual('PK', ReadHeader(TempBlob, 2), StrSubstNo('SaveAs did not produce a %1 package.', Format));
    end;

    local procedure ReadHeader(var TempBlob: Codeunit "Temp Blob"; Length: Integer) Header: Text
    var
        Input: InStream;
    begin
        TempBlob.CreateInStream(Input);
        Input.ReadText(Header, Length);
    end;
}
