// Issue #167: a report whose FormatRegion property names an unknown region. Windows accepts the
// property and formats the dataset with its fallback; the method CurrReport.FormatRegion() is a
// different path and is refused there as here.
report 70074 "BC Linux Format Region Zz"
{
    UsageCategory = None;
    UseRequestPage = false;
    DefaultRenderingLayout = Rdlc;
    FormatRegion = 'zz-ZZ';

    dataset
    {
        dataitem(Row; "Integer")
        {
            DataItemTableView = where(Number = const(1));

            column(D1Col; DMY2Date(31, 1, 2025)) { }
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
}
