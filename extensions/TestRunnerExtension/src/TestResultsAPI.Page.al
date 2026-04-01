/// <summary>
/// API page exposing Test Method Line results for OData reading.
/// Used by the WebSocket test runner to read results after test execution.
/// </summary>
page 50006 "Test Results API"
{
    PageType = API;
    APIPublisher = 'custom';
    APIGroup = 'automation';
    APIVersion = 'v1.0';
    EntityName = 'testResult';
    EntitySetName = 'testResults';
    SourceTable = "Test Method Line";
    Editable = false;
    DelayedInsert = false;
    InsertAllowed = false;
    ModifyAllowed = false;
    DeleteAllowed = false;

    layout
    {
        area(Content)
        {
            repeater(Results)
            {
                field(testSuite; Rec."Test Suite") { }
                field(lineType; Rec."Line Type") { }
                field(testCodeunit; Rec."Test Codeunit") { }
                field(name; Rec.Name) { }
                field(functionName; Rec."Function") { }
                field(run; Rec.Run) { }
                field(result; Rec.Result) { }
                field(startTime; Rec."Start Time") { }
                field(finishTime; Rec."Finish Time") { }
                field(errorMessagePreview; Rec."Error Message Preview") { }
            }
        }
    }
}
