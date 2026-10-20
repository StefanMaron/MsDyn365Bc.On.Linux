using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

internal static class NativeRdlcService
{
    private const string StateDirectory = "/run/bc-rdlc";
    private const string ReadyFile = StateDirectory + "/ready";
    private static readonly ManualResetEventSlim Ready = new(false);
    private static readonly CancellationTokenSource Stopping = new();
    private static readonly object ProcessGate = new();
    private static Process? child;
    private static ConstructorInfo? clientConstructor;
    private static ConstructorInfo? communicationConstructor;
    private static FieldInfo? diagnosticsField;
    private static int started;
    private static int port;
    private static string lastError = "The reporting service is starting.";

    // BC 29 replaced the gRPC reporting protocol with HTTP: the service is an
    // ASP.NET Core host (Kestrel when started on port 0), the NST talks to it
    // through ReportingServiceHttpClient with a per-process antiforgery token,
    // and NavEnvironment.CustomReportingServiceClient (the factory the 27/28
    // path replaces) is gone. In that mode the client comes from a hook on
    // NavSession.InitReportingClientAsync instead, see PrepareHttpMode.
    private static bool httpMode;
    private static object? httpStartup;
    private static FieldInfo? antiforgeryField;
    private static FieldInfo? instanceNameField;
    private static ConstructorInfo? httpClientConstructor;
    private static ConstructorInfo? httpDiagnosticsConstructor;
    private static MethodInfo? clientTaskFactory;
    private static PropertyInfo? decorateProperty;
    private static PropertyInfo? environmentInstance;
    private static volatile int httpPort;
    private static volatile string httpToken = string.Empty;
    private static int generation;
    private static object? cachedClient;
    private static int cachedGeneration = -1;

    // BC_RDLC_ACTIVE is written by scripts/entrypoint.sh Step 2c and is 1 only
    // when the ReportViewer patches actually applied to THIS BC build. Asking
    // for the renderer is not the same as having one.
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("BC_RDLC_RENDERER") == "mono" &&
        Environment.GetEnvironmentVariable("BC_RDLC_ACTIVE") == "1";

    private static string ServiceDirectory =>
        Environment.GetEnvironmentVariable("BC_RDLC_SERVICE_DIR") ?? "/bc/service/SideServices";

