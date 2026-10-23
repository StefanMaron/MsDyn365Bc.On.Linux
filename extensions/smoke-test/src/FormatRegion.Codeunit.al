// Regression guard for issue #157: a report's FormatRegion formats with Windows (NLS) culture
// data. ICU gives da-DK a '.' time separator (Windows ':'), es-ES a one-digit day and month in
// the short date (Windows dd/MM/yyyy), nl-NL a two-digit one (Windows d-M-yyyy), en-GB a
// one-digit day in the long date, sv-SE no 'den ' in the long date, and sv-SE and nb-NO a
// U+2212 minus sign. The expected values are a Windows BC 28.5 container's (the corpus's
// codeunit 81070). Dataset columns are read from Report.SaveAs Xml, as the corpus does: AL
// Format() of a Date builds its own pattern and does not show the short date difference.
codeunit 70071 "BC Linux Format Region"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure DanishTimeUsesColon()
    begin
        LibraryAssert.AreEqual('14:30:15', ColumnValue(Dataset('da-DK'), 'T1Col'), 'da-DK time');
    end;

    [Test]
    procedure SpanishShortDateHasTwoDigits()
    begin
        LibraryAssert.AreEqual('31/01/2025', ColumnValue(Dataset('es-ES'), 'D1Col'), 'es-ES short date');
    end;

    [Test]
    procedure BritishLongDateHasTwoDigitDay()
    begin
        LibraryAssert.AreEqual('05 March 2024', ColumnValue(Dataset('en-GB'), 'LongCol'), 'en-GB long date');
    end;

    [Test]
    procedure DutchShortDateHasOneDigitDayAndMonth()
    begin
        LibraryAssert.AreEqual('31-1-2025', ColumnValue(Dataset('nl-NL'), 'D1Col'), 'nl-NL short date');
    end;

    [Test]
    procedure SwedishAndNorwegianMinusIsAsciiHyphen()
    begin
        LibraryAssert.AreEqual('-', CopyStr(Part(Run('sv-SE'), 1), 1, 1), 'sv-SE minus sign');
        LibraryAssert.AreEqual('-', CopyStr(Part(Run('nb-NO'), 1), 1, 1), 'nb-NO minus sign');
    end;

    [Test]
    procedure SwedishLongDateHasDen()
    begin
        LibraryAssert.AreEqual('den 5 March 2024', ColumnValue(Dataset('sv-SE'), 'LongCol'), 'sv-SE long date');
    end;

    // Issue #167: an unknown region falls back to en-US with a zero-padded date, as on Windows.
    [Test]
    procedure UnknownRegionFallsBackToPaddedDate()
    begin
        LibraryAssert.AreEqual('01/31/2025', ColumnValue(UnknownRegionDataset(), 'D1Col'), 'zz-ZZ short date');
    end;

    // Issues #167 and #174: a Windows container groups fr-FR thousands with U+00A0 on BC 27.5,
    // 28.5 and 29.0 (CLDR: U+202F).
    [Test]
    procedure FrenchGroupSeparatorIsNoBreakSpace()
    var
        Formatted: Text;
    begin
        Formatted := Part(Run('fr-FR'), 2);
        LibraryAssert.IsTrue(Formatted[2] = 160, 'fr-FR group separator is U+00A0, got ' + Formatted);
    end;

    local procedure UnknownRegionDataset(): Text
    var
        TempBlob: Codeunit "Temp Blob";
        OutS: OutStream;
        InS: InStream;
        Xml: Text;
    begin
        TempBlob.CreateOutStream(OutS);
        Report.SaveAs(Report::"BC Linux Format Region Zz", '', ReportFormat::Xml, OutS);
        TempBlob.CreateInStream(InS, TextEncoding::UTF8);
        InS.Read(Xml);
        exit(Xml);
    end;

    local procedure Run(Region: Text): Text
    begin
        exit(Dataset(Region).Split('|').Get(1));
    end;

    // Returns "<Format() log>|<dataset XML>".
    local procedure Dataset(Region: Text): Text
    var
        FormatLog: Codeunit "BC Linux Format Region Log";
        TempBlob: Codeunit "Temp Blob";
        OutS: OutStream;
        InS: InStream;
        Xml: Text;
    begin
        FormatLog.SetRegion(Region);
        TempBlob.CreateOutStream(OutS);
        Report.SaveAs(Report::"BC Linux Format Region", '', ReportFormat::Xml, OutS);
        TempBlob.CreateInStream(InS, TextEncoding::UTF8);
        InS.Read(Xml);
        exit(FormatLog.Result() + '|' + Xml);
    end;

    local procedure ColumnValue(Data: Text; Column: Text): Text
    var
        Start: Integer;
        Rest: Text;
    begin
        Start := StrPos(Data, '<Column name="' + Column + '"');
        if Start = 0 then
            Error('column %1 not in the dataset', Column);
        Rest := CopyStr(Data, Start);
        Rest := CopyStr(Rest, StrPos(Rest, '>') + 1);
        exit(CopyStr(Rest, 1, StrPos(Rest, '<') - 1));
    end;

    local procedure Part(Value: Text; Index: Integer): Text
    begin
        exit(Value.Split('#').Get(Index));
    end;
}
