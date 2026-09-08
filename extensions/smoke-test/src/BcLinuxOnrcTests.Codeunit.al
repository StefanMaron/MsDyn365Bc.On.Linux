// Regression guard for issue #78.
//
// Opening a card on an empty linked part, and landing on its blank draft
// line, used to raise the part page's OnNewRecord trigger 6 times on this
// tier — a real BC tier (Windows onprem and an online sandbox, both
// measured) raises it 3 times for the identical sequence. The cause was
// this image's own PatchTestPageClient forcing
// CommunicationBroker.DefaultChannelOptions.Async to false, which bypassed
// the queue BC otherwise uses to coalesce rapid-fire binding-manager
// notifications during a page/part fill. Fixed by leaving Async at its
// real (true) value by default — see scripts/entrypoint.sh's
// BC_TESTPAGE_ASYNC_PATCH gate and src/tools/PatchNclTestPage/PatchTestPageClient.cs.
//
// This pins the absolute count on bc-linux's own version matrix. The
// corpus's own fixture (StefanMaron/BusinessCentral.AL.Language.Tests,
// TestPagePartOnNewRecordCount.al) intentionally moved to delta assertions
// after this fix landed, since a shared fixture can't assume which tier a
// given consumer runs on. Delta assertions pass whether the absolute is 3
// or 6, so nothing downstream continues to check the number this issue is
// about. This test is where that check lives now.
//
// `TestPage` needs no app.json dependency beyond what SmokeTest1/2 already
// declare (Library Assert) — see SmokeTest1.Codeunit.al for why this app
// deliberately does not declare Microsoft's Test Runner. Verified by
// compiling against this app's own .alpackages with the real AL compiler.
//
// 3 is measured directly on BC 28.4 (locally, on a rebuilt image with the
// bc-service volume wiped) and on an online Windows sandbox — not yet on
// every version this matrix runs. That's what this PR's CI matrix (27.0
// through 28.5, plus the preview legs) establishes: if some version
// genuinely differs, this test goes red there and the constant needs to
// become version-conditional, rather than the version being assumed to
// match 28.4.
codeunit 70002 "BC Linux ONRC Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    local procedure Initialize()
    var
        Header: Record "BC Linux ONRC Header";
        Line: Record "BC Linux ONRC Line";
        Log: Record "BC Linux ONRC Log";
    begin
        Log.DeleteAll();
        Line.DeleteAll();
        Header.DeleteAll();

        Header.Init();
        Header."No." := 'H1';
        Header.Insert();
    end;

    local procedure OnNewRecordCount(): Integer
    var
        Log: Record "BC Linux ONRC Log";
    begin
        exit(Log.Count());
    end;

    [Test]
    procedure OpenCardOnEmptyLinkedPart_RaisesOnNewRecordExactlyThreeTimes()
    var
        Card: TestPage "BC Linux ONRC Card";
    begin
        Initialize();

        Card.OpenEdit();
        Card.GoToKey('H1');
        LibraryAssert.IsFalse(Card.Lines.First(),
            'the part has no lines, so First() must return false and land on the draft line');

        LibraryAssert.AreEqual(3, OnNewRecordCount(),
            'landing on the draft line of an empty linked part must raise OnNewRecord exactly ' +
            '3 times (issue #78) — 6 means this tier is forcing TestPageClient''s communication ' +
            'channels synchronous again (BC_TESTPAGE_ASYNC_PATCH), 0-2 means it stopped ' +
            'coalescing correctly in the other direction');

        Card.Close();
    end;

    [Test]
    procedure NewOnEmptyLinkedPart_CostsExactlyOneMoreThanOpeningIt()
    var
        Card: TestPage "BC Linux ONRC Card";
        AfterOpen: Integer;
    begin
        Initialize();

        Card.OpenEdit();
        Card.GoToKey('H1');
        LibraryAssert.IsFalse(Card.Lines.First(), 'the part has no lines');
        AfterOpen := OnNewRecordCount();

        Card.Lines.New();
        LibraryAssert.AreEqual(AfterOpen + 1, OnNewRecordCount(),
            'New() on an empty linked part must raise OnNewRecord exactly once more than ' +
            'opening the card on that empty part already did (issue #78)');

        Card.Close();
    end;
}
