namespace BCLinux.Rendering.Tests;

using System.Utilities;

report 70100 "BC Linux Native RDLC"
{
    DefaultRenderingLayout = NativeInvoice;
    UseRequestPage = false;

    dataset
    {
        dataitem(Line; "Integer")
        {
            DataItemTableView = sorting(Number);

            column(Description; StrSubstNo('Invoice line %1', Format(Number, 0, '<Integer,3><Filler Character,0>')))
            {
            }
            column(Quantity; Number)
            {
            }
            column(UnitPrice; 1.25)
            {
            }

            trigger OnPreDataItem()
            begin
                SetRange(Number, 1, 120);
            end;
        }
    }

    rendering
    {
        layout(NativeInvoice)
        {
            Type = RDLC;
            LayoutFile = 'layout/NativeRdlc.rdlc';
            Caption = 'Native RDLC invoice';
        }
    }
}
