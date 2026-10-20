page 70063 "BC Linux Date Entry Card"
{
    PageType = Card;
    SourceTable = "BC Linux Date Entry";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            group(General)
            {
                field(CodeCtl; Rec."Code") { ApplicationArea = All; }
                field(EntryDateCtl; Rec.EntryDate) { ApplicationArea = All; }
            }
        }
    }
}
