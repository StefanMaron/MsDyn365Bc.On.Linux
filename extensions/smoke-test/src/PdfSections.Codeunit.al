// Regression guard for issue #155: a Word layout with section breaks, saved as PDF.
//
// BC renders each WordMergeDataItem record to its own PDF and merges them with Aspose.PDF,
// which uses System.Drawing.Drawing2D.Matrix while it makes the result compliant. DrawingStub
// had no Matrix, so the compliant merge failed with a TypeLoadException, BC retried without
// compliance, and SaveAs returned true with "Unable to merge pdf streams due to internal
// error. Retry without compliance" (InvalidOperation:TypeLoad) as the last error. A Windows
// container leaves no last error.
//
// The check looks for the merge error only. In the version matrix the selective app filter
// leaves out the Base Application, and SaveAs then also leaves "The metadata object Page 1560
// was not found" as the last error, which has nothing to do with the merge.
codeunit 70069 "BC Linux Pdf Sections"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SectionBreaksMergeToPdfWithoutLastError()
    var
        Entry: Record "BC Linux Date Entry";
        TempBlob: Codeunit "Temp Blob";
        OutS: OutStream;
        InS: InStream;
        Head: Text;
    begin
        Entry."Code" := 'R1';
        if Entry.Insert() then;
        Entry."Code" := 'R2';
        if Entry.Insert() then;
        ClearLastError();
        TempBlob.CreateOutStream(OutS);
        if not Report.SaveAs(Report::"BC Linux Pdf Sections", '', ReportFormat::Pdf, OutS) then
            Error('SaveAs returned false');
        if GetLastErrorText().Contains('merge pdf') or GetLastErrorCode().Contains('TypeLoad') then
            Error('last error: %1 | %2', GetLastErrorText(), GetLastErrorCode());
        TempBlob.CreateInStream(InS);
        InS.ReadText(Head, 4);
        if Head <> '%PDF' then
            Error('output does not start with %PDF: %1', Head);
    end;
}
