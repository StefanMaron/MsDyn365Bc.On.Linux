// Regression guard for issue #78 — see BcLinuxOnrcTests.Codeunit.al.
// Header table for a linked card+part shape (one part, linked on the line
// table's first primary-key field), the same shape as every document card
// in the Base Application.
table 70003 "BC Linux ONRC Header"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
