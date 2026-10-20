// Regression guard for issues #93 and #139: a Confirm in a part's OnOpenPage with no
// ConfirmHandler bound.
//
// On a Windows container the host TestPage's OpenView fails with the unhandled UI error of
// that Confirm. The refusal comes from the AL test runner codeunit, which the hub does not
// run, so on the hub the host opens and nothing is raised.
// scripts/classify-handler-codeunits.py routes a test that names the "Unhandled UI" text to
// the websocket runner, so under test_runner=auto this codeunit runs there on every BC
// version. If it fails on a BC 28+ leg, check which leg ran it first.
//
// The objects live in this file because nothing else uses them.
page 70021 "BC Linux Confirm Part"
{
    PageType = CardPart;
    SourceTable = "BC Linux Windows Text Blob";

    layout
    {
        area(Content)
        {
            field(PartId; Rec.Id) { ApplicationArea = All; }
        }
    }

    trigger OnOpenPage()
    begin
        if Confirm('bc-linux part asks') then;
    end;
}

page 70022 "BC Linux Confirm Host"
{
    PageType = Card;
    SourceTable = "BC Linux Windows Text Blob";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            field(HostId; Rec.Id) { ApplicationArea = All; }
            part(ConfirmPart; "BC Linux Confirm Part") { ApplicationArea = All; }
        }
    }
}

codeunit 70020 "BC Linux Part Confirm Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure UnhandledConfirmInAPartFailsOpenView()
    var
        Host: TestPage "BC Linux Confirm Host";
    begin
        asserterror Host.OpenView();
        LibraryAssert.AreEqual('Unhandled UI: Confirm bc-linux part asks', GetLastErrorText(), 'error text');
        LibraryAssert.AreEqual('MissingUIHandler', GetLastErrorCode(), 'error code');
    end;
}
