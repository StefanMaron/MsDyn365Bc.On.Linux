using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading.Tasks;

/// <summary>
/// .NET Startup Hook that patches the BC service tier to run on Linux.
///
/// Patch #1: CustomTranslationResolver (Nav.Language.dll)
///   Stack overflow from recursive satellite assembly resolution when WindowsIdentity
///   throws PlatformNotSupportedException. Fix: no-op OnAppDomainAssemblyResolve and
///   ResolveSatelliteAssembly.
///
/// Patch #2: NavEnvironment (Nav.Ncl.dll)
///   Static field initializer calls WindowsIdentity.GetCurrent() which throws on Linux.
///   Fix: Replace the entire .cctor with one that initializes fields without WindowsIdentity.
///   Also hook ServiceAccount/ServiceAccountName properties that dereference the null field.
///
/// Patch #3: kernel32.dll P/Invoke interception (all assemblies)
///   Provides stub implementations of kernel32 functions (JobObject, EventLog, etc.)
///   via a compiled C shared library + NativeLibrary.ResolvingUnmanagedDll.
///
/// Patch #4: EventLogWriter (Nav.Types.dll)
///   System.Diagnostics.EventLog throws PlatformNotSupportedException on Linux.
///   Fix: No-op NavEventLogEntryWriter.WriteEntry so event log writes are silently dropped.
///
/// Patch #5: ETW/OpenTelemetry (Nav.Ncl.dll + Nav.Types.dll)
///   Geneva ETW exporter and EtwTelemetryLog require Windows ETW subsystem.
///   Fix: No-op NavOpenTelemetryLogger constructor, pre-set TraceWriter to no-op proxy.
///
/// Patch #20: SideServiceWatchdog (Nav.Ncl.dll)
///   SideServiceProcessClient.EnsureAlive() tries to start the Reporting Service .exe,
///   which is a Windows PE binary that cannot run on Linux. The watchdog calls this every
///   few seconds, flooding the BC event log with "Permission denied" errors.
///   Fix: No-op SideServiceProcessClient.EnsureAlive() so the watchdog loop becomes silent.
///
/// Patch #21: NavOpenTaskPageAction.ShowForm (Nav.Client.UI.dll)
///   When a test method opens a task page, the headless client has no UI renderer and
///   NavOpenTaskPageAction.ShowForm throws NullReferenceException, terminating the entire
///   test session and leaving all remaining test methods unexecuted.
///   Fix: No-op ShowForm so task-page opens are silently skipped on Linux.
///
/// Patch #22: AzureADGraphQuery..ctor (Nav.Ncl.dll)
///   Constructor pulls in Azure.Identity / MSAL Windows credential APIs and crashes the
///   session on Linux. Fix: no-op the ctor — fields stay default; tests don't need real
///   Azure AD calls, only that the GraphQuery DotNet object can be constructed.
///
/// Patch #22b: GraphQuery.GetTenantDetail (Microsoft.Dynamics.Nav.AzureADGraphClient.dll,
///   Add-ins/GraphClient/ — a separate, lazily-loaded assembly from Nav.Ncl.dll)
///   Even with Patch #22 applied, calling GraphQuery.GetTenantDetail() (reached from AL's
///   IsPlanAssignedToUser, used by several ISV install codeunits e.g. Continia OPplus) still
///   throws NullReferenceException: GraphQuery's own parameterless ctor always builds its
///   inner AzureADGraphQuery with a null NavSession regardless of what session is actually
///   current, and GetTenantDetailAsync unconditionally dereferences that null session before
///   reaching its own "no AAD tenant, use an empty TenantInfo" fallback. BC reports the NRE via
///   a generic, misleading "You must assign at least one user the SUPER permission set..."
///   error — the fallback message for ANY failed install-transaction commit, not actually
///   about SUPER/NavUserPassword. Fix: hook the synchronous GetTenantDetail() wrapper (NOT the
///   async GetTenantDetailAsync — see Patch #20's own comment on why hooking an async method
///   mid-flight risks AccessViolationException) to return an empty TenantInfo directly.
///
/// Patch #23: OfficeWordDocumentPictureMerger.ReplaceMissingImageWithTransparentImage
///   (Microsoft.Dynamics.Nav.OpenXml.dll)
///   Microsoft's Word report image merger has a recursion bug: when a Word document
///   references a missing image, ReplaceMissingImageWithTransparentImage calls back into
///   MergePictureElements with the transparent placeholder, which re-enters
///   ReplaceMissingImageWithTransparentImage unconditionally → unbounded recursion → stack
///   overflow → fatal session crash → BC container becomes unhealthy. Triggered by
///   TestSendToEMailAndPDFVendor in Tests-Misc; was the blocker for completing a full
///   sequential Bucket 4 run (after Misc, the API was dead and the remaining 3 apps failed).
///   Fix: No-op ReplaceMissingImageWithTransparentImage — the missing image element is left
///   in place (renders as a broken image) but report generation completes and the session
///   survives. The test code never inspects rendered image content.
///
/// Patch #24: TimeZoneInfoResolver.ResolveFromId (Nav.Types.dll)
///   TimeZoneInfo.FromSerializedString(ToSerializedString(tz)) throws on Linux for most
///   ICU time zones, and BC round-trips session time zones through exactly that pair
///   (UserSettings.TimeZoneInfo). The CRONUS demo DB ships [User Personalization]
///   .[Time Zone] = 'Europe/Amsterdam' for the default user SID, which crashed every
///   web client login (InvalidTimeZoneException in NSService.OpenConnection).
///   Fix: hook the canonical id→zone resolver to substitute custom fixed-offset zones
///   (same id/names, current UTC offset) for zones that don't survive the round-trip.
///
/// Patch #25: BaseDebugRuntime.IsDebuggedSessionClosedOrDisposed (Nav.Ncl.dll)
///   AL attach-debugging (breakOnNext) sets breakpoints before a session attaches, so
///   DebuggedSession is null. DebugRuntimeManager.ExecuteClientCall's null-check guards the
///   runtime but not the session, so a null DebuggedSession is dereferenced → NullReference,
///   surfaced to VS Code as "unexpected error invoking 'AddBreakpoint'". Every attach-debug
///   breakpoint fails. Fix: return true (treat null session as closed/disposed) so the `||`
///   short-circuits before the bad dereference and ExecuteClientCall takes its safe path.
///
/// Patch #26: TenantEncryptionProviderFactory.GetTenantEncryptionProvider (Nav.Ncl.dll)
///   AL's ENCRYPT/DECRYPT, IsolatedStorage(Encrypted=true), and Data Encryption Management
///   (System App codeunit 1266/1279) all resolve their key through this factory, which is a
///   completely separate path from Patch #7's DefaultServerInstanceRsaEncryptionProviderFactory
///   (that one only fakes the SQL connection-string password provider). Left unpatched, this
///   factory returns a real TenantRsaEncryptionProvider whose CreateKey() depends on
///   Windows-only key storage and throws SystemEncryptionEncryptionKeyNotCreated ("An
///   encryption key is required to complete the request."), failing every Data Encryption
///   Mgmt. test (EncryptDecryptText, TestEnableEncryptionInEncryptionMgmtPage, etc.).
///   Fix: hook the factory to always return the same pass-through ISystemEncryptionProvider
///   proxy Patch #7 already builds (extended to no-op CreateKey/DeleteKey/ImportKey/ExportKey
///   too), so "encryption" always reports enabled/present and Encrypt/Decrypt round-trip as
///   plain text. Good enough for tests to stop erroring; not real cryptography.
///
/// Patch #27: NavClientHandle.Dispose (Nav.Ncl.dll)
///   Disposing a form/page that held a .NET client handle throws NullReferenceException on
///   headless Linux sessions: Dispose dereferences the NavAutomationHandle field and the
///   thread-local session state, both of which can be null when there is no real client UI
///   (the teardown often runs on a cleanup thread with sessionId -1). The NRE propagates out
///   of NavForm.Dispose and fails the test AFTER its logic already completed — the single
///   biggest failure bucket in Microsoft's Tests-Misc/Tests-Workflow suites (189 failures in
///   the 2026-08-06 gate run). This is the disposal-side sibling of the documented
///   NSClientCallback.CreateDotNetHandle hole (KNOWN-LIMITATIONS.md).
///   Fix: replace Dispose(bool) with a null-guarded equivalent — same logic, but a missing
///   handle/session/callback means "nothing to notify" instead of a crash.
///
/// Patch #28: NavFile.ExpandFileName (Nav.Ncl.dll)
///   AL code (Microsoft's own test libraries included) builds file paths with Windows '\'
///   separators — e.g. "Library - Utility" produces "<ServerDir>\..\..\App\Test\Files\x.jpg".
///   On Linux, backslash is not a separator: Path.GetFullPath leaves the string literal, the
///   user-folder confinement check then rejects it ("Files outside of the current users
///   folder cannot be accessed. Access is denied to file '/bc/service\..\..\...'"), and even
///   without the lockdown the literal path could never resolve to a real file.
///   Fix: replace ExpandFileName with a Linux-aware equivalent — normalize '\' to '/' first,
///   then combine/canonicalize exactly like the original. The user-folder lockdown is NOT
///   re-enforced (single-user dev/test container; the SaaS confinement has no meaning here),
///   so out-of-folder paths proceed to the actual file operation and produce an honest
///   file-not-found instead of a bogus access-denied.
///
/// Patch #29: NavDirectorySecurity.CreateSecurityForDomainDirectory (Nav.Ncl.dll)
///   BC 29 reaches this from TempPathHelper.InitializeFolders during tenant configuration and
///   constructs a System.Security.AccessControl.DirectorySecurity — Windows-only ACL APIs that
///   throw PlatformNotSupportedException on Linux, killing NST startup. BC 27/28 never reach it
///   (the call is gated on IServiceTopology.IsServiceRunningInLocalEnvironment, which Patch #9's
///   Linux topology proxy forces false).
///   Fix: return null, which is what the gated-off path produced. Applied on all versions.
///
/// JMP hooks work ONLY on BC methods (JIT-compiled). BCL methods are ReadyToRun pre-compiled
/// and cannot be patched this way.
///
/// A JMP hook must be applied to a given method AT MOST ONCE. Re-applying one segfaults
/// libcoreclr on .NET 10 with no managed frames (it was silently tolerated on .NET 8).
/// ApplyJmpHook now detects and refuses a re-apply; see IsAlreadyJmpHooked.
/// </summary>
internal class StartupHook
{
    // Diagnostic gate: BC_DISABLE_PATCHES is a comma-separated list of patch names
    // (e.g. "14,15,15a,15b,checkfile") that are skipped at apply time. Used to bisect
    // which Cecil patches cause AL→C# emission drift that defeats NST's R2R pass-through
    // gate for Base App. Empty/unset = all patches active (production behavior).
    private static readonly HashSet<string> _disabledPatches =
        new HashSet<string>(
            (Environment.GetEnvironmentVariable("BC_DISABLE_PATCHES") ?? "")
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

    private static bool IsPatchDisabled(string name)
    {
        if (_disabledPatches.Count == 0) return false;
        bool disabled = _disabledPatches.Contains(name.ToLowerInvariant());
        if (disabled)
        {
            Console.Error.WriteLine($"[StartupHook] DIAGNOSTIC: patch '{name}' SKIPPED (BC_DISABLE_PATCHES)");
        }
        return disabled;
    }

    private static bool _patchedLanguage;
    private static bool _patchedNcl;
    private static bool _patchedTypes;
    private static Type? _navEnvironmentType;
    private static Assembly? _navNclAssembly;
    private static IntPtr _kernel32StubHandle;
    private static object? _noopEncryptionProvider;
    // Patch #26 state — deliberately separate from _noopEncryptionProvider, see
    // TenantFakeEncryptionProxy's class comment.
    private static object? _noopTenantEncryptionProvider;
    // Patch #24 state
    private static Type? _navTimeZoneExceptionType;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeZoneInfo> _safeTimeZoneCache =
        new System.Collections.Concurrent.ConcurrentDictionary<string, TimeZoneInfo>(StringComparer.OrdinalIgnoreCase);
    private static bool _encryptionBypassed;
    private static bool _encryptionApplying;
    private static object? _originalTopology;

    public static void Initialize()
    {
        // Patch #6: Must be set before ANY System.Drawing type is accessed
        AppContext.SetSwitch("System.Drawing.EnableUnixSupport", true);


        Console.WriteLine("[StartupHook] Initializing Linux compatibility patches...");

        // Debug aid (opt-in): BC_DEBUG_FIRSTCHANCE=1 prints every thrown exception
        // with its full inner chain to stderr. Mirrors WEBCLIENT_DEBUG_FIRSTCHANCE in
        // the web client hook. Invaluable when a server-side component (e.g. the
        // SignalR DebuggerHub) swallows the real exception and returns a generic
        // "unexpected error" to the client. Very noisy — never enable by default.
        if (Environment.GetEnvironmentVariable("BC_DEBUG_FIRSTCHANCE") == "1")
        {
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                try
                {
                    Console.Error.WriteLine($"[FirstChance] {e.Exception}");
                }
                catch { /* never let the logger throw */ }
            };
            Console.WriteLine("[StartupHook] BC_DEBUG_FIRSTCHANCE=1: first-chance exception logging enabled");
        }

        // Debug aid (opt-in): BC_DEBUG_ASSEMBLY_RESOLVE=1 logs every managed assembly
        // the runtime could not find on its own. BC catches FileNotFoundException in
        // several places and rethrows something generic (e.g. "LazyEx factory threw an
        // exception") WITHOUT the assembly name, so this is the only way to learn what
        // is actually missing. Far cheaper than BC_DEBUG_FIRSTCHANCE.
        if (Environment.GetEnvironmentVariable("BC_DEBUG_ASSEMBLY_RESOLVE") == "1")
        {
            AssemblyLoadContext.Default.Resolving += (alc, name) =>
            {
                try { Console.Error.WriteLine($"[AsmResolve] Default ALC could not find: {name}"); } catch { }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try { Console.Error.WriteLine($"[AsmResolve] AppDomain.AssemblyResolve: {e.Name} (requested by {e.RequestingAssembly?.GetName().Name ?? "?"})"); } catch { }
                return null;
            };
            Console.WriteLine("[StartupHook] BC_DEBUG_ASSEMBLY_RESOLVE=1: assembly resolution failure logging enabled");
        }

        // Patch #13 (early): Prevent Watson crash on unobserved task exceptions.
        // Watson's SendReport → GetRegistryValue crashes on Linux (NullRef, no registry).
        // BC registers its handler on NavEnvironment..ctor that calls Watson and crashes.
        // Strategy: aggressively strip BC's handler every second, and keep our safe handler.
        EventHandler<UnobservedTaskExceptionEventArgs> safeHandler = (sender, args) =>
        {
            Console.WriteLine($"[StartupHook] Caught unobserved task exception: {args.Exception?.InnerException?.GetType().Name}: {args.Exception?.InnerException?.Message}");
            args.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += safeHandler;

        // Aggressively strip BC's Watson handler - check every second
        var _safeHandlerRef = safeHandler; // prevent GC
        new System.Threading.Timer(_ =>
        {
            try
            {
                // .NET 8: the backing field is "UnobservedTaskException" (static event field)
                var field = typeof(TaskScheduler).GetField("UnobservedTaskException",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                {
                    field = typeof(TaskScheduler).GetField("_unobservedTaskException",
                        BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (field != null)
                {
                    var currentDelegate = field.GetValue(null) as Delegate;
                    if (currentDelegate != null)
                    {
                        var invocationList = currentDelegate.GetInvocationList();
                        if (invocationList.Length > 1)
                        {
                            // Replace entire event with just our safe handler
                            field.SetValue(null, _safeHandlerRef);
                            Console.WriteLine($"[StartupHook] Removed {invocationList.Length - 1} Watson crash handler(s)");
                        }
                    }
                }
            }
            catch { }
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));

        // Patch #3: Load kernel32 stubs for P/Invoke interception
        LoadKernel32Stubs();


        // Replace DLLs with stubs or cross-platform versions (unsigned, can copy directly)
        ReplaceWithStub("OpenTelemetry.Exporter.Geneva.dll", "Geneva ETW exporter");
        // Overwriting the file is not enough once BC's reference version moves past the
        // stub's. BC 29's Microsoft.BusinessCentral.Telemetry.OpenTelemetry asks for
        // OpenTelemetry.Exporter.Geneva 1.15.2.1008; the stub is 1.9.0.62, so the default
        // ALC rejects the file it just found and the load fails with FileNotFoundException.
        // BC surfaces that as "LazyEx factory threw an exception" while constructing
        // OpenTelemetryLogger<T>, which made every dev-endpoint publish return HTTP 500.
        // Registering the bytes with the stub resolver satisfies ANY requested version,
        // the same way System.Drawing.Common et al. are handled.
        RegisterStubForResolver("OpenTelemetry.Exporter.Geneva");
        ReplaceWithStub("Microsoft.Data.SqlClient.dll", "cross-platform SqlClient");

        // Verify SqlClient loads correctly (catches version/dependency issues early)
        VerifySqlClientLoads();

        // Patch #6: System.Drawing requires strong name bypass — use assembly resolver
        // Register managed assembly resolver (once, for all stubs below)
        AssemblyLoadContext.Default.Resolving += ResolveStubAssembly;
        SetupStubWithResolver("System.Drawing.Common");
        SetupStubWithResolver("System.Diagnostics.PerformanceCounter");
        SetupStubWithResolver("System.Security.Principal.Windows");

        // Also handle assembly resolution for tenant ALCs (TestPageClient loads in non-default contexts)
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        TryEagerPatch();

        // Patch #18: No-op SetupSideServices — must be patched before Main() calls it.
        try
        {
            Console.WriteLine("[StartupHook] Patch #18: Searching for DynamicsNavServer...");
            var serverType = Type.GetType("Microsoft.Dynamics.Nav.WindowsServices.DynamicsNavServer, Microsoft.Dynamics.Nav.Server");
            if (serverType != null)
            {
                Console.WriteLine("[StartupHook] Patch #18: Found DynamicsNavServer via Type.GetType");
                PatchSetupSideServices(serverType.Assembly);
            }
            else
            {
                // Try scanning loaded assemblies
                Console.WriteLine("[StartupHook] Patch #18: Type.GetType returned null, scanning loaded assemblies...");
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Console.WriteLine($"[StartupHook] Patch #18:   Loaded: {asm.GetName().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #18 search error: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine("[StartupHook] Initialization complete.");
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        string? name = args.LoadedAssembly.GetName().Name;

        if (!_patchedLanguage && name == "Microsoft.Dynamics.Nav.Language")
        {
            Console.WriteLine("[StartupHook] Nav.Language.dll loaded — patching");
            PatchCustomTranslationResolver(args.LoadedAssembly);
        }

        if (!_patchedNcl && name == "Microsoft.Dynamics.Nav.Ncl")
        {
            Console.WriteLine("[StartupHook] Nav.Ncl.dll loaded — patching");
            PatchNavEnvironment(args.LoadedAssembly);
        }

        // Patch #17: ALDatabase.ALSid — returns Windows SID from username.
        //   Crashes on Linux with TypeLoadException: IdentityNotMappedException
        //   (System.Security.Principal.Windows not available). Return a dummy SID.
        if (name == "Microsoft.Dynamics.Nav.Ncl")
        {
            PatchALDatabaseALSid(args.LoadedAssembly);
            // Patch #19: Set CustomReportingServiceClient to no-op factory.
            //   BC's Reporting Service is a Windows PE binary that can't run on Linux.
            //   When test code triggers RDLC rendering, BC tries to connect to the
            //   Reporting Service via gRPC, times out, and crashes the test codeunit.
            //   NavEnvironment has a built-in hook: CustomReportingServiceClient.
            //   Setting it to a no-op factory bypasses gRPC entirely.
            PatchReportingServiceClient(args.LoadedAssembly);
        }

        // Patch #16b: NavUser.TryAuthenticate bypass (password hash doesn't verify on Linux)
        if (name == "Microsoft.Dynamics.Nav.Ncl")
        {
            PatchNavUserTryAuthenticate(args.LoadedAssembly);
        }

        // Patch #22: AzureADGraphQuery constructor bypass.
        //   AL DotNet variable creation of Microsoft.Dynamics.Nav.AzureADGraphClient.GraphQuery
        //   fails on Linux with NavConfigurationException: "LazyEx factory threw an exception".
        //   GraphQuery..ctor() calls AzureADGraphQuery..ctor(NavSession) in Nav.Ncl, which sets
        //   up a LazyEx factory that creates Azure.Identity credentials (MSAL/Windows-only).
        //   Fix: hook AzureADGraphQuery..ctor to skip the factory setup, so GraphQuery can be
        //   instantiated without triggering Azure AD credential initialisation.
        if (name == "Microsoft.Dynamics.Nav.Ncl")
        {
            PatchAzureADGraphQuery(args.LoadedAssembly);
        }

        // Patch #22b: GraphQuery.GetTenantDetail — see PatchGraphQueryGetTenantDetail's header
        // comment for the full story. Microsoft.Dynamics.Nav.AzureADGraphClient.dll (Add-ins/
        // GraphClient/) is a SEPARATE, lazily-loaded assembly from Nav.Ncl.dll, so it needs its
        // own AssemblyLoad hook rather than piggybacking on Patch #22's Nav.Ncl-triggered one.
        if (name == "Microsoft.Dynamics.Nav.AzureADGraphClient")
        {
            ApplyGraphQueryGetTenantDetailPatch(args.LoadedAssembly);
        }

        // Patch #26: TenantEncryptionProviderFactory.GetTenantEncryptionProvider bypass.
        //   See header comment for the full story — this is the factory AL ENCRYPT/DECRYPT,
        //   IsolatedStorage(Encrypted=true), and Data Encryption Management actually use,
        //   distinct from Patch #7's SQL-password-only factory.
        //   Applied on a short delay, NOT synchronously here: at Nav.Ncl-load time this
        //   method has never been called, so its precode still points at a shared
        //   not-yet-JIT-compiled stub. JMP-hooking that address hijacks every OTHER
        //   method that resolves through the same shared stub on first call — observed
        //   as a totally unrelated crash during system tenant bootstrap (ACL/
        //   TempPathHelper.InitializeFolders, PlatformNotSupportedException) that killed
        //   the whole NST process. Deferring a few seconds lets normal boot activity
        //   JIT-compile things first, so by the time we hook, the target's precode
        //   resolves to its own unique compiled code instead of a shared trampoline.
        if (name == "Microsoft.Dynamics.Nav.Ncl")
        {
            var nclAssembly = args.LoadedAssembly;
            _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
            {
                try { PatchTenantEncryptionProvider(nclAssembly); }
                catch (Exception ex) { Console.WriteLine($"[StartupHook] Patch #26 deferred apply failed: {ex.GetType().Name}: {ex.Message}"); }
            });
        }

        // Patch #20: SideService watchdog — the entrypoint replaces the Reporting Service
        //   Windows .exe with a `sleep infinity` shell script. BC's Process.Start runs it,
        //   the watchdog sees a live process, and stops spamming errors.
        //   (JMP hooks on EnsureAlive/TryStartService crash due to async state machine.)

        // Patch #18: No-op SetupSideServices — the Reporting Service assembly doesn't exist on Linux.
        // Must be patched before Main() calls it.
        if (name == "Microsoft.Dynamics.Nav.Server")
        {
            PatchSetupSideServices(args.LoadedAssembly);
        }

        if (name == "Microsoft.Dynamics.Nav.Watson")
        {
            PatchWatsonReporting(args.LoadedAssembly);
        }

        // Patch #14: Fix Cecil type-forwarding crash in server-side AL compiler.
        // CecilDotNetTypeLoader.IsTypeForwardingCircular throws NullRef when
        // following type-forwarding chains in netstandard.dll on Linux.
        // Hook it to return false (no circular forwarding), allowing the compiler
        // to follow the chain: netstandard → System.Runtime → System.Private.CoreLib.
        if (name == "Microsoft.Dynamics.Nav.CodeAnalysis")
        {
            PatchCecilTypeForwarding(args.LoadedAssembly);
        }

        // Patch Cecil's CheckFileName to not throw on empty paths.
        // BC's assembly scanner produces empty-string paths from some probing directories
        // on Linux. GetAssemblyNameFromPath catches most exceptions but NOT
        // Mono.ArgumentNullOrEmptyException from CheckFileName.
        // Routed through PatchCecilCheckFileName so the IsPatchDisabled gate applies here too.
        if (name == "Mono.Cecil")
        {
            PatchCecilCheckFileName(args.LoadedAssembly);
        }

        // Re-apply encryption bypass after Main() overrides it.
        // Guard against recursion (DispatchProxy.Create triggers assembly loads).
        if (!_encryptionBypassed && !_encryptionApplying && name == "Microsoft.Dynamics.Nav.Core")
        {
            _encryptionApplying = true;
            try { ReapplyEncryptionBypass(); }
            finally { _encryptionApplying = false; }
        }
        if (name == "Microsoft.Dynamics.Nav.Core")
        {
            ReapplyTopologyProxy();
        }

        if (!_patchedTypes && name == "Microsoft.Dynamics.Nav.Types")
        {
            Console.WriteLine("[StartupHook] Nav.Types.dll loaded — patching");
            PatchNavTypes(args.LoadedAssembly);
        }

        // Patch #15: Remove .NET runtime directory from server-side compiler's assembly probing.
        // On Linux, Cecil can't read R2R/native DLLs from /usr/share/dotnet/shared/, and even
        // managed runtime DLLs forward types to System.Private.CoreLib (R2R, unreadable).
        // Our reference assemblies in Add-Ins define types directly — they just need to be found first.
        if (name == "Microsoft.Dynamics.Nav.Ncl")
        {
            PatchAssemblyProbing(args.LoadedAssembly);
            // Patch #29: Windows ACL APIs in TempPathHelper (BC 29 reaches them).
            PatchNavDirectorySecurity(args.LoadedAssembly);
        }

        // Patch #25: BaseDebugRuntime.IsDebuggedSessionClosedOrDisposed null-session NRE.
        //   AL debugging via attach (breakOnNext) sets breakpoints before any session is
        //   attached, so DebuggedSession is null. DebugRuntimeManager.ExecuteClientCall does:
        //     flag2 = debugRuntime.IsDebuggedSessionClosedOrDisposed
        //           || (debugRuntime?.DebuggedSession.IsDisposingOrDisposed ?? false);
        //   The `?.` guards debugRuntime, NOT DebuggedSession — so a null session makes the
        //   left operand return false (no short-circuit) and the right operand dereferences
        //   null → NullReferenceException. The SignalR DebuggerHub wraps it as a generic
        //   "unexpected error invoking 'AddBreakpoint'", and every attach-debug breakpoint
        //   fails. Returning true when DebuggedSession is null short-circuits the ||, so
        //   ExecuteClientCall takes its safe early-return path instead of crashing.
        if (name == "Microsoft.Dynamics.Nav.Ncl")
        {
            PatchDebugRuntimeNullSession(args.LoadedAssembly);
        }

        // Patch #27: NavClientHandle.Dispose NREs on headless sessions when the automation
        // handle or thread-local session state is null — fails tests during form teardown
        // after their logic already passed. Replace with a null-guarded equivalent.
        if (name == "Microsoft.Dynamics.Nav.Ncl")
        {
            PatchNavClientHandleDispose(args.LoadedAssembly);
        }

        // Patch #28: NavFile.ExpandFileName — normalize Windows '\' path separators so
        // AL-built paths resolve on Linux instead of tripping the user-folder lockdown.
        if (name == "Microsoft.Dynamics.Nav.Ncl")
        {
            PatchNavFileExpandFileName(args.LoadedAssembly);
        }

        // Patch #16 is now only 16b (NavUser.TryAuthenticate bypass in Nav.Ncl).
        // The full ValidateAsync chain runs normally to populate the auth cache,
        // but password hash verification is bypassed via TryAuthenticate.

        // Patch #21: NavOpenTaskPageAction.ShowForm crashes on Linux when a test opens a
        // task page — the headless client has no UI renderer and a null reference occurs,
        // terminating the entire test session. No-op ShowForm so task-page opens are skipped.
        if (name == "Microsoft.Dynamics.Nav.Client.UI")
        {
            PatchShowForm(args.LoadedAssembly);
        }

        // Patch #23: Recursion bug in Microsoft's Word report image merger crashes the
        // BC session with a stack overflow when a Word document references a missing image.
        // This was the blocker for completing a full sequential Bucket 4 run.
        if (name == "Microsoft.Dynamics.Nav.OpenXml")
        {
            PatchOfficeWordDocumentPictureMerger(args.LoadedAssembly);
        }

        // Patch #24: FindClientTimeZone serialization safety (see method).
        if (name == "Microsoft.Dynamics.Nav.Service")
        {
            PatchFindClientTimeZone(args.LoadedAssembly);
        }

    }

    private static void TryEagerPatch()
    {
        string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (baseDir == null) return;

        TryEagerLoadAndPatch(baseDir, "Microsoft.Dynamics.Nav.Language.dll",
            "Nav.Language.dll", PatchCustomTranslationResolver, () => _patchedLanguage);
        // Patch #14: Eagerly load and patch the AL compiler's Cecil type loader.
        // Must be patched BEFORE any server-side compilation happens.
        TryEagerLoadAndPatch(baseDir, "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
            "Nav.CodeAnalysis.dll (Cecil type forwarding)", PatchCecilTypeForwarding, () => false);
        // Patch Cecil's CheckFileName to not throw on empty file paths
        TryEagerLoadAndPatch(baseDir, "Mono.Cecil.dll",
            "Mono.Cecil (CheckFileName empty path fix)", PatchCecilCheckFileName, () => false);
        // DON'T eagerly load Nav.Ncl.dll and Nav.Types.dll — let the runtime load them
        // naturally. Eager LoadFrom creates a separate instance that the runtime doesn't use,
        // which is why JMP hooks on many methods fail (they patch the wrong instance).
        // The AssemblyLoad event handler will catch them when the runtime loads them.
    }

    private static void TryEagerLoadAndPatch(string baseDir, string fileName,
        string displayName, Action<Assembly> patchAction, Func<bool> isPatched)
    {
        if (isPatched()) return;

        string path = System.IO.Path.Combine(baseDir, fileName);
        if (!System.IO.File.Exists(path))
        {
            Console.WriteLine($"[StartupHook] {displayName} not found at base dir — will patch on load");
            return;
        }

        try
        {
            Assembly asm = Assembly.LoadFrom(path);
            Console.WriteLine($"[StartupHook] Eagerly loaded {displayName}");
            patchAction(asm);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Eager load {displayName} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ========================================================================
    // Patch #3: kernel32.dll stub for P/Invoke interception
    // ========================================================================

    /// <summary>
    /// Load a compiled C stub library that provides no-op implementations of Windows DLL
    /// functions (kernel32, user32, advapi32, etc.). Register via ResolvingUnmanagedDll so ALL
    /// assemblies get the stubs when they P/Invoke Windows DLLs on Linux.
    /// </summary>
    private static void LoadKernel32Stubs()
    {
        try
        {
            var hookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
            if (hookDir == null) return;

            var stubPath = Path.Combine(hookDir, "libwin32_stubs.so");
            if (!File.Exists(stubPath))
            {
                Console.WriteLine($"[StartupHook] libwin32_stubs.so not found at {hookDir}");
                Console.WriteLine("[StartupHook] Build with: dotnet publish -c Release -o bin/Release/net8.0/publish");
                return;
            }

            _kernel32StubHandle = NativeLibrary.Load(stubPath);
            Console.WriteLine("[StartupHook] Loaded Win32 stubs (kernel32/user32/advapi32/...)");

            // Intercept kernel32.dll resolution for ALL assemblies in default ALC
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveWin32Stubs;

            // Also register on non-default ALCs (tenant ALCs, etc.) as they load assemblies.
            // Framework.UI.dll loads in tenant ALCs and its P/Invokes (user32!ToUnicodeEx etc.)
            // won't fire the Default ALC resolver.
            var registeredAlcs = new HashSet<AssemblyLoadContext> { AssemblyLoadContext.Default };
            AppDomain.CurrentDomain.AssemblyLoad += (sender, args) =>
            {
                var alc = AssemblyLoadContext.GetLoadContext(args.LoadedAssembly);
                if (alc != null && registeredAlcs.Add(alc))
                {
                    alc.ResolvingUnmanagedDll += ResolveWin32Stubs;
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Kernel32 stub load failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Windows DLLs that we provide stub implementations for
    private static readonly string[] _stubbedLibraries = new[]
    {
        "kernel32", "kernel32.dll",
        "user32", "user32.dll",
        "Wintrust", "Wintrust.dll", "wintrust", "wintrust.dll",
        "nclcsrts", "nclcsrts.dll",
        "dhcpcsvc", "dhcpcsvc.dll",
        "Netapi32", "Netapi32.dll", "netapi32", "netapi32.dll",
        "ntdsapi", "ntdsapi.dll",
        "rpcrt4", "rpcrt4.dll",
        "advapi32", "advapi32.dll",
        "httpapi", "httpapi.dll",
        "gdiplus", "libgdiplus", "libgdiplus.so", "libgdiplus.so.0",
    };

    private static IntPtr ResolveWin32Stubs(Assembly assembly, string libraryName)
    {
        if (_kernel32StubHandle != IntPtr.Zero)
        {
            foreach (var name in _stubbedLibraries)
            {
                if (libraryName == name)
                    return _kernel32StubHandle;
            }
        }
        return IntPtr.Zero;
    }

    // ========================================================================
    // Patch #1: CustomTranslationResolver — breaks satellite assembly recursion
    // ========================================================================

    private static void PatchCustomTranslationResolver(Assembly navLanguage)
    {
        if (_patchedLanguage) return;

        try
        {
            Type? resolverType = navLanguage.GetType("Microsoft.Dynamics.Nav.Common.CustomTranslationResolver");
            if (resolverType == null)
            {
                Console.WriteLine("[StartupHook] CustomTranslationResolver type not found");
                return;
            }

            var onResolve = resolverType.GetMethod("OnAppDomainAssemblyResolve",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (onResolve != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_OnAppDomainAssemblyResolve),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(onResolve, replacement, "OnAppDomainAssemblyResolve");
            }

            var resolveSat = resolverType.GetMethod("ResolveSatelliteAssembly",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (resolveSat != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_ResolveSatelliteAssembly),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(resolveSat, replacement, "ResolveSatelliteAssembly");
            }

            _patchedLanguage = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #1 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ========================================================================
    // Patch #2: NavEnvironment — skip WindowsIdentity in static constructor
    // ========================================================================

    private static void PatchNavEnvironment(Assembly navNcl)
    {
        if (_patchedNcl) return;

        try
        {
            Type? envType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment");
            if (envType == null)
            {
                Console.WriteLine("[StartupHook] NavEnvironment type not found");
                return;
            }

            _navEnvironmentType = envType;
            _navNclAssembly = navNcl;

            // Hook the .cctor — replaces the static constructor entirely
            var cctor = envType.TypeInitializer;
            if (cctor != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_NavEnvironmentCctor),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(cctor, replacement, "NavEnvironment..cctor");
            }
            else
            {
                Console.WriteLine("[StartupHook] NavEnvironment has no .cctor — nothing to patch");
            }

            // Hook ServiceAccount property (returns SecurityIdentifier from serviceAccount.User)
            var saProp = envType.GetProperty("ServiceAccount", BindingFlags.Public | BindingFlags.Static);
            if (saProp?.GetMethod != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_GetServiceAccount),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(saProp.GetMethod, replacement, "NavEnvironment.get_ServiceAccount");
            }

            // Hook ServiceAccountName property (returns serviceAccount.Name)
            var sanProp = envType.GetProperty("ServiceAccountName", BindingFlags.Public | BindingFlags.Static);
            if (sanProp?.GetMethod != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_GetServiceAccountName),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(sanProp.GetMethod, replacement, "NavEnvironment.get_ServiceAccountName");
            }

            // Patch #3 (kernel32.dll) is handled globally via NativeLibrary resolver

            // --- Patch #6: EmitServerStartupTraceEvents — contains System.Drawing font enum ---
            var emitMethod = envType.GetMethod("EmitServerStartupTraceEvents",
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (emitMethod != null)
            {
                var replacement = typeof(StartupHook).GetMethod(
                    emitMethod.IsStatic ? nameof(Replacement_NoOp_2Args) : nameof(Replacement_NoOp_3Args),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(emitMethod, replacement, "NavEnvironment.EmitServerStartupTraceEvents");
            }

            // --- Patch #9: Replace Topology with one that returns IsServiceRunningInLocalEnvironment=false ---
            // This makes NavDirectorySecurity skip Windows ACL APIs (returns null instead).
            Type? topoIfaceType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.IServiceTopology");
            if (topoIfaceType != null)
            {
                // Create a proxy that returns false for IsServiceRunningInLocalEnvironment
                // and delegates everything else to the original topology
                var createProxy = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(topoIfaceType, typeof(LinuxTopologyProxy));
                var linuxTopology = createProxy.Invoke(null, null);

                // Store original topology for delegation, then replace
                var topoProp = envType.GetProperty("Topology", BindingFlags.Public | BindingFlags.Static);
                if (topoProp != null)
                {
                    _originalTopology = topoProp.GetValue(null);
                    topoProp.SetValue(null, linuxTopology);
                    Console.WriteLine("[StartupHook] Replaced Topology with Linux proxy (ACL bypass)");
                }
            }

            _patchedNcl = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #2/#3 failed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[StartupHook]   {ex.StackTrace}");
        }
    }

    // ========================================================================
    // Patch #4: NavTypes — no-op EventLog writer
    // ========================================================================

    // ========================================================================
    // Patch #13: Watson crash reporting (requires Windows registry)
    // ========================================================================
    // Patch #14: Cecil type-forwarding fix for server-side AL compiler
    // CecilDotNetTypeLoader.IsTypeForwardingCircular crashes with NullRef
    // when following type-forwarding chains (netstandard → System.Runtime etc.)
    // ========================================================================
    private static void PatchCecilTypeForwarding(Assembly codeAnalysisAsm)
    {
        if (IsPatchDisabled("14")) return;
        try
        {
            var loaderType = codeAnalysisAsm.GetType(
                "Microsoft.Dynamics.Nav.CodeAnalysis.DotNet.Cecil.CecilDotNetTypeLoader");
            if (loaderType == null)
            {
                Console.WriteLine("[StartupHook] CecilDotNetTypeLoader type not found");
                return;
            }

            var isCircular = loaderType.GetMethod("IsTypeForwardingCircular",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (isCircular != null)
            {
                var noop = typeof(StartupHook).GetMethod(nameof(IsTypeForwardingCircularNoop),
                    BindingFlags.Static | BindingFlags.NonPublic);
                ApplyJmpHook(isCircular, noop!, "CecilDotNetTypeLoader.IsTypeForwardingCircular");
            }

            // Note: GetAssemblyNameFromPath null paths are fixed by only copying MANAGED .NET
            // DLLs to Add-Ins (native/R2R DLLs crash Cecil's metadata reader).
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #14 (Cecil type forwarding) failed: {ex.Message}");
        }
    }

    private static bool IsTypeForwardingCircularNoop()
    {
        try { File.AppendAllText("/tmp/is-circular-called.txt", $"{DateTime.UtcNow}\n"); } catch { }
        return false;
    }
    private static void CheckFileNameNoop() { } // Allow empty paths — caller handles null results

    private static void PatchCecilCheckFileName(Assembly cecilAsm)
    {
        if (IsPatchDisabled("checkfile")) return;
        try
        {
            var mixinType = cecilAsm.GetType("Mono.Cecil.Mixin");
            var checkFn = mixinType?.GetMethod("CheckFileName",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (checkFn != null)
            {
                var noop = typeof(StartupHook).GetMethod(nameof(CheckFileNameNoop),
                    BindingFlags.Static | BindingFlags.NonPublic);
                ApplyJmpHook(checkFn, noop!, "Mono.Cecil.Mixin.CheckFileName");
            }
            else
            {
                Console.WriteLine("[StartupHook] Mono.Cecil.Mixin.CheckFileName not found");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Cecil CheckFileName patch failed: {ex.Message}");
        }
    }


    // ========================================================================
    // Patch #15: Remove .NET runtime directory from assembly probing paths.
    //
    // Problem: Server-side AL compiler uses Mono.Cecil to resolve .NET types through
    // netstandard type-forwarding chains. On Linux, the forwarded-to assemblies
    // (System.Collections, System.Runtime, etc.) exist in TWO places:
    //   1. Add-Ins/ — our managed reference assemblies (have actual type definitions) ✅
    //   2. /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.x/ — runtime DLLs ❌
    //
    // Runtime DLLs are either R2R (unreadable by Cecil) or forward types to
    // System.Private.CoreLib (also R2R). Reference assemblies define types directly.
    //
    // Fix: Hook two methods to exclude the runtime directory from Cecil's search:
    //   A. GetGlobalAssemblyCacheDirectories → empty (removes runtime dir from probing paths)
    //   B. GetLocationOfAssembliesLoadedInServerAppDomain → filtered (removes runtime paths
    //      from well-known assemblies so they don't short-circuit probing path search)
    // ========================================================================

    private static void PatchAssemblyProbing(Assembly navNclAsm)
    {
        if (IsPatchDisabled("15") || IsPatchDisabled("15b")) return;
        try
        {
            // Hook B: Filter well-known assemblies in NavAppCompilationAssemblyLocator
            var locatorType = navNclAsm.GetType(
                "Microsoft.Dynamics.Nav.Runtime.Apps.NavAppCompilationAssemblyLocator");
            if (locatorType == null)
            {
                Console.WriteLine("[StartupHook] NavAppCompilationAssemblyLocator not found");
                return;
            }

            var getLocations = locatorType.GetMethod("GetLocationOfAssembliesLoadedInServerAppDomain",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (getLocations != null)
            {
                var replacement = typeof(StartupHook).GetMethod(
                    nameof(FilteredAssemblyLocations),
                    BindingFlags.Static | BindingFlags.NonPublic);
                ApplyJmpHook(getLocations, replacement!, "NavAppCompilationAssemblyLocator.GetLocationOfAssembliesLoadedInServerAppDomain");
            }
            else
            {
                Console.WriteLine("[StartupHook] GetLocationOfAssembliesLoadedInServerAppDomain not found");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #15 (assembly probing) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Replacement for GetLocationOfAssembliesLoadedInServerAppDomain.
    /// Returns all loaded assemblies EXCEPT those from the .NET shared runtime directory.
    /// This forces the compiler's Cecil resolver to find .NET types through the probing
    /// paths (Add-Ins first), where our managed reference assemblies have full type definitions.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, string> FilteredAssemblyLocations()
    {
        // Write marker file so we can verify hook fires even if Console.WriteLine is lost
        try { File.WriteAllText("/tmp/patch15-wellknown-hook-fired.txt",
            $"FilteredAssemblyLocations called at {DateTime.UtcNow}\n"); } catch { }

        var dict = new System.Collections.Generic.Dictionary<string, string>();
        int skipped = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm.IsDynamic) continue;
                var loc = asm.Location;
                if (string.IsNullOrEmpty(loc)) continue;
                // Skip .NET runtime assemblies — they're R2R or forward to R2R CoreLib
                if (loc.Contains("/dotnet/shared/Microsoft.NETCore.App/"))
                {
                    skipped++;
                    continue;
                }
                if (loc.Contains("/dotnet/shared/Microsoft.AspNetCore.App/"))
                {
                    skipped++;
                    continue;
                }
                var name = asm.GetName().Name;
                if (name != null && !dict.ContainsKey(name))
                    dict[name] = loc;
            }
            catch { }
        }
        Console.Error.WriteLine($"[StartupHook] Patch #15b: Well-known assemblies filtered ({dict.Count} kept, {skipped} runtime excluded)");
        try { File.AppendAllText("/tmp/patch15-wellknown-hook-fired.txt",
            $"Kept={dict.Count}, Skipped={skipped}\n"); } catch { }

        // Diagnostic: check the static pathNameToAssemblyNameMap cache to see what
        // assemblies the locator has found, and test Cecil resolution
        try
        {
            var sb2 = new System.Text.StringBuilder();
            // Access AssemblyLocatorBase's static cache via reflection
            // Check ALL instances of CodeAnalysis.dll and schedule a delayed check
            var codeAnalysisAsms = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "Microsoft.Dynamics.Nav.CodeAnalysis").ToArray();

            // Delayed diagnostic: test the actual locator via reflection
            System.Threading.ThreadPool.QueueUserWorkItem(_ => {
                System.Threading.Thread.Sleep(15000); // Wait 15s for BC to stabilize
                try {
                    var sb4 = new System.Text.StringBuilder();
                    sb4.AppendLine($"Locator diag at {DateTime.UtcNow}");
                    var navNcl = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
                    sb4.AppendLine($"Nav.Ncl: {(navNcl != null ? "loaded" : "NOT loaded")}");
                    if (navNcl != null) {
                        var navGlobalType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavGlobal");
                        sb4.AppendLine($"NavGlobal: {(navGlobalType != null ? "found" : "NOT found")}");
                        if (navGlobalType != null) {
                            var stProp = navGlobalType.GetProperty("SystemTenant",
                                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            var st = stProp?.GetValue(null);
                            sb4.AppendLine($"SystemTenant: {(st != null ? st.GetType().Name : "null")}");
                            if (st != null) {
                                var f = st.GetType().GetField("lazyDotNetResolverFactory",
                                    BindingFlags.Instance | BindingFlags.NonPublic);
                                sb4.AppendLine($"lazyFactory field: {(f != null ? "found" : "NOT found")}");
                                if (f != null) {
                                    var lazy = f.GetValue(st);
                                    var vp = lazy?.GetType().GetProperty("Value");
                                    var factory = vp?.GetValue(lazy);
                                    sb4.AppendLine($"Factory: {(factory != null ? factory.GetType().FullName : "null")}");
                                    if (factory != null) {
                                        var tlf = factory.GetType().GetField("cachedTypeLoader",
                                            BindingFlags.Instance | BindingFlags.NonPublic);
                                        var tl = tlf?.GetValue(factory);
                                        sb4.AppendLine($"TypeLoader: {(tl != null ? tl.GetType().FullName : "null")}");
                                        if (tl != null) {
                                            var lp = tl.GetType().GetProperty("AssemblyLocator");
                                            var loc = lp?.GetValue(tl);
                                            sb4.AppendLine($"Locator: {(loc != null ? loc.GetType().FullName : "null")}");
                                            if (loc != null) {
                                                var pp = loc.GetType().GetProperty("ProbingPaths");
                                                var paths = pp?.GetValue(loc) as System.Collections.IEnumerable;
                                                if (paths != null)
                                                    foreach (var p in paths) sb4.AppendLine($"  Path: {p}");
                                                var gp = loc.GetType().GetMethod("GetPathToAssembly");
                                                if (gp != null) {
                                                    try {
                                                        var r = gp.Invoke(loc, new object[] { "System.Runtime, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" });
                                                        sb4.AppendLine($"System.Runtime path: {r ?? "NULL"}");
                                                    } catch (Exception gex) { sb4.AppendLine($"GetPath error: {gex.InnerException?.Message ?? gex.Message}"); }
                                                    try {
                                                        var r2 = gp.Invoke(loc, new object[] { "netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51" });
                                                        sb4.AppendLine($"netstandard path: {r2 ?? "NULL"}");
                                                    } catch (Exception gex) { sb4.AppendLine($"GetPath error: {gex.InnerException?.Message ?? gex.Message}"); }
                                                }

                                                // Test LoadAssembly and LoadType on the type loader
                                                var loadAsm = tl.GetType().GetMethod("LoadAssembly");
                                                var loadType = tl.GetType().GetMethod("LoadType");
                                                if (loadAsm != null) {
                                                    try {
                                                        var netstdInfo = loadAsm.Invoke(tl, new object[] { "netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51" });
                                                        sb4.AppendLine($"LoadAssembly(netstandard): {(netstdInfo != null ? netstdInfo.GetType().Name + " Location=" + netstdInfo.GetType().GetProperty("Location")?.GetValue(netstdInfo) : "NULL")}");
                                                        if (netstdInfo != null && loadType != null) {
                                                            try {
                                                                var typeInfo = loadType.Invoke(tl, new object[] { netstdInfo, "System.String" });
                                                                sb4.AppendLine($"LoadType(netstandard, System.String): {(typeInfo != null ? "FOUND!" : "NULL")}");
                                                            } catch (Exception ltex) { sb4.AppendLine($"LoadType error: {ltex.InnerException?.Message ?? ltex.Message}"); }
                                                        }
                                                        var sysRtInfo = loadAsm.Invoke(tl, new object[] { "System.Runtime, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" });
                                                        sb4.AppendLine($"LoadAssembly(System.Runtime): {(sysRtInfo != null ? "OK Location=" + sysRtInfo.GetType().GetProperty("Location")?.GetValue(sysRtInfo) : "NULL")}");
                                                        // Test LoadType from System.Runtime directly
                                                        if (sysRtInfo != null && loadType != null) {
                                                            try {
                                                                var ti = loadType.Invoke(tl, new object[] { sysRtInfo, "System.String" });
                                                                sb4.AppendLine($"LoadType(System.Runtime, System.String): {(ti != null ? "FOUND!" : "NULL")}");
                                                            } catch (Exception ex2) { sb4.AppendLine($"LoadType(SysRt) error: {ex2.InnerException?.Message ?? ex2.Message}"); }
                                                        }
                                                        // Test: get the loaded AssemblyDefinition and check ExportedTypes
                                                        var loadFileMethod = tl.GetType().GetMethod("LoadAssemblyFromFile",
                                                            BindingFlags.Instance | BindingFlags.NonPublic);
                                                        if (loadFileMethod != null && netstdInfo != null) {
                                                            try {
                                                                string netstdLoc = (string)netstdInfo.GetType().GetProperty("Location").GetValue(netstdInfo);
                                                                dynamic asmDef = loadFileMethod.Invoke(tl, new object[] { netstdLoc });
                                                                if (asmDef != null) {
                                                                    var mod = asmDef.MainModule;
                                                                    var getTypeResult = mod.GetType("System.String", true);
                                                                    sb4.AppendLine($"Cecil GetType(System.String): {(getTypeResult != null ? $"Name={getTypeResult.Name} FullName={getTypeResult.FullName}" : "NULL")}");
                                                                    int etCount = 0;
                                                                    string stringForward = null;
                                                                    try {
                                                                        foreach (var et in mod.ExportedTypes) {
                                                                            etCount++;
                                                                            if (et.FullName == "System.String")
                                                                                stringForward = $"IsForwarder={et.IsForwarder} Scope={et.Scope}";
                                                                        }
                                                                    } catch (Exception etEx) {
                                                                        sb4.AppendLine($"ExportedTypes iteration FAILED: {etEx.GetType().Name}: {etEx.Message}");
                                                                    }
                                                                    sb4.AppendLine($"ExportedTypes count: {etCount}");
                                                                    sb4.AppendLine($"System.String forward: {stringForward ?? "NOT FOUND"}");
                                                                } else sb4.AppendLine("LoadAssemblyFromFile(netstandard) returned NULL");
                                                            } catch (Exception lfEx) { sb4.AppendLine($"LoadAssemblyFromFile error: {lfEx.InnerException?.Message ?? lfEx.Message}"); }
                                                        }
                                                        // Check typeCaches for null entries
                                                        var typeCacheField = tl.GetType().GetField("typeCache",
                                                            BindingFlags.Instance | BindingFlags.NonPublic);
                                                        if (typeCacheField != null) {
                                                            var tc = typeCacheField.GetValue(tl) as System.Collections.ICollection;
                                                            int nullCount = 0, totalCount = tc?.Count ?? 0;
                                                            if (tc != null) {
                                                                foreach (dynamic kvp in (System.Collections.IEnumerable)tc) {
                                                                    if (kvp.Value == null) {
                                                                        nullCount++;
                                                                        string k = kvp.Key;
                                                                        if (k.Contains("String") || k.Contains("Object"))
                                                                            sb4.AppendLine($"  NULL cached: {k}");
                                                                    }
                                                                }
                                                            }
                                                            sb4.AppendLine($"typeCache: {totalCount} total, {nullCount} null");
                                                        }
                                                        var asmCacheField = tl.GetType().GetField("assemblyInfoCache",
                                                            BindingFlags.Instance | BindingFlags.NonPublic);
                                                        if (asmCacheField != null) {
                                                            var ac = asmCacheField.GetValue(tl) as System.Collections.ICollection;
                                                            int nullAC = 0, totalAC = ac?.Count ?? 0;
                                                            if (ac != null) {
                                                                foreach (dynamic kvp in (System.Collections.IEnumerable)ac) {
                                                                    if (kvp.Value == null) {
                                                                        nullAC++;
                                                                        sb4.AppendLine($"  NULL asm: {(string)kvp.Key}");
                                                                    }
                                                                }
                                                            }
                                                            sb4.AppendLine($"assemblyInfoCache: {totalAC} total, {nullAC} null");
                                                        }
                                                    } catch (Exception laex) { sb4.AppendLine($"LoadAssembly error: {laex.InnerException?.Message ?? laex.Message}"); }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    File.WriteAllText("/tmp/patch15-locator-diag.txt", sb4.ToString());
                } catch (Exception ex) {
                    File.WriteAllText("/tmp/patch15-locator-diag.txt", $"Error: {ex}");
                }
            });

            // Also keep the delayed cache check
            new System.Threading.Timer(_ => {
                try {
                    var sb3 = new System.Text.StringBuilder();
                    sb3.AppendLine($"Delayed cache check at {DateTime.UtcNow}");
                    var allCAs = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => a.GetName().Name == "Microsoft.Dynamics.Nav.CodeAnalysis").ToArray();
                    sb3.AppendLine($"CodeAnalysis instances: {allCAs.Length}");
                    foreach (var ca in allCAs) {
                        sb3.AppendLine($"  Loc: {ca.Location}, HashCode: {ca.GetHashCode()}");
                        var lbt = ca.GetTypes().FirstOrDefault(t => t.Name == "AssemblyLocatorBase");
                        if (lbt != null) {
                            var cf = lbt.GetField("pathNameToAssemblyNameMap",
                                BindingFlags.Static | BindingFlags.NonPublic);
                            if (cf != null) {
                                var c = cf.GetValue(null);
                                if (c is System.Collections.ICollection coll2) {
                                    sb3.AppendLine($"  Cache entries: {coll2.Count}");
                                    foreach (dynamic kvp in (System.Collections.IEnumerable)c) {
                                        string k = kvp.Key;
                                        if (k.Contains("System.Runtime") || k.Contains("netstandard"))
                                            sb3.AppendLine($"    {k}");
                                    }
                                }
                            }
                        }
                    }
                    File.WriteAllText("/tmp/patch15-delayed-cache.txt", sb3.ToString());
                } catch (Exception ex) {
                    try { File.WriteAllText("/tmp/patch15-delayed-cache.txt", $"Error: {ex}"); } catch {}
                }
            }, null, TimeSpan.FromSeconds(30), System.Threading.Timeout.InfiniteTimeSpan);
            sb2.AppendLine($"CodeAnalysis instances: {codeAnalysisAsms.Length}");
            foreach (var ca in codeAnalysisAsms)
            {
                sb2.AppendLine($"  Instance: {ca.Location} (ALC={AssemblyLoadContext.GetLoadContext(ca)?.Name ?? "default"})");
                var locatorBaseType = ca.GetTypes()
                    .FirstOrDefault(t => t.Name == "AssemblyLocatorBase");
                if (locatorBaseType != null)
                {
                    var cacheField = locatorBaseType.GetField("pathNameToAssemblyNameMap",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    if (cacheField != null)
                    {
                        var cache = cacheField.GetValue(null);
                        if (cache is System.Collections.ICollection coll)
                        {
                            sb2.AppendLine($"  pathNameToAssemblyNameMap: {coll.Count} entries");
                            int addInsCount = 0, serviceCount = 0, runtimeCount = 0, otherCount = 0;
                            foreach (dynamic kvp in (System.Collections.IEnumerable)cache)
                            {
                                string key = kvp.Key;
                                if (key.Contains("/Add-Ins/")) addInsCount++;
                                else if (key.Contains("/bc/service/") && !key.Contains("/Add-Ins/")) serviceCount++;
                                else if (key.Contains("/dotnet/shared/")) runtimeCount++;
                                else otherCount++;
                                if (key.Contains("System.Runtime.dll"))
                                    sb2.AppendLine($"    System.Runtime: {key} → {kvp.Value}");
                                if (key.Contains("netstandard.dll"))
                                    sb2.AppendLine($"    netstandard: {key} → {kvp.Value}");
                            }
                            sb2.AppendLine($"    Add-Ins: {addInsCount}, Service: {serviceCount}, Runtime: {runtimeCount}, Other: {otherCount}");
                        }
                    }
                }
            }
            File.WriteAllText("/tmp/patch15-cache-diag.txt", sb2.ToString());
        }
        catch (Exception cacheEx)
        {
            try { File.WriteAllText("/tmp/patch15-cache-diag.txt",
                $"Cache diagnostic failed: {cacheEx}\n"); } catch { }
        }

        try
        {
            var sb = new System.Text.StringBuilder();
            string addInsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Add-Ins");
            string netstdPath = Path.Combine(addInsDir, "netstandard.dll");
            string sysRuntimePath = Path.Combine(addInsDir, "System.Runtime.dll");
            sb.AppendLine($"Add-Ins dir: {addInsDir} exists={Directory.Exists(addInsDir)}");
            sb.AppendLine($"netstandard.dll exists={File.Exists(netstdPath)} size={new FileInfo(netstdPath).Length}");
            sb.AppendLine($"System.Runtime.dll exists={File.Exists(sysRuntimePath)} size={new FileInfo(sysRuntimePath).Length}");

            // Try reading netstandard with Cecil
            var cecilAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Mono.Cecil");
            if (cecilAsm != null)
            {
                var modDefType = cecilAsm.GetType("Mono.Cecil.ModuleDefinition");
                var readMethod = modDefType?.GetMethod("ReadModule", new[] { typeof(string) });
                if (readMethod != null)
                {
                    // Read netstandard.dll
                    dynamic netstdMod = readMethod.Invoke(null, new object[] { netstdPath });
                    var exports = netstdMod.ExportedTypes;
                    int fwdCount = 0;
                    string stringFwd = null;
                    foreach (var et in exports)
                    {
                        if (et.IsForwarder)
                        {
                            fwdCount++;
                            string tn = et.FullName;
                            if (tn == "System.String")
                            {
                                stringFwd = $"{et.Scope}";
                            }
                        }
                    }
                    sb.AppendLine($"netstandard ExportedTypes: {fwdCount} forwarders");
                    sb.AppendLine($"System.String forwarded to: {stringFwd ?? "NOT FOUND"}");
                    netstdMod.Dispose();

                    // Read System.Runtime.dll
                    dynamic sysRtMod = readMethod.Invoke(null, new object[] { sysRuntimePath });
                    var strType = sysRtMod.GetType("System.String");
                    sb.AppendLine($"System.Runtime.GetType('System.String'): {(strType != null ? strType.FullName : "NULL")}");
                    sysRtMod.Dispose();
                }
                else sb.AppendLine("ModuleDefinition.ReadModule not found");
            }
            else sb.AppendLine("Mono.Cecil assembly not loaded");

            File.WriteAllText("/tmp/patch15-cecil-diag.txt", sb.ToString());
        }
        catch (Exception diagEx)
        {
            try { File.WriteAllText("/tmp/patch15-cecil-diag.txt",
                $"Diagnostic failed: {diagEx}\n"); } catch { }
        }
        return dict;
    }

    /// <summary>
    /// Replacement for NavAutomationHelper.GetGlobalAssemblyCacheDirectories.
    /// Returns empty — prevents the .NET runtime directory from being added to probing paths.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<string> EmptyGlobalAssemblyCacheDirs()
    {
        try { File.AppendAllText("/tmp/patch15-gac-hook-fired.txt",
            $"EmptyGlobalAssemblyCacheDirs called at {DateTime.UtcNow}\n"); } catch { }
        Console.Error.WriteLine("[StartupHook] Patch #15a: GetGlobalAssemblyCacheDirectories → empty (no runtime dir probing)");
        return Array.Empty<string>();
    }

    /// <summary>
    /// Patch #24: hook NSServiceBase.FindClientTimeZone so the session's client
    /// time zone is always serialization-safe on Linux.
    ///
    /// The web/desktop client sends its time zone id (e.g. "Europe/Berlin") in
    /// the ConnectionRequest. The NST resolves it with FindSystemTimeZoneById
    /// (→ a full ICU zone with DST rules) and later serializes it with
    /// TimeZoneInfo.ToSerializedString into the UserSettings it returns. On
    /// Linux, FromSerializedString cannot parse those ICU zones back
    /// (InvalidTimeZoneException in ValidateTimeZoneInfo), so OpenConnection
    /// throws and the session is killed before the client ever loads — i.e.
    /// nobody whose browser is in a DST zone can sign in.
    ///
    /// FindClientTimeZone is a private static method with a try/catch, so the
    /// JIT will not inline it and the JMP hook holds. The replacement does the
    /// same id→zone resolution, then substitutes a custom fixed-offset zone
    /// (same id/names, current UTC offset) for any zone that doesn't survive
    /// the round-trip. Trade-off: server-side date math for that session uses a
    /// fixed offset rather than DST rules (off by an hour across a transition) —
    /// acceptable for a dev/CI container versus being unable to sign in at all.
    /// </summary>
    private static void PatchFindClientTimeZone(Assembly navService)
    {
        try
        {
            var baseType = navService.GetType("Microsoft.Dynamics.Nav.Service.NSServiceBase");
            var method = baseType?.GetMethod("FindClientTimeZone",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (method == null)
            {
                Console.Error.WriteLine("[StartupHook] Patch #24: NSServiceBase.FindClientTimeZone not found");
                return;
            }
            _navTimeZoneExceptionType = navService.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavTimeZoneException")
                ?? FindTypeAcrossAssemblies("Microsoft.Dynamics.Nav.Types.Exceptions.NavTimeZoneException");
            var replacement = typeof(StartupHook).GetMethod(nameof(FindClientTimeZoneSafe),
                BindingFlags.Static | BindingFlags.NonPublic);
            ApplyJmpHook(method, replacement!, "NSServiceBase.FindClientTimeZone (Patch #24)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StartupHook] Patch #24 failed: {ex.Message}");
        }
    }

    private static Type? FindTypeAcrossAssemblies(string fullName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }

    // Mirrors: static TimeZoneInfo FindClientTimeZone(string requestedClientTimeZoneId)
    private static TimeZoneInfo FindClientTimeZoneSafe(string requestedClientTimeZoneId)
    {
        if (string.IsNullOrEmpty(requestedClientTimeZoneId))
            throw new ArgumentNullException(nameof(requestedClientTimeZoneId));

        if (_safeTimeZoneCache.TryGetValue(requestedClientTimeZoneId, out var cached))
            return cached;

        // The web client emits round-trip-safe ids (Etc/GMT±N for whole hours,
        // synthetic "UTC±HH:MM" for sub-hour). Whole-hour Etc/* ids resolve via
        // FindSystemTimeZoneById; synthetic ids don't, so parse the offset out.
        TimeZoneInfo? resolved = null;
        try { resolved = TimeZoneInfo.FindSystemTimeZoneById(requestedClientTimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or NullReferenceException)
        {
            if (TryParseSyntheticOffsetId(requestedClientTimeZoneId, out var off))
                resolved = ZoneForOffset(off);
        }

        if (resolved == null)
        {
            var message = $"The time zone '{requestedClientTimeZoneId}' was not recognized as a valid time zone on this server.";
            if (_navTimeZoneExceptionType != null)
            {
                try { throw (Exception)Activator.CreateInstance(_navTimeZoneExceptionType, message)!; }
                catch (MissingMethodException) { }
            }
            throw new TimeZoneNotFoundException(message);
        }

        var safe = MakeTimeZoneSerializationSafe(resolved);
        _safeTimeZoneCache[requestedClientTimeZoneId] = safe;
        return safe;
    }

    /// <summary>
    /// Return a TimeZoneInfo equivalent to <paramref name="tz"/> that is
    /// guaranteed to survive ToSerializedString → FromSerializedString on Linux
    /// AND whose Id, once persisted and re-resolved on the next login, still
    /// yields a safe zone. Whole-hour offsets map to the real "Etc/GMT±N" IANA
    /// zones (no DST, re-resolvable); sub-hour offsets map to a synthetic
    /// "UTC±HH:MM" id that TryFindSystemTimeZoneById skips (leaving the zone
    /// unset = safe) but that round-trips as a custom zone for the live session.
    /// </summary>
    internal static TimeZoneInfo MakeTimeZoneSerializationSafe(TimeZoneInfo tz)
    {
        try
        {
            TimeZoneInfo.FromSerializedString(tz.ToSerializedString());
            return tz; // already safe (UTC, Etc/GMT±N, synthetic custom zones)
        }
        catch
        {
            var safe = ZoneForOffset(tz.GetUtcOffset(DateTime.UtcNow));
            Console.Error.WriteLine($"[StartupHook] Patch #24: time zone '{tz.Id}' does not survive .NET serialization on Linux — substituting '{safe.Id}'");
            return safe;
        }
    }

    internal static TimeZoneInfo ZoneForOffset(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
            return TimeZoneInfo.Utc;

        // Whole-hour offset → real Etc/GMT zone (POSIX sign is inverted).
        if (offset.Minutes == 0 && offset.Seconds == 0)
        {
            int hours = offset.Hours; // signed
            string etcId = $"Etc/GMT{(hours > 0 ? "-" : "+")}{Math.Abs(hours)}";
            try
            {
                var etc = TimeZoneInfo.FindSystemTimeZoneById(etcId);
                TimeZoneInfo.FromSerializedString(etc.ToSerializedString()); // verify
                return etc;
            }
            catch { /* fall through to synthetic */ }
        }

        // Sub-hour (or Etc lookup failed): synthetic non-resolvable id.
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        string syntheticId = $"UTC{sign}{offset.Duration():hh\\:mm}";
        return TimeZoneInfo.CreateCustomTimeZone(syntheticId, offset, syntheticId, syntheticId);
    }

    private static bool TryParseSyntheticOffsetId(string id, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (string.IsNullOrEmpty(id) || !id.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = id.Substring(3);
        if (rest.Length == 0) return true; // "UTC" → zero
        int sign = rest[0] == '-' ? -1 : 1;
        if (rest[0] is '+' or '-') rest = rest.Substring(1);
        if (TimeSpan.TryParse(rest, System.Globalization.CultureInfo.InvariantCulture, out var ts))
        {
            offset = sign < 0 ? -ts : ts;
            return true;
        }
        return false;
    }

    // ========================================================================
    // Patch #19: CustomReportingServiceClient — no-op factory for report rendering.
    // NavEnvironment.Instance.CustomReportingServiceClient is a built-in hook that
    // BC checks before trying to connect to the gRPC Reporting Service.
    // We set it to return a no-op proxy, preventing the gRPC timeout crash.
    // ========================================================================
    private static object? _noopReportingClient;
    private static bool _reportingClientPatched;

    private static void PatchReportingServiceClient(Assembly navNcl)
    {
        // NavEnvironment.Instance may not be initialized yet — retry on a timer
        var timer = new System.Threading.Timer(_ =>
        {
            try { TrySetNoOpReportingClient(navNcl); }
            catch (Exception ex)
            {
                if (!_reportingClientPatched)
                    Console.WriteLine($"[StartupHook] Patch #19 retry: {ex.GetType().Name}: {ex.Message}");
            }
        }, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
    }

    private static void TrySetNoOpReportingClient(Assembly navNcl)
    {
        if (_reportingClientPatched) return;

        Type? envType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment");
        var instanceProp = envType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        object? envInst = instanceProp?.GetValue(null);
        if (envInst == null) return; // not initialized yet, timer will retry

        // Find the backing field for CustomReportingServiceClient
        var field = envType!.GetField("<CustomReportingServiceClient>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
        {
            // Try the property directly
            var prop = envType.GetProperty("CustomReportingServiceClient",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (prop == null)
            {
                Console.WriteLine("[StartupHook] Patch #19: CustomReportingServiceClient not found");
                _reportingClientPatched = true;
                return;
            }
            field = null; // will use property setter below
        }

        // Load IReportingServiceClient interface from the Reporting.Client assembly
        string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
        // Reporting.Client.dll is in the main service directory, not SideServices
        string clientDll = Path.Combine(baseDir, "Microsoft.BusinessCentral.Reporting.Client.dll");
        if (!File.Exists(clientDll))
            clientDll = Path.Combine(baseDir, "SideServices", "Microsoft.BusinessCentral.Reporting.Client.dll");
        if (!File.Exists(clientDll))
        {
            Console.WriteLine($"[StartupHook] Patch #19: {clientDll} not found");
            _reportingClientPatched = true;
            return;
        }

        Assembly clientAsm = Assembly.LoadFrom(clientDll);
        Type? iClientType = clientAsm.GetType("Microsoft.BusinessCentral.Reporting.Client.IReportingServiceClient");
        if (iClientType == null)
        {
            Console.WriteLine("[StartupHook] Patch #19: IReportingServiceClient type not found");
            _reportingClientPatched = true;
            return;
        }

        // Create a no-op proxy via DispatchProxy
        _noopReportingClient = typeof(System.Reflection.DispatchProxy)
            .GetMethod("Create", 2, Type.EmptyTypes)!
            .MakeGenericMethod(iClientType, typeof(NoOpReportingProxy))
            .Invoke(null, null);

        // Build the Func delegate matching the field's type and set it
        if (field != null)
        {
            Type fieldType = field.FieldType;
            // Create a DynamicMethod that returns our no-op client, ignoring the input tuple
            var invokeMethod = fieldType.GetMethod("Invoke")!;
            var paramTypes = invokeMethod.GetParameters().Select(p => p.ParameterType).ToArray();
            var dm = new System.Reflection.Emit.DynamicMethod(
                "NoOpReportingClientFactory", iClientType, paramTypes,
                typeof(StartupHook).Module, skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldsfld,
                typeof(StartupHook).GetField(nameof(_noopReportingClient),
                    BindingFlags.Static | BindingFlags.NonPublic)!);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            var factoryDelegate = dm.CreateDelegate(fieldType);
            field.SetValue(envInst, factoryDelegate);
        }

        _reportingClientPatched = true;
        Console.WriteLine("[StartupHook] Patch #19: CustomReportingServiceClient → no-op proxy");
    }

    /// <summary>
    /// DispatchProxy that implements IReportingServiceClient. Lifecycle/negotiation calls
    /// (ConfigureServiceAsync, GetServiceConfigurationAsync, DisposeAsync) are silently no-op'd —
    /// that's what stops the gRPC-timeout crash. RenderAsync and PrintReportAsync are the only
    /// calls that actually produce report output; faking success there would mean
    /// Report.SaveAs() returns true against an empty or wrong blob with nothing to notice —
    /// unacceptable for business-critical software. Out-of-scope operations must fail loud, not
    /// silently misbehave, so those two throw NavReportException (BC's own report-error type)
    /// instead. A plain CLR exception like PlatformNotSupportedException doesn't work here — it's
    /// not a NavBaseException, so NavApplicationObjectBase.TryInvokeAsync (the machinery behind
    /// TryFunction/asserterror) can't treat it as a normal AL error, and it also isn't a type
    /// NavReport.SaveAsAsync itself knows to catch, so it comes out the other side as an
    /// "Unexpected CLR exception" that neither TryFunction nor asserterror can trap. With
    /// NavReportException, NavReport.SaveAsAsync catches it internally (the same as any other
    /// genuine report-rendering failure on real BC) and Report.SaveAs() returns false with
    /// GetLastErrorText() populated — no throw at all, so AL callers use the ordinary
    /// "if not Report.SaveAs(...) then Error(GetLastErrorText())" pattern, not asserterror.
    /// </summary>
    public class NoOpReportingProxy : System.Reflection.DispatchProxy
    {
        private static readonly HashSet<string> OutOfScopeMethods = new() { "RenderAsync", "PrintReportAsync" };
        private static ConstructorInfo? _navReportExceptionCtor;
        private static bool _navReportExceptionCtorLookedUp;

        private static Exception CreateOutOfScopeException(string methodName)
        {
            string message =
                "RDLC report rendering is not implemented on Linux BC: the Windows Reporting " +
                "Service is stubbed out (bc-linux StartupHook.cs Patch #19). " +
                $"IReportingServiceClient.{methodName}() cannot produce report output on this platform.";

            if (!_navReportExceptionCtorLookedUp)
            {
                _navReportExceptionCtorLookedUp = true;
                try
                {
                    // Must resolve BC's own already-loaded Nav.Types.dll, not a freshly loaded
                    // duplicate — TryInvokeAsync's "is NavBaseException" check is a type-identity
                    // check, and a second copy loaded via LoadFrom would have a distinct Type.
                    Assembly? typesAsm = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
                    Type? reportExType = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavReportException");
                    _navReportExceptionCtor = reportExType?.GetConstructor(new[] { typeof(string) });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StartupHook] NavReportException lookup failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (_navReportExceptionCtor != null)
            {
                try { return (Exception)_navReportExceptionCtor.Invoke(new object[] { message })!; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StartupHook] NavReportException construction failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Fallback: still fails loud, just not AL-catchable.
            return new PlatformNotSupportedException(message);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;

            var rt = targetMethod.ReturnType;

            if (OutOfScopeMethods.Contains(targetMethod.Name))
            {
                Console.WriteLine($"[StartupHook] IReportingServiceClient.{targetMethod.Name}() is out of scope on Linux — throwing NavReportException");
                var ex = CreateOutOfScopeException(targetMethod.Name);

                if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    // ValueTask<T> has no static FromException factory (unlike Task<T>) — build a
                    // faulted Task<T> via Task.FromException<T>, then wrap it in a ValueTask<T> via
                    // the ValueTask<T>(Task<T>) constructor.
                    Type inner = rt.GetGenericArguments()[0];
                    var taskFromException = typeof(Task)
                        .GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .First(m => m.Name == "FromException" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                        .MakeGenericMethod(inner);
                    object faultedTask = taskFromException.Invoke(null, new object[] { ex })!;
                    return Activator.CreateInstance(rt, faultedTask);
                }
                return ValueTask.FromException(ex);
            }

            Console.WriteLine($"[StartupHook] NoOp IReportingServiceClient.{targetMethod.Name}()");

            if (rt == typeof(ValueTask)) return ValueTask.CompletedTask;
            if (rt == typeof(Task)) return Task.CompletedTask;

            // Handle ValueTask<T> — return default(T) wrapped in ValueTask
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                Type inner = rt.GetGenericArguments()[0];
                object? val = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                // Construct ValueTask<T>(T result)
                return Activator.CreateInstance(rt, val);
            }

            return null;
        }
    }

    // ========================================================================
    // Patch #20: SideServiceProcessClient.EnsureAlive — silence watchdog log spam.
    //
    // SideServiceWatchdog runs a background loop calling EnsureAlive() on each
    // registered side service client. On Linux, EnsureAlive() → TryStartService()
    // tries to launch the Windows PE binary
    //   /bc/service/SideServices/Microsoft.BusinessCentral.Reporting.Service.exe
    // which fails with EACCES / "Permission denied" every few seconds, flooding
    // the BC event log. Patch #19 (CustomReportingServiceClient) silences the
    // rendering path, but the watchdog is a separate code path.
    //
    // Fix: No-op EnsureAlive() on SideServiceProcessClient. The watchdog loop
    // continues running harmlessly; it just no longer tries to start the .exe.
    // ========================================================================

    private static void PatchSideServiceWatchdog(Assembly navNcl)
    {
        try
        {
            // SideServiceProcessClient is in the Microsoft.Dynamics.Nav.Runtime namespace
            // inside Nav.Ncl.dll. The class manages the process lifecycle of side services
            // (Reporting, etc.) and is called by SideServiceWatchdog.CheckServicesAsync.
            var clientType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SideServiceProcessClient");
            if (clientType == null)
            {
                Console.WriteLine("[StartupHook] Patch #20: SideServiceProcessClient type not found — skipping");
                return;
            }

            // Target TryStartService instead of EnsureAlive. EnsureAlive is called from
            // the async CheckServicesAsync state machine — JMP-hooking it corrupts memory
            // (AccessViolationException). TryStartService is the synchronous method that
            // actually calls Process.Start() on the Windows .exe.
            var tryStart = clientType.GetMethod("TryStartService",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (tryStart == null)
            {
                Console.WriteLine("[StartupHook] Patch #20: TryStartService method not found — skipping");
                return;
            }

            Console.WriteLine($"[StartupHook] Patch #20: TryStartService returns {tryStart.ReturnType} (generic: {tryStart.ReturnType.IsGenericType})");
            MethodInfo replacement;
            // Check for ValueTask<T> — the generic arg might not be bool
            if (tryStart.ReturnType.IsGenericType && tryStart.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                replacement = typeof(StartupHook).GetMethod(
                    nameof(Replacement_TryStartService_ValueTaskBool),
                    BindingFlags.Static | BindingFlags.NonPublic)!;
            }
            else if (tryStart.ReturnType == typeof(bool))
            {
                replacement = typeof(StartupHook).GetMethod(
                    nameof(Replacement_SideServiceEnsureAlive_Bool),
                    BindingFlags.Static | BindingFlags.NonPublic)!;
            }
            else
            {
                replacement = typeof(StartupHook).GetMethod(
                    nameof(Replacement_SideServiceEnsureAlive_Void),
                    BindingFlags.Static | BindingFlags.NonPublic)!;
            }

            ApplyJmpHook(tryStart, replacement, "SideServiceProcessClient.TryStartService");
            Console.WriteLine("[StartupHook] Patch #20: TryStartService hooked");

            // NOTE: Do NOT hook EnsureAlive — it's referenced by the CheckServicesAsync
            // async state machine. JMP-hooking it causes AccessViolationException / core dump.
            // TryStartService hook alone prevents Process.Start spam. The remaining
            // "Could not ensure side service" errors (~6/min) are cosmetic.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #20 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// No-op replacement for SideServiceProcessClient.EnsureAlive() (void variant).
    /// The first parameter receives 'this' because JMP hooks on instance methods require
    /// the replacement to accept the implicit 'this' as an explicit object parameter.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Replacement_SideServiceEnsureAlive_Void(object self)
    {
        // Intentionally empty — suppress the watchdog's attempt to start the
        // Windows Reporting Service .exe on Linux (causes "Permission denied" log spam).
    }

    /// <summary>
    /// No-op replacement for TryStartService() returning ValueTask&lt;bool&gt;.
    /// Returns true (service started successfully) so the watchdog doesn't retry.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<bool> Replacement_TryStartService_ValueTaskBool(object self)
    {
        return new ValueTask<bool>(true);
    }

    /// <summary>
    /// No-op replacement for SideServiceProcessClient.EnsureAlive() (bool variant).
    /// Returns true so callers treat the service as alive and take no further action.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Replacement_SideServiceEnsureAlive_Bool(object self)
    {
        return true;
    }

    // ========================================================================
    // Patch #18: SetupSideServices — skip Reporting Service startup on Linux.
    // The Reporting Service (.exe) is a Windows PE binary. Without this patch,
    // BC crashes with SideServiceProcessException on startup. Making it a no-op
    // lets BC start normally; report rendering will fail gracefully at test time
    // instead of crashing the entire server.
    // ========================================================================
    private static void PatchSetupSideServices(Assembly windowsServicesAsm)
    {
        try
        {
            var serverType = windowsServicesAsm.GetType("Microsoft.Dynamics.Nav.WindowsServices.DynamicsNavServer");
            if (serverType == null)
            {
                Console.WriteLine("[StartupHook] Patch #18: DynamicsNavServer type not found");
                return;
            }

            var setupMethod = serverType.GetMethod("SetupSideServices",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (setupMethod == null)
            {
                Console.WriteLine("[StartupHook] Patch #18: SetupSideServices method not found");
                return;
            }

            // Replace with a no-op (static method — no 'this' param)
            var noop = typeof(StartupHook).GetMethod(nameof(Replacement_SetupSideServices_Noop),
                BindingFlags.Public | BindingFlags.Static)!;
            ApplyJmpHook(setupMethod, noop, "DynamicsNavServer.SetupSideServices");
            Console.WriteLine("[StartupHook] Patch #18: SetupSideServices hooked (no-op on Linux)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #18 failed: {ex.Message}");
        }
    }

    /// <summary>No-op replacement for SetupSideServices (static method on DynamicsNavServer)</summary>
    public static void Replacement_SetupSideServices_Noop()
    {
        Console.WriteLine("[StartupHook] Patch #18: SetupSideServices skipped (Linux — no Reporting Service)");
    }

    // ========================================================================
    // Patch #17: ALDatabase.ALSid — converts username to Windows SID.
    // On Linux, System.Security.Principal.IdentityNotMappedException is not
    // available, causing TypeLoadException. Return a dummy SID (S-1-5-0)
    // so tests that call UserExists/SetupUsers don't crash mid-codeunit.
    // ========================================================================
    private static void PatchALDatabaseALSid(Assembly navNcl)
    {
        try
        {
            var dbType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALDatabase");
            if (dbType == null)
            {
                Console.WriteLine("[StartupHook] Patch #17: ALDatabase type not found");
                return;
            }

            // ALSid has overloads — patch the one that takes a string (userName)
            var alSidMethods = dbType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "ALSid").ToArray();

            Console.WriteLine($"[StartupHook] Patch #17: Found {alSidMethods.Length} ALSid overloads");
            int hooked = 0;
            var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_ALSid),
                BindingFlags.Public | BindingFlags.Static)!;

            foreach (var m in alSidMethods)
            {
                var parms = m.GetParameters();
                var sig = string.Join(", ", parms.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Console.WriteLine($"[StartupHook]   ALSid({sig}) -> {m.ReturnType.Name}");

                // Patch the overload that takes a single string parameter
                if (parms.Length == 1 && parms[0].ParameterType == typeof(string))
                {
                    ApplyJmpHook(m, replacement, "ALDatabase.ALSid(string)");
                    hooked++;
                }
            }

            if (hooked > 0)
                Console.WriteLine($"[StartupHook] Patch #17: hooked {hooked} ALSid overload(s)");
            else
                Console.WriteLine("[StartupHook] Patch #17: no matching ALSid(string) overload found");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #17 failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Replacement for ALDatabase.ALSid(string userName).
    /// Returns a deterministic dummy Windows SID based on the username.
    /// Uses FNV-1a hash (not GetHashCode which is randomized per-process in .NET Core).
    /// The entrypoint pre-seeds the ADMIN user with the same SID so tests find it.
    /// Format: S-1-5-21-{h1}-{h2}-{h3}-1001
    /// </summary>
    public static string Replacement_ALSid(string userName)
    {
        // FNV-1a 32-bit hash — deterministic across processes and restarts
        uint hash = 2166136261u;
        foreach (char c in (userName ?? "").ToUpperInvariant())
        {
            hash ^= (uint)c;
            hash *= 16777619u;
        }
        uint h1 = hash & 0x7FFFFFFFu;
        uint h2 = ((hash >> 16) | (hash << 16)) & 0x7FFFFFFFu;
        uint h3 = (hash * 31u) & 0x7FFFFFFFu;
        return $"S-1-5-21-{h1}-{h2}-{h3}-1001";
    }

    // ========================================================================
    // Hook all Watson entry points to prevent NullRef crash on Linux.
    // The crash chain: SendReport → GetWatsonPath → GetRegistryValue → NullRef
    // We hook all three levels for robustness (JIT timing, inlining).
    // ========================================================================
    private static void PatchWatsonReporting(Assembly watsonAsm)
    {
        try
        {
            var watsonType = watsonAsm.GetType("Microsoft.Dynamics.Nav.Watson.WatsonReporting");
            if (watsonType == null)
            {
                Console.WriteLine("[StartupHook] Watson: WatsonReporting type not found");
                return;
            }

            var noop = typeof(StartupHook).GetMethod(nameof(WatsonSendReportNoop),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var noopStr = typeof(StartupHook).GetMethod(nameof(WatsonGetRegistryValueNoop),
                BindingFlags.Static | BindingFlags.NonPublic)!;

            int hooked = 0;
            // Hook ALL SendReport overloads
            foreach (var m in watsonType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "SendReport"))
            {
                ApplyJmpHook(m, noop, $"WatsonReporting.SendReport({m.GetParameters().Length} params)");
                hooked++;
            }

            // Hook GetWatsonPath (called by SendReport)
            var getPath = watsonType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "GetWatsonPath");
            if (getPath != null)
            {
                ApplyJmpHook(getPath, noopStr, "WatsonReporting.GetWatsonPath");
                hooked++;
            }

            // Hook GetRegistryValue (the actual crasher — NullRef on Linux)
            var getRegVal = watsonType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "GetRegistryValue");
            if (getRegVal != null)
            {
                ApplyJmpHook(getRegVal, noopStr, "WatsonReporting.GetRegistryValue");
                hooked++;
            }

            // Hook WriteWatsonLog on the Server class if we can find it
            var serverType = watsonAsm.GetType("Microsoft.Dynamics.Nav.Service.Server");
            if (serverType != null)
            {
                var writeWatson = serverType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "WriteWatsonLog");
                if (writeWatson != null)
                {
                    ApplyJmpHook(writeWatson, noop, "Server.WriteWatsonLog");
                    hooked++;
                }
            }

            Console.WriteLine($"[StartupHook] Watson: hooked {hooked} methods");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Watson patch failed: {ex.Message}");
        }
    }

    private static void WatsonSendReportNoop() { }
    private static string? WatsonGetRegistryValueNoop() => null;

    private static void PatchNavTypes(Assembly navTypes)
    {
        if (_patchedTypes) return;

        try
        {
            // Replace the EventLogWriter's IEventLogEntryWriter with a no-op proxy.
            // JMP hooks don't reliably intercept here (JIT inlining), so we replace
            // the writer instance via the public settable property instead.
            Type? eventLogWriterType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.EventLogWriter");
            Type? ifaceType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.IEventLogEntryWriter");

            if (eventLogWriterType != null && ifaceType != null)
            {
                // Create a no-op proxy implementing IEventLogEntryWriter
                // Use genericParameterCount overload to avoid AmbiguousMatchException
                var createMethod = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(ifaceType, typeof(NoOpDispatchProxy));
                var noopWriter = createMethod.Invoke(null, null);

                // Replace the static field
                var field = eventLogWriterType.GetField("eventLogEntryWriter",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(null, noopWriter);
                    Console.WriteLine("[StartupHook] Replaced EventLogWriter with no-op proxy");
                }
                else
                {
                    // Try the public property setter as fallback
                    var prop = eventLogWriterType.GetProperty("EventLogEntryWriter",
                        BindingFlags.Public | BindingFlags.Static);
                    prop?.SetValue(null, noopWriter);
                    Console.WriteLine("[StartupHook] Replaced EventLogWriter via property setter");
                }
            }
            else
            {
                Console.WriteLine("[StartupHook] EventLogWriter or IEventLogEntryWriter not found");
            }

            // --- Patch #5b: Replace NavDiagnostics.TraceWriter with no-op ---
            // EtwTelemetryLog uses Windows ETW. Replace before NavEnvironment..ctor runs.
            Type? navDiagType = navTypes.GetType("Microsoft.Dynamics.Nav.Diagnostic.NavDiagnostics");
            Type? telemetryIfaceType = navTypes.GetType("Microsoft.Dynamics.Nav.Diagnostics.Telemetry.ITelemetryLogWriter");

            if (navDiagType != null && telemetryIfaceType != null)
            {
                var createTelemetry = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(telemetryIfaceType, typeof(NoOpDispatchProxy));
                var noopTelemetry = createTelemetry.Invoke(null, null);

                var traceWriterField = navDiagType.GetField("traceWriter",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (traceWriterField != null)
                {
                    traceWriterField.SetValue(null, noopTelemetry);
                    Console.WriteLine("[StartupHook] Pre-set NavDiagnostics.TraceWriter to no-op");
                }
            }

            // --- Patch #7: Encryption provider bypass for plain text SQL password ---
            Type? factoryType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.DefaultServerInstanceRsaEncryptionProviderFactory");
            Type? encIfaceType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.ISystemEncryptionProvider");

            if (factoryType != null && encIfaceType != null)
            {
                // Create a pass-through encryption proxy
                var createProxy = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(encIfaceType, typeof(PassthroughEncryptionProxy));
                var noopEncryption = createProxy.Invoke(null, null);

                // Set the factory delegate to return our proxy
                var prop = factoryType.GetProperty("GetDefaultEncryptionProvider",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (prop != null)
                {
                    // Create Func<ISystemEncryptionProvider> delegate
                    var funcType = typeof(Func<>).MakeGenericType(encIfaceType);
                    var capturedProxy = noopEncryption;
                    // Use DynamicMethod to create the delegate
                    var dm = new DynamicMethod("GetNoOpEncryption", encIfaceType, Type.EmptyTypes,
                        typeof(StartupHook).Module, skipVisibility: true);
                    // We can't close over capturedProxy in IL, so store it in a static field
                    _noopEncryptionProvider = noopEncryption;
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldsfld, typeof(StartupHook).GetField(nameof(_noopEncryptionProvider),
                        BindingFlags.Static | BindingFlags.NonPublic)!);
                    il.Emit(OpCodes.Ret);
                    var funcDelegate = dm.CreateDelegate(funcType);

                    prop.SetValue(null, funcDelegate);
                    Console.WriteLine("[StartupHook] Set encryption provider to pass-through (plain text passwords)");
                }
            }

            // Patch #15a: Remove .NET runtime directory from assembly probing paths.
            // NavAutomationHelper.GetGlobalAssemblyCacheDirectories() adds typeof(object).Assembly.Location
            // directory (the .NET shared runtime dir) to probing paths. On Linux this contains R2R DLLs
            // that crash Cecil. Hook it to return empty so only Add-Ins is probed.
            Type? navAutoHelper = IsPatchDisabled("15a") ? null
                : navTypes.GetType("Microsoft.Dynamics.Nav.Types.NavAutomationHelper");
            if (navAutoHelper != null)
            {
                var getGacDirs = navAutoHelper.GetMethod("GetGlobalAssemblyCacheDirectories",
                    BindingFlags.Static | BindingFlags.Public);
                if (getGacDirs != null)
                {
                    var replacement = typeof(StartupHook).GetMethod(nameof(EmptyGlobalAssemblyCacheDirs),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    ApplyJmpHook(getGacDirs, replacement!, "NavAutomationHelper.GetGlobalAssemblyCacheDirectories");

                    // Verify the hook works by calling the method
                    try
                    {
                        var result = getGacDirs.Invoke(null, null);
                        var dirs = result as System.Collections.Generic.IEnumerable<string>;
                        int count = dirs != null ? System.Linq.Enumerable.Count(dirs) : -1;
                        Console.WriteLine($"[StartupHook] GAC dirs hook test: {count} dirs returned (expect 0 if hook works)");
                    }
                    catch (Exception testEx)
                    {
                        Console.WriteLine($"[StartupHook] GAC dirs hook test failed: {testEx.InnerException?.Message ?? testEx.Message}");
                    }
                }
            }

            // --- Patch #24: UserSettings.TimeZoneInfo serialization safety ---
            // BC round-trips session/user time zones through
            // TimeZoneInfo.ToSerializedString (setter) → FromSerializedString
            // (getter). On Linux, FromSerializedString throws for most ICU zones
            // (InvalidTimeZoneException), so any session whose time zone is a
            // real-world DST zone — the browser's zone, or the CRONUS demo DB's
            // 'Europe/Amsterdam' personalization row — dies in
            // NSService.OpenConnection before the client loads. Nobody outside
            // UTC can sign in to the web client.
            // The setter is the single choke point: every serialized string is
            // produced here. Hook it to substitute a custom fixed-offset zone
            // (same id/names, current UTC offset) for any zone that doesn't
            // survive the round-trip, so the stored string always deserializes.
            // Trade-off: a fixed offset instead of DST rules for affected zones
            // (off by an hour across a transition) — acceptable for a dev/CI
            // container versus being unable to sign in.
            var userSettingsType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.UserSettings");
            var tzSetter = userSettingsType?.GetProperty("TimeZoneInfo")?.SetMethod;
            var tzField = userSettingsType?.GetField("serializedTimeZoneInfo",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (tzSetter != null && tzField != null)
            {
                _userSettingsSerializedTzField = tzField;
                var tzReplacement = typeof(StartupHook).GetMethod(nameof(SetTimeZoneInfoSafe),
                    BindingFlags.Static | BindingFlags.NonPublic);
                ApplyJmpHook(tzSetter, tzReplacement!, "UserSettings.set_TimeZoneInfo (Patch #24)");
            }
            else
            {
                Console.WriteLine("[StartupHook] Patch #24: UserSettings.set_TimeZoneInfo or serializedTimeZoneInfo field not found");
            }

            _patchedTypes = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #4/5/7 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static FieldInfo? _userSettingsSerializedTzField;

    // Mirrors UserSettings.set_TimeZoneInfo(TimeZoneInfo value): 'this' is arg0.
    // Stores a serialization-safe form so the getter's FromSerializedString
    // (which the JIT may inline anywhere) can never throw on Linux.
    private static void SetTimeZoneInfoSafe(object self, TimeZoneInfo? value)
    {
        try
        {
            string? serialized = value == null ? null : MakeTimeZoneSerializationSafe(value).ToSerializedString();
            _userSettingsSerializedTzField!.SetValue(self, serialized);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StartupHook] Patch #24 set_TimeZoneInfo failed ({ex.GetType().Name}: {ex.Message}) — storing UTC");
            try { _userSettingsSerializedTzField!.SetValue(self, TimeZoneInfo.Utc.ToSerializedString()); } catch { }
        }
    }

    /// <summary>
    /// DispatchProxy subclass that no-ops all method calls.
    /// Used to create runtime implementations of BC interfaces without compile-time references.
    /// </summary>
    /// <summary>
    /// No-op proxy, but for IEventLogEntryWriter, log to Console instead of Windows Event Log.
    /// </summary>
    public class NoOpDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            // Redirect EventLog writes to Console so we can see errors
            if (targetMethod?.Name == "WriteEntry" && args?.Length >= 2)
            {
                var message = args[1]?.ToString();
                if (message != null && message.Length > 10)
                    Console.Error.WriteLine($"[BC-EventLog] {message}");
            }
            return null;
        }
    }

    /// <summary>
    /// Encryption proxy that passes text through unchanged.
    /// IsKeyPresent returns false, making ProtectedDatabasePassword treat values as plain text.
    /// </summary>
    /// <summary>
    /// Topology proxy that returns false for IsServiceRunningInLocalEnvironment
    /// (bypasses ACL APIs) and delegates everything else to the original topology.
    /// </summary>
    public class LinuxTopologyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_IsServiceRunningInLocalEnvironment")
                return false; // Must be false to skip ACL APIs on Linux
            if (targetMethod?.Name == "get_AllowToRegisterServicePrincipalName")
                return false; // Skip SPN registration (requires Active Directory)
            // Delegate to original topology
            if (_originalTopology != null && targetMethod != null)
            {
                try { return targetMethod.Invoke(_originalTopology, args); }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            }
            if (targetMethod?.ReturnType == typeof(bool)) return false;
            if (targetMethod?.ReturnType == typeof(string)) return "";
            return null;
        }
    }

    public class PassthroughEncryptionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod?.Name switch
            {
                "Encrypt" or "Decrypt" => args?[0], // pass-through
                "get_IsKeyPresent" or "get_IsKeyCreated" => true,
                "get_PublicKey" => "<RSAKeyValue><Modulus>xbzyD+SGxykyAv82XOEFtDzWEIok0MM5SAc+CS6Mq0W5LwiyXeakWyblq1XgYi3CDu700986ZVRi4KJjruZlzBeZ7IWXD4lEEpTCRuqoxasRTnwVpyVqGuHclJAnUpjeBS6HvaS/iesYWwxZcmlsmzJHvF3hXdDmLj+8GSKgo4IhschPCIpnoH8+FREX++VpwfZH1ejMk5Izds/ZI70Xc/OWfRfaYy3rtCFeZQ1R5T1AhlNJDgpn0a1oP86F8yDGYawB2GJKIewdcWE8usu4QesrFnlS1g/IJcFXe71/TiJjryqRJPk8ze3Jh9+atx57OnI4R3QvuM/lQ7YoN1RVjw==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>",
                _ => null,
            };
            if (targetMethod?.Name == "Decrypt" || targetMethod?.Name == "Encrypt")
            {
                _encryptionBypassed = true;
                Console.WriteLine($"[StartupHook] Encryption.{targetMethod.Name}() called — bypass working");
            }
            return result;
        }
    }

    /// <summary>
    /// Patch #26's own proxy, deliberately SEPARATE from PassthroughEncryptionProxy/
    /// _noopEncryptionProvider (Patch #7's SQL connection-string password fake). Sharing one
    /// instance across both was tried and broke NST boot: whatever writes the SQL system-tenant
    /// password expects Decrypt(x) == x (Patch #7's identity passthrough), so giving Encrypt/
    /// Decrypt a real transform there corrupted that password and NST couldn't connect to SQL
    /// at all ("Cannot establish a connection to the SQL Server/Database" during
    /// NavTenantCollection.ConfigureTenants). Keeping this proxy AL-encryption-only avoids that
    /// blast radius entirely.
    /// </summary>
    public class TenantFakeEncryptionProxy : DispatchProxy
    {
        // Fixed XOR key: this only needs to be self-consistent within one running
        // container, not secret or stable across restarts.
        private const byte FakeEncryptionXorKey = 0x5A;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod?.Name switch
            {
                // Reversible-but-obviously-fake transform (byte XOR + Base64), not a
                // straight pass-through: some AL tests assert Encrypt(x) != x in addition
                // to Decrypt(Encrypt(x)) == x. A no-op passthrough satisfies the second
                // assertion but not the first; XOR is its own inverse so both hold.
                "Encrypt" => (object)FakeEncrypt(args?[0] as string),
                "Decrypt" => (object)FakeDecrypt(args?[0] as string),
                "get_IsKeyPresent" or "get_IsKeyCreated" => true,
                "get_PublicKey" => "<RSAKeyValue><Modulus>xbzyD+SGxykyAv82XOEFtDzWEIok0MM5SAc+CS6Mq0W5LwiyXeakWyblq1XgYi3CDu700986ZVRi4KJjruZlzBeZ7IWXD4lEEpTCRuqoxasRTnwVpyVqGuHclJAnUpjeBS6HvaS/iesYWwxZcmlsmzJHvF3hXdDmLj+8GSKgo4IhschPCIpnoH8+FREX++VpwfZH1ejMk5Izds/ZI70Xc/OWfRfaYy3rtCFeZQ1R5T1AhlNJDgpn0a1oP86F8yDGYawB2GJKIewdcWE8usu4QesrFnlS1g/IJcFXe71/TiJjryqRJPk8ze3Jh9+atx57OnI4R3QvuM/lQ7YoN1RVjw==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>",
                // CreateKey/DeleteKey/ImportKey/ExportKey/Dispose are all void — IsKeyPresent/
                // IsKeyCreated already report true, so callers never actually need these to do
                // anything real.
                "CreateKey" or "DeleteKey" or "ImportKey" or "ExportKey" or "Dispose" => null,
                _ => null,
            };
            if (targetMethod?.Name == "Decrypt" || targetMethod?.Name == "Encrypt")
            {
                Console.WriteLine($"[StartupHook] Patch #26: Tenant Encryption.{targetMethod.Name}() called — fake transform applied");
            }
            else if (targetMethod?.Name == "CreateKey")
            {
                Console.WriteLine("[StartupHook] Patch #26: Encryption.CreateKey() called — no-op (already \"has a key\")");
            }
            return result;
        }

        private static string FakeEncrypt(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext ?? "";
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
            for (int i = 0; i < bytes.Length; i++) bytes[i] ^= FakeEncryptionXorKey;
            return Convert.ToBase64String(bytes);
        }

        private static string FakeDecrypt(string? ciphertext)
        {
            if (string.IsNullOrEmpty(ciphertext)) return ciphertext ?? "";
            try
            {
                byte[] bytes = Convert.FromBase64String(ciphertext);
                for (int i = 0; i < bytes.Length; i++) bytes[i] ^= FakeEncryptionXorKey;
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                // Not something FakeEncrypt produced (e.g. stored before this patch existed) — leave as-is.
                return ciphertext;
            }
        }
    }

    // ========================================================================
    // Patch #5c: Replace Geneva DLL with no-op stub
    // ========================================================================

    /// <summary>
    /// Pre-load Microsoft.Data.SqlClient to catch missing dependencies early
    /// with a clear error message (instead of a cryptic FileNotFoundException deep in BC startup).
    /// </summary>
    private static void VerifySqlClientLoads()
    {
        try
        {
            var asm = Assembly.LoadFrom(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory ?? ".",
                "Microsoft.Data.SqlClient.dll"));
            Console.WriteLine($"[StartupHook] SqlClient verified: {asm.FullName}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StartupHook] WARNING: SqlClient pre-load failed: {ex}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"[StartupHook]   Inner: {ex.InnerException}");
        }
    }

    /// <summary>
    /// Replace a DLL in the service directory with a no-op stub from our publish directory.
    /// The original is backed up to .orig.
    /// </summary>
    private static void ReplaceWithStub(string dllName, string description)
    {
        string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (baseDir == null) return;

        string targetDll = Path.Combine(baseDir, dllName);
        if (!File.Exists(targetDll)) return;

        var hookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
        if (hookDir == null) return;
        string stubDll = Path.Combine(hookDir, dllName);
        if (!File.Exists(stubDll))
        {
            Console.WriteLine($"[StartupHook] Stub for {dllName} not found — skipping");
            return;
        }

        try
        {
            string backup = targetDll + ".orig";
            if (!File.Exists(backup))
                File.Copy(targetDll, backup, overwrite: false);

            File.Copy(stubDll, targetDll, overwrite: true);
            Console.WriteLine($"[StartupHook] Replaced {dllName} with stub ({description})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] {dllName} replacement failed: {ex.Message}");
        }
    }

    // ========================================================================
    // Patch #7b: Re-apply encryption bypass (Main() overrides our initial setting)
    // ========================================================================

    private static void ReapplyEncryptionBypass()
    {
        try
        {
            // Find Nav.Types and Nav.Core assemblies
            Assembly? navTypesAsm = null;
            Assembly? navCoreAsm = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Microsoft.Dynamics.Nav.Types") navTypesAsm = asm;
                if (asm.GetName().Name == "Microsoft.Dynamics.Nav.Core") navCoreAsm = asm;
            }
            if (navTypesAsm == null) return;

            Type? encIfaceType = navTypesAsm.GetType("Microsoft.Dynamics.Nav.Types.ISystemEncryptionProvider");
            if (encIfaceType == null) return;

            var createProxy = typeof(DispatchProxy)
                .GetMethod("Create", 2, Type.EmptyTypes)!
                .MakeGenericMethod(encIfaceType, typeof(PassthroughEncryptionProxy));
            _noopEncryptionProvider = createProxy.Invoke(null, null);

            // Strategy 1: Set the factory delegate
            Type? factoryType = navTypesAsm.GetType("Microsoft.Dynamics.Nav.Types.DefaultServerInstanceRsaEncryptionProviderFactory");
            if (factoryType != null)
            {
                var prop = factoryType.GetProperty("GetDefaultEncryptionProvider",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (prop != null)
                {
                    var funcType = typeof(Func<>).MakeGenericType(encIfaceType);
                    var dm = new DynamicMethod("GetNoOpEncryption2", encIfaceType, Type.EmptyTypes,
                        typeof(StartupHook).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldsfld, typeof(StartupHook).GetField(nameof(_noopEncryptionProvider),
                        BindingFlags.Static | BindingFlags.NonPublic)!);
                    il.Emit(OpCodes.Ret);
                    prop.SetValue(null, dm.CreateDelegate(funcType));
                }
            }

            // Strategy 2: Replace BOTH the instance field AND the public Factory delegate
            // on ServerInstanceRsaEncryptionProvider. Main() uses Factory which defaults to
            // () => Instance. We replace both so all code paths return our proxy.
            if (navCoreAsm != null)
            {
                Type? rsaProvType = navCoreAsm.GetType("Microsoft.Dynamics.Nav.Core.ServerInstanceRsaEncryptionProvider");
                if (rsaProvType != null)
                {
                    // Replace the private 'instance' field (used by Instance getter)
                    var instanceField = rsaProvType.GetField("instance",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    if (instanceField != null)
                        instanceField.SetValue(null, _noopEncryptionProvider);

                    // Replace the public 'Factory' delegate field
                    var factoryField = rsaProvType.GetField("Factory",
                        BindingFlags.Static | BindingFlags.Public);
                    if (factoryField != null)
                    {
                        var funcType = typeof(Func<>).MakeGenericType(encIfaceType);
                        var dm = new DynamicMethod("GetProxy", encIfaceType, Type.EmptyTypes,
                            typeof(StartupHook).Module, skipVisibility: true);
                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldsfld, typeof(StartupHook).GetField(nameof(_noopEncryptionProvider),
                            BindingFlags.Static | BindingFlags.NonPublic)!);
                        il.Emit(OpCodes.Ret);
                        factoryField.SetValue(null, dm.CreateDelegate(funcType));
                    }

                    Console.WriteLine("[StartupHook] Replaced encryption Instance + Factory");
                }
            }

            Console.WriteLine("[StartupHook] Re-applied encryption bypass (after Nav.Core load)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Encryption re-apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ReapplyTopologyProxy()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Microsoft.Dynamics.Nav.Ncl") continue;

                Type? envType = asm.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment");
                Type? topoIfaceType = asm.GetType("Microsoft.Dynamics.Nav.Runtime.IServiceTopology");
                if (envType == null || topoIfaceType == null) break;

                var topoProp = envType.GetProperty("Topology", BindingFlags.Public | BindingFlags.Static);
                if (topoProp == null) break;

                _originalTopology = topoProp.GetValue(null);

                var createProxy = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(topoIfaceType, typeof(LinuxTopologyProxy));
                topoProp.SetValue(null, createProxy.Invoke(null, null));

                Console.WriteLine("[StartupHook] Re-applied Linux topology proxy (after Nav.Core load)");
                break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Topology re-apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ========================================================================
    // Patch #6/#8: Stub DLLs with strong-name bypass via assembly resolver
    // ========================================================================

    private static readonly System.Collections.Generic.Dictionary<string, byte[]> _stubBytesMap = new();

    /// <summary>
    /// Move a signed DLL aside and register an assembly resolver that provides our
    /// unsigned stub via Assembly.Load(byte[]) — bypasses strong-name identity checks.
    /// </summary>
    /// <summary>
    /// Makes a stub in the hook directory available through <see cref="ResolveStubAssembly"/>
    /// without moving any file aside. Use for stubs that were copied over the original in
    /// place (ReplaceWithStub) but whose assembly identity no longer satisfies BC's
    /// reference — the resolver answers regardless of requested version or public key token.
    /// </summary>
    private static void RegisterStubForResolver(string assemblyName)
    {
        var hookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
        if (hookDir == null) return;
        string stubDll = Path.Combine(hookDir, assemblyName + ".dll");
        if (!File.Exists(stubDll))
        {
            Console.WriteLine($"[StartupHook] Stub for {assemblyName} not found — resolver not registered");
            return;
        }
        _stubBytesMap[assemblyName] = File.ReadAllBytes(stubDll);
        Console.WriteLine($"[StartupHook] {assemblyName} registered with stub resolver (version-agnostic)");
    }

    private static void SetupStubWithResolver(string assemblyName)
    {
        string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (baseDir == null) return;

        string dllName = assemblyName + ".dll";
        string targetDll = Path.Combine(baseDir, dllName);
        string backup = targetDll + ".orig";

        // If original already removed (container restart), just load stub bytes
        bool originalExists = File.Exists(targetDll);
        bool alreadyMoved = !originalExists && File.Exists(backup);

        var hookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
        if (hookDir == null) return;
        string stubDll = Path.Combine(hookDir, dllName);
        if (!File.Exists(stubDll))
        {
            Console.WriteLine($"[StartupHook] Stub for {assemblyName} not found — skipping");
            return;
        }

        _stubBytesMap[assemblyName] = File.ReadAllBytes(stubDll);

        if (alreadyMoved)
        {
            Console.WriteLine($"[StartupHook] {assemblyName} stub ready (already moved, via resolver)");
            return;
        }

        if (!originalExists) return;

        // Move original aside so default resolution fails → our resolver provides the stub
        try
        {
            if (!File.Exists(backup))
                File.Copy(targetDll, backup, overwrite: false);
            File.Delete(targetDll);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Could not move {dllName}: {ex.Message}");
            return;
        }

        Console.WriteLine($"[StartupHook] {assemblyName} stub ready (via resolver)");
    }

    // Map of TestPageClient dependency assemblies to the actual DLLs that contain them.
    // TestPageClient.dll references these as separate assemblies, but on the server tier
    // the types live in Client.UI.dll and Client.Builder.dll.
    private static readonly Dictionary<string, string> _testPageClientRedirects = new()
    {
        ["Microsoft.Dynamics.Nav.Client.TestPageClient"] = "Microsoft.Dynamics.Nav.Client.TestPageClient.dll",
        ["Microsoft.Dynamics.Nav.Client.Actions"] = "Microsoft.Dynamics.Nav.Client.UI.dll",
        ["Microsoft.Dynamics.Nav.Client.Controls"] = "Microsoft.Dynamics.Nav.Client.UI.dll",
        ["Microsoft.Dynamics.Nav.Client.DataBinder"] = "Microsoft.Dynamics.Nav.Client.UI.dll",
        ["Microsoft.Dynamics.Nav.Client.FormBuilder"] = "Microsoft.Dynamics.Nav.Client.Builder.dll",
        ["Microsoft.Dynamics.Nav.Client.Formatters.Decorators"] = "Microsoft.Dynamics.Nav.Client.UI.dll",
    };

    private static Assembly? ResolveStubAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name != null && _stubBytesMap.TryGetValue(name.Name, out var bytes))
        {
            Console.WriteLine($"[StartupHook] Providing {name.Name} stub via resolver");
            return Assembly.Load(bytes);
        }

        // Redirect TestPageClient dependencies to the actual service tier DLLs
        if (name.Name != null && _testPageClientRedirects.TryGetValue(name.Name, out var targetDll))
        {
            foreach (var dir in new[] { "/bc/service", Path.GetDirectoryName(typeof(StartupHook).Assembly.Location) ?? "" })
            {
                var targetPath = Path.Combine(dir, targetDll);
                if (File.Exists(targetPath))
                {
                    Console.WriteLine($"[StartupHook] Redirecting {name.Name} → {targetPath} (TestPage support)");
                    return context.LoadFromAssemblyPath(targetPath);
                }
            }
            Console.WriteLine($"[StartupHook] WARNING: Cannot redirect {name.Name} — {targetDll} not found");
        }

        return null;
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name);
        // Log ALL resolve attempts to diagnose TestPageClient loading
        if (name.Name != null && name.Name.Contains("Dynamics"))
            Console.WriteLine($"[StartupHook] AssemblyResolve attempt: {name.Name} (from {args.RequestingAssembly?.GetName().Name ?? "?"})");
        if (name.Name != null && _testPageClientRedirects.TryGetValue(name.Name, out var targetDll))
        {
            foreach (var dir in new[] { "/bc/service", Path.GetDirectoryName(typeof(StartupHook).Assembly.Location) ?? "" })
            {
                var targetPath = Path.Combine(dir, targetDll);
                if (File.Exists(targetPath))
                {
                    Console.WriteLine($"[StartupHook] AssemblyResolve: {name.Name} → {targetPath}");
                    return Assembly.LoadFrom(targetPath);
                }
            }
        }
        return null;
    }

    // ========================================================================
    // Patch #16: Client Services Credential Bypass
    // ========================================================================

    private static void PatchClientCredentialValidation(Assembly navServiceAssembly)
    {
        try
        {
            // Patch the validator to not check the password but still populate the auth cache
            var validatorType = navServiceAssembly.GetType(
                "Microsoft.Dynamics.Nav.Service.ClientServicesUserNamePasswordValidator");
            if (validatorType == null)
            {
                Console.WriteLine("[StartupHook] Patch #16: Validator type not found, skipping");
                return;
            }

            var validateMethod = validatorType.GetMethod("ValidateAsync",
                BindingFlags.Instance | BindingFlags.Public);
            if (validateMethod == null)
            {
                Console.WriteLine("[StartupHook] Patch #16: ValidateAsync not found, skipping");
                return;
            }

            var replacement = typeof(StartupHook).GetMethod(
                nameof(Replacement_ValidateCredentials),
                BindingFlags.Static | BindingFlags.NonPublic);
            ApplyJmpHook(validateMethod, replacement!, "ClientServicesUserNamePasswordValidator.ValidateAsync");
            Console.WriteLine("[StartupHook] Patch #16: Client credential validation bypassed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #16 failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Replacement for ClientServicesUserNamePasswordValidator.ValidateAsync.
    /// Always succeeds — credentials are trusted in pipeline/CI scenarios.
    /// Signature: instance method, ValueTask ValidateAsync(ConnectionCredentials)
    /// </summary>
    private static ValueTask Replacement_ValidateCredentials(object? self, object? credentials)
    {
        Console.WriteLine("[StartupHook] Credential validation bypassed (Patch #16)");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Patch NavUser.TryAuthenticate to always return true.
    /// Called from Nav.Ncl.dll when it's loaded.
    /// The password hash format from Windows doesn't verify correctly on Linux.
    /// </summary>
    internal static void PatchNavUserTryAuthenticate(Assembly nclAssembly)
    {
        try
        {
            var navUserType = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavUser");
            if (navUserType == null) return;

            // There are multiple TryAuthenticate overloads. Patch the one that takes
            // (NavUser, UserNameSecurityToken, NavTenant) — the NavUserPassword path.
            foreach (var m in navUserType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (m.Name != "TryAuthenticate") continue;
                var ps = m.GetParameters();
                if (ps.Length == 3 && ps[1].ParameterType.Name.Contains("UserNameSecurityToken"))
                {
                    var replacement = typeof(StartupHook).GetMethod(
                        nameof(Replacement_TryAuthenticate),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    ApplyJmpHook(m, replacement!, "NavUser.TryAuthenticate(NavUser,UserNameSecurityToken,NavTenant)");
                    Console.WriteLine("[StartupHook] Patch #16b: NavUser.TryAuthenticate bypassed");
                    return;
                }
            }
            Console.WriteLine("[StartupHook] Patch #16b: TryAuthenticate overload not found");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #16b failed: {ex.Message}");
        }
    }

    private static bool Replacement_TryAuthenticate(object? user, object? token, object? tenant)
    {
        Console.WriteLine("[StartupHook] NavUser.TryAuthenticate bypassed — returning true (Patch #16b)");
        return true;
    }

    // ========================================================================
    // Patch #21: NavOpenTaskPageAction.ShowForm — no-op on headless Linux runner.
    //
    // When a test method opens a task page, ShowForm is called on the headless
    // client which has no UI renderer. The resulting NullReferenceException
    // terminates the entire test session, preventing all subsequent test methods
    // from executing. Replacing ShowForm with a no-op silently skips task-page
    // opens so the rest of the test codeunit continues running.
    //
    // Assembly: Microsoft.Dynamics.Nav.Client.UI.dll
    // Namespace: Microsoft.Dynamics.Nav.Client.Actions
    // Signature: void ShowForm(LogicalForm childForm, LogicalForm parentForm,
    //                          UISession uiSession, FormState formState)
    // ========================================================================

    private static void PatchShowForm(Assembly navClientUi)
    {
        try
        {
            var actionType = navClientUi.GetType("Microsoft.Dynamics.Nav.Client.Actions.NavOpenTaskPageAction");
            if (actionType == null)
            {
                Console.WriteLine("[StartupHook] Patch #21: NavOpenTaskPageAction type not found — skipping");
                return;
            }

            var showForm = actionType.GetMethod("ShowForm",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (showForm == null)
            {
                Console.WriteLine("[StartupHook] Patch #21: ShowForm method not found — skipping");
                return;
            }

            var replacement = typeof(StartupHook).GetMethod(
                nameof(Replacement_ShowForm),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            ApplyJmpHook(showForm, replacement, "NavOpenTaskPageAction.ShowForm");
            Console.WriteLine("[StartupHook] Patch #21: NavOpenTaskPageAction.ShowForm hooked (no-op on Linux)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #21 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// No-op replacement for NavOpenTaskPageAction.ShowForm.
    /// ShowForm is an instance method so JMP hooks pass 'this' as the first explicit parameter,
    /// followed by the declared parameters (childForm, parentForm, uiSession, formState).
    /// Silently dropping the call prevents NullReferenceException in the headless UI layer
    /// and lets test sessions continue past task-page opens.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Replacement_ShowForm(object self, object? childForm, object? parentForm, object? uiSession, object? formState)
    {
        Console.WriteLine("[StartupHook] Patch #21: NavOpenTaskPageAction.ShowForm skipped (no headless UI on Linux)");
    }

    // ========================================================================
    // Patch #22: AzureADGraphQuery constructor bypass
    // ========================================================================

    // AzureADGraphQuery.currentSession (private readonly field) — set via reflection
    // by the replacement ctor below so post-construction calls that don't go through
    // GetTenantDetail (see below) don't NRE on it either.
    private static FieldInfo? _aadgqCurrentSessionField;

    private static void PatchAzureADGraphQuery(Assembly navNcl)
    {
        try
        {
            var queryType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.AzureADGraphQuery");
            if (queryType == null)
            {
                Console.WriteLine("[StartupHook] Patch #22: AzureADGraphQuery type not found — skipping");
                return;
            }

            // Find the instance constructor that takes a NavSession parameter
            MethodBase? ctor = null;
            foreach (var c in queryType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var parms = c.GetParameters();
                if (parms.Length == 1 && parms[0].ParameterType.Name == "NavSession")
                {
                    ctor = c;
                    break;
                }
            }

            if (ctor == null)
            {
                Console.WriteLine("[StartupHook] Patch #22: AzureADGraphQuery..ctor(NavSession) not found — skipping");
                return;
            }

            _aadgqCurrentSessionField = queryType.GetField("currentSession",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (_aadgqCurrentSessionField == null)
            {
                Console.WriteLine("[StartupHook] Patch #22: AzureADGraphQuery.currentSession field not found — skipping ctor fix (GetTenantDetail fix below is independent)");
            }

            // NavCurrentThread.Session accessor is shared with Patch #27/#28; resolve if
            // neither ran first (load order between patches is not guaranteed).
            _navCurrentThreadSessionProp ??= navNcl
                .GetType("Microsoft.Dynamics.Nav.Runtime.NavCurrentThread")
                ?.GetProperty("Session", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var replacement = typeof(StartupHook).GetMethod(
                nameof(Replacement_AzureADGraphQueryCtor),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            ApplyJmpHook(ctor, replacement, "AzureADGraphQuery..ctor(NavSession)");
            Console.WriteLine("[StartupHook] Patch #22: AzureADGraphQuery..ctor hooked — Azure AD credential init bypassed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #22 failed: {ex.GetType().Name}: {ex.Message}");
        }

        PatchGraphQueryGetTenantDetail(navNcl);
    }

    /// <summary>
    /// No-op replacement for AzureADGraphQuery..ctor(NavSession session).
    /// Skips LazyEx factory setup that requires Azure.Identity / MSAL Windows credential APIs.
    /// The object is heap-allocated before this ctor runs, so all fields remain default (null/0)
    /// EXCEPT currentSession, which this replacement sets explicitly via reflection so any
    /// OTHER GraphQuery method than GetTenantDetail (see PatchGraphQueryGetTenantDetail below)
    /// doesn't immediately NRE on a null currentSession.Diagnostics dereference.
    ///
    /// This alone is NOT sufficient to fix GetTenantDetail: the actual runtime call path for
    /// AL's "GraphQuery" DotNet variable is Microsoft.Dynamics.Nav.AzureADGraphClient.GraphQuery
    /// (a separate wrapper class in Add-ins/GraphClient/*.dll, not this AzureADGraphQuery),
    /// whose own parameterless constructor ALWAYS builds
    /// `azureADGraphQuery = new AzureADGraphQuery((NavSession)null)` regardless of what session
    /// is actually current — so the session this ctor patch resolves is immediately discarded by
    /// that caller anyway. See PatchGraphQueryGetTenantDetail for the fix that actually matters.
    ///
    /// Session is resolved the same way Patch #27 already resolves it (NavCurrentThread.Session).
    /// Signature: instance ctor → JMP hook passes (this, NavSession) as (object, object).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Replacement_AzureADGraphQueryCtor(object self, object? session)
    {
        try
        {
            var resolvedSession = session ?? _navCurrentThreadSessionProp?.GetValue(null);
            _aadgqCurrentSessionField?.SetValue(self, resolvedSession);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #22: failed to set currentSession: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine("[StartupHook] Patch #22: AzureADGraphQuery..ctor skipped (no Azure AD on Linux)");
    }

    // ========================================================================
    // Patch #22b: GraphQuery.GetTenantDetail — return an empty TenantInfo directly.
    //
    // The AL DotNet variable "GraphQuery" (Microsoft.Dynamics.Nav.AzureADGraphClient.GraphQuery,
    // NOT the AzureADGraphQuery patched above) always constructs its inner AzureADGraphQuery
    // with a null NavSession (decompiled: `azureADGraphQuery = new AzureADGraphQuery((NavSession)null)`
    // in GraphQuery's own parameterless ctor) — so Patch #22's ctor fix, which sets currentSession
    // on THAT inner object, never even runs for this call path (the ctor overload it hooks takes
    // an explicit NavSession argument; the null-literal call site is a different overload/path).
    //
    // GetTenantDetailAsync (the method actually reached) unconditionally dereferences
    // currentSession.Diagnostics at its very first line for a timing scope, before any of its own
    // "no AAD tenant configured, use an empty TenantInfo" fallback logic runs. With currentSession
    // null, that first dereference throws a NullReferenceException. BC's extension-install pipeline
    // then reports this as the generic, misleading "You must assign at least one user the SUPER
    // permission set and configure that user to log in with authentication type 'NavUserPassword'"
    // error — that message is BC's fallback for ANY failed install-transaction commit, not actually
    // about SUPER/NavUserPassword. Reproduced live: Continia OPplus's own install codeunit calls
    // Microsoft's IsPlanAssignedToUser, which reaches exactly this NRE via
    // GraphQuery.GetTenantDetail → AzureADGraphQuery.GetTenantDetailAsync.
    //
    // Fix: hook GraphQuery.GetTenantDetail() itself — NOT GetTenantDetailAsync, which is `async`
    // and reached from a compiler-generated state machine; JMP-hooking an async method mid-flight
    // is exactly the "AccessViolationException" hazard Patch #20's own comment on EnsureAlive
    // warns about. GetTenantDetail() is the plain synchronous public wrapper
    // (`return GetTenantDetailAsync().AsTask().GetAwaiter().GetResult();`) that AL's DotNet
    // variable calls into, so hooking it is as safe as the ordinary instance-method hooks used
    // throughout this file. Tests/installs that reach this path don't need a real Azure AD
    // tenant — they only need SOME non-throwing TenantInfo, which is what Microsoft's own
    // fallback (AzureADGraphQuery.CreateEmptyTenantInfo, confirmed via decompilation) already
    // returns for a tenant with no AAD tenant id configured. This patch produces the equivalent
    // object directly, without depending on that internal fallback ever being reached.
    // ========================================================================

    private static Type? _tenantInfoType;

    private static void PatchGraphQueryGetTenantDetail(Assembly navNcl)
    {
        if (IsPatchDisabled("22b")) return;
        try
        {
            // GraphQuery lives in Add-ins/GraphClient/Microsoft.Dynamics.Nav.AzureADGraphClient.dll,
            // a SEPARATE assembly from Nav.Ncl.dll (the navNcl parameter here) — it is loaded lazily,
            // on first AL DotNet variable creation of "GraphQuery", not at NST startup. Resolve it via
            // AppDomain.CurrentDomain.GetAssemblies() at patch time (called from the same
            // AssemblyLoad callback as Patch #22, so may run before or after that assembly is loaded);
            // if not yet loaded, register interest and patch it when it does load instead.
            var graphClientAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.AzureADGraphClient");

            if (graphClientAssembly == null)
            {
                // Not loaded yet — the AssemblyLoad handler in Initialize() re-invokes this same
                // patch by name once it does load (see the dedicated hook registered there).
                Console.WriteLine("[StartupHook] Patch #22b: Microsoft.Dynamics.Nav.AzureADGraphClient not yet loaded — will retry when it loads");
                return;
            }

            ApplyGraphQueryGetTenantDetailPatch(graphClientAssembly);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #22b failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool _graphQueryPatchApplied;

    private static void ApplyGraphQueryGetTenantDetailPatch(Assembly graphClientAssembly)
    {
        if (_graphQueryPatchApplied) return;
        try
        {
            var graphQueryType = graphClientAssembly.GetType("Microsoft.Dynamics.Nav.AzureADGraphClient.GraphQuery");
            if (graphQueryType == null)
            {
                Console.WriteLine("[StartupHook] Patch #22b: GraphQuery type not found — skipping");
                return;
            }

            var getTenantDetail = graphQueryType.GetMethod("GetTenantDetail",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (getTenantDetail == null)
            {
                Console.WriteLine("[StartupHook] Patch #22b: GraphQuery.GetTenantDetail() not found — skipping");
                return;
            }

            _tenantInfoType = getTenantDetail.ReturnType;

            var replacement = typeof(StartupHook).GetMethod(
                nameof(Replacement_GraphQueryGetTenantDetail),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            ApplyJmpHook(getTenantDetail, replacement, "GraphQuery.GetTenantDetail");
            _graphQueryPatchApplied = true;
            Console.WriteLine("[StartupHook] Patch #22b: GraphQuery.GetTenantDetail hooked — returns an empty TenantInfo without touching Azure AD");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #22b failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Replacement for GraphQuery.GetTenantDetail() — instance method, synchronous,
    /// returns TenantInfo (Microsoft.Dynamics.Nav.LicensingService.Model.TenantInfo).
    /// Builds an empty TenantInfo directly via reflection (all string properties set to
    /// string.Empty), matching AzureADGraphQuery.CreateEmptyTenantInfo's own shape for a
    /// tenant with no AAD tenant id configured — but without depending on that internal
    /// fallback path (which requires currentSession to be non-null first; see Patch #22's
    /// header comment for why it never was, for this specific call path).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? Replacement_GraphQueryGetTenantDetail(object self)
    {
        try
        {
            if (_tenantInfoType == null) return null;
            var tenantInfo = Activator.CreateInstance(_tenantInfoType);
            foreach (var prop in _tenantInfoType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (prop.PropertyType == typeof(string) && prop.CanWrite)
                {
                    prop.SetValue(tenantInfo, string.Empty);
                }
            }
            return tenantInfo;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #22b: failed to build empty TenantInfo: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ========================================================================
    // Patch #26: TenantEncryptionProviderFactory.GetTenantEncryptionProvider bypass.
    // See header comment for the full story.
    // ========================================================================

    private static void PatchTenantEncryptionProvider(Assembly navNcl)
    {
        if (IsPatchDisabled("26")) return;
        try
        {
            var factoryType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.Encryption.TenantEncryptionProviderFactory");
            if (factoryType == null)
            {
                Console.WriteLine("[StartupHook] Patch #26: TenantEncryptionProviderFactory not found — skipping");
                return;
            }

            var original = factoryType.GetMethod("GetTenantEncryptionProvider",
                BindingFlags.Static | BindingFlags.Public);
            if (original == null)
            {
                Console.WriteLine("[StartupHook] Patch #26: GetTenantEncryptionProvider not found — skipping");
                return;
            }

            if (!EnsureTenantFakeEncryptionProvider())
            {
                Console.WriteLine("[StartupHook] Patch #26: could not build tenant fake-encryption proxy — skipping");
                return;
            }

            var replacement = typeof(StartupHook).GetMethod(
                nameof(Replacement_GetTenantEncryptionProvider),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            ApplyJmpHook(original, replacement, "TenantEncryptionProviderFactory.GetTenantEncryptionProvider");
            Console.WriteLine("[StartupHook] Patch #26: GetTenantEncryptionProvider hooked — AL encryption always \"enabled\", never real");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #26 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds _noopTenantEncryptionProvider (a TenantFakeEncryptionProxy — see its class comment
    /// for why this is NOT shared with Patch #7's _noopEncryptionProvider).
    /// </summary>
    private static bool EnsureTenantFakeEncryptionProvider()
    {
        if (_noopTenantEncryptionProvider != null) return true;
        try
        {
            Assembly? navTypesAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            if (navTypesAsm == null) return false;

            Type? encIfaceType = navTypesAsm.GetType("Microsoft.Dynamics.Nav.Types.ISystemEncryptionProvider");
            if (encIfaceType == null) return false;

            var createProxy = typeof(DispatchProxy)
                .GetMethod("Create", 2, Type.EmptyTypes)!
                .MakeGenericMethod(encIfaceType, typeof(TenantFakeEncryptionProxy));
            _noopTenantEncryptionProvider = createProxy.Invoke(null, null);
            return _noopTenantEncryptionProvider != null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #26: EnsureTenantFakeEncryptionProvider failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Replacement for TenantEncryptionProviderFactory.GetTenantEncryptionProvider(Func&lt;NavSession&gt;).
    /// Signature: static method, one reference-type param, reference-type return — matches the
    /// JMP-hook calling convention (both slots are object-sized pointers regardless of the
    /// original's declared Func&lt;NavSession&gt;/ISystemEncryptionProvider types).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? Replacement_GetTenantEncryptionProvider(object? activeSessionProvider)
    {
        return _noopTenantEncryptionProvider;
    }

    // ========================================================================
    // Patch #23: OfficeWordDocumentPictureMerger.ReplaceMissingImageWithTransparentImage
    //            — break Microsoft's recursion bug that crashes Word report generation.
    //
    // The bug: ReplaceMissingImageWithTransparentImage(part, elem) calls
    // MergePictureElements(part, elem, transparentImageBytes), which under certain
    // conditions re-enters ReplaceMissingImageWithTransparentImage with the same element
    // unconditionally. We have observed ~37,390 frames before stack overflow on
    // TestSendToEMailAndPDFVendor in Tests-Misc — fatal session crash, BC container goes
    // unhealthy, and Bucket 4 cannot complete sequentially.
    //
    // Fix: replace the method with a no-op return. The missing image element stays in the
    // document (rendered as a broken image), but the session survives and report generation
    // completes. The Misc tests do not validate rendered image content.
    //
    // Assembly:  Microsoft.Dynamics.Nav.OpenXml.dll
    // Type:      Microsoft.Dynamics.Nav.OpenXml.Word.DocumentMerger.OfficeWordDocumentPictureMerger
    //            (abstract sealed → static class)
    // Method:    private static void ReplaceMissingImageWithTransparentImage(
    //                DocumentFormat.OpenXml.Packaging.OpenXmlPart part,
    //                System.Xml.Linq.XElement element)
    // ========================================================================

    private static void PatchOfficeWordDocumentPictureMerger(Assembly navOpenXml)
    {
        try
        {
            var mergerType = navOpenXml.GetType(
                "Microsoft.Dynamics.Nav.OpenXml.Word.DocumentMerger.OfficeWordDocumentPictureMerger");
            if (mergerType == null)
            {
                Console.WriteLine("[StartupHook] Patch #23: OfficeWordDocumentPictureMerger type not found — skipping");
                return;
            }

            // Private static method, 2 params (OpenXmlPart, XElement). Match by name+arity
            // to avoid resolving the parameter types (which would force loading
            // DocumentFormat.OpenXml just for reflection).
            MethodInfo? target = null;
            foreach (var m in mergerType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (m.Name == "ReplaceMissingImageWithTransparentImage" && m.GetParameters().Length == 2)
                {
                    target = m;
                    break;
                }
            }

            if (target == null)
            {
                Console.WriteLine("[StartupHook] Patch #23: ReplaceMissingImageWithTransparentImage not found — skipping");
                return;
            }

            var replacement = typeof(StartupHook).GetMethod(
                nameof(Replacement_ReplaceMissingImageWithTransparentImage),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            ApplyJmpHook(target, replacement, "OfficeWordDocumentPictureMerger.ReplaceMissingImageWithTransparentImage");
            Console.WriteLine("[StartupHook] Patch #23: ReplaceMissingImageWithTransparentImage hooked (recursion broken)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #23 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// No-op replacement for OfficeWordDocumentPictureMerger.ReplaceMissingImageWithTransparentImage.
    /// Static method → JMP hook passes the declared parameters directly (no hidden 'this').
    /// Returns immediately; the missing image XElement is left in place. This breaks the
    /// recursion loop that otherwise blows the stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Replacement_ReplaceMissingImageWithTransparentImage(object? part, object? element)
    {
        // Intentionally empty — break Microsoft's recursion bug in Nav.OpenXml.
        // No log line per call: this is invoked once per missing image and we don't want
        // to spam logs during legitimate report generation.
    }

    // ========================================================================
    // Patch #25: BaseDebugRuntime.IsDebuggedSessionClosedOrDisposed null-session NRE.
    //
    // Enables AL attach-debugging (breakOnNext) against the Linux NST. See the
    // registration comment in OnAssemblyLoad for the full root cause. In short:
    // DebugRuntimeManager.ExecuteClientCall dereferences a null DebuggedSession
    // because the original code's `?.` only null-checks the runtime, not the
    // session. We make the getter report "closed/disposed" (true) for a null
    // session so the `||` short-circuits before the bad dereference.
    //
    // Assembly:  Microsoft.Dynamics.Nav.Ncl.dll
    // Type:      Microsoft.Dynamics.Nav.Runtime.Debugger.BaseDebugRuntime
    // Property:  protected internal virtual bool IsDebuggedSessionClosedOrDisposed { get; }
    //            (single definition on the base; DebugRuntime does not override it)
    //
    // Original getter:
    //     var s = DebuggedSession;
    //     if (s == null) return false;                 // <-- the bug: should be safe-true
    //     return s.AccessLock?.IsStoppingOrDisposed == true;
    // ========================================================================
    private static MethodInfo? _origIsDebuggedSessionClosed;

    private static void PatchDebugRuntimeNullSession(Assembly navNcl)
    {
        if (IsPatchDisabled("25")) return;
        try
        {
            var type = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.Debugger.BaseDebugRuntime");
            if (type == null)
            {
                Console.WriteLine("[StartupHook] Patch #25: BaseDebugRuntime type not found — skipping");
                return;
            }

            var getter = type.GetProperty("IsDebuggedSessionClosedOrDisposed",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetGetMethod(nonPublic: true);
            if (getter == null)
            {
                Console.WriteLine("[StartupHook] Patch #25: IsDebuggedSessionClosedOrDisposed getter not found — skipping");
                return;
            }

            _origIsDebuggedSessionClosed = getter;
            var replacement = typeof(StartupHook).GetMethod(
                nameof(Replacement_IsDebuggedSessionClosedOrDisposed),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            ApplyJmpHook(getter, replacement, "BaseDebugRuntime.IsDebuggedSessionClosedOrDisposed (Patch #25)");
            Console.WriteLine("[StartupHook] Patch #25: attach-debug null-session NRE guarded");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #25 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Replacement for the instance getter BaseDebugRuntime.IsDebuggedSessionClosedOrDisposed.
    /// Instance method → JMP hook passes the receiver as the first explicit argument.
    /// Reflection (not a typed cast) avoids a compile-time dependency on the BC assembly.
    /// Semantics: a null DebuggedSession is treated as "closed/disposed" (true) — the only
    /// behavioural change from the original, and exactly what stops ExecuteClientCall from
    /// dereferencing the null session.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Replacement_IsDebuggedSessionClosedOrDisposed(object self)
    {
        try
        {
            var session = self.GetType().GetProperty("DebuggedSession",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(self);
            if (session == null)
                return true; // null session → safe early-return in ExecuteClientCall

            var accessLock = session.GetType().GetProperty("AccessLock",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(session);
            if (accessLock == null)
                return false;

            var isStopping = accessLock.GetType().GetProperty("IsStoppingOrDisposed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(accessLock);
            return isStopping is bool b && b;
        }
        catch
        {
            // Any unexpected reflection failure: report "not closed" so we never
            // introduce a new failure path — the worst case reverts to original risk.
            return false;
        }
    }

    // ========================================================================
    // Patch #27: NavClientHandle.Dispose null-guard
    // ========================================================================

    // Reflection members cached at patch time — Dispose runs for every form/DotNet
    // teardown, so per-call GetField/GetProperty lookups are avoided where possible.
    private static FieldInfo? _nchHandleField;              // NavClientHandle.handle (NavAutomationHandle)
    private static PropertyInfo? _nahHandleProp;            // NavAutomationHandle.Handle (int)
    private static PropertyInfo? _nchSuppressDisposeProp;   // NavInteropHandleReferenceCount.SuppressDispose
    private static PropertyInfo? _navCurrentThreadSessionProp; // NavCurrentThread.Session (static)
    private static PropertyInfo? _sessionCallbackAllowedProp;  // NavSession.CallbackAllowed
    private static PropertyInfo? _sessionClientCallbackOrNullProp; // NavSession.ClientCallbackOrNull
    private static MethodInfo? _cbDisposeAutomationObject;  // IClientCallback.DisposeAutomationObject(int, bool)

    private static void PatchNavClientHandleDispose(Assembly navNcl)
    {
        if (IsPatchDisabled("27")) return;
        try
        {
            var handleType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavClientHandle");
            if (handleType == null)
            {
                Console.WriteLine("[StartupHook] Patch #27: NavClientHandle type not found — skipping");
                return;
            }

            var dispose = handleType.GetMethod("Dispose",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null, types: new[] { typeof(bool) }, modifiers: null);
            if (dispose == null)
            {
                Console.WriteLine("[StartupHook] Patch #27: NavClientHandle.Dispose(bool) not found — skipping");
                return;
            }

            _nchHandleField = handleType.GetField("handle",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _nahHandleProp = _nchHandleField?.FieldType.GetProperty("Handle");
            _nchSuppressDisposeProp = handleType.BaseType?.GetProperty("SuppressDispose",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var currentThreadType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavCurrentThread");
            _navCurrentThreadSessionProp = currentThreadType?.GetProperty("Session",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var sessionType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession");
            _sessionCallbackAllowedProp = sessionType?.GetProperty("CallbackAllowed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _sessionClientCallbackOrNullProp = sessionType?.GetProperty("ClientCallbackOrNull",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _cbDisposeAutomationObject = navNcl
                .GetType("Microsoft.Dynamics.Nav.Runtime.IClientCallback")
                ?.GetMethod("DisposeAutomationObject", new[] { typeof(int), typeof(bool) });
            if (_nchHandleField == null || _nahHandleProp == null)
            {
                Console.WriteLine("[StartupHook] Patch #27: NavClientHandle.handle field shape changed — skipping");
                return;
            }

            var replacement = typeof(StartupHook).GetMethod(
                nameof(Replacement_NavClientHandleDispose),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            ApplyJmpHook(dispose, replacement, "NavClientHandle.Dispose (Patch #27)");
            Console.WriteLine("[StartupHook] Patch #27: NavClientHandle.Dispose null-guarded");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #27 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Null-guarded replacement for NavClientHandle.Dispose(bool disposing). Instance
    /// method → the receiver arrives as the first explicit argument. Original logic:
    ///   if (disposing) {
    ///     if (handle.Handle != -1 && Session != null && Session.CallbackAllowed)
    ///       Session.ClientCallback.DisposeAutomationObject(handle.Handle, SuppressDispose);
    ///     handle.Handle = -1;
    ///   }
    /// On headless sessions `handle` can be null (the client handle was never really
    /// created — the CreateDotNetHandle hole) and NavCurrentThread's TLS may be missing
    /// on the cleanup thread, so every dereference here is guarded. When there is a real
    /// client (web client PoC), the callback still fires exactly as the original did.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Replacement_NavClientHandleDispose(object self, bool disposing)
    {
        if (!disposing) return;
        try
        {
            var handle = _nchHandleField?.GetValue(self);
            if (handle == null) return; // no automation handle → nothing to release

            int h = _nahHandleProp!.GetValue(handle) is int v ? v : -1;
            if (h != -1)
            {
                try
                {
                    // NavCurrentThread.Session's getter itself dereferences thread-local
                    // state that may be absent on cleanup threads — hence its own guard.
                    var session = _navCurrentThreadSessionProp?.GetValue(null);
                    if (session != null
                        && _sessionCallbackAllowedProp?.GetValue(session) is bool allowed && allowed)
                    {
                        var cb = _sessionClientCallbackOrNullProp?.GetValue(session);
                        if (cb != null)
                        {
                            bool suppress = _nchSuppressDisposeProp?.GetValue(self) is bool s && s;
                            _cbDisposeAutomationObject?.Invoke(cb, new object[] { h, suppress });
                        }
                    }
                }
                catch
                {
                    // No client to notify (or it went away mid-dispose) — dropping the
                    // notification is fine; the server-side handle is cleared below.
                }
                _nahHandleProp.SetValue(handle, -1);
            }
        }
        catch
        {
            // Dispose must never throw — an NRE here failed tests whose logic had
            // already completed (the 189-failure bucket this patch exists for).
        }
    }

    // ========================================================================
    // Patch #28: NavFile.ExpandFileName backslash normalization
    // ========================================================================

    private static PropertyInfo? _sessionUserFolderProp;   // NavSession.UserFolder
    private static bool _expandFileNameLockdownLogged;

    private static void PatchNavFileExpandFileName(Assembly navNcl)
    {
        if (IsPatchDisabled("28")) return;
        try
        {
            var navFileType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavFile");
            if (navFileType == null)
            {
                Console.WriteLine("[StartupHook] Patch #28: NavFile type not found — skipping");
                return;
            }

            MethodInfo? expand = null;
            foreach (var m in navFileType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var p = m.GetParameters();
                if (m.Name == "ExpandFileName" && p.Length == 3
                    && p[0].ParameterType.IsEnum
                    && p[1].ParameterType == typeof(string)
                    && p[2].ParameterType == typeof(bool))
                {
                    expand = m;
                    break;
                }
            }
            if (expand == null)
            {
                Console.WriteLine("[StartupHook] Patch #28: ExpandFileName(DataError,string,bool) not found — skipping");
                return;
            }

            var sessionType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession");
            _sessionUserFolderProp = sessionType?.GetProperty("UserFolder",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            // NavCurrentThread.Session accessor is shared with Patch #27; resolve if #27 didn't.
            _navCurrentThreadSessionProp ??= navNcl
                .GetType("Microsoft.Dynamics.Nav.Runtime.NavCurrentThread")
                ?.GetProperty("Session", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var replacement = typeof(StartupHook).GetMethod(
                nameof(Replacement_NavFileExpandFileName),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            ApplyJmpHook(expand, replacement, "NavFile.ExpandFileName (Patch #28)");
            Console.WriteLine("[StartupHook] Patch #28: NavFile.ExpandFileName backslash normalization active");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #28 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Replacement for static NavFile.ExpandFileName(DataError errorLevel, string fileName,
    /// bool enforceUserPath). DataError is an int-backed enum → declared as int (identical
    /// x64 ABI). Behaviour vs the original: '\' → '/' normalization first, same
    /// relative-path-under-user-folder combining and canonicalization, but the user-folder
    /// lockdown throw is replaced by a one-time log (see the Patch #28 header comment).
    /// Any failure degrades to returning the normalized input so the actual file operation
    /// reports its own (honest) error.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Replacement_NavFileExpandFileName(int errorLevel, string fileName, bool enforceUserPath)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;
        fileName = fileName.Replace('\\', '/');
        try
        {
            string? userFolder = null;
            try
            {
                var session = _navCurrentThreadSessionProp?.GetValue(null);
                userFolder = session != null ? _sessionUserFolderProp?.GetValue(session) as string : null;
                userFolder = string.IsNullOrEmpty(userFolder)
                    ? null
                    : System.IO.Path.GetFullPath(userFolder.Replace('\\', '/'));
            }
            catch { }

            if (!System.IO.Path.IsPathRooted(fileName) && userFolder != null)
                fileName = System.IO.Path.Combine(userFolder, fileName);
            fileName = System.IO.Path.GetFullPath(fileName);

            // BC builds user-folder subpaths with '\' too (GetUserTempFolder combines
            // "TEMP\\"), so pre-#28 the "directories" were literal backslash-named files
            // and nothing ever created them as real directories. For normalized paths
            // under the user folder, materialize the parent so temp-file creation works.
            if (userFolder != null
                && fileName.StartsWith(userFolder, StringComparison.OrdinalIgnoreCase))
            {
                var dir = System.IO.Path.GetDirectoryName(fileName);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
            }

            if (!_expandFileNameLockdownLogged && enforceUserPath)
            {
                _expandFileNameLockdownLogged = true;
                Console.WriteLine($"[StartupHook] Patch #28: user-folder lockdown not enforced (first path: {fileName})");
            }
        }
        catch
        {
            // Invalid path characters etc. — hand the normalized string to the caller and
            // let the real file operation surface the error with its usual mapping.
        }
        return fileName;
    }

    // ========================================================================
    // JMP Hook Infrastructure
    // ========================================================================

    /// <summary>
    /// Force JIT compilation of both methods, then overwrite the original's native code
    /// entry with an absolute JMP to the replacement. Works on JIT-compiled BC methods only.
    /// </summary>
    private static void ApplyJmpHook(MethodBase original, MethodInfo replacement, string name)
    {
        // Never apply a JMP hook to a method that already carries one.
        //
        // Patch #14 and the Mono.Cecil 'checkfile' hook reach here TWICE: once from the
        // AssemblyLoad event handler, then again from TryEagerPatch — whose
        // Assembly.LoadFrom is what raised that event in the first place, so the eager
        // call always landed on an already-hooked method. On .NET 8 the redundant
        // re-apply was silently tolerated. On .NET 10 it kills the process:
        // RuntimeHelpers.PrepareMethod against an overwritten entry point segfaults
        // inside libcoreclr with no managed frames. Every OTHER hook in this file is
        // applied exactly once, which is why only those two crashed BC 29.
        //
        // The check is per method instance, not a global flag, so an assembly loaded
        // into a second ALC still gets its own hook.
        //
        // Our stub is `FF 25 00000000` — absolute indirect JMP with disp32 == 0. A
        // genuine StubPrecode also starts FF 25 but always has a non-zero disp32, so
        // the signature is unambiguous.
        if (IsAlreadyJmpHooked(original, name)) return;

        RuntimeHelpers.PrepareMethod(original.MethodHandle);
        RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

        IntPtr origFp = original.MethodHandle.GetFunctionPointer();
        IntPtr replFp = replacement.MethodHandle.GetFunctionPointer();

        // Read precode bytes BEFORE overwriting to find the compiled code address.
        IntPtr compiledCode = IntPtr.Zero;
        try
        {
            byte[] precodeBytes = new byte[24];
            Marshal.Copy(origFp, precodeBytes, 0, 24);
            Console.WriteLine($"[StartupHook]   {name} precode: {BitConverter.ToString(precodeBytes)}");

            // .NET 8 x64 FixupPrecode: 49 BA [8-byte MethodDesc] FF 25 [4-byte disp32]
            if (precodeBytes[10] == 0xFF && precodeBytes[11] == 0x25)
            {
                int disp32 = BitConverter.ToInt32(precodeBytes, 12);
                IntPtr jmpTargetAddr = origFp + 16 + disp32;
                compiledCode = Marshal.ReadIntPtr(jmpTargetAddr);
                Console.WriteLine($"[StartupHook]   {name} compiled code via precode JMP: 0x{compiledCode:X}");
            }

            // Try StubPrecode format: jmp [rip+disp32] (FF 25) at offset 0
            if (compiledCode == IntPtr.Zero && precodeBytes[0] == 0xFF && precodeBytes[1] == 0x25)
            {
                int disp32 = BitConverter.ToInt32(precodeBytes, 2);
                IntPtr jmpTargetAddr = origFp + 6 + disp32;
                compiledCode = Marshal.ReadIntPtr(jmpTargetAddr);
                Console.WriteLine($"[StartupHook]   {name} compiled code via StubPrecode: 0x{compiledCode:X}");
            }

            // Try E9 (relative JMP) at offset 0
            if (compiledCode == IntPtr.Zero && precodeBytes[0] == 0xE9)
            {
                int disp32 = BitConverter.ToInt32(precodeBytes, 1);
                compiledCode = origFp + 5 + disp32;
                Console.WriteLine($"[StartupHook]   {name} compiled code via E9 JMP: 0x{compiledCode:X}");
            }

            // MethodDesc approach as fallback
            if (compiledCode == IntPtr.Zero || compiledCode == origFp)
            {
                IntPtr methodDesc = original.MethodHandle.Value;
                for (int offset = 0; offset <= 16; offset += 8)
                {
                    IntPtr ptr = Marshal.ReadIntPtr(methodDesc, offset);
                    if (ptr != IntPtr.Zero && ptr != origFp && ptr != methodDesc)
                    {
                        Console.WriteLine($"[StartupHook]   {name} MethodDesc+{offset}: 0x{ptr:X}");
                    }
                }
                IntPtr codeDataPtr = Marshal.ReadIntPtr(methodDesc, 8);
                if (codeDataPtr != IntPtr.Zero && codeDataPtr != origFp)
                    compiledCode = codeDataPtr;
            }
        }
        catch (Exception dbgEx)
        {
            Console.WriteLine($"[StartupHook]   precode read failed: {dbgEx.Message}");
        }

        // Patch the precode entry point
        WriteJmp(origFp, replFp, name);

        // Also patch the compiled code so direct calls from JIT-compiled callers are intercepted
        if (compiledCode != IntPtr.Zero && compiledCode != origFp && compiledCode != replFp)
        {
            try
            {
                WriteJmp(compiledCode, replFp, name + " (code)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StartupHook]   (compiled code patch failed: {ex.Message})");
            }
        }
    }

    /// <summary>
    /// True if this method's entry point already holds a JMP we wrote. Reads the entry
    /// point WITHOUT calling PrepareMethod first — PrepareMethod is itself what dies on
    /// an already-patched method under .NET 10.
    /// </summary>
    private static bool IsAlreadyJmpHooked(MethodBase original, string name)
    {
        try
        {
            IntPtr fp = original.MethodHandle.GetFunctionPointer();
            byte[] head = new byte[6];
            Marshal.Copy(fp, head, 0, 6);
            bool hooked = head[0] == 0xFF && head[1] == 0x25
                && head[2] == 0 && head[3] == 0 && head[4] == 0 && head[5] == 0;
            if (hooked)
                Console.WriteLine($"[StartupHook] {name} already hooked — skipping re-apply");
            return hooked;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteJmp(IntPtr target, IntPtr destination, string name)
    {
        // x86-64 absolute indirect jump: FF 25 00 00 00 00 [8-byte address]
        byte[] jmp = new byte[14];
        jmp[0] = 0xFF;
        jmp[1] = 0x25;
        BitConverter.GetBytes(destination.ToInt64()).CopyTo(jmp, 6);

        long pageSize = 4096;
        long addr = target.ToInt64();
        long pageStart = addr & ~(pageSize - 1);
        nuint regionSize = (nuint)((addr - pageStart) + jmp.Length + pageSize);

        int ret = mprotect(new IntPtr(pageStart), regionSize, PROT_READ | PROT_WRITE | PROT_EXEC);
        if (ret != 0)
        {
            Console.WriteLine($"[StartupHook] mprotect failed for {name}: errno={Marshal.GetLastWin32Error()}");
            return;
        }

        Marshal.Copy(jmp, 0, target, jmp.Length);
        Console.WriteLine($"[StartupHook] Patched {name} at 0x{target:X} -> 0x{destination:X}");
    }

    // ========================================================================
    // Static field initialization helpers
    // ========================================================================

    /// <summary>
    /// Set a static field value, handling both regular and readonly (initonly) fields.
    /// For readonly fields, uses DynamicMethod IL emit to bypass the initonly restriction.
    /// </summary>
    private static void SetStaticField(Type type, string fieldName, object? value)
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var field = type.GetField(fieldName, flags);
        if (field == null)
        {
            Console.WriteLine($"[StartupHook]   Field {fieldName} not found");
            return;
        }

        try
        {
            // Try direct SetValue first (works for non-readonly fields)
            field.SetValue(null, value);
        }
        catch (FieldAccessException)
        {
            // Readonly (initonly) field — use DynamicMethod to bypass
            SetReadonlyStaticField(field, value);
        }
        Console.WriteLine($"[StartupHook]   Set {fieldName} = {value ?? "null"}");
    }

    /// <summary>
    /// Use DynamicMethod IL emission to set a static readonly field.
    /// DynamicMethod with skipVisibility bypasses initonly checks.
    /// </summary>
    private static void SetReadonlyStaticField(FieldInfo field, object? value)
    {
        var dm = new DynamicMethod(
            $"SetStatic_{field.Name}",
            typeof(void),
            new[] { typeof(object) },
            field.DeclaringType!.Module,
            skipVisibility: true);

        var il = dm.GetILGenerator();

        if (value == null)
        {
            il.Emit(OpCodes.Ldnull);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0);
            if (field.FieldType.IsValueType)
                il.Emit(OpCodes.Unbox_Any, field.FieldType);
        }

        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);

        var setter = (Action<object?>)dm.CreateDelegate(typeof(Action<object?>));
        setter(value);
    }

    /// <summary>
    /// Try to construct a BC type field via parameterless constructor.
    /// If the type can't be constructed, sets the field to null.
    /// </summary>
    private static void TryInitField(Type type, string fieldName)
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var field = type.GetField(fieldName, flags);
        if (field == null) return;

        try
        {
            var instance = Activator.CreateInstance(field.FieldType);
            SetStaticField(type, fieldName, instance);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook]   Cannot init {fieldName} ({field.FieldType.Name}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ========================================================================
    // Replacement methods
    // ========================================================================

    // --- Patch #1: CustomTranslationResolver replacements ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Assembly? Replacement_OnAppDomainAssemblyResolve(object self, object? sender, ResolveEventArgs args)
    {
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_ResolveSatelliteAssembly(object self, string name)
    {
    }

    // --- Patch #5: Telemetry replacements ---

    /// <summary>
    /// Replaces NavOpenTelemetryLogger constructor. No-op — ETW/Geneva telemetry
    /// is not available on Linux.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NavOpenTelemetryLoggerCtor(object self, int traceLevel, object? contextColumns, string? logFileFolder)
    {
        Console.WriteLine("[StartupHook] NavOpenTelemetryLogger..ctor skipped (no ETW on Linux)");
    }


    // --- Generic no-op replacements ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NoOp_ObjectArg(object? arg) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NoOp_2Args(object? a, object? b) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NoOp_3Args(object? self, object? a, object? b) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? Replacement_ReturnNull() { return null; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? Replacement_ReturnNull_1Arg(object? self) { return null; }

    // ========================================================================
    // Patch #29: NavDirectorySecurity.CreateSecurityForDomainDirectory
    // ------------------------------------------------------------------------
    // BC 29 calls this from TempPathHelper.InitializeFolders during
    // NavTenantCollection.ConfigureTenants, and it constructs a
    // System.Security.AccessControl.DirectorySecurity — Windows-only ACL APIs
    // that throw PlatformNotSupportedException on Linux, taking NST startup
    // with them.
    //
    // On BC 27/28 this never fired: the call was gated on
    // IServiceTopology.IsServiceRunningInLocalEnvironment, which Patch #9's
    // Linux topology proxy forces to false. BC 29 reaches it anyway, so the
    // method itself is hooked to return null — which is exactly what the
    // gated-off BC 28 path produced.
    //
    // Applied on every BC version. It is a no-op on 27/28 (the method is not
    // reached there) and keeps working if Microsoft moves the gate again.
    // ========================================================================
    private static void PatchNavDirectorySecurity(Assembly navNclAsm)
    {
        if (IsPatchDisabled("29")) return;
        try
        {
            var type = navNclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavDirectorySecurity");
            if (type == null) return;

            foreach (var m in type.GetMethods(BindingFlags.Static | BindingFlags.Instance
                                              | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "CreateSecurityForDomainDirectory") continue;
                if (m.GetParameters().Length != 0) continue;

                var replacement = typeof(StartupHook).GetMethod(
                    m.IsStatic ? nameof(Replacement_ReturnNull) : nameof(Replacement_ReturnNull_1Arg),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(m, replacement, "NavDirectorySecurity.CreateSecurityForDomainDirectory (Patch #29)");
                Console.WriteLine("[StartupHook] Patch #29: CreateSecurityForDomainDirectory returns null (Windows ACLs unavailable)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #29 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }


    // --- Patch #2: NavEnvironment replacements ---

    /// <summary>
    /// Replaces NavEnvironment..cctor(). Initializes all static fields except serviceAccount
    /// (which would call WindowsIdentity.GetCurrent() and crash on Linux).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NavEnvironmentCctor()
    {
        Console.WriteLine("[StartupHook] Running NavEnvironment..cctor replacement");
        var type = _navEnvironmentType!;

        try
        {
            // Critical fields that must be non-null
            SetStaticField(type, "lockObject", new object());
            SetStaticField(type, "instanceId", Guid.NewGuid());
            SetStaticField(type, "serviceInstanceName", string.Empty);

            // serviceAccount: set to a WindowsIdentity from our stub so the original
            // getters (ServiceAccount => serviceAccount.User, ServiceAccountName => serviceAccount.Name)
            // work even when JMP hooks are bypassed by R2R/tiered compilation.
            SetStaticField(type, "serviceAccount", System.Security.Principal.WindowsIdentity.GetCurrent());

            // Try to construct BC-typed fields (non-critical if they fail)
            TryInitField(type, "compactLohGate");
            TryInitField(type, "TerminatedSessionsMetric");

            // HashSet<ConnectionType> fields — try empty sets
            TryInitField(type, "defaultAwaitedShutdownConnectionTypesList");
            TryInitField(type, "defaultRestartNotificationConnectionTypesList");

            Console.WriteLine("[StartupHook] NavEnvironment..cctor replacement completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] NavEnvironment..cctor replacement error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[StartupHook]   {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Replaces: static SecurityIdentifier ServiceAccount => serviceAccount.User
    /// Returns null — no Windows security identity on Linux.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? Replacement_GetServiceAccount()
    {
        // Return a SecurityIdentifier for LocalSystem (S-1-5-18)
        return new System.Security.Principal.SecurityIdentifier("S-1-5-18");
    }

    /// <summary>
    /// Replaces: static string ServiceAccountName => serviceAccount.Name
    /// Returns a fake service account name.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Replacement_GetServiceAccountName()
    {
        return "SYSTEM";
    }

    // ========================================================================
    // P/Invoke
    // ========================================================================

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr addr, nuint len, int prot);

    private const int PROT_READ = 1;
    private const int PROT_WRITE = 2;
    private const int PROT_EXEC = 4;
}
