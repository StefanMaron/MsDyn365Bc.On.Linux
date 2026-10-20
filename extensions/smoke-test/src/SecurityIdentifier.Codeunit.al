// Regression guard for issue #137: System.Security.Principal.SecurityIdentifier.
//
// This tier replaces the System.Security.Principal.Windows assembly with a stub
// (src/stubs/WindowsPrincipalStub). Its SecurityIdentifier used to answer BinaryLength 28 and
// IsAccountSid true for every SID, and took any text as a SID. It now parses and measures the
// SID the way .NET does on Windows.
//
// Every expected value is what Windows reported: the first three tests are from official
// Windows BC containers (BC 27.5 onprem, 29.0 onprem, 29.0 sandbox), the rest from .NET on a
// Windows Server 2025 runner.
//
// Compiled only with BCLINUX_DOTNET_FRAMEWORK, which test-versions.yml sets on BC 29+ legs.
// The AL 16/17 compilers of the BC 27/28 legs cannot resolve a .NET framework assembly on
// Linux (AL0451). The stub is the same source for every BC version.
#if BCLINUX_DOTNET_FRAMEWORK
dotnet
{
    assembly("System.Security.Principal.Windows")
    {
        type("System.Security.Principal.SecurityIdentifier"; BcLinuxSecurityIdentifier) { }
    }
}

codeunit 70042 "BC Linux Security Id Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";

    [Test]
    procedure BuiltinAdministratorsIsSixteenBytes()
    var
        Sid: DotNet BcLinuxSecurityIdentifier;
    begin
        Sid := Sid.SecurityIdentifier('S-1-5-32-544');
        LibraryAssert.AreEqual('S-1-5-32-544', Sid.Value, 'Value');
        LibraryAssert.AreEqual(16, Sid.BinaryLength, 'BinaryLength');
    end;

    [Test]
    procedure BuiltinAdministratorsIsNotAnAccountSid()
    var
        Sid: DotNet BcLinuxSecurityIdentifier;
    begin
        Sid := Sid.SecurityIdentifier('S-1-5-32-544');
        LibraryAssert.IsFalse(Sid.IsAccountSid(), 'IsAccountSid');
    end;

    [Test]
    procedure TextThatIsNotASidIsRefused()
    var
        Sid: DotNet BcLinuxSecurityIdentifier;
    begin
        asserterror Sid := Sid.SecurityIdentifier('not a sid');
        LibraryAssert.ExpectedError('A call to System.Security.Principal.SecurityIdentifier failed with this message: Value was invalid. (Parameter ''sddlForm'')');
        LibraryAssert.AreEqual('DotNetInvoke:Argument', GetLastErrorCode(), 'error code');
    end;

    [Test]
    procedure DomainAccountIsAnAccountSid()
    var
        Sid: DotNet BcLinuxSecurityIdentifier;
    begin
        Sid := Sid.SecurityIdentifier('S-1-5-21-1004336348-1177238915-682003330-512');
        LibraryAssert.AreEqual(28, Sid.BinaryLength, 'BinaryLength');
        LibraryAssert.IsTrue(Sid.IsAccountSid(), 'IsAccountSid');
        LibraryAssert.AreEqual('S-1-5-21-1004336348-1177238915-682003330', Sid.AccountDomainSid.Value, 'AccountDomainSid');
    end;

    [Test]
    procedure TextIsNormalizedLikeWindows()
    var
        Sid: DotNet BcLinuxSecurityIdentifier;
    begin
        Sid := Sid.SecurityIdentifier('s-1-5-032-544');
        LibraryAssert.AreEqual('S-1-5-32-544', Sid.Value, 'lower case s, leading zero');
        Sid := Sid.SecurityIdentifier('S-1-5-0x20-544');
        LibraryAssert.AreEqual('S-1-5-32-544', Sid.Value, 'hex sub-authority');
        Sid := Sid.SecurityIdentifier('BA');
        LibraryAssert.AreEqual('S-1-5-32-544', Sid.Value, 'SDDL alias');
        Sid := Sid.SecurityIdentifier('S-1-5-18');
        LibraryAssert.AreEqual(12, Sid.BinaryLength, 'BinaryLength of S-1-5-18');
    end;

    [Test]
    procedure MalformedSidsAreRefused()
    begin
        CheckRefused('S-1-5');
        CheckRefused('S-1-5-');
        CheckRefused(' S-1-5-18');
        CheckRefused('S-1-5-18 ');
        CheckRefused('S-1-5-+5');
        CheckRefused('DA');
        CheckRefused('');
    end;

    local procedure CheckRefused(Text: Text)
    var
        Sid: DotNet BcLinuxSecurityIdentifier;
    begin
        asserterror Sid := Sid.SecurityIdentifier(Text);
        LibraryAssert.ExpectedError('Value was invalid. (Parameter ''sddlForm'')');
    end;
}
#endif
