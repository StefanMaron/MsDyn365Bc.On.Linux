// Smoke test codeunit 2: trivial string + boolean checks.
// Used by test-versions.yml to verify the bc-linux substrate end-to-end
// across all supported BC versions.
//
// See SmokeTest1.Codeunit.al for why this fixture depends on Library Assert
// and deliberately NOT on Microsoft's Test Runner.
codeunit 70001 "BC Linux Smoke Test 2"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure TestStringConcatenation()
    var
        Result: Text;
    begin
        Result := 'Hello' + ', ' + 'World';
        LibraryAssert.AreEqual('Hello, World', Result, 'String concatenation failed');
    end;

    [Test]
    procedure TestBooleanLogic()
    begin
        LibraryAssert.IsTrue(true and not false, 'Boolean logic failed');
    end;

    // Regression guard for GitHub issue #52 / StartupHook Patch #31.
    // Platform (system/virtual) table captions come from CaptionML strings keyed
    // by Windows three-letter language names. On Linux (.NET on ICU) the "CHT"
    // abbreviation was unresolvable and fell back to the English LCID, so every
    // caption of All Profile / User / Company came back in Traditional Chinese
    // ("所有設定檔") inside otherwise-English error messages, no matter what the
    // session language was. Base Application tables were never affected, which
    // is why only platform tables are checked here. An ASCII check is used
    // instead of comparing against the exact English caption so a Microsoft
    // rename does not turn this into a false alarm. RecordRef is used because
    // this app.json (on purpose, see SmokeTest1) declares no platform/application
    // dependency, so the platform tables cannot be referenced by name.
    [Test]
    procedure TestPlatformTableCaptionsAreEnglish()
    begin
        GlobalLanguage(1033);
        AssertTableCaptionsAscii(2000000006, 'Company');
        AssertTableCaptionsAscii(2000000120, 'User');
        AssertTableCaptionsAscii(2000000178, 'All Profile');
    end;

    local procedure AssertTableCaptionsAscii(TableNo: Integer; Name: Text)
    var
        RecRef: RecordRef;
        i: Integer;
    begin
        RecRef.Open(TableNo);
        AssertAscii(RecRef.Caption(), Name + ' table caption');
        for i := 1 to RecRef.FieldCount() do
            AssertAscii(RecRef.FieldIndex(i).Caption(),
                StrSubstNo('%1 field %2 caption', Name, RecRef.FieldIndex(i).Number));
        RecRef.Close();
    end;

    local procedure AssertAscii(Value: Text; What: Text)
    var
        i: Integer;
    begin
        LibraryAssert.AreNotEqual('', Value, What + ' is empty');
        for i := 1 to StrLen(Value) do
            LibraryAssert.IsTrue(Value[i] < 128,
                StrSubstNo('%1 is not English in an en-US session (issue #52 / Patch #31): "%2"', What, Value));
    end;
}
