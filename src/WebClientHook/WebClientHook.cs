using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// .NET Startup Hook that patches Microsoft's BC Web Client server
/// (Prod.Client.WebCoreApp) to run self-hosted on Kestrel on Linux.
///
/// Deliberately separate from the NST's StartupHook: that hook contains
/// patches that assume the NST process (encryption providers, side
/// services, Cecil compiler fixes) and must not run in the web server.
///
/// Patch #W1: Microsoft.Extensions.Logging.EventLog
///   The web server registers an EventLog logging provider unconditionally.
///   System.Diagnostics.EventLog throws PlatformNotSupportedException on
///   Linux the moment the first logger is created (inside
///   WebHostBuilder.Build()), killing the host before startup.
///   Fix: JMP-hook EventLogLoggerProvider.CreateLogger to return
///   NullLogger.Instance.
///
/// Patch #W2: Microsoft.Dynamics.Framework.UI.WebBase.FilePersistenceManager
///   The resource/thumbnail persistence layer builds paths with hardcoded
///   backslash constants ("Resources\ExtractedResources\"), which on Linux
///   become literal filename characters and fail the startup directory
///   check (DirectoryNotFoundException in WebHost.BuildApplication).
///   All file access funnels through FilePersistenceManager, so hooking its
///   five methods with separator-normalizing reimplementations fixes the
///   whole layer at one choke point.
///
/// JMP hooks only work on JIT-compiled (IL) methods. App-local NuGet
/// assemblies are IL on Linux (win-x64 R2R prejit is ignored off-Windows),
/// so hooks on them are safe here.
/// </summary>
internal class StartupHook
{
    private static object? _nullLogger;

