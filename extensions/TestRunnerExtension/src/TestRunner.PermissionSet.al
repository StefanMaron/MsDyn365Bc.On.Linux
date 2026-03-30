permissionset 50005 "Test Runner Exec"
{
    Assignable = true;
    Caption = 'Test Runner Exec';
    Permissions =
        tabledata "Log Table" = RIMD,
        tabledata "Codeunit Run Request" = RIMD;
}
