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
        // The BC web client's LogicalThread captures GetCurrent().AccessToken and
        // re-impersonates it on session threads via RunImpersonated (which this
        // stub runs without impersonation), so an invalid handle is fine.
        public Microsoft.Win32.SafeHandles.SafeAccessTokenHandle AccessToken =>
            new Microsoft.Win32.SafeHandles.SafeAccessTokenHandle();
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

        // The web client's CallbackService.InvokeWithImpersonation<T> awaits this on the
        // OpenSession path. It is missing here, so the async state machine throws
        // MissingMethodException the first time it runs, OpenSession never answers over
        // /csh, and the browser sits on "Getting ready..." until it gives up. Same
        // no-op semantics as RunImpersonated above.
        public static global::System.Threading.Tasks.Task<T> RunImpersonatedAsync<T>(
            Microsoft.Win32.SafeHandles.SafeAccessTokenHandle safeAccessTokenHandle,
            Func<global::System.Threading.Tasks.Task<T>> func)
        {
            return func();
        }

        public static global::System.Threading.Tasks.Task RunImpersonatedAsync(
            Microsoft.Win32.SafeHandles.SafeAccessTokenHandle safeAccessTokenHandle,
            Func<global::System.Threading.Tasks.Task> func)
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

    /// <summary>
    /// A SID parsed, measured and formatted the way .NET's own SecurityIdentifier does it on
    /// Windows (issue #137). The real type hands the text to ConvertStringSidToSid and then checks
    /// the binary form; this one parses the same grammar itself. Every rule below was measured
    /// with .NET on a Windows Server 2025 runner:
    ///  * "S-" or "s-", then revision, authority and at least one sub-authority, each decimal
    ///    digits or 0x/0X and hex digits. Leading zeros are still decimal ("S-1-5-032-544" is
    ///    S-1-5-32-544). No sign, no white space, nothing after the last number.
    ///  * The authority is 48 bits; a larger value is invalid. A sub-authority that does not fit
    ///    in 32 bits is clamped to 4294967295 (wcstoul).
    ///  * A two-letter SDDL alias ("BA", "SY", "WD", any case) stands for its fixed SID. Aliases
    ///    that need a domain ("DA") are invalid on a machine that is not in one.
    ///  * Invalid text: ArgumentException "Value was invalid." for parameter "sddlForm". A
    ///    revision other than 1 and more than 15 sub-authorities are reported by the binary-form
    ///    check instead, for parameter "binaryForm".
    ///  * IsAccountSid / AccountDomainSid: S-1-5-21 followed by at least three more
    ///    sub-authorities; the domain is S-1-5-21 and those three.
    /// Two known differences: "LA" and "LG" name the local Administrator and Guest of the Windows
    /// machine and are refused here, and a hex revision ("S-0x1-5-18") is read as 1 here while
    /// Windows returns S-1-5-24 for it.
    /// </summary>
    public sealed class SecurityIdentifier : IdentityReference, IComparable<SecurityIdentifier>
    {
        public static readonly int MinBinaryLength = 8;
        public static readonly int MaxBinaryLength = 68;
        private const int MaxSubAuthorities = 15;
        private const string ArrayTooSmall = "Destination array is not long enough to copy all the required data. Check array length and offset.";

        private readonly ulong _authority;
        private readonly uint[] _subAuthorities;
        private readonly string _value;

        public SecurityIdentifier(string sddlForm)
        {
            if (sddlForm is null) throw new ArgumentNullException(nameof(sddlForm));
            if (!TryParse(sddlForm, out ulong revision, out ulong authority, out var subs))
                throw new ArgumentException("Value was invalid.", nameof(sddlForm));
            CheckBinaryShape(revision, subs.Count);
            _authority = authority;
            _subAuthorities = subs.ToArray();
            _value = Format(_authority, _subAuthorities);
        }

        public SecurityIdentifier(byte[] binaryForm, int offset)
        {
            if (binaryForm is null) throw new ArgumentNullException(nameof(binaryForm));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "Non-negative number required.");
            if (binaryForm.Length - offset < MinBinaryLength)
                throw new ArgumentOutOfRangeException(nameof(binaryForm), ArrayTooSmall);
            CheckBinaryShape(binaryForm[offset], binaryForm[offset + 1]);
            int count = binaryForm[offset + 1];
            if (binaryForm.Length - (8 + 4 * count) < offset)
                throw new ArgumentException(ArrayTooSmall, nameof(binaryForm));
            ulong authority = 0;
            for (int i = 0; i < 6; i++) authority = (authority << 8) | binaryForm[offset + 2 + i];
            var subs = new uint[count];
            for (int i = 0; i < count; i++)
            {
                int at = offset + 8 + 4 * i;
                subs[i] = (uint)(binaryForm[at] | binaryForm[at + 1] << 8 | binaryForm[at + 2] << 16 | binaryForm[at + 3] << 24);
            }
            _authority = authority;
            _subAuthorities = subs;
            _value = Format(_authority, _subAuthorities);
        }

        public SecurityIdentifier(WellKnownSidType sidType, SecurityIdentifier? domainSid)
        {
            string? text = WellKnown(sidType);
            if (text is null)
                throw new ArgumentException("Value was invalid.", nameof(sidType));
            // Built from the parts, not parsed: NTAuthoritySid (S-1-5) has no sub-authority,
            // which the text form refuses and the binary form allows.
            string[] parts = text.Split('-');
            _authority = ulong.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            _subAuthorities = new uint[parts.Length - 3];
            for (int i = 3; i < parts.Length; i++)
                _subAuthorities[i - 3] = uint.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
            _value = Format(_authority, _subAuthorities);
        }

        private SecurityIdentifier(ulong authority, uint[] subAuthorities)
        {
            _authority = authority;
            _subAuthorities = subAuthorities;
            _value = Format(authority, subAuthorities);
        }

        public SecurityIdentifier? AccountDomainSid
        {
            get
            {
                if (_authority != 5 || _subAuthorities.Length < 4 || _subAuthorities[0] != 21) return null;
                return new SecurityIdentifier(5, new[] { _subAuthorities[0], _subAuthorities[1], _subAuthorities[2], _subAuthorities[3] });
            }
        }

        public int BinaryLength => 8 + 4 * _subAuthorities.Length;
        public override string Value => _value;

        public override bool Equals(object? o) => o is SecurityIdentifier sid && sid._value == _value;
        public bool Equals(SecurityIdentifier? sid) => sid is not null && sid._value == _value;
        public override int GetHashCode() => _value.GetHashCode();
        public override string ToString() => _value;

        // The real BCL type declares these; BC 29's NavEnvironment startup path
        // calls op_Inequality and dies with MissingMethodException without them.
        public static bool operator ==(SecurityIdentifier? left, SecurityIdentifier? right)
            => left is null ? right is null : left.Equals(right);
        public static bool operator !=(SecurityIdentifier? left, SecurityIdentifier? right)
            => !(left == right);

        /// <summary>The order .NET uses: authority, then the number of sub-authorities, then each one.</summary>
        public int CompareTo(SecurityIdentifier? sid)
        {
            if (sid is null) throw new ArgumentNullException(nameof(sid));
            if (_authority != sid._authority) return _authority < sid._authority ? -1 : 1;
            if (_subAuthorities.Length != sid._subAuthorities.Length) return _subAuthorities.Length < sid._subAuthorities.Length ? -1 : 1;
            for (int i = 0; i < _subAuthorities.Length; i++)
                if (_subAuthorities[i] != sid._subAuthorities[i]) return _subAuthorities[i] < sid._subAuthorities[i] ? -1 : 1;
            return 0;
        }

        public bool IsAccountSid() => AccountDomainSid is not null;

        public bool IsEqualDomainSid(SecurityIdentifier sid)
        {
            var mine = AccountDomainSid;
            return mine is not null && mine == sid?.AccountDomainSid;
        }

        public bool IsWellKnown(WellKnownSidType type)
        {
            string? text = WellKnown(type);
            return text is not null && text == _value;
        }

        public override bool IsValidTargetType(Type targetType) => true;
        public override IdentityReference Translate(Type targetType) => this;

        public void GetBinaryForm(byte[] binaryForm, int offset)
        {
            if (binaryForm is null) throw new ArgumentNullException(nameof(binaryForm));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "Non-negative number required.");
            if (binaryForm.Length - offset < BinaryLength)
                throw new ArgumentOutOfRangeException(nameof(binaryForm), ArrayTooSmall);
            binaryForm[offset] = 1;
            binaryForm[offset + 1] = (byte)_subAuthorities.Length;
            for (int i = 0; i < 6; i++) binaryForm[offset + 2 + i] = (byte)(_authority >> (8 * (5 - i)));
            for (int i = 0; i < _subAuthorities.Length; i++)
            {
                uint v = _subAuthorities[i];
                int at = offset + 8 + 4 * i;
                binaryForm[at] = (byte)v;
                binaryForm[at + 1] = (byte)(v >> 8);
                binaryForm[at + 2] = (byte)(v >> 16);
                binaryForm[at + 3] = (byte)(v >> 24);
            }
        }

        private static void CheckBinaryShape(ulong revision, int subAuthorityCount)
        {
            if (revision != 1)
                throw new ArgumentException("SIDs with revision other than '1' are not supported.", "binaryForm");
            if (subAuthorityCount > MaxSubAuthorities)
                throw new ArgumentException("The number of sub-authorities must not exceed 15.", "binaryForm");
        }

        private static string Format(ulong authority, uint[] subs)
        {
            var sb = new System.Text.StringBuilder("S-1-");
            sb.Append(authority.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (uint s in subs) sb.Append('-').Append(s.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static bool TryParse(string text, out ulong revision, out ulong authority, out System.Collections.Generic.List<uint> subs)
        {
            revision = 0; authority = 0; subs = new System.Collections.Generic.List<uint>();
            if (text.Length == 2)
            {
                string? aliased = Alias(text.ToUpperInvariant());
                if (aliased is null) return false;
                text = aliased;
            }
            if (text.Length < 2 || (text[0] != 'S' && text[0] != 's') || text[1] != '-') return false;
            string[] parts = text.Substring(2).Split('-');
            if (parts.Length < 3) return false; // revision, authority and at least one sub-authority
            if (!TryNumber(parts[0], out revision, out bool revOverflow) || revOverflow || revision > 255) return false;
            if (!TryNumber(parts[1], out authority, out bool authOverflow) || authOverflow || authority > 0xFFFFFFFFFFFFUL) return false;
            for (int i = 2; i < parts.Length; i++)
            {
                if (!TryNumber(parts[i], out ulong sub, out bool overflow)) return false;
                subs.Add(overflow || sub > uint.MaxValue ? uint.MaxValue : (uint)sub);
            }
            return true;
        }

        /// <summary>Decimal digits, or 0x/0X and hex digits. Nothing else, not even a sign.</summary>
        private static bool TryNumber(string s, out ulong value, out bool overflow)
        {
            value = 0; overflow = false;
            bool hex = s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X');
            ulong radix = hex ? 16UL : 10UL;
            int start = hex ? 2 : 0;
            if (s.Length == start) return false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                int d = c >= '0' && c <= '9' ? c - '0'
                    : hex && c >= 'a' && c <= 'f' ? c - 'a' + 10
                    : hex && c >= 'A' && c <= 'F' ? c - 'A' + 10
                    : -1;
                if (d < 0) return false;
                if (overflow) continue;
                if (value > (ulong.MaxValue - (ulong)d) / radix) { overflow = true; continue; }
                value = value * radix + (ulong)d;
            }
            return true;
        }

        /// <summary>The SDDL SID aliases Windows accepts on a machine that is not in a domain.</summary>
        private static string? Alias(string alias)
        {
            switch (alias)
            {
                case "AA": return "S-1-5-32-579";
                case "AC": return "S-1-15-2-1";
                case "AN": return "S-1-5-7";
                case "AO": return "S-1-5-32-548";
                case "AS": return "S-1-18-1";
                case "AU": return "S-1-5-11";
                case "BA": return "S-1-5-32-544";
                case "BG": return "S-1-5-32-546";
                case "BO": return "S-1-5-32-551";
                case "BU": return "S-1-5-32-545";
                case "CD": return "S-1-5-32-574";
                case "CG": return "S-1-3-1";
                case "CO": return "S-1-3-0";
                case "CY": return "S-1-5-32-569";
                case "ED": return "S-1-5-9";
                case "ER": return "S-1-5-32-573";
                case "ES": return "S-1-5-32-576";
                case "HA": return "S-1-5-32-578";
                case "HI": return "S-1-16-12288";
                case "HO": return "S-1-5-32-584";
                case "IS": return "S-1-5-32-568";
                case "IU": return "S-1-5-4";
                case "LS": return "S-1-5-19";
                case "LU": return "S-1-5-32-559";
                case "LW": return "S-1-16-4096";
                case "ME": return "S-1-16-8192";
                case "MP": return "S-1-16-8448";
                case "MS": return "S-1-5-32-577";
                case "MU": return "S-1-5-32-558";
                case "NO": return "S-1-5-32-556";
                case "NS": return "S-1-5-20";
                case "NU": return "S-1-5-2";
                case "OW": return "S-1-3-4";
                case "PO": return "S-1-5-32-550";
                case "PS": return "S-1-5-10";
                case "PU": return "S-1-5-32-547";
                case "RA": return "S-1-5-32-575";
                case "RC": return "S-1-5-12";
                case "RD": return "S-1-5-32-555";
                case "RE": return "S-1-5-32-552";
                case "RM": return "S-1-5-32-580";
                case "RU": return "S-1-5-32-554";
                case "SH": return "S-1-5-32-585";
                case "SI": return "S-1-16-16384";
                case "SO": return "S-1-5-32-549";
                case "SS": return "S-1-18-2";
                case "SU": return "S-1-5-6";
                case "SY": return "S-1-5-18";
                case "UD": return "S-1-5-84-0-0-0-0-0";
                case "WD": return "S-1-1-0";
                case "WR": return "S-1-5-33";
                default: return null;
            }
        }

        private static string? WellKnown(WellKnownSidType type)
        {
            switch (type)
            {
                case WellKnownSidType.NullSid: return "S-1-0-0";
                case WellKnownSidType.WorldSid: return "S-1-1-0";
                case WellKnownSidType.LocalSid: return "S-1-2-0";
                case WellKnownSidType.CreatorOwnerSid: return "S-1-3-0";
                case WellKnownSidType.CreatorGroupSid: return "S-1-3-1";
                case WellKnownSidType.CreatorOwnerServerSid: return "S-1-3-2";
                case WellKnownSidType.CreatorGroupServerSid: return "S-1-3-3";
                case WellKnownSidType.NTAuthoritySid: return "S-1-5";
                case WellKnownSidType.DialupSid: return "S-1-5-1";
                case WellKnownSidType.NetworkSid: return "S-1-5-2";
                case WellKnownSidType.BatchSid: return "S-1-5-3";
                case WellKnownSidType.InteractiveSid: return "S-1-5-4";
                case WellKnownSidType.ServiceSid: return "S-1-5-6";
                case WellKnownSidType.AnonymousSid: return "S-1-5-7";
                case WellKnownSidType.ProxySid: return "S-1-5-8";
                case WellKnownSidType.EnterpriseControllersSid: return "S-1-5-9";
                case WellKnownSidType.SelfSid: return "S-1-5-10";
                case WellKnownSidType.AuthenticatedUserSid: return "S-1-5-11";
                case WellKnownSidType.RestrictedCodeSid: return "S-1-5-12";
                case WellKnownSidType.TerminalServerSid: return "S-1-5-13";
                case WellKnownSidType.RemoteLogonIdSid: return "S-1-5-14";
                case WellKnownSidType.LocalSystemSid: return "S-1-5-18";
                case WellKnownSidType.LocalServiceSid: return "S-1-5-19";
                case WellKnownSidType.NetworkServiceSid: return "S-1-5-20";
                case WellKnownSidType.BuiltinDomainSid: return "S-1-5-32";
                case WellKnownSidType.BuiltinAdministratorsSid: return "S-1-5-32-544";
                case WellKnownSidType.BuiltinUsersSid: return "S-1-5-32-545";
                case WellKnownSidType.BuiltinGuestsSid: return "S-1-5-32-546";
                case WellKnownSidType.BuiltinPowerUsersSid: return "S-1-5-32-547";
                case WellKnownSidType.BuiltinAccountOperatorsSid: return "S-1-5-32-548";
                case WellKnownSidType.BuiltinSystemOperatorsSid: return "S-1-5-32-549";
                case WellKnownSidType.BuiltinPrintOperatorsSid: return "S-1-5-32-550";
                case WellKnownSidType.BuiltinBackupOperatorsSid: return "S-1-5-32-551";
                case WellKnownSidType.BuiltinReplicatorSid: return "S-1-5-32-552";
                case WellKnownSidType.BuiltinPreWindows2000CompatibleAccessSid: return "S-1-5-32-554";
                case WellKnownSidType.BuiltinRemoteDesktopUsersSid: return "S-1-5-32-555";
                case WellKnownSidType.BuiltinNetworkConfigurationOperatorsSid: return "S-1-5-32-556";
                default: return null;
            }
        }
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

    // Names and values as in the real enum. AL resolves a DotNet enum member by name, and BC's own
    // code was compiled against the real numbers. NetworkSid used to be 6 (CreatorGroupServerSid).
    public enum WellKnownSidType
    {
        NullSid = 0, WorldSid = 1, LocalSid = 2, CreatorOwnerSid = 3, CreatorGroupSid = 4,
        CreatorOwnerServerSid = 5, CreatorGroupServerSid = 6, NTAuthoritySid = 7, DialupSid = 8,
        NetworkSid = 9, BatchSid = 10, InteractiveSid = 11, ServiceSid = 12, AnonymousSid = 13,
        ProxySid = 14, EnterpriseControllersSid = 15, SelfSid = 16, AuthenticatedUserSid = 17,
        RestrictedCodeSid = 18, TerminalServerSid = 19, RemoteLogonIdSid = 20, LogonIdsSid = 21,
        LocalSystemSid = 22, LocalServiceSid = 23, NetworkServiceSid = 24, BuiltinDomainSid = 25,
        BuiltinAdministratorsSid = 26, BuiltinUsersSid = 27, BuiltinGuestsSid = 28,
        BuiltinPowerUsersSid = 29, BuiltinAccountOperatorsSid = 30, BuiltinSystemOperatorsSid = 31,
        BuiltinPrintOperatorsSid = 32, BuiltinBackupOperatorsSid = 33, BuiltinReplicatorSid = 34,
        BuiltinPreWindows2000CompatibleAccessSid = 35, BuiltinRemoteDesktopUsersSid = 36,
        BuiltinNetworkConfigurationOperatorsSid = 37,
    }

    // BC's ALDatabase.ALSid uses this exception type when Windows SID lookup fails.
    // On Linux the type doesn't exist in the runtime — provide a stub.
    public sealed class IdentityNotMappedException : SystemException
    {
        public IdentityNotMappedException() : base("Identity not mapped") { }
        public IdentityNotMappedException(string message) : base(message) { }
        public IdentityNotMappedException(string message, Exception innerException) : base(message, innerException) { }
        public IdentityReferenceCollection? UnmappedIdentities { get; }
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