    public static void Initialize()
    {
        Console.Error.WriteLine("[WebClientHook] Initializing BC Web Client Linux patches...");

        // Patch #W5: route Win32 P/Invokes to libwin32_stubs.so (NST Patch #3
        // equivalent). Safety net for P/Invokes not covered by managed hooks.
        RegisterWin32StubResolver();

        // Debug aid (WEBCLIENT_DEBUG_FIRSTCHANCE=1): the web client has no
        // console logging provider, so mid-response exceptions vanish. This
        // prints every thrown exception with its stack.
        if (Environment.GetEnvironmentVariable("WEBCLIENT_DEBUG_FIRSTCHANCE") == "1")
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                try
                {
                    Console.Error.WriteLine($"[FirstChance] {e.Exception}");
                }
                catch { }
            };
        }

        AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
        {
            try
            {
                OnAssemblyLoad(args.LoadedAssembly);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WebClientHook] Patch failed for {args.LoadedAssembly.GetName().Name}: {ex}");
            }
        };

        // Assemblies already loaded before the hook ran (unlikely, but cheap)
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { OnAssemblyLoad(asm); }
            catch (Exception ex) { Console.Error.WriteLine($"[WebClientHook] Patch failed for {asm.GetName().Name}: {ex}"); }
        }
    }

    private static void OnAssemblyLoad(Assembly asm)
    {
        switch (asm.GetName().Name)
        {
            case "Microsoft.Extensions.Logging.EventLog":
                PatchEventLogProvider(asm);
                break;
            case "Microsoft.Dynamics.Nav.Types":
                PatchNavTypesEventLog(asm);
                break;
            case "Microsoft.Dynamics.Framework.UI.Web":
                PatchFileHelper(asm);
                break;
            case "Microsoft.Dynamics.Nav.Client.Builder":
                PatchTimeZoneProvider(asm);
                break;
            case "Microsoft.Dynamics.Framework.UI.WebBase":
                PatchFilePersistenceManager(asm);
                PatchTimeZoneDetection(asm);
                break;
        }
    }

    // ------------------------------------------------------------------
    // Patch #W6b: TimeZoneHelper.DetectTimeZone — round-trip-safe zones
    // The browser reports its time zone as raw offsets (base offset, DST
    // offset, DST period) and DetectTimeZone maps them to a system (ICU)
    // TimeZoneInfo. That zone is serialized into the NST OpenConnection
    // request with ToSerializedString — which FromSerializedString cannot
    // parse back on Linux for most ICU zones (dotnet/runtime quirk), so
    // login dies with InvalidTimeZoneException in NSService.OpenConnection.
    // Fix: do the same base-offset matching, but return a custom
    // TimeZoneInfo (system zone's id/names, current UTC offset) — custom
    // zones round-trip reliably. Wall-clock times are correct; the only
    // drift is across a DST transition mid-session.
    // DetectTimeZone is large (LINQ + lambdas) so the JMP hook cannot be
    // bypassed by JIT inlining.
    // ------------------------------------------------------------------
    private static void PatchTimeZoneDetection(Assembly asm)
    {
        var helper = asm.GetType("Microsoft.Dynamics.Framework.UI.WebBase.TimeZoneHelper");
        var original = helper?.GetMethod("DetectTimeZone", BindingFlags.Public | BindingFlags.Instance);
        var replacement = typeof(StartupHook).GetMethod(nameof(DetectTimeZoneReplacement), BindingFlags.NonPublic | BindingFlags.Static);
        if (original == null || replacement == null)
        {
            Console.Error.WriteLine("[WebClientHook] TimeZoneHelper.DetectTimeZone hook setup failed");
            return;
        }
        ApplyJmpHook(original, replacement!, "TimeZoneHelper.DetectTimeZone");
    }

    // Mirrors: TimeZoneInfo DetectTimeZone(int clientTimeZoneOffset, int dstOffset,
    //          DateTime dstPeriodStart, DateTime dstPeriodEnd, UISession uiSession)
    private static object? DetectTimeZoneReplacement(object self, int clientTimeZoneOffset, int dstOffset,
        DateTime dstPeriodStart, DateTime dstPeriodEnd, object uiSession)
    {
        try
        {
            // Browser reports the base offset and (separately) DST info. Compute
            // the offset in effect right now, then map it to a round-trip-safe
            // zone whose id stays safe after the NST persists and re-resolves it.
            var baseOffset = TimeSpan.FromMinutes(clientTimeZoneOffset);
            bool dstActive = dstOffset != 0 && dstPeriodStart != DateTime.MinValue && dstPeriodEnd != DateTime.MinValue &&
                IsWithinDstPeriod(DateTime.UtcNow + baseOffset, dstPeriodStart, dstPeriodEnd);
            var effectiveOffset = baseOffset + (dstActive ? TimeSpan.FromMinutes(dstOffset) : TimeSpan.Zero);

            var safe = ZoneForOffset(effectiveOffset);
            Console.Error.WriteLine($"[WebClientHook] Browser time zone (base {clientTimeZoneOffset}min, dst {dstOffset}min) → '{safe.Id}' (offset {effectiveOffset})");
            return safe;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebClientHook] DetectTimeZone replacement failed ({ex.GetType().Name}: {ex.Message}) — using UTC");
            return TimeZoneInfo.Utc;
        }
    }

    private static bool IsWithinDstPeriod(DateTime local, DateTime start, DateTime end)
    {
        // Northern hemisphere: start < end. Southern: start > end (DST wraps year end).
        return start <= end ? local >= start && local < end : local >= start || local < end;
    }

    // ------------------------------------------------------------------
    // Patch #W6: ConfigurationTimeZoneProvider.get_TimeZone — round-trip-safe zones
    // The web client resolves the BROWSER's IANA time zone via
    // FindSystemTimeZoneById and the session layer serializes it with
    // TimeZoneInfo.ToSerializedString into the NST OpenConnection request.
    // On Linux, FromSerializedString(ToSerializedString(tz)) throws
    // SerializationException for most ICU zones (dotnet/runtime quirk), so
    // any user whose browser is not in UTC cannot log in
    // (InvalidTimeZoneException in NSService.OpenConnection).
    // Fix: return a custom TimeZoneInfo with the same id/names and the
    // CURRENT utc offset — custom zones round-trip reliably. Wall-clock
    // times are correct for the session; the only drift is across a DST
    // transition mid-session, which is acceptable for a dev tool.
    // This getter is called through the ITimeZoneProvider interface, so the
    // JMP hook cannot be bypassed by JIT inlining (unlike the UserSettings
    // property setter, which small-method inlining would skip).
    // ------------------------------------------------------------------
    private static FieldInfo? _tzConfigSettingsField;
    private static MethodInfo? _tzGetStringSetting;

    private static void PatchTimeZoneProvider(Assembly asm)
    {
        var provider = asm.GetType("Microsoft.Dynamics.Nav.Client.FormBuilder.ConfigurationTimeZoneProvider");
        var getter = provider?.GetProperty("TimeZone")?.GetMethod;
        var replacement = typeof(StartupHook).GetMethod(nameof(GetTimeZoneReplacement), BindingFlags.NonPublic | BindingFlags.Static);
        if (provider == null || getter == null || replacement == null)
        {
            Console.Error.WriteLine("[WebClientHook] ConfigurationTimeZoneProvider hook setup failed");
            return;
        }
        _tzConfigSettingsField = provider.GetField("configSettings", BindingFlags.NonPublic | BindingFlags.Instance);
        ApplyJmpHook(getter, replacement!, "ConfigurationTimeZoneProvider.get_TimeZone");
    }

    private static object? GetTimeZoneReplacement(object self)
    {
        try
        {
            var configSettings = _tzConfigSettingsField?.GetValue(self);
            if (configSettings == null) return null;
            _tzGetStringSetting ??= configSettings.GetType().GetMethod("GetStringSetting", new[] { typeof(string) });
            var id = (string?)_tzGetStringSetting?.Invoke(configSettings, new object[] { "TimeZone" });
            if (string.IsNullOrEmpty(id)) return null;
            var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return MakeSerializationSafe(tz);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebClientHook] get_TimeZone replacement failed ({ex.GetType().Name}: {ex.Message}) — using UTC");
            return TimeZoneInfo.Utc;
        }
    }

    private static TimeZoneInfo MakeSerializationSafe(TimeZoneInfo tz)
    {
        try
        {
            TimeZoneInfo.FromSerializedString(tz.ToSerializedString());
            return tz; // already safe (UTC, Etc/GMT±N, synthetic custom zones)
        }
        catch
        {
            var safe = ZoneForOffset(tz.GetUtcOffset(DateTime.UtcNow));
            Console.Error.WriteLine($"[WebClientHook] TimeZone '{tz.Id}' does not survive .NET serialization on Linux — using '{safe.Id}'");
            return safe;
        }
    }

    /// <summary>
    /// Map a UTC offset to a zone that (a) survives ToSerializedString →
    /// FromSerializedString on Linux and (b) stays safe after the NST persists
    /// its id and re-resolves it on the next login. Whole-hour offsets use the
    /// real "Etc/GMT±N" IANA zones; sub-hour offsets use a synthetic
    /// "UTC±HH:MM" id that won't re-resolve to a DST-bearing ICU zone.
    /// Mirrors StartupHook.ZoneForOffset on the NST side — keep them in sync.
    /// </summary>
    private static TimeZoneInfo ZoneForOffset(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
            return TimeZoneInfo.Utc;

        if (offset.Minutes == 0 && offset.Seconds == 0)
        {
            int hours = offset.Hours; // signed; Etc/GMT sign is inverted
            string etcId = $"Etc/GMT{(hours > 0 ? "-" : "+")}{Math.Abs(hours)}";
            try
            {
                var etc = TimeZoneInfo.FindSystemTimeZoneById(etcId);
                TimeZoneInfo.FromSerializedString(etc.ToSerializedString());
                return etc;
            }
            catch { /* fall through */ }
        }

        string sign = offset < TimeSpan.Zero ? "-" : "+";
        string syntheticId = $"UTC{sign}{offset.Duration():hh\\:mm}";
        return TimeZoneInfo.CreateCustomTimeZone(syntheticId, offset, syntheticId, syntheticId);
    }

    // ------------------------------------------------------------------
    // Patch #W4: FileHelper.GetSymbolicLinkTarget — managed reimplementation
    // The original P/Invokes kernel32 CreateFile + GetFinalPathNameByHandle
    // just to resolve symlinks for a FileSystemWatcher path. Use the BCL.
    // ------------------------------------------------------------------
    private static void PatchFileHelper(Assembly asm)
    {
        var fileHelper = asm.GetType("Microsoft.Dynamics.Framework.UI.Web.FileHelper");
        var original = fileHelper?.GetMethod("GetSymbolicLinkTarget", BindingFlags.NonPublic | BindingFlags.Static);
        var replacement = typeof(StartupHook).GetMethod(nameof(GetSymbolicLinkTargetReplacement), BindingFlags.NonPublic | BindingFlags.Static);
        if (original == null || replacement == null)
        {
            Console.Error.WriteLine("[WebClientHook] FileHelper.GetSymbolicLinkTarget hook setup failed");
            return;
        }
        ApplyJmpHook(original, replacement!, "FileHelper.GetSymbolicLinkTarget");
    }

    private static string GetSymbolicLinkTargetReplacement(string fileName)
    {
        try
        {
            var fi = new System.IO.FileInfo(fileName);
            var target = fi.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? System.IO.Path.GetFullPath(fileName);
        }
        catch
        {
            return System.IO.Path.GetFullPath(fileName);
        }
    }

    // ------------------------------------------------------------------
    // Patch #W5: Win32 DllImport resolver → libwin32_stubs.so
    // ------------------------------------------------------------------
    private static IntPtr _win32StubHandle;

    private static void RegisterWin32StubResolver()
    {
        string stubPath = Environment.GetEnvironmentVariable("WIN32_STUBS_SO") ?? "/bc/hook/libwin32_stubs.so";
        if (!System.IO.File.Exists(stubPath))
        {
            Console.Error.WriteLine($"[WebClientHook] libwin32_stubs.so not found at {stubPath} — Win32 P/Invoke fallback disabled");
            return;
        }

        var win32Names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kernel32", "kernel32.dll", "user32", "user32.dll",
            "advapi32", "advapi32.dll", "httpapi", "httpapi.dll",
            "wintrust", "wintrust.dll", "gdiplus", "gdiplus.dll",
        };

        System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (asm, name) =>
        {
            if (!win32Names.Contains(name)) return IntPtr.Zero;
            if (_win32StubHandle == IntPtr.Zero)
                NativeLibrary.TryLoad(stubPath, out _win32StubHandle);
            if (_win32StubHandle != IntPtr.Zero)
                Console.Error.WriteLine($"[WebClientHook] Redirected unmanaged '{name}' (from {asm.GetName().Name}) → libwin32_stubs.so");
            return _win32StubHandle;
        };
        Console.Error.WriteLine($"[WebClientHook] Win32 P/Invoke resolver registered ({stubPath})");
    }

    // ------------------------------------------------------------------
    // Patch #W1: EventLogLoggerProvider.CreateLogger → NullLogger.Instance
    // ------------------------------------------------------------------
    private static void PatchEventLogProvider(Assembly asm)
    {
        var providerType = asm.GetType("Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider");
        var createLogger = providerType?.GetMethod("CreateLogger", BindingFlags.Public | BindingFlags.Instance);
        if (createLogger == null)
        {
            Console.Error.WriteLine("[WebClientHook] EventLogLoggerProvider.CreateLogger not found");
            return;
        }

        // NullLogger lives in Microsoft.Extensions.Logging.Abstractions, which is
        // guaranteed loaded by the time the EventLog assembly loads (it references it).
        var abstractions = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Extensions.Logging.Abstractions")
            ?? Assembly.Load("Microsoft.Extensions.Logging.Abstractions");
        var nullLoggerType = abstractions.GetType("Microsoft.Extensions.Logging.Abstractions.NullLogger`1") != null
            ? abstractions.GetType("Microsoft.Extensions.Logging.Abstractions.NullLogger")
            : abstractions.GetType("Microsoft.Extensions.Logging.Abstractions.NullLogger");
        _nullLogger = nullLoggerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                      ?? nullLoggerType?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (_nullLogger == null)
        {
            Console.Error.WriteLine("[WebClientHook] NullLogger.Instance not found");
            return;
        }

        var replacement = typeof(StartupHook).GetMethod(nameof(CreateLoggerReplacement),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        ApplyJmpHook(createLogger, replacement, "EventLogLoggerProvider.CreateLogger");
    }

    // Mirrors instance method ILogger CreateLogger(string name): 'this' maps to arg0.
    private static object? CreateLoggerReplacement(object self, string name) => _nullLogger;

    // ------------------------------------------------------------------
    // Patch #W2: FilePersistenceManager — normalize '\' to '/' in paths
    // ------------------------------------------------------------------
    private static ConstructorInfo? _cachedFileInfoCtor;
    private static MethodInfo? _cachedFileInfoListAsReadOnly;
    private static Type? _cachedFileInfoListType;

    private static void PatchFilePersistenceManager(Assembly asm)
    {
        var fpm = asm.GetType("Microsoft.Dynamics.Framework.UI.WebBase.FilePersistenceManager");
        if (fpm == null)
        {
            Console.Error.WriteLine("[WebClientHook] FilePersistenceManager not found");
            return;
        }

        // Cache reflection bits needed by the GetFiles replacement.
        var cfi = asm.GetType("Microsoft.Dynamics.Framework.UI.WebBase.CachedFileInfo");
        if (cfi != null)
        {
            _cachedFileInfoCtor = cfi.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 6);
            _cachedFileInfoListType = typeof(System.Collections.Generic.List<>).MakeGenericType(cfi);
            _cachedFileInfoListAsReadOnly = _cachedFileInfoListType.GetMethod("AsReadOnly");
        }

        Hook(fpm, "Delete", nameof(DeleteReplacement));
        Hook(fpm, "DirectoryExists", nameof(DirectoryExistsReplacement));
        Hook(fpm, "FileExists", nameof(FileExistsReplacement));
        Hook(fpm, "Save", nameof(SaveReplacement));
        Hook(fpm, "GetFiles", nameof(GetFilesReplacement));
    }

    private static void Hook(Type type, string method, string replacementName)
    {
        var original = type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
        var replacement = typeof(StartupHook).GetMethod(replacementName, BindingFlags.NonPublic | BindingFlags.Static);
        if (original == null || replacement == null)
        {
            Console.Error.WriteLine($"[WebClientHook] {type.Name}.{method} hook setup failed");
            return;
        }
        ApplyJmpHook(original, replacement, $"{type.Name}.{method}");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void DeleteReplacement(object self, string path) =>
        System.IO.File.Delete(Normalize(path));

    private static bool DirectoryExistsReplacement(object self, string path) =>
        System.IO.Directory.Exists(Normalize(path));

    private static bool FileExistsReplacement(object self, string path) =>
        System.IO.File.Exists(Normalize(path));

    private static long SaveReplacement(object self, string filePath, System.IO.Stream stream)
    {
        if (filePath == null) throw new ArgumentNullException(nameof(filePath));
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        string path = Normalize(filePath);
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);
        using var fs = System.IO.File.Create(path, 4096, System.IO.FileOptions.None);
        stream.CopyTo(fs, 4096);
        return fs.Length;
    }

    // Mirrors: ReadOnlyCollection<CachedFileInfo> GetFiles(string path, bool includeSubdirectories)
    private static object? GetFilesReplacement(object self, string path, bool includeSubdirectories)
    {
        var dirInfo = new System.IO.DirectoryInfo(Normalize(path).TrimEnd('/'));
        var list = (System.Collections.IList)Activator.CreateInstance(_cachedFileInfoListType!)!;
        var option = includeSubdirectories
            ? System.IO.SearchOption.AllDirectories
            : System.IO.SearchOption.TopDirectoryOnly;
        if (dirInfo.Exists)
        {
            foreach (var fi in dirInfo.GetFiles("*", option))
            {
                string ext = fi.Extension.Length == 0 ? "" : fi.Extension.Substring(1);
                var item = _cachedFileInfoCtor!.Invoke(new object?[]
                {
                    fi.Name.Substring(0, fi.Name.Length - fi.Extension.Length),
                    fi.FullName,
                    ext,
                    fi.CreationTimeUtc,
                    fi.Length,
                    includeSubdirectories ? fi.Directory?.Name : null
                });
                list.Add(item);
            }
        }
        return _cachedFileInfoListAsReadOnly!.Invoke(list, null);
    }

    // ------------------------------------------------------------------
    // Patch #W3: Nav.Types EventLogWriter → no-op proxy (same as NST Patch #4)
    // The web client links the NST's Nav.Types.dll, whose background event
    // queue calls System.Diagnostics.EventLog and aborts the process on Linux.
    // JMP hooks are unreliable here (JIT inlining), so replace the static
    // writer instance instead — exactly like the NST StartupHook does.
    // ------------------------------------------------------------------
    private static void PatchNavTypesEventLog(Assembly navTypes)
    {
        var eventLogWriterType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.EventLogWriter");
        var ifaceType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.IEventLogEntryWriter");
        if (eventLogWriterType == null || ifaceType == null)
        {
            Console.Error.WriteLine("[WebClientHook] EventLogWriter or IEventLogEntryWriter not found");
            return;
        }

        var createMethod = typeof(System.Reflection.DispatchProxy)
            .GetMethod("Create", 2, Type.EmptyTypes)!
            .MakeGenericMethod(ifaceType, typeof(NoOpEventLogProxy));
        var noopWriter = createMethod.Invoke(null, null);

        var field = eventLogWriterType.GetField("eventLogEntryWriter",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (field != null)
        {
            field.SetValue(null, noopWriter);
            Console.Error.WriteLine("[WebClientHook] Replaced Nav.Types EventLogWriter with no-op proxy");
        }
        else
        {
            var prop = eventLogWriterType.GetProperty("EventLogEntryWriter",
                BindingFlags.Public | BindingFlags.Static);
            prop?.SetValue(null, noopWriter);
            Console.Error.WriteLine("[WebClientHook] Replaced Nav.Types EventLogWriter via property setter");
        }
    }

    public class NoOpEventLogProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            // Surface event log writes on stderr so real errors stay visible
            if (targetMethod?.Name == "WriteEntry" && args?.Length >= 2)
            {
                var message = args[1]?.ToString();
                if (message != null && message.Length > 10)
                    Console.Error.WriteLine($"[BC-EventLog] {message}");
            }
            return null;
        }
    }

    // ========================================================================
    // JMP hook machinery (same technique as the NST StartupHook)
    // ========================================================================
    private const int PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr addr, nuint len, int prot);

    private static void ApplyJmpHook(MethodBase original, MethodInfo replacement, string name)
    {
        RuntimeHelpers.PrepareMethod(original.MethodHandle);
        RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

        IntPtr origFp = original.MethodHandle.GetFunctionPointer();
        IntPtr replFp = replacement.MethodHandle.GetFunctionPointer();

        // Locate the compiled code behind the precode so direct calls are also hooked.
        IntPtr compiledCode = IntPtr.Zero;
        try
        {
            byte[] precode = new byte[24];
            Marshal.Copy(origFp, precode, 0, 24);

            // .NET 8 x64 FixupPrecode: 49 BA [MethodDesc] FF 25 [disp32]
            if (precode[10] == 0xFF && precode[11] == 0x25)
            {
                int disp32 = BitConverter.ToInt32(precode, 12);
                compiledCode = Marshal.ReadIntPtr(origFp + 16 + disp32);
            }
            else if (precode[0] == 0xFF && precode[1] == 0x25) // StubPrecode
            {
                int disp32 = BitConverter.ToInt32(precode, 2);
                compiledCode = Marshal.ReadIntPtr(origFp + 6 + disp32);
            }
            else if (precode[0] == 0xE9) // relative JMP
            {
                int disp32 = BitConverter.ToInt32(precode, 1);
                compiledCode = origFp + 5 + disp32;
            }
        }
        catch { /* best effort */ }

        WriteJmp(origFp, replFp, name);
        if (compiledCode != IntPtr.Zero && compiledCode != origFp && compiledCode != replFp)
        {
            try { WriteJmp(compiledCode, replFp, name + " (code)"); }
            catch (Exception ex) { Console.Error.WriteLine($"[WebClientHook]   compiled code patch failed: {ex.Message}"); }
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

        if (mprotect(new IntPtr(pageStart), regionSize, PROT_READ | PROT_WRITE | PROT_EXEC) != 0)
        {
            Console.Error.WriteLine($"[WebClientHook] mprotect failed for {name}: errno={Marshal.GetLastWin32Error()}");
            return;
        }

        Marshal.Copy(jmp, 0, target, jmp.Length);
        Console.Error.WriteLine($"[WebClientHook] Patched {name} at 0x{target:X} -> 0x{destination:X}");
    }
}
