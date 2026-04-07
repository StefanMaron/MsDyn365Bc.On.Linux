// Smoke test codeunit 2: trivial string + boolean checks.
// Used by test-versions.yml to verify the bc-linux substrate end-to-end
// across all supported BC versions.
codeunit 70001 "BC Linux Smoke Test 2"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure TestStringConcatenation()
    var
        Result: Text;
    begin
        Result := 'Hello' + ', ' + 'World';
        if Result <> 'Hello, World' then
            Error('String concatenation failed: %1', Result);
    end;

    [Test]
    procedure TestBooleanLogic()
    begin
        if not (true and not false) then
            Error('Boolean logic failed');
    end;
}
