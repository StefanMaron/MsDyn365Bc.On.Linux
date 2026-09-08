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
            }
        }
    }

    trigger OnAfterGetRecord()
    var
        Probe: Codeunit "BC Linux RDLC Probe";
        TempBlob: Codeunit "Temp Blob";
        Base64Convert: Codeunit "Base64 Convert";
        PdfInput: InStream;
    begin
        Probe.CreatePdf(TempBlob);
        TempBlob.CreateInStream(PdfInput);
        PdfBase64 := Base64Convert.ToBase64(PdfInput);
    end;

    var
        PdfBase64: Text;
}
