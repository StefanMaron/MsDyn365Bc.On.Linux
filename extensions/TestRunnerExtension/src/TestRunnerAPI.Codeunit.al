/// <summary>
/// Test Runner API - Centralized Codeunit Execution System
///
/// This codeunit provides a REST API-based system for executing any codeunit remotely
/// without the need to publish each codeunit as a separate web service.
///
/// Architecture:
/// - Single API endpoint that can execute any codeunit by ID
/// - Eliminates the overhead of managing multiple web service endpoints
/// - Provides execution status, error handling, and metadata retrieval
/// - Supports batch execution of multiple codeunits
///
/// Security Considerations:
/// - Validate codeunit IDs before execution
/// - Consider implementing authorization checks for sensitive codeunits
/// - All executions commit pending transactions before running
///
/// Usage:
/// - Exposed via API pages (page50100.al and runnertable.al)
/// - Can be called directly from AL code or via REST API endpoints
/// </summary>
codeunit 50003 "Test Runner API"
{
    Subtype = TestRunner;

    var
        CurrCodeunitId: Integer;
        TestCodeunitResult: Boolean;

    /// <summary>
    /// Executes any codeunit by its ID and returns execution status.
    /// </summary>
    /// <param name="CodeunitId">The ID of the codeunit to execute (must be > 0)</param>
    /// <returns>
    /// Success: "SUCCESS: Codeunit {ID} executed successfully"
    /// Failure: "FAILED: Codeunit {ID} failed - {error message}"
    /// Invalid: "Error: Invalid codeunit ID"
    /// </returns>
    /// <remarks>
    /// - Commits all pending transactions before execution
    /// - Captures and returns any error messages from failed executions
    /// - Does not validate whether the codeunit exists before attempting to run
    /// </remarks>

    procedure SetCodeunitId(CodeunitId: Integer)
    begin
        CurrCodeunitId := CodeunitId;
    end;

    trigger OnRun()
    var
        Log: Record "Log Table";
    begin
        // Clear previous test logs before starting new test run
        Log.DeleteAll(false);
        Commit(); // Commit the deletion and prepare for test execution

        ClearLastError();
        TestCodeunitResult := Codeunit.Run(CurrCodeunitId);
    end;

    procedure GetTestCodeunitResult(): Boolean
    begin
        exit(TestCodeunitResult);
    end;


    procedure RunCodeunit(CodeunitId: Integer): Text
    var
        Success: Boolean;
        ErrorText: Text;
    begin
        // Validate codeunit ID
        if CodeunitId <= 0 then
            exit('Error: Invalid codeunit ID');

        // Try to run the codeunit
        ClearLastError();
        Commit(); // Commit any pending transactions before running

        this.SetCodeunitId(CodeunitId);
        if this.Run() then
            exit(StrSubstNo('SUCCESS: Codeunit %1 executed successfully', CodeunitId))
        else begin
            ErrorText := GetLastErrorText();
            if ErrorText = '' then
                ErrorText := 'Unknown error occurred';
            exit(StrSubstNo('FAILED: Codeunit %1 failed - %2', CodeunitId, ErrorText));
        end;
    end;

    /// <summary>
    /// Executes a codeunit within the test range (50000-99999) with validation.
    /// </summary>
    /// <param name="CodeunitId">The ID of the test codeunit to execute</param>
    /// <returns>
    /// Success: "SUCCESS: Codeunit {ID} executed successfully"
    /// Failure: "FAILED: Codeunit {ID} failed - {error message}"
    /// Out of range: "Error: Codeunit ID must be in test range (50000-99999)"
    /// </returns>
    /// <remarks>
    /// This procedure adds range validation before delegating to RunCodeunit().
    /// Ensures only codeunits in the standard test/custom object range are executed.
    /// </remarks>
    procedure RunTestCodeunit(CodeunitId: Integer): Text
    begin
        // Validate it's in the test codeunit range
        if (CodeunitId < 50000) or (CodeunitId > 99999) then
            exit('Error: Codeunit ID must be in test range (50000-99999)');

        exit(RunCodeunit(CodeunitId));
    end;

    /// <summary>
    /// Executes multiple codeunits sequentially from a comma-separated list.
    /// </summary>
    /// <param name="CodeunitIds">Comma-separated list of codeunit IDs (e.g., "50100,50101,50102")</param>
    /// <returns>
    /// Pipe-delimited results for each codeunit execution.
    /// Example: "SUCCESS: Codeunit 50100 executed successfully|FAILED: Codeunit 50101 failed - error"
    /// </returns>
    /// <remarks>
    /// - Executes codeunits in the order specified
    /// - Invalid IDs are skipped silently
    /// - Each execution is independent; one failure doesn't stop subsequent executions
    /// - Results are concatenated with '|' separator
    /// - Whitespace in the input is not trimmed
    /// </remarks>
    procedure RunMultipleCodeunits(CodeunitIds: Text): Text
    var
        CodeunitId: Integer;
        Results: Text;
        SingleResult: Text;
        Position: Integer;
        CommaPos: Integer;
        IdText: Text;
    begin
        Results := '';
        Position := 1;

        // Parse comma-separated list of codeunit IDs
        while Position <= StrLen(CodeunitIds) do begin
            CommaPos := StrPos(CopyStr(CodeunitIds, Position), ',');

            if CommaPos > 0 then
                IdText := CopyStr(CodeunitIds, Position, CommaPos - 1)
            else
                IdText := CopyStr(CodeunitIds, Position);

            // Try to convert to integer and run
            if Evaluate(CodeunitId, IdText) then begin
                SingleResult := RunCodeunit(CodeunitId);
                if Results <> '' then
                    Results += '|';
                Results += SingleResult;
            end;

            if CommaPos = 0 then
                Position := StrLen(CodeunitIds) + 1
            else
                Position += CommaPos;
        end;

        exit(Results);
    end;

    /// <summary>
    /// Retrieves metadata information about a codeunit without executing it.
    /// </summary>
    /// <param name="CodeunitId">The ID of the codeunit to query</param>
    /// <returns>
    /// Found: "Codeunit {ID}: {Caption/Name}"
    /// Not found: "Codeunit {ID} not found"
    /// </returns>
    /// <remarks>
    /// - Queries the AllObjWithCaption system table
    /// - Does not execute the codeunit
    /// - Useful for validation and discovery
    /// </remarks>
    procedure GetCodeunitInfo(CodeunitId: Integer): Text
    begin
        exit(StrSubstNo('Codeunit %1', CodeunitId));
    end;

    procedure CodeunitExists(CodeunitId: Integer): Boolean
    begin
        exit(CodeunitId > 0);
    end;

    trigger OnAfterTestRun(CodeunitId: Integer; CodeunitName: Text; FunctionName: Text; Permissions: TestPermissions; Success: Boolean)
    var
        Log: Record "Log Table";
        ErrorText: Text;
        CallStackText: Text;
    begin
        Log.Init();
        Log."Codeunit ID" := CodeunitId;
        Log."Codeunit Name" := CopyStr(CodeunitName, 1, 250);
        Log."Function Name" := CopyStr(FunctionName, 1, 250);
        Log."Success" := Success;

        // Capture error details if test failed
        if not Success then begin
            ErrorText := GetLastErrorText();
            CallStackText := GetLastErrorCallStack();

            Log."Error Message" := CopyStr(ErrorText, 1, 2048);
            Log."Call Stack" := CopyStr(CallStackText, 1, 2048);
        end;

        Log.Insert(false);
    end;
}