// Regression guard for issue #78 — see BcLinuxOnrcTests.Codeunit.al.
//
// A counting witness, not an assignment one. An assignment made from
// OnNewRecord (e.g. setting a default field value) is idempotent — it cannot
// tell one firing from six. Appending a row here can: the count is a plain
// Integer a test can assert an exact value against.
//
// It is a separate table from the line, because the record buffer a part
// page's OnNewRecord runs against is reset by the platform step that raises
// the trigger, and a draft line nobody types into is never saved at all. A
// row inserted into a second table survives both.
table 70005 "BC Linux ONRC Log"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Entry No."; Integer) { AutoIncrement = true; }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
