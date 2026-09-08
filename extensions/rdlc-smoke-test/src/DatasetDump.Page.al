namespace BCLinux.Rendering.Tests;

using System.Text;
using System.Utilities;
using Microsoft.Sales.Document;

/// <summary>
/// Diagnostic only. Emits a report's DATASET as XML, which the NST produces
/// itself — the reporting service is not involved. Used to answer whether a
/// column the RDLC layout wants is missing from the data BC generates, or is
/// only missing by the time it reaches the renderer.
/// </summary>
page 70104 "BC Linux RDLC Dataset API"
{
    PageType = API;
    APIPublisher = 'bclinux';
    APIGroup = 'testing';
    APIVersion = 'v1.0';
    EntityName = 'rdlcDataset';
    EntitySetName = 'rdlcDatasets';
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
            repeater(Data)
            {
                field(id; Rec.Number) { }
                field(datasetBase64; DatasetBase64) { }
            }
        }
    }

    trigger OnAfterGetRecord()
    var
        SalesHeader: Record "Sales Header";
        SalesRef: RecordRef;
        TempBlob: Codeunit "Temp Blob";
        Base64Convert: Codeunit "Base64 Convert";
        DataOut: OutStream;
        DataIn: InStream;
    begin
        SalesHeader.SetRange("Document Type", SalesHeader."Document Type"::Order);
        SalesHeader.SetRange("No.", '101001');
        SalesRef.GetTable(SalesHeader);
        Clear(TempBlob);
        TempBlob.CreateOutStream(DataOut);
        if not Report.SaveAs(1305, '', ReportFormat::Xml, DataOut, SalesRef) then
            Error('Dataset dump failed: %1', GetLastErrorText());
        TempBlob.CreateInStream(DataIn);
        DatasetBase64 := Base64Convert.ToBase64(DataIn);
    end;

    var
        DatasetBase64: Text;
}
