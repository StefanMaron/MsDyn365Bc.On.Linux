// Regression guard for issue #140: the object an error names when a test method opens a
// table that is not in the runtime app.
//
// A table declared with ObsoleteState = Moved is not part of the runtime app, so
// RecordRef.Open fails with "You tried to invoke the Table object with the ID n from the
// object X". X is the caller of the test method. On a Windows container and on the
// websocket runner that is the test runner codeunit "Test Runner - Mgt.". The hub calls the
// test method with no AL caller, and BC names the test codeunit itself.
// scripts/classify-handler-codeunits.py routes a test that names a test runner codeunit to
// the websocket runner, so under test_runner=auto this codeunit runs there on every BC
// version.
table 70024 "BC Linux Moved Table"
{
    DataClassification = SystemMetadata;
    ObsoleteState = Moved;
    ObsoleteReason = 'Regression guard for bc-linux issue #140';
    ObsoleteTag = '1.0';
    MovedTo = '11111111-2222-3333-4444-555555555555';

    fields
    {
        field(1; Id; Integer) { }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

codeunit 70023 "BC Linux Moved Table Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure OpenOfAMovedTableNamesTheTestRunner()
    var
        RR: RecordRef;
    begin
        asserterror RR.Open(70024);
        LibraryAssert.ExpectedError('You tried to invoke the Table object with the ID 70024 from the object Test Runner - Mgt.');
        LibraryAssert.AreEqual('AL:MetadataNotFound', GetLastErrorCode(), 'error code');
    end;
}