    internal static void Start(Assembly ncl)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            return;
        if (Environment.GetEnvironmentVariable("BC_RDLC_TRUST_LAYOUTS") != "1")
            throw new InvalidOperationException("Native RDLC requires BC_RDLC_TRUST_LAYOUTS=1: Mono is not a CAS sandbox.");
        port = int.Parse(Environment.GetEnvironmentVariable("BC_RDLC_PORT") ?? "5005",
            CultureInfo.InvariantCulture);
        if (port < 1 || port > 65535)
            throw new ArgumentOutOfRangeException("BC_RDLC_PORT");
        Directory.CreateDirectory(StateDirectory);
        File.Delete(ReadyFile);
        // Decided and hooked synchronously, so that a BC build whose reporting
        // client this code does not understand throws here, and Patch #19's
        // no-op client is installed instead of leaving BC with no client.
        Type navEnvironment = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment", true)!;
        var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        httpMode = navEnvironment.GetField("<CustomReportingServiceClient>k__BackingField", all) == null &&
                   navEnvironment.GetProperty("CustomReportingServiceClient", all) == null;
        if (httpMode)
            PrepareHttpMode(ncl, navEnvironment);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Stopping.Cancel();
            lock (ProcessGate)
                StopChild(child);
        };
        Task.Run(() => InitializeAsync(ncl)).ContinueWith(task =>
        {
            Ready.Reset();
            Exception error = task.Exception!.GetBaseException();
            RecordFailure(error);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private static async Task InitializeAsync(Assembly ncl)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        Type environmentType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment", true)!;
        PropertyInfo instanceProperty = environmentType.GetProperty("Instance", flags)
            ?? throw new MissingMemberException(environmentType.FullName, "Instance");
        object? environment = null;
        var clock = Stopwatch.StartNew();
        while (environment == null)
        {
            environment = instanceProperty.GetValue(null);
            if (clock.Elapsed > TimeSpan.FromMinutes(2))
                throw new TimeoutException("NavEnvironment did not initialize for native RDLC.");
            if (environment == null)
                await Task.Delay(100, Stopping.Token).ConfigureAwait(false);
        }
        if (httpMode)
        {
            // Port 0 is what makes the BC 29 service use Kestrel (any other
            // port selects HTTP.sys, which is Windows-only). It then picks a
            // free loopback port and prints REPORTING_SERVICE_PORT=<n>.
            Type httpStartupType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.ReportingProcessStartup", true)!;
            httpStartup = Activator.CreateInstance(httpStartupType, new object[] { 0 })!;
            MethodInfo httpConfigure = httpStartupType.GetMethod("InitializeReportingServiceConfiguration", flags)
                ?? throw new MissingMethodException(httpStartupType.FullName, "InitializeReportingServiceConfiguration");
            Console.Error.WriteLine("[StartupHook] Patch #19a: BC HTTP reporting client with supervised native RDLC.");
            await SuperviseAsync(httpStartup, httpConfigure).ConfigureAwait(false);
            return;
        }
        FieldInfo factoryField = environmentType.GetField("<CustomReportingServiceClient>k__BackingField", flags)
            ?? throw new MissingFieldException(environmentType.FullName, "CustomReportingServiceClient");
        MethodInfo invoke = factoryField.FieldType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters();
        if (parameters.Length != 1 || !parameters[0].ParameterType.IsGenericType ||
            parameters[0].ParameterType.GetGenericTypeDefinition() != typeof(ValueTuple<,>))
            throw new NotSupportedException("Unexpected BC reporting-client factory signature.");
        Type tupleType = parameters[0].ParameterType;
        diagnosticsField = tupleType.GetField("Item1")!;
        Type communicationInterface = tupleType.GenericTypeArguments[1];
        Type communicationType = communicationInterface.Assembly.GetType(
            "Microsoft.BusinessCentral.Reporting.Common.LocalhostCommunicationFactory", true)!;
        communicationConstructor = communicationType.GetConstructor(new[] { typeof(int) })
            ?? throw new MissingMethodException(communicationType.FullName, ".ctor(Int32)");
        Type clientType = invoke.ReturnType.Assembly.GetType(
            "Microsoft.BusinessCentral.Reporting.Client.ReportingServiceGrpcClient", true)!;
        clientConstructor = clientType.GetConstructor(new[] { diagnosticsField.FieldType, communicationInterface })
            ?? throw new MissingMethodException(clientType.FullName, "reporting client constructor");

        var factory = new DynamicMethod("LinuxRdlcClientFactory", invoke.ReturnType,
            new[] { tupleType }, typeof(NativeRdlcService).Module, true);
        var il = factory.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Box, tupleType);
        il.Emit(OpCodes.Call, typeof(NativeRdlcService).GetMethod(nameof(CreateClient),
            BindingFlags.Public | BindingFlags.Static)!);
        il.Emit(OpCodes.Castclass, invoke.ReturnType);
        il.Emit(OpCodes.Ret);
        factoryField.SetValue(environment, factory.CreateDelegate(factoryField.FieldType));

        // Reuse BC's own settings builder so compact serialization, streaming,
        // AppDomain policy and diagnostics cannot drift from the NST.
        Type startupType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.ReportingProcessStartup", true)!;
        object startup = Activator.CreateInstance(startupType, new object[] { port })!;
        MethodInfo configure = startupType.GetMethod("InitializeReportingServiceConfiguration", flags)
            ?? throw new MissingMethodException(startupType.FullName, "InitializeReportingServiceConfiguration");
        Console.Error.WriteLine("[StartupHook] Patch #19: original gRPC client with supervised native RDLC.");
        await SuperviseAsync(startup, configure).ConfigureAwait(false);
    }

    public static object CreateClient(object input)
    {
        if (!Ready.Wait(TimeSpan.FromSeconds(60), Stopping.Token))
            throw ReportingError(Volatile.Read(ref lastError));
        object communication = communicationConstructor!.Invoke(new object[] { port });
        return clientConstructor!.Invoke(new[] { diagnosticsField!.GetValue(input), communication });
    }

    private static async Task SuperviseAsync(object startup, MethodInfo configure)
    {
        while (!Stopping.IsCancellationRequested)
        {
            Ready.Reset();
            File.Delete(ReadyFile);
            TaskCompletionSource<int>? announced = null;
            if (httpMode)
            {
                // A fresh token per process, the way BC's own side-service
                // lifecycle does it (OnBeforeStarted also forgets the old port).
                startup.GetType().GetMethod("OnBeforeStarted")!.Invoke(startup, null);
                httpToken = (string)antiforgeryField!.GetValue(startup)!;
                httpPort = 0;
                announced = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var process = new Process { StartInfo = StartInfo(), EnableRaisingEvents = true };
            process.Exited += (_, _) => Ready.Reset();
            if (announced != null)
            {
                process.OutputDataReceived += (_, e) =>
                {
                    const string prefix = "REPORTING_SERVICE_PORT=";
                    if (e.Data != null && e.Data.StartsWith(prefix, StringComparison.Ordinal) &&
                        int.TryParse(e.Data.AsSpan(prefix.Length).Trim(), NumberStyles.None,
                            CultureInfo.InvariantCulture, out int announcedPort) &&
                        announcedPort > 0 && announcedPort < 65536)
                        announced.TrySetResult(announcedPort);
                };
            }
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Could not start the native RDLC process.");
                lock (ProcessGate)
                    child = process;
                if (announced != null)
                {
                    process.BeginOutputReadLine();
                    Task first = await Task.WhenAny(announced.Task,
                        process.WaitForExitAsync(Stopping.Token)).WaitAsync(TimeSpan.FromSeconds(90), Stopping.Token)
                        .ConfigureAwait(false);
                    if (first != announced.Task)
                        throw new IOException("Native RDLC exited before announcing its port.");
                    httpPort = await announced.Task.ConfigureAwait(false);
                    Interlocked.Increment(ref generation);
                }
                File.WriteAllText(StateDirectory + "/pid", process.Id.ToString(CultureInfo.InvariantCulture));
                Task exited = process.WaitForExitAsync(Stopping.Token);
                Task configured = ConfigureAsync(startup, configure);
                if (await Task.WhenAny(configured, exited).ConfigureAwait(false) == exited)
                    throw new IOException("Native RDLC exited before configuration.");
                await configured.WaitAsync(TimeSpan.FromSeconds(90), Stopping.Token).ConfigureAwait(false);
                if (process.HasExited)
                    throw new IOException("Native RDLC exited during configuration.");
                File.Delete(StateDirectory + "/error");
                int servicePort = httpMode ? httpPort : port;
                File.WriteAllText(ReadyFile, servicePort.ToString(CultureInfo.InvariantCulture));
                Ready.Set();
                Console.Error.WriteLine("[RDLC] Original reporting service configured and ready on loopback port " + servicePort);
                await exited.ConfigureAwait(false);
                RecordFailure(new IOException("Native RDLC exited with code " + process.ExitCode + "; restarting."));
            }
            catch (Exception ex) when (IsServiceFailure(ex))
            {
                RecordFailure(ex);
            }
            finally
            {
                Ready.Reset();
                File.Delete(ReadyFile);
                lock (ProcessGate)
                {
                    StopChild(process);
                    if (ReferenceEquals(child, process))
                        child = null;
                }
            }
            await Task.Delay(2000, Stopping.Token).ConfigureAwait(false);
        }
    }

    private static async Task ConfigureAsync(object startup, MethodInfo configure)
    {
        object result;
        try { result = configure.Invoke(startup, null)!; }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        // BC 27/28 return a ValueTask, BC 29 a Task.
        if (result is ValueTask operation)
            await operation.ConfigureAwait(false);
        else if (result is Task task)
            await task.ConfigureAwait(false);
        else
            throw new NotSupportedException("Unexpected BC reporting configuration return type.");
    }

    private static ProcessStartInfo StartInfo()
    {
        var info = new ProcessStartInfo("/bc/scripts/start-rdlc-service.sh")
        {
            UseShellExecute = false,
            WorkingDirectory = ServiceDirectory
        };
        // Report code must not inherit the NST's SQL credentials or startup hook.
        info.Environment.Clear();
        info.Environment["PATH"] = "/bc/rdlc/compiler:/usr/local/bin:/usr/bin:/bin";
        info.Environment["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "en_US.UTF-8";
        info.Environment["LC_ALL"] = info.Environment["LANG"];
        info.Environment["BC_RDLC_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        info.Environment["BC_RDLC_SERVICE_DIR"] = ServiceDirectory;
        info.Environment["RDLC_TRACE"] = Environment.GetEnvironmentVariable("BC_RDLC_TRACE") ?? "0";
        if (httpMode)
        {
            // start-rdlc-service.sh then passes BC 29's argument syntax and copies
            // the service's stdout to us as well as to the log, so the
            // REPORTING_SERVICE_PORT line reaches the hook.
            info.Environment["BC_RDLC_PROTOCOL"] = "http";
            info.Environment["AntiforgeryToken"] = httpToken;
            info.RedirectStandardOutput = true;
        }
        return info;
    }

    private static void PrepareHttpMode(Assembly ncl, Type navEnvironment)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        Type sessionType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession", true)!;
        MethodInfo init = sessionType.GetMethod("InitReportingClientAsync", flags, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(sessionType.FullName, "InitReportingClientAsync");
        Type startupType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.ReportingProcessStartup", true)!;
        MethodInfo hostUrl = startupType.GetMethod("GetReportingServiceHostUrl", flags, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(startupType.FullName, "GetReportingServiceHostUrl");
        if (startupType.GetConstructor(new[] { typeof(int) }) == null)
            throw new MissingMethodException(startupType.FullName, ".ctor(Int32)");
        if (startupType.GetMethod("OnBeforeStarted") == null)
            throw new MissingMethodException(startupType.FullName, "OnBeforeStarted");
        antiforgeryField = startupType.GetField("AntiforgeryToken", flags)
            ?? throw new MissingFieldException(startupType.FullName, "AntiforgeryToken");
        instanceNameField = startupType.GetField("serverInstanceName", flags)
            ?? throw new MissingFieldException(startupType.FullName, "serverInstanceName");

        Type taskType = init.ReturnType;
        if (!taskType.IsGenericType || taskType.GetGenericTypeDefinition() != typeof(Task<>))
            throw new NotSupportedException("Unexpected InitReportingClientAsync return type " + taskType);
        Type clientInterface = taskType.GenericTypeArguments[0];
        Assembly clientAssembly = clientInterface.Assembly;
        Type httpClientType = clientAssembly.GetType(
            "Microsoft.BusinessCentral.Reporting.Client.ReportingServiceHttpClient", true)!;
        Type diagnosticsType = clientAssembly.GetType(
            "Microsoft.BusinessCentral.Reporting.Client.ReportingServiceClientDiagnosticsInfo", true)!;
        httpDiagnosticsConstructor = diagnosticsType.GetConstructor(Type.EmptyTypes)
            ?? throw new MissingMethodException(diagnosticsType.FullName, ".ctor()");
        httpClientConstructor = httpClientType.GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 4 && p[0].ParameterType == diagnosticsType &&
                       p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(string);
            }) ?? throw new MissingMethodException(httpClientType.FullName, "reporting HTTP client constructor");
        environmentInstance = navEnvironment.GetProperty("Instance", flags)
            ?? throw new MissingMemberException(navEnvironment.FullName, "Instance");
        decorateProperty = navEnvironment.GetProperty("DecorateReportingServiceClient", flags);
        clientTaskFactory = typeof(NativeRdlcService).GetMethod(nameof(GetClientAsync),
            BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(clientInterface);

        StartupHook.ApplyJmpHook(init,
            typeof(NativeRdlcService).GetMethod(nameof(Replacement_InitReportingClientAsync))!,
            "NavSession.InitReportingClientAsync (native RDLC)");
        StartupHook.ApplyJmpHook(hostUrl,
            typeof(NativeRdlcService).GetMethod(nameof(Replacement_GetReportingServiceHostUrl))!,
            "ReportingProcessStartup.GetReportingServiceHostUrl (native RDLC)");
        Console.WriteLine("[StartupHook] Patch #19a: BC 29 HTTP reporting protocol; InitReportingClientAsync and GetReportingServiceHostUrl hooked");
    }

    // BC builds this URL from the machine's DNS name, which in a container is
    // not the loopback address Kestrel listens on. The path is the one BC
    // builds; in Kestrel mode the service strips everything before
    // /ReportingService/.
    public static string Replacement_GetReportingServiceHostUrl(object startup)
    {
        string instance = instanceNameField?.GetValue(startup) as string ?? "BC";
        return string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/{1}/ReportingService/",
            httpPort, instance);
    }

    // Replaces NavSession.InitReportingClientAsync, which asks BC's side-service
    // watchdog for the reporting process. Patch #18 keeps that watchdog empty,
    // because we supervise the process ourselves. Declared `object` because
    // Task<IReportingServiceClient> lives in an assembly this one cannot
    // reference; both are one object pointer at the ABI level.
    public static object Replacement_InitReportingClientAsync(object session)
    {
        return clientTaskFactory!.Invoke(null, null)!;
    }

    private static async Task<T> GetClientAsync<T>() where T : class
    {
        if (!Ready.IsSet)
        {
            bool ready = await Task.Run(() => Ready.Wait(TimeSpan.FromSeconds(60), Stopping.Token))
                .ConfigureAwait(false);
            if (!ready)
                throw ReportingError(Volatile.Read(ref lastError));
        }
        lock (ProcessGate)
        {
            // One client per service process: a restarted process has a new
            // port and a new antiforgery token.
            int current = Volatile.Read(ref generation);
            if (cachedClient == null || cachedGeneration != current)
            {
                object client = httpClientConstructor!.Invoke(new object?[]
                {
                    httpDiagnosticsConstructor!.Invoke(null),
                    Replacement_GetReportingServiceHostUrl(httpStartup!), httpToken, null
                });
                // Keep any decorator installed on NavEnvironment, as BC's own code does.
                object? environment = environmentInstance!.GetValue(null);
                if (environment != null && decorateProperty?.GetValue(environment) is Delegate decorate)
                    client = decorate.DynamicInvoke(client)!;
                cachedClient = client;
                cachedGeneration = current;
            }
            return (T)cachedClient;
        }
    }

    private static bool IsServiceFailure(Exception exception)
    {
        if (exception is IOException or Win32Exception or TimeoutException)
            return true;
        for (Type? type = exception.GetType(); type != null; type = type.BaseType)
            if (type.FullName == "Microsoft.Dynamics.Nav.Types.Exceptions.NavBaseException" ||
                type.FullName == "Grpc.Core.RpcException")
                return true;
        return false;
    }

    private static void RecordFailure(Exception exception)
    {
        Volatile.Write(ref lastError, exception.Message);
        Console.Error.WriteLine("[RDLC] " + exception);
        File.WriteAllText(StateDirectory + "/error", exception.ToString());
    }

    private static Exception ReportingError(string detail)
    {
        Type? type = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types")
            ?.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavReportException");
        ConstructorInfo? constructor = type?.GetConstructor(new[] { typeof(string) });
        string message = "Native RDLC is unavailable: " + detail;
        return constructor == null ? new PlatformNotSupportedException(message) :
            (Exception)constructor.Invoke(new object[] { message });
    }

    private static void StopChild(Process? process)
    {
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process may have exited between HasExited and Kill.
        }
    }
}
