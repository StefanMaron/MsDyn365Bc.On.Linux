// Regression guard for issue #153: a '?' in an in-memory filter.
//
// BC 27 matches the parts of a filter around a '?' through kernel32 FindNLSString, and the
// Linux stub of that function answered "not found" for every input (StartupHook Patch #41).
// So on BC 27 no temporary-record filter with a '?' matched anything, and every date typed
// into a page was refused: BC checks it, formatted as ddMMyy, against the license's date
// filter, which is "??11*|??12*|??01*|??02*" in the BC 27.5 Cronus license. A Windows
// BC 27.5 container stores both dates below. BC 28 and later never had the problem.
codeunit 70064 "BC Linux License Date Filter"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure QuestionMarkFilterMatchesInMemory()
    var
        TempEntry: Record "BC Linux Date Entry" temporary;
    begin
        TempEntry."Code" := '301225';
        TempEntry.Insert();
        TempEntry.SetFilter("Code", '??12*');
        if TempEntry.IsEmpty() then
            Error('filter ??12* did not match 301225');
        TempEntry.SetFilter("Code", '30122?');
        if TempEntry.IsEmpty() then
            Error('filter 30122? did not match 301225');
        TempEntry.SetFilter("Code", '??11*');
        if not TempEntry.IsEmpty() then
            Error('filter ??11* matched 301225');
    end;

    [Test]
    procedure DecemberDateIsAccepted()
    begin
        SetDate('12/30/2025', DMY2Date(30, 12, 2025));
    end;

    [Test]
    procedure DecemberClosingDateIsAccepted()
    begin
        SetDate('C12/31/2025', ClosingDate(DMY2Date(31, 12, 2025)));
    end;

    local procedure SetDate(Entry: Text; Expected: Date)
    var
        DateEntry: Record "BC Linux Date Entry";
        Card: TestPage "BC Linux Date Entry Card";
    begin
        if not DateEntry.Get('D1') then begin
            DateEntry."Code" := 'D1';
            DateEntry.Insert();
        end;
        Card.OpenEdit();
        Card.GoToRecord(DateEntry);
        Card.EntryDateCtl.SetValue(Entry);
        Card.Close();
        DateEntry.Get('D1');
        if DateEntry.EntryDate <> Expected then
            Error('stored %1, expected %2', DateEntry.EntryDate, Expected);
    end;
}
