// Regression guard for issue #78 — see BcLinuxOnrcTests.Codeunit.al.
table 70004 "BC Linux ONRC Line"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Header No."; Code[20]) { }
        field(2; "Line No."; Integer) { }
        field(3; Descr; Text[50]) { }
    }

    keys
    {
        key(PK; "Header No.", "Line No.") { Clustered = true; }
    }
}
