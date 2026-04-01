/// <summary>
/// WebSocket Test Runner Page — designed for the WebSocket client services runner.
///
/// Opens via ws://host:7085 → OpenForm(50005). The WebSocket runner sets CodeunitIds,
/// calls AddCodeunits to populate the suite, then calls RunNextCodeunit in a loop,
/// reading TestResultJson after each call.
///
/// This avoids the complexity of page 130455 (Command Line Test Tool) which requires
/// keyboard mapping P/Invoke stubs and complex page state that doesn't work on Linux.
/// </summary>
page 50005 "WS Test Runner"
{
    PageType = Card;
    ApplicationArea = All;
    UsageCategory = None;
    SourceTable = "AL Test Suite";
    InsertAllowed = true;
    Caption = 'WS Test Runner';

    layout
    {
        area(Content)
        {
            field(SuiteName; Rec.Name) { ApplicationArea = All; }
            field(StatusText; StatusText) { ApplicationArea = All; Caption = 'Status'; }
            field(TestResultJson; TestResultJson) { ApplicationArea = All; Caption = 'TestResultJson'; }
            field(CodeunitIdsField; CodeunitIds) { ApplicationArea = All; Caption = 'Codeunit IDs'; }
            field(TotalCodeunits; TotalCodeunits) { ApplicationArea = All; Caption = 'Total Codeunits'; }
            field(CompletedCodeunits; CompletedCodeunits) { ApplicationArea = All; Caption = 'Completed'; }
        }
    }

    actions
    {
        area(Processing)
        {
            action(AddCodeunits)
            {
                ApplicationArea = All;
                Caption = 'Add Codeunits';
                trigger OnAction()
                begin
                    DoAddCodeunits();
                end;
            }
            action(RunNextCodeunit)
            {
                ApplicationArea = All;
                Caption = 'Run Next Codeunit';
                trigger OnAction()
                begin
                    DoRunNextCodeunit();
                end;
            }
        }
    }

    trigger OnOpenPage()
    begin
        EnsureSuite();
        StatusText := 'Ready. Set CodeunitIds and call AddCodeunits.';
    end;

    var
        TestResultJson: Text;
        StatusText: Text[250];
        CodeunitIds: Text[2048];
        TotalCodeunits: Integer;
        CompletedCodeunits: Integer;
        SuiteName: Code[10];

    local procedure EnsureSuite()
    begin
        SuiteName := 'WSRUN';
        if not Rec.Get(SuiteName) then begin
            Rec.Init();
            Rec.Name := SuiteName;
            Rec."Test Runner Id" := 130451;
            Rec.Insert(true);
            Commit();
        end;
    end;

    local procedure DoAddCodeunits()
    var
        SuiteRunner: Codeunit "Test Suite Runner";
        TestMethodLine: Record "Test Method Line";
        IdText: Text;
        CuId: Integer;
        Pos: Integer;
        CommaPos: Integer;
        Ids: Text;
    begin
        EnsureSuite();

        // Clear existing lines
        TestMethodLine.SetRange("Test Suite", SuiteName);
        TestMethodLine.DeleteAll(true);

        Ids := CodeunitIds;
        SuiteRunner.InitSuiteKeep(SuiteName);

        // Parse comma-separated IDs
        Pos := 1;
        while Pos <= StrLen(Ids) do begin
            CommaPos := StrPos(CopyStr(Ids, Pos), ',');

            if CommaPos > 0 then
                IdText := CopyStr(Ids, Pos, CommaPos - 1)
            else
                IdText := CopyStr(Ids, Pos);

            if Evaluate(CuId, IdText) then
                SuiteRunner.AddTestCodeunit(CuId);

            if CommaPos = 0 then
                Pos := StrLen(Ids) + 1
            else
                Pos += CommaPos;
        end;

        UpdateCounts();
        StatusText := StrSubstNo('Added %1 test codeunits', TotalCodeunits);
        Commit();
    end;

    local procedure DoRunNextCodeunit()
    var
        TestMethodLine: Record "Test Method Line";
        FuncLine: Record "Test Method Line";
        ResultObj: JsonObject;
        ResultArray: JsonArray;
        MethodObj: JsonObject;
        Passed: Integer;
        Failed: Integer;
        Skipped: Integer;
    begin
        TestResultJson := '';
        EnsureSuite();

        // Find next codeunit not yet executed
        TestMethodLine.SetRange("Test Suite", SuiteName);
        TestMethodLine.SetRange("Line Type", TestMethodLine."Line Type"::Codeunit);
        TestMethodLine.SetRange(Run, true);
        TestMethodLine.SetRange(Result, TestMethodLine.Result::" ");
        if not TestMethodLine.FindFirst() then begin
            TestResultJson := 'All tests executed.';
            StatusText := 'All tests executed.';
            exit;
        end;

        StatusText := StrSubstNo('Running CU %1...', TestMethodLine."Test Codeunit");
        Commit();

        // Run via the test runner
        Rec.Get(SuiteName);
        Codeunit.Run(Rec."Test Runner Id", TestMethodLine);

        // Build result JSON
        ResultObj.Add('codeUnit', TestMethodLine."Test Codeunit");
        ResultObj.Add('name', TestMethodLine.Name);

        FuncLine.SetRange("Test Suite", SuiteName);
        FuncLine.SetRange("Test Codeunit", TestMethodLine."Test Codeunit");
        FuncLine.SetRange("Line Type", FuncLine."Line Type"::"Function");
        if FuncLine.FindSet() then
            repeat
                Clear(MethodObj);
                MethodObj.Add('method', FuncLine."Function");
                case FuncLine.Result of
                    FuncLine.Result::Success:
                        begin
                            MethodObj.Add('result', 2);
                            MethodObj.Add('message', '');
                            Passed += 1;
                        end;
                    FuncLine.Result::Failure:
                        begin
                            MethodObj.Add('result', 1);
                            MethodObj.Add('message', FuncLine."Error Message Preview");
                            Failed += 1;
                        end;
                    else begin
                        MethodObj.Add('result', 0);
                        MethodObj.Add('message', '');
                        Skipped += 1;
                    end;
                end;
                ResultArray.Add(MethodObj);
            until FuncLine.Next() = 0;

        ResultObj.Add('testResults', ResultArray);
        ResultObj.Add('passed', Passed);
        ResultObj.Add('failed', Failed);
        ResultObj.Add('skipped', Skipped);
        ResultObj.WriteTo(TestResultJson);

        CompletedCodeunits += 1;
        if Failed > 0 then
            StatusText := StrSubstNo('CU %1: %2 passed, %3 failed', TestMethodLine."Test Codeunit", Passed, Failed)
        else
            StatusText := StrSubstNo('CU %1: %2 passed', TestMethodLine."Test Codeunit", Passed);

        Commit();
    end;

    local procedure UpdateCounts()
    var
        TestMethodLine: Record "Test Method Line";
        DoneLine: Record "Test Method Line";
    begin
        TestMethodLine.SetRange("Test Suite", SuiteName);
        TestMethodLine.SetRange("Line Type", TestMethodLine."Line Type"::Codeunit);
        TestMethodLine.SetRange(Run, true);
        TotalCodeunits := TestMethodLine.Count();

        DoneLine.CopyFilters(TestMethodLine);
        DoneLine.SetFilter(Result, '<>%1', DoneLine.Result::" ");
        CompletedCodeunits := DoneLine.Count();
    end;
}
