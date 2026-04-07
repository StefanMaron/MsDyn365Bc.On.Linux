// Smoke test codeunit 1: trivial sanity checks (date + arithmetic).
// Used by test-versions.yml to verify the bc-linux substrate end-to-end
// across all supported BC versions.
codeunit 70000 "BC Linux Smoke Test 1"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure TestDateSanity()
    begin
        if Today() < 20200101D then
            Error('Date sanity check failed: %1', Today());
    end;

    [Test]
    procedure TestBasicArithmetic()
    begin
        if 6 * 7 <> 42 then
            Error('Basic arithmetic failed');
    end;
}
