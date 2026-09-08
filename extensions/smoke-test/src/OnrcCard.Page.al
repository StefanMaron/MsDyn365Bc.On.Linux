// Regression guard for issue #78 — see BcLinuxOnrcTests.Codeunit.al.
page 70007 "BC Linux ONRC Card"
{
    PageType = Card;
    SourceTable = "BC Linux ONRC Header";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            part(Lines; "BC Linux ONRC Lines")
            {
                ApplicationArea = All;
                SubPageLink = "Header No." = field("No.");
            }
        }
    }
}
