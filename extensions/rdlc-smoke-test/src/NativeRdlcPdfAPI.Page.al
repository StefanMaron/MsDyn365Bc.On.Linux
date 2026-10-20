namespace BCLinux.Rendering.Tests;

using System.Text;
using System.Utilities;

page 70102 "BC Linux RDLC PDF API"
{
    PageType = API;
    APIPublisher = 'bclinux';
    APIGroup = 'testing';
    APIVersion = 'v1.0';
    EntityName = 'rdlcPdf';
    EntitySetName = 'rdlcPdfs';
    SourceTable = "Integer";
    SourceTableView = where(Number = const(1));
    ODataKeyFields = Number;
    Editable = false;
    InsertAllowed = false;
    ModifyAllowed = false;
    DeleteAllowed = false;

    layout
    {
        area(Content)
        {
            repeater(Pdf)
            {
                field(id; Rec.Number)
                {
                }
                field(pdfBase64; PdfBase64)
                {
                }
                field(wordBase64; WordBase64)
                {
                }
                field(excelBase64; ExcelBase64)
                {
                }
            }
        }
    }

    trigger OnAfterGetRecord()
    begin
        PdfBase64 := Render(ReportFormat::Pdf);
        WordBase64 := Render(ReportFormat::Word);
        ExcelBase64 := Render(ReportFormat::Excel);
    end;

    local procedure Render(Format: ReportFormat): Text
    var
        Probe: Codeunit "BC Linux RDLC Probe";
        TempBlob: Codeunit "Temp Blob";
        Base64Convert: Codeunit "Base64 Convert";
        Input: InStream;
    begin
        Probe.CreateDocument(TempBlob, Format);
        TempBlob.CreateInStream(Input);
        exit(Base64Convert.ToBase64(Input));
    end;

    var
        PdfBase64: Text;
        WordBase64: Text;
        ExcelBase64: Text;
}
