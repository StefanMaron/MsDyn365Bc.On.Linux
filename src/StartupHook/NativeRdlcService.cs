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
            using var process = new Process { StartInfo = StartInfo(), EnableRaisingEvents = true };
            process.Exited += (_, _) => Ready.Reset();
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Could not start the native RDLC process.");
                lock (ProcessGate)
                    child = process;
                File.WriteAllText(StateDirectory + "/pid", process.Id.ToString(CultureInfo.InvariantCulture));
                Task exited = process.WaitForExitAsync(Stopping.Token);
                Task configured = ConfigureAsync(startup, configure);
                if (await Task.WhenAny(configured, exited).ConfigureAwait(false) == exited)
                    throw new IOException("Native RDLC exited before configuration.");
                await configured.WaitAsync(TimeSpan.FromSeconds(90), Stopping.Token).ConfigureAwait(false);
                if (process.HasExited)
                    throw new IOException("Native RDLC exited during configuration.");
                File.Delete(StateDirectory + "/error");
                File.WriteAllText(ReadyFile, port.ToString(CultureInfo.InvariantCulture));
                Ready.Set();
                Console.Error.WriteLine("[RDLC] Original reporting service configured and ready on loopback port " + port);
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
        if (result is not ValueTask operation)
            throw new NotSupportedException("Unexpected BC reporting configuration return type.");
        await operation.ConfigureAwait(false);
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
        return info;
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
