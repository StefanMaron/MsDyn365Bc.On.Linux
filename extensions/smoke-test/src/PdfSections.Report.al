report 70068 "BC Linux Pdf Sections"
{
    UsageCategory = None;
    UseRequestPage = false;
    DefaultRenderingLayout = SectionsMerge;
    WordMergeDataItem = Row;

    dataset
    {
        dataitem(Row; "BC Linux Date Entry")
        {
            column(RowId; "Code") { }
        }
    }

    rendering
    {
        layout(SectionsMerge)
        {
            Type = Word;
            LayoutFile = 'layout/PdfSections.docx';
        }
    }
}
