// Regression guard for issue #134: Format(Date, 0, 7) abbreviated month names.
//
// Format 7 is the region's long date with MMMM shortened to MMM, and .NET takes the MMM
// names from the culture of the session language. ICU (Linux) and NLS (Windows) disagree on
// them for German and Spanish: ICU says "Jan.", "März", "Sept." and "ene", "sept"; Windows
// says "Jan", "Mrz", "Sep" and "ene.", "sep.". `PatchNclTestPage culture` sets the Windows
// names for de-DE and es-ES (BcLinuxCulture.WindowsAbbreviatedMonths).
//
// Every expected value is what an official Windows container reported (BC 27.5 and 29.0).
// English and French were already the same on both and are checked so that the patch does
// not move them.
codeunit 70040 "BC Linux Month Names Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure GermanAbbreviatedMonthsAreWindowsNames()
    begin
        CheckMonths(1031, 'Jan,Feb,Mrz,Apr,Mai,Jun,Jul,Aug,Sep,Okt,Nov,Dez');
    end;

    [Test]
    procedure SpanishAbbreviatedMonthsAreWindowsNames()
    begin
        CheckMonths(3082, 'ene.,feb.,mar.,abr.,may.,jun.,jul.,ago.,sep.,oct.,nov.,dic.');
    end;

    [Test]
    procedure EnglishAbbreviatedMonthsAreUnchanged()
    begin
        CheckMonths(1033, 'Jan,Feb,Mar,Apr,May,Jun,Jul,Aug,Sep,Oct,Nov,Dec');
    end;

    [Test]
    procedure FrenchAbbreviatedMonthsAreUnchanged()
    begin
        CheckMonths(1036, 'janv.,févr.,mars,avr.,mai,juin,juil.,août,sept.,oct.,nov.,déc.');
    end;

    local procedure CheckMonths(Language: Integer; Expected: Text)
    var
        Names: List of [Text];
        Actual: Text;
        Month: Integer;
        PreviousLanguage: Integer;
    begin
        Names := Expected.Split(',');
        PreviousLanguage := GlobalLanguage();
        GlobalLanguage(Language);
        for Month := 1 to 12 do begin
            Actual := MonthPart(Format(DMY2Date(1, Month, 2024), 0, 7));
            if Actual <> Names.Get(Month) then begin
                GlobalLanguage(PreviousLanguage);
                LibraryAssert.AreEqual(Names.Get(Month), Actual,
                    StrSubstNo('language %1, month %2: %3', Language, Month, Format(DMY2Date(1, Month, 2024), 0, 7)));
            end;
        end;
        GlobalLanguage(PreviousLanguage);
    end;

    // Format 7 is "<month> <day>, <year>" under the en-US region. The region decides the
    // order, so take the one part that is not a number instead of a fixed position.
    local procedure MonthPart(Formatted: Text): Text
    var
        Part: Text;
        Number: Integer;
    begin
        foreach Part in Formatted.Split(' ') do begin
            Part := Part.TrimEnd(',');
            if (Part <> '') and not Evaluate(Number, Part) then
                exit(Part);
        end;
        exit(Formatted);
    end;
}
