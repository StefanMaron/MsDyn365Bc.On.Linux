/// <summary>
/// Codeunit Run Request - State-tracked execution requests for the Test Runner API
///
/// This table stores execution requests with status tracking, providing a stateful
/// alternative to the stateless Test Runner API (codeunit 50199).
///
/// Purpose:
/// - Track codeunit execution requests with persistent state
/// - Store execution results and timestamps
/// - Enable asynchronous execution patterns via REST API
/// - Provide execution history with success/failure tracking
///
/// State Machine:
/// Pending → Running → Finished (success)
///                  → Error (failure)
///
/// API Endpoint:
/// /api/custom/automation/v1.0/codeunitRunRequests
///
/// Usage Pattern:
/// 1. POST to create new request with CodeunitId
/// 2. POST to .../Microsoft.NAV.runCodeunit action to execute
/// 3. GET to check Status and LastResult
/// 4. Query LastExecutionUTC for execution timestamp
/// </summary>
table 50003 "Codeunit Run Request"
{
    Caption = 'Codeunit Run Request';
    DataClassification = SystemMetadata;
    LookupPageId = "Codeunit Run Requests";
    DrillDownPageId = "Codeunit Run Requests";

    fields
    {
        /// <summary>
        /// Unique identifier for the execution request (GUID).
        /// </summary>
        /// <remarks>
        /// Auto-generated on insert if not provided.
        /// Used as OData key field for API access.
        /// </remarks>
        field(1; Id; Guid)
        {
            Caption = 'Id';
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// The ID of the codeunit to execute (single codeunit mode).
        /// </summary>
        /// <remarks>
        /// Used for single codeunit execution. Set either this OR CodeunitIds.
        /// No validation is performed - invalid IDs will result in Error status.
        /// </remarks>
        field(2; CodeunitId; Integer)
        {
            Caption = 'Codeunit Id';
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// Comma-separated list or range of codeunit IDs (batch mode).
        /// </summary>
        /// <remarks>
        /// Supports ranges like "50000-99999" and comma-separated IDs like "76550,76551,76554".
        /// When set, RunCodeunit() iterates over all matching IDs.
        /// Takes precedence over CodeunitId when non-empty.
        /// </remarks>
        field(6; CodeunitIds; Text[2048])
        {
            Caption = 'Codeunit Ids';
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// Current execution status of the request.
        /// </summary>
        /// <remarks>
        /// Values:
        /// - Pending: Request created, not yet executed
        /// - Running: Execution in progress (set at start of RunCodeunit)
        /// - Finished: Execution completed successfully
        /// - Error: Execution failed (check LastResult for error message)
        /// </remarks>
        field(3; Status; Option)
        {
            Caption = 'Status';
            OptionMembers = Pending,Running,Finished,Error;
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// Result message from the last execution attempt.
        /// </summary>
        /// <remarks>
        /// Success: "Success"
        /// Failure: Contains the error message text
        /// Maximum length: 250 characters (error messages may be truncated)
        /// </remarks>
        field(4; LastResult; Text[250])
        {
            Caption = 'Last Result';
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// Timestamp of the last execution attempt (UTC timezone).
        /// </summary>
        /// <remarks>
        /// Updated by RunCodeunit() procedure.
        /// Always in UTC - convert to local time if needed for display.
        /// </remarks>
        field(5; LastExecutionUTC; DateTime)
        {
            Caption = 'Last Execution (UTC)';
            DataClassification = SystemMetadata;
        }
    }

    keys
    {
        /// <summary>
        /// Primary key on Id field.
        /// </summary>
        key(PK; Id) { Clustered = true; }
    }

    /// <summary>
    /// OnInsert trigger - Initializes new request records.
    /// </summary>
    /// <remarks>
    /// - Auto-generates GUID if not provided
    /// - Defaults Status to Pending (kept via no-op statement)
    /// </remarks>
    trigger OnInsert()
    begin
        if IsNullGuid(Id) then
            Id := CreateGuid();
        if Status = Status::Pending then; // keep default status
    end;
}

/// <summary>
/// Codeunit Run Requests API - REST endpoint for state-tracked codeunit execution
///
/// This API page exposes the Codeunit Run Request table via OData/REST endpoints,
/// enabling remote codeunit execution with persistent state tracking.
///
/// Endpoint:
/// /api/custom/automation/v1.0/codeunitRunRequests
///
/// Operations:
/// - GET: List all execution requests
/// - GET(id): Retrieve specific request by GUID
/// - POST: Create new execution request
/// - PATCH(id): Update request fields (e.g., CodeunitId)
/// - DELETE(id): Remove execution request
/// - POST(id)/Microsoft.NAV.runCodeunit: Execute the codeunit (service-enabled action)
/// </summary>
page 50002 "Codeunit Run Requests"
{
    PageType = API;
    Caption = 'Codeunit Run Requests';
    APIPublisher = 'custom';
    APIGroup = 'automation';
    APIVersion = 'v1.0';
    EntityName = 'codeunitRunRequest';
    EntitySetName = 'codeunitRunRequests';
    SourceTable = "Codeunit Run Request";
    DelayedInsert = true;
    ODataKeyFields = Id;

    layout
    {
        area(content)
        {
            group(General)
            {
                field(Id; Rec.Id) { Editable = false; }
                field(CodeunitId; Rec.CodeunitId) { }
                field(CodeunitIds; Rec.CodeunitIds) { }
                field(Status; Rec.Status) { Editable = false; }
                field(LastResult; Rec.LastResult) { Editable = false; }
                field(LastExecutionUTC; Rec.LastExecutionUTC) { Editable = false; }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(RunNow)
            {
                Caption = 'Run';
                ApplicationArea = All;
                trigger OnAction()
                begin
                    RunCodeunit();
                end;
            }
        }
    }

    /// <summary>
    /// Sets up the test suite (creates suite, discovers test methods) without running.
    /// Used by the WebSocket test runner to populate the suite before executing via
    /// page 130455 which requires a client session for TestPage support.
    /// </summary>
    [ServiceEnabled]
    procedure SetupSuite(): Boolean
    var
        SuiteRunner: Codeunit "Test Suite Runner";
        IdList: List of [Text];
        IdText: Text;
        CuId: Integer;
        RangeStart: Integer;
        RangeEnd: Integer;
        i: Integer;
        DashPos: Integer;
    begin
        if Rec.CodeunitIds = '' then
            exit(false);

        // Use runner 130451 (Isol. Codeunit) for proper test isolation.
        // Results are stored in Test Method Line (read via testResults OData API).
        SuiteRunner.InitSuite('DEFAULT');
        OverrideSuiteRunner('DEFAULT', 130451);

        // Parse comma-separated IDs and ranges (same as RunCodeunit)
        IdList := Rec.CodeunitIds.Split(',');
        foreach IdText in IdList do begin
            DashPos := IdText.IndexOf('-');
            if DashPos > 0 then begin
                if Evaluate(RangeStart, IdText.Substring(1, DashPos - 1)) and
                   Evaluate(RangeEnd, IdText.Substring(DashPos + 1)) then
                    for i := RangeStart to RangeEnd do
                        SuiteRunner.AddTestCodeunit(i);
            end else
                if Evaluate(CuId, IdText) then
                    SuiteRunner.AddTestCodeunit(CuId);
        end;

        Rec.Status := Rec.Status::Pending;
        Rec.LastResult := 'Suite ready';
        Rec.Modify(true);
        exit(true);
    end;

    local procedure OverrideSuiteRunner(SuiteName: Code[10]; RunnerId: Integer)
    var
        ALTestSuite: Record "AL Test Suite";
    begin
        if ALTestSuite.Get(SuiteName) then begin
            ALTestSuite."Test Runner Id" := RunnerId;
            ALTestSuite.Modify(true);
        end;
    end;

    [ServiceEnabled]
    procedure RunCodeunit(): Boolean
    var
        SuiteRunner: Codeunit "Test Suite Runner";
        Log: Record "Log Table";
        ResultText: Text;
        AllSuccess: Boolean;
        IdList: List of [Text];
        IdToken: Text;
        RangeTokens: List of [Text];
        StartId: Integer;
        EndId: Integer;
        CuId: Integer;
        Passed: Integer;
        Failed: Integer;
        AddedAny: Boolean;
    begin
        if Rec.Status = Rec.Status::Running then
            Error('Already running.');

        Rec.Status := Rec.Status::Running;
        Rec.Modify(true);
        Commit();

        AllSuccess := true;
        SuiteRunner.InitSuite('LINUX'); // Uses TestRunnerAPI (50003) as runner — logs to Log Table

        if Rec.CodeunitIds <> '' then begin
            // Batch mode: clear Log Table before starting so we get clean results
            Log.DeleteAll(false);
            Commit();
            // Batch mode: parse "76550,76551,76554" or "76550-76554"
            // bc-test passes only actual test codeunit IDs discovered from AL source
            IdList := Rec.CodeunitIds.Split(',');
            foreach IdToken in IdList do begin
                IdToken := IdToken.Trim();
                if IdToken.Contains('-') then begin
                    RangeTokens := IdToken.Split('-');
                    if (RangeTokens.Count = 2) and Evaluate(StartId, RangeTokens.Get(1)) and Evaluate(EndId, RangeTokens.Get(2)) then
                        for CuId := StartId to EndId do begin
                            SuiteRunner.AddTestCodeunit(CuId);
                            AddedAny := true;
                        end;
                end else
                    if Evaluate(CuId, IdToken) then begin
                        SuiteRunner.AddTestCodeunit(CuId);
                        AddedAny := true;
                    end;
            end;

            if not AddedAny then begin
                Rec.Status := Rec.Status::Finished;
                Rec.LastResult := 'No test codeunits found';
                Rec.LastExecutionUTC := CurrentDateTime();
                Rec.Modify(true);
                exit(true);
            end;

            SuiteRunner.RunSuite(); // TestRunnerAPI.OnAfterTestRun populates Log Table

            // Count results from Log Table
            Log.SetRange(Success, true);
            Passed := Log.Count();
            Log.SetRange(Success, false);
            Failed := Log.Count();

            if Failed = 0 then
                ResultText := 'Success'
            else
                ResultText := StrSubstNo('%1 test(s) failed', Failed);
        end else begin
            // Single mode — TestRunnerAPI.OnAfterTestRun populates Log Table
            Rec.TestField(CodeunitId);
            SuiteRunner.AddTestCodeunit(Rec.CodeunitId);
            ResultText := SuiteRunner.RunSingleCodeunit(Rec.CodeunitId);
        end;

        if ResultText = 'Success' then begin
            Rec.Status := Rec.Status::Finished;
            Rec.LastResult := 'Success';
        end else begin
            Rec.Status := Rec.Status::Error;
            Rec.LastResult := CopyStr(ResultText, 1, 250);
            AllSuccess := false;
        end;

        Rec.LastExecutionUTC := CurrentDateTime();
        Rec.Modify(true);
        exit(AllSuccess);
    end;
}