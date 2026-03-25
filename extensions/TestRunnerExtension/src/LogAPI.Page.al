/// <summary>
/// API Page for Log Table - Exposes test execution logs via OData
/// </summary>
page 50003 "Log Entries API"
{
    PageType = API;
    APIPublisher = 'custom';
    APIGroup = 'automation';
    APIVersion = 'v1.0';
    EntityName = 'logEntry';
    EntitySetName = 'logEntries';
    SourceTable = "Log Table";
    DelayedInsert = true;
    ODataKeyFields = "Entry No.";

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field(entryNo; Rec."Entry No.")
                {
                    ApplicationArea = All;
                    Caption = 'Entry No.';
                }
                field(message; Rec."Message")
                {
                    ApplicationArea = All;
                    Caption = 'Message';
                }
                field(computerName; Rec."Computer Name")
                {
                    ApplicationArea = All;
                    Caption = 'Computer Name';
                }
                field(codeunitId; Rec."Codeunit ID")
                {
                    ApplicationArea = All;
                    Caption = 'Codeunit ID';
                }
                field(codeunitName; Rec."Codeunit Name")
                {
                    ApplicationArea = All;
                    Caption = 'Codeunit Name';
                }
                field(functionName; Rec."Function Name")
                {
                    ApplicationArea = All;
                    Caption = 'Function Name';
                }
                field(success; Rec."Success")
                {
                    ApplicationArea = All;
                    Caption = 'Success';
                }
                field(errorMessage; Rec."Error Message")
                {
                    ApplicationArea = All;
                    Caption = 'Error Message';
                }
                field(callStack; Rec."Call Stack")
                {
                    ApplicationArea = All;
                    Caption = 'Call Stack';
                }
            }
        }
    }
}
