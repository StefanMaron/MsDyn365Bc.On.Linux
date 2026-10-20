// Holds a Media and a MediaSet for MediaBytes.Codeunit.al (issue #135).
table 70052 "BC Linux Media Probe"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; Id; Integer) { }
        field(2; Picture; Media) { }
        field(3; Pictures; MediaSet) { }
    }

    keys
    {
        key(PK; Id) { }
    }
}
