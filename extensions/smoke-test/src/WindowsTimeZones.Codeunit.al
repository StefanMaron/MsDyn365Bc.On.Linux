// Regression guard for issue #132: the virtual table "Time Zone" (2000000164).
//
// BC fills the table from TimeZoneInfo.GetSystemTimeZones(). On Windows that is the 144
// Windows time zones, with Windows ids and display names; on Linux it was 419 IANA zones, so
// AL code that looks up 'W. Europe Standard Time' found nothing. `PatchNclTestPage culture`
// hands the table the Windows list instead (BcLinuxTimeZones.cs).
//
// Every expected value is what an official Windows container reported (BC 27.5 and 29.0).
codeunit 70041 "BC Linux Time Zone Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure TableHoldsTheWindowsTimeZones()
    var
        TimeZone: Record "Time Zone";
    begin
        LibraryAssert.AreEqual(144, TimeZone.Count(), 'number of time zones');
    end;

    [Test]
    procedure WindowsIdsAreNumberedAsOnWindows()
    begin
        CheckZone(1, 'Dateline Standard Time', '(UTC-12:00) International Date Line West');
        CheckZone(2, 'UTC-11', '');
        CheckZone(3, 'Aleutian Standard Time', '');
        CheckZone(50, 'UTC', '(UTC) Coordinated Universal Time');
        CheckZone(55, 'W. Europe Standard Time', '(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna');
        CheckZone(144, 'Line Islands Standard Time', '');
    end;

    [Test]
    procedure IanaIdsAreNotListed()
    var
        TimeZone: Record "Time Zone";
    begin
        TimeZone.SetRange(ID, 'Europe/Berlin');
        LibraryAssert.RecordIsEmpty(TimeZone);
    end;

    local procedure CheckZone(No: Integer; Id: Text; DisplayName: Text)
    var
        TimeZone: Record "Time Zone";
    begin
        TimeZone.SetRange(ID, Id);
        LibraryAssert.IsTrue(TimeZone.FindFirst(), 'time zone ' + Id);
        LibraryAssert.AreEqual(No, TimeZone."No.", 'No. of ' + Id);
        if DisplayName <> '' then
            LibraryAssert.AreEqual(DisplayName, TimeZone."Display Name", 'Display Name of ' + Id);
    end;
}
