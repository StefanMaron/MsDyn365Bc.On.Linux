report 70070 "BC Linux Format Region"
{
    UsageCategory = None;
    UseRequestPage = false;
    DefaultRenderingLayout = Rdlc;

    dataset
    {
        dataitem(Row; "Integer")
        {
            DataItemTableView = where(Number = const(1));

            column(D1Col; DMY2Date(31, 1, 2025)) { }
            column(T1Col; 143015T) { }
            column(NegCol; -1234.5) { }
            column(LongCol; LongDate) { }

            trigger OnAfterGetRecord()
            begin
                LongDate := Format(DMY2Date(5, 3, 2024), 0, 4);
            end;
        }
    }

    rendering
    {
        layout(Rdlc)
        {
            Type = RDLC;
            LayoutFile = 'layout/FormatRegion.rdlc';
        }
    }

    trigger OnPreReport()
    var
        FormatLog: Codeunit "BC Linux Format Region Log";
    begin
        CurrReport.FormatRegion(FormatLog.Region());
        FormatLog.Record(Format(-1234.5) + '#' + Format(1234567.891));
    end;

    var
        LongDate: Text;
}
