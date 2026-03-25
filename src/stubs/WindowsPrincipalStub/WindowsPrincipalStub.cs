// Stub for System.Security.Principal.Windows on Linux.
// Provides a dummy WindowsIdentity that doesn't throw PlatformNotSupportedException.

using System.Security.Claims;
using Microsoft.Win32.SafeHandles;

namespace System.Security.Principal
{
    public class WindowsIdentity : ClaimsIdentity, IDisposable
    {
        private static readonly WindowsIdentity _current = new WindowsIdentity();

        public WindowsIdentity() : base("SYSTEM") { }
        public WindowsIdentity(string sUserPrincipalName) : base(sUserPrincipalName) { }
        public WindowsIdentity(IntPtr userToken) : base("SYSTEM") { }

        public static WindowsIdentity GetCurrent() => _current;
        public static WindowsIdentity GetCurrent(bool ifImpersonating) => _current;
        public static WindowsIdentity GetCurrent(TokenAccessLevels desiredAccess) => _current;
        public static WindowsIdentity? GetAnonymous() => new WindowsIdentity("Anonymous");

        public override string? Name => "SYSTEM";
        public override string AuthenticationType => "Negotiate";
        public override bool IsAuthenticated => true;
        public SecurityIdentifier? User => new SecurityIdentifier("S-1-5-18");
        public SecurityIdentifier? Owner => null;
        public IntPtr Token => IntPtr.Zero;
        public TokenImpersonationLevel ImpersonationLevel => TokenImpersonationLevel.None;
        public bool IsAnonymous => false;
        public bool IsGuest => false;
        public bool IsSystem => true;

        public void Dispose() { }

        public WindowsImpersonationContext Impersonate() => new WindowsImpersonationContext();
        public static WindowsImpersonationContext Impersonate(IntPtr userToken) => new WindowsImpersonationContext();

        public static void RunImpersonated(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle safeAccessTokenHandle, Action action)
        {
            action(); // Just run the action without impersonation
        }

        public static T RunImpersonated<T>(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle safeAccessTokenHandle, Func<T> func)
        {
            return func();
        }
    }

    public class WindowsImpersonationContext : IDisposable
    {
        public void Undo() { }
        public void Dispose() { }
    }

    public class WindowsPrincipal : ClaimsPrincipal
    {
        public WindowsPrincipal(WindowsIdentity ntIdentity) : base(ntIdentity) { }
        public virtual bool IsInRole(string role) => true;
        public virtual bool IsInRole(int rid) => true;
        public virtual bool IsInRole(WindowsBuiltInRole role) => true;
        public virtual bool IsInRole(SecurityIdentifier sid) => true;
    }

    public sealed class SecurityIdentifier : IdentityReference
    {
        private readonly string _value;

        public SecurityIdentifier(string sddlForm) { _value = sddlForm; }
        public SecurityIdentifier(WellKnownSidType sidType, SecurityIdentifier? domainSid)
        {
            _value = "S-1-5-18"; // Local System
        }
        public SecurityIdentifier(byte[] binaryForm, int offset) { _value = "S-1-5-18"; }
        public SecurityIdentifier? AccountDomainSid => null;
        public int BinaryLength => 28;
        public override string Value => _value;

        public override bool Equals(object? o) => o is SecurityIdentifier sid && sid._value == _value;
        public override int GetHashCode() => _value.GetHashCode();
        public override string ToString() => _value;
        public bool IsAccountSid() => true;
        public bool IsWellKnown(WellKnownSidType type) => _value == "S-1-5-18" && type == WellKnownSidType.LocalSystemSid;
        public override bool IsValidTargetType(Type targetType) => true;
        public override IdentityReference Translate(Type targetType) => this;
        public void GetBinaryForm(byte[] binaryForm, int offset) { }
    }

    public abstract class IdentityReference
    {
        public abstract string Value { get; }
        public abstract bool IsValidTargetType(Type targetType);
        public abstract IdentityReference Translate(Type targetType);
    }

    public sealed class NTAccount : IdentityReference
    {
        private readonly string _name;
        public NTAccount(string name) { _name = name; }
        public NTAccount(string domainName, string accountName) { _name = domainName + "\\" + accountName; }
        public override string Value => _name;
        public override string ToString() => _name;
        public override bool Equals(object? o) => o is NTAccount nt && nt._name == _name;
        public override int GetHashCode() => _name.GetHashCode();
        public override bool IsValidTargetType(Type targetType) => true;
        public override IdentityReference Translate(Type targetType) => this;
    }

    public enum TokenAccessLevels { AssignPrimary = 1, Duplicate = 2, Impersonate = 4, Query = 8, MaximumAllowed = 0x2000000 }
    // TokenImpersonationLevel is NOT redefined here — it exists in System.Runtime (BCL).
    // Redefining it causes "Method not found" because the caller expects the BCL version.
    public enum WindowsBuiltInRole { Administrator = 544, User = 545, Guest = 546 }
    public class IdentityReferenceCollection : System.Collections.ObjectModel.Collection<IdentityReference>
    {
        public IdentityReferenceCollection() { }
        public IdentityReferenceCollection(int capacity) { }
        public IdentityReferenceCollection Translate(Type targetType) => this;
    }

    public enum WellKnownSidType
    {
        NullSid = 0, WorldSid = 1, LocalSid = 2, NetworkSid = 6,
        BuiltinAdministratorsSid = 26, BuiltinUsersSid = 27, LocalSystemSid = 22,
        NtAuthoritySid = 7, NetworkServiceSid = 24, LocalServiceSid = 23,
        AuthenticatedUserSid = 17,
    }
}

namespace Microsoft.Win32.SafeHandles
{
    public sealed class SafeAccessTokenHandle : System.Runtime.InteropServices.SafeHandle
    {
        public SafeAccessTokenHandle() : base(System.IntPtr.Zero, true) { }
        public SafeAccessTokenHandle(System.IntPtr handle) : base(handle, true) { }
        public static SafeAccessTokenHandle InvalidHandle => new SafeAccessTokenHandle();
        public override bool IsInvalid => handle == System.IntPtr.Zero;
        protected override bool ReleaseHandle() => true;
    }
}

namespace System.Security.AccessControl
{
    public abstract class ObjectSecurity { protected ObjectSecurity() { } }
    public abstract class CommonObjectSecurity : ObjectSecurity { protected CommonObjectSecurity(bool isContainer) { } }
    public abstract class NativeObjectSecurity : CommonObjectSecurity
    {
        protected NativeObjectSecurity(bool isContainer, ResourceType resourceType) : base(isContainer) { }
    }
    public abstract class FileSystemSecurity : NativeObjectSecurity
    {
        internal FileSystemSecurity() : base(false, ResourceType.FileObject) { }
    }
    public sealed class DirectorySecurity : FileSystemSecurity { public DirectorySecurity() { } }
    public enum ResourceType { Unknown = 0, FileObject = 1 }
    public enum FileSystemRights { FullControl = 0x1F01FF }
    public enum InheritanceFlags { None = 0, ContainerInherit = 1, ObjectInherit = 2 }
    public enum PropagationFlags { None = 0 }
    public enum AccessControlType { Allow = 0, Deny = 1 }

    public sealed class FileSystemAccessRule
    {
        public FileSystemAccessRule(System.Security.Principal.IdentityReference identity, FileSystemRights rights,
            InheritanceFlags inheritance, PropagationFlags propagation, AccessControlType type) { }
    }
}
