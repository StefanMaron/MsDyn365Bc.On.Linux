/// <summary>
/// Wraps the MS Test Framework (codeunits 130450-130456) to run test codeunits
/// using the proper AL Test Suite infrastructure with disabled isolation.
///
/// This replicates how BCApps CI runs tests via Run-TestsInBcContainer:
/// - Uses codeunit 130451 (Test Runner - Isol. Disabled)
/// - Populates Test Method Line via codeunit 130452 (Get Methods)
/// - Supports disabling individual test methods
/// - Results tracked in Test Method Line table
/// </summary>
codeunit 50004 "Test Suite Runner"
{
    Permissions = tabledata "AL Test Suite" = rimd,
                  tabledata "Test Method Line" = rimd;

    var
        SuiteName: Code[10];

    /// <summary>
    /// Initializes a test suite for running test codeunits.
    /// Creates the suite if it doesn't exist, clears previous results.
    /// Sets the test runner to 130451 (disabled isolation).
    /// </summary>
    procedure InitSuite(Name: Code[10])
    var
        ALTestSuite: Record "AL Test Suite";
        TestMethodLine: Record "Test Method Line";
    begin
        SuiteName := Name;

        if not ALTestSuite.Get(SuiteName) then begin
            ALTestSuite.Init();
            ALTestSuite.Name := SuiteName;
            ALTestSuite."Test Runner Id" := 130451; // Isol. Disabled
            ALTestSuite.Insert(true);
        end else begin
            ALTestSuite."Test Runner Id" := 130451;
            ALTestSuite.Modify(true);
        end;

        // Clear existing test methods
        TestMethodLine.SetRange("Test Suite", SuiteName);
        TestMethodLine.DeleteAll(true);
    end;

    /// <summary>
    /// Adds a test codeunit to the suite. Uses codeunit 130452 (Test Runner - Get Methods)
    /// to discover all test methods in the codeunit.
    /// </summary>
    procedure AddTestCodeunit(CodeunitId: Integer)
    var
        TestMethodLine: Record "Test Method Line";
        LastLineNo: Integer;
    begin
        // Get the last line number
        TestMethodLine.SetRange("Test Suite", SuiteName);
        if TestMethodLine.FindLast() then
            LastLineNo := TestMethodLine."Line No.";

        // Insert codeunit line
        TestMethodLine.Init();
        TestMethodLine."Test Suite" := SuiteName;
        TestMethodLine."Line No." := LastLineNo + 10000;
        TestMethodLine."Line Type" := TestMethodLine."Line Type"::Codeunit;
        TestMethodLine."Test Codeunit" := CodeunitId;
        TestMethodLine.Run := true;
        TestMethodLine.Name := CopyStr(Format(CodeunitId), 1, MaxStrLen(TestMethodLine.Name));
        TestMethodLine.Insert(true);

        // Use Test Runner - Get Methods to discover test functions
        TestMethodLine."Skip Logging Results" := true;
        Commit();
        Codeunit.Run(Codeunit::"Test Runner - Get Methods", TestMethodLine);
    end;

    /// <summary>
    /// Disables a specific test method so it won't be executed.
    /// Matches BCApps DisabledTests/*.json behavior.
    /// </summary>
    procedure DisableTestMethod(CodeunitId: Integer; MethodName: Text)
    var
        TestMethodLine: Record "Test Method Line";
    begin
        TestMethodLine.SetRange("Test Suite", SuiteName);
        TestMethodLine.SetRange("Test Codeunit", CodeunitId);
        TestMethodLine.SetRange("Line Type", TestMethodLine."Line Type"::"Function");

        if MethodName = '*' then begin
            // Disable all methods in the codeunit
            if TestMethodLine.FindSet() then
                repeat
                    TestMethodLine.Run := false;
                    TestMethodLine.Modify(true);
                until TestMethodLine.Next() = 0;
        end else begin
            TestMethodLine.SetRange("Function", MethodName);
            if TestMethodLine.FindFirst() then begin
                TestMethodLine.Run := false;
                TestMethodLine.Modify(true);
            end;
        end;
    end;

    /// <summary>
    /// Runs all enabled tests in the suite using codeunit 130451.
    /// Results are stored in Test Method Line records.
    /// </summary>
    procedure RunSuite(): Boolean
    var
        ALTestSuite: Record "AL Test Suite";
        TestMethodLine: Record "Test Method Line";
    begin
        ALTestSuite.Get(SuiteName);

        TestMethodLine.SetRange("Test Suite", SuiteName);
        TestMethodLine.SetRange("Line Type", TestMethodLine."Line Type"::Codeunit);
        TestMethodLine.SetRange(Run, true);

        if not TestMethodLine.FindFirst() then
            exit(true);

        Commit();
        Codeunit.Run(ALTestSuite."Test Runner Id", TestMethodLine);
        exit(true);
    end;

    /// <summary>
    /// Runs a single test codeunit from the suite.
    /// Returns the result as JSON matching BCApps TestResultJson format.
    /// </summary>
    procedure RunSingleCodeunit(CodeunitId: Integer): Text
    var
        ALTestSuite: Record "AL Test Suite";
        TestMethodLine: Record "Test Method Line";
        ResultLine: Record "Test Method Line";
        TotalMethods: Integer;
        PassedMethods: Integer;
        FailedMethods: Integer;
        SkippedMethods: Integer;
        ResultText: Text;
    begin
        ALTestSuite.Get(SuiteName);

        // Find the codeunit line
        TestMethodLine.SetRange("Test Suite", SuiteName);
        TestMethodLine.SetRange("Line Type", TestMethodLine."Line Type"::Codeunit);
        TestMethodLine.SetRange("Test Codeunit", CodeunitId);
        if not TestMethodLine.FindFirst() then
            exit('{"error": "Codeunit not found in suite"}');

        if not TestMethodLine.Run then
            exit('{"status": "Skipped", "codeunitId": ' + Format(CodeunitId) + '}');

        // Run it
        Commit();
        Codeunit.Run(ALTestSuite."Test Runner Id", TestMethodLine);

        // Collect results from function lines
        ResultLine.SetRange("Test Suite", SuiteName);
        ResultLine.SetRange("Test Codeunit", CodeunitId);
        ResultLine.SetRange("Line Type", ResultLine."Line Type"::"Function");

        if ResultLine.FindSet() then
            repeat
                TotalMethods += 1;
                case ResultLine.Result of
                    ResultLine.Result::Success:
                        PassedMethods += 1;
                    ResultLine.Result::Failure:
                        FailedMethods += 1;
                    else
                        if not ResultLine.Run then
                            SkippedMethods += 1;
                end;
            until ResultLine.Next() = 0;

        // Refresh codeunit line for overall result
        TestMethodLine.Find();

        if FailedMethods = 0 then
            ResultText := 'Success'
        else
            ResultText := StrSubstNo('%1 test(s) failed', FailedMethods);

        exit(ResultText);
    end;

    /// <summary>
    /// Gets the count of test methods with each result status.
    /// </summary>
    procedure GetResults(var Passed: Integer; var Failed: Integer; var Skipped: Integer)
    var
        TestMethodLine: Record "Test Method Line";
    begin
        TestMethodLine.SetRange("Test Suite", SuiteName);
        TestMethodLine.SetRange("Line Type", TestMethodLine."Line Type"::"Function");

        TestMethodLine.SetRange(Result, TestMethodLine.Result::Success);
        Passed := TestMethodLine.Count();

        TestMethodLine.SetRange(Result, TestMethodLine.Result::Failure);
        Failed := TestMethodLine.Count();

        TestMethodLine.SetRange(Result);
        TestMethodLine.SetRange(Run, false);
        Skipped := TestMethodLine.Count();
    end;
}
