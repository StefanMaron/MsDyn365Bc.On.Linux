// Regression guard for issue #78 — see BcLinuxOnrcTests.Codeunit.al.
// An ordinary editable, insert-allowed repeater, so it shows the implicit
// blank draft line past its data the way every Base Application line grid
// does.
page 70006 "BC Linux ONRC Lines"
{
    PageType = ListPart;
    SourceTable = "BC Linux ONRC Line";
    ApplicationArea = All;
    AutoSplitKey = true;

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(Descr; Rec.Descr) { ApplicationArea = All; }
            }
        }
    }

    // One log row per firing — see OnrcLog.Table.al for why a row instead of
    // a field assignment. The insert goes to a table this page does not own,
    // so it survives NewRecord's own re-init of the buffer this trigger runs
    // against, and survives a draft line nobody ever types into.
    trigger OnNewRecord(BelowxRec: Boolean)
    var
        Log: Record "BC Linux ONRC Log";
    begin
        Log.Init();
        Log.Insert(true);
    end;
}
