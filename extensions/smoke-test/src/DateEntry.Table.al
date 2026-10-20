table 70062 "BC Linux Date Entry"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Code"; Code[10]) { }
        field(2; EntryDate; Date) { ClosingDates = true; }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
