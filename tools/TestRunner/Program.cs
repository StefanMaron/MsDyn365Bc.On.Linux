using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

// BC Test Runner — connects via WebSocket client services and invokes RunNextTest.
// Usage: dotnet run -- <host:port> [--company <name>]
// The test suite and method lines must be pre-populated (via SQL or the run-tests.sh wrapper).
// This tool opens the Command Line Test Tool (page 130455) and calls RunNextTest in a loop.
// Results are read from the TestResultJson field after each invocation.

var host = "localhost:7085";
var company = "CRONUS International Ltd.";
var user = "admin";
var password = "Admin123!";

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
    else if (args[i] == "--company" && i + 1 < args.Length) company = args[++i];
    else if (args[i] == "--user" && i + 1 < args.Length) user = args[++i];
    else if (args[i] == "--password" && i + 1 < args.Length) password = args[++i];
    else if (!args[i].StartsWith("--")) host = args[i];
}

int exitCode = 1;
try
{
    exitCode = await RunTests(host, company, user, password);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: {ex.Message}");
}
return exitCode;

async Task<int> RunTests(string host, string company, string user, string password)
{
    Console.WriteLine($"Connecting to ws://{host}/ws/connect");
    var ws = new ClientWebSocket();
    var authBytes = Encoding.UTF8.GetBytes($"{user}:{password}");
    ws.Options.SetRequestHeader("Authorization", $"Basic {Convert.ToBase64String(authBytes)}");
    var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
    await ws.ConnectAsync(new Uri($"ws://{host}/ws/connect"), cts.Token);

    var handler = new WebSocketMessageHandler(ws);
    var rpc = new JsonRpc(handler);

    // Capture MetadataToken from responses
    var tokenCapture = new MetadataTokenCapture();
    rpc.TraceSource.Switch.Level = System.Diagnostics.SourceLevels.Verbose;
    rpc.TraceSource.Listeners.Add(tokenCapture);

    var callbacks = new ClientCallbacks();
    rpc.AddLocalRpcTarget(callbacks);
    rpc.StartListening();

    // OpenConnection
    await rpc.InvokeWithCancellationAsync<JToken>("OpenConnection",
        new object[] { new {
            LCID = 1033, DefaultLCID = 1033, TimeZoneId = "UTC",
            Credentials = new { UserName = user, Password = password }
        }}, cts.Token);
    Console.WriteLine("Connected.");

    long versionNumber = tokenCapture.MetadataToken;

    // OpenCompany
    Console.WriteLine($"Opening company '{company}'...");
    try
    {
        await rpc.InvokeWithCancellationAsync<JToken>("OpenCompany",
            new object[] { company, false }, cts.Token);
    }
    catch (RemoteInvocationException ex)
    {
        Console.Error.WriteLine($"OpenCompany warning: {ex.Message[..Math.Min(100, ex.Message.Length)]}");
    }
    versionNumber = tokenCapture.MetadataToken;

    // OpenForm 130455
    Console.WriteLine("Opening Command Line Test Tool (page 130455)...");
    var formCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    formCts.CancelAfter(TimeSpan.FromSeconds(30));
    var formResult = await rpc.InvokeWithCancellationAsync<JToken>("OpenForm",
        new object[] { new {
            HasMainForm = true,
            States = new[] { new { FormId = 130455, TableView = new { TableId = 130450 } } },
            ControlIds = new string?[] { null },
            VersionNumber = versionNumber,
            MainFormHandle = Guid.Empty
        }}, formCts.Token);

    if (formResult == null || formResult.Type == JTokenType.Null)
    {
        Console.Error.WriteLine("ERROR: Cannot open page 130455. Is the test toolkit installed?");
        return 1;
    }

    var formState = formResult["States"]?[0];
    Console.WriteLine($"Form opened (handle={formState?["ServerFormHandle"]})");

    // GetPage to position Rec
    try
    {
        var pageResult = await rpc.InvokeWithCancellationAsync<JToken>("GetPage",
            new object[] {
                new { PageSize = 50, IncludeMoreDataInformation = true, IncludeNonRowData = true },
                formState
            }, cts.Token);
        if (pageResult?["State"] != null) formState = pageResult["State"];
    }
    catch { /* non-fatal */ }

    // ClearTestResults
    Console.WriteLine("Clearing previous results...");
    try
    {
        var r = await rpc.InvokeWithCancellationAsync<JToken>("InvokeApplicationMethod",
            new object[] {
                new { ApplicationCodeType = 1, ObjectId = 0, MethodName = "ClearTestResults", DataSetState = formState },
                formState
            }, cts.Token);
        if (r?["DataSetState"] != null) formState = r["DataSetState"];
    }
    catch (RemoteInvocationException ex)
    {
        Console.Error.WriteLine($"ClearTestResults warning: {ex.Message[..Math.Min(100, ex.Message.Length)]}");
    }

    // RunNextTest loop
    int totalPassed = 0, totalFailed = 0, totalSkipped = 0;
    var startTime = DateTime.UtcNow;
    bool sessionAlive = true;

    while (sessionAlive)
    {
        Console.Write("Running next test... ");
        try
        {
            var actionCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            actionCts.CancelAfter(TimeSpan.FromMinutes(5));

            var result = await rpc.InvokeWithCancellationAsync<JToken>("InvokeApplicationMethod",
                new object[] {
                    new { ApplicationCodeType = 1, ObjectId = 0, MethodName = "RunNextTest", DataSetState = formState },
                    formState
                }, actionCts.Token);

            if (result?["DataSetState"] != null) formState = result["DataSetState"];

            // The TestResultJson is in the form's page variables, accessible via GetPage
            // For now, try to read it from the response
            Console.WriteLine("OK");
        }
        catch (RemoteInvocationException ex) when (ex.Message.Contains("All tests executed"))
        {
            Console.WriteLine("All tests executed.");
            break;
        }
        catch (RemoteInvocationException ex) when (ex.Message.Contains("does not exist"))
        {
            Console.Error.WriteLine($"\nERROR: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is RemoteInvocationException || ex is OperationCanceledException
            || ex is ConnectionLostException)
        {
            // Session died during test execution — this is expected with test isolation
            Console.WriteLine($"session ended ({ex.GetType().Name})");
            sessionAlive = false;

            // Try to reconnect for the next test
            try
            {
                rpc.Dispose();
                ws.Dispose();

                ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", $"Basic {Convert.ToBase64String(authBytes)}");
                await ws.ConnectAsync(new Uri($"ws://{host}/ws/connect"), cts.Token);

                handler = new WebSocketMessageHandler(ws);
                rpc = new JsonRpc(handler);
                rpc.TraceSource.Switch.Level = System.Diagnostics.SourceLevels.Verbose;
                rpc.TraceSource.Listeners.Add(tokenCapture);
                rpc.AddLocalRpcTarget(new ClientCallbacks());
                rpc.StartListening();

                await rpc.InvokeWithCancellationAsync<JToken>("OpenConnection",
                    new object[] { new {
                        LCID = 1033, DefaultLCID = 1033, TimeZoneId = "UTC",
                        Credentials = new { UserName = user, Password = password }
                    }}, cts.Token);

                try { await rpc.InvokeWithCancellationAsync<JToken>("OpenCompany",
                    new object[] { company, false }, cts.Token); } catch { }

                versionNumber = tokenCapture.MetadataToken;
                formResult = await rpc.InvokeWithCancellationAsync<JToken>("OpenForm",
                    new object[] { new {
                        HasMainForm = true,
                        States = new[] { new { FormId = 130455, TableView = new { TableId = 130450 } } },
                        ControlIds = new string?[] { null },
                        VersionNumber = versionNumber,
                        MainFormHandle = Guid.Empty
                    }}, cts.Token);

                if (formResult?["States"]?[0] != null)
                {
                    formState = formResult["States"]![0]!;
                    try
                    {
                        var pr = await rpc.InvokeWithCancellationAsync<JToken>("GetPage",
                            new object[] {
                                new { PageSize = 50, IncludeMoreDataInformation = true, IncludeNonRowData = true },
                                formState
                            }, cts.Token);
                        if (pr?["State"] != null) formState = pr["State"];
                    }
                    catch { }
                    sessionAlive = true;
                    Console.WriteLine("  Reconnected, continuing...");
                }
            }
            catch (Exception reconnectEx)
            {
                Console.Error.WriteLine($"  Reconnect failed: {reconnectEx.Message[..Math.Min(80, reconnectEx.Message.Length)]}");
                break;
            }
        }
    }

    var elapsed = DateTime.UtcNow - startTime;
    Console.WriteLine($"\nTest execution completed in {elapsed.TotalSeconds:F0}s");
    Console.WriteLine("Read results from SQL (see run-tests.sh wrapper).");

    try { await rpc.InvokeAsync("CloseConnection"); } catch { }
    rpc.Dispose();
    ws.Dispose();
    return 0;
}

// --- Callback handlers ---
class ClientCallbacks
{
    [JsonRpcMethod("Confirm")] public Task Confirm(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("ProcessServerRequests")] public Task ProcessServerRequests(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("FormRunModal")] public Task FormRunModal(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("FormClose")] public Task FormClose(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("FormActivate")] public Task FormActivate(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("OnSessionTerminating")] public Task OnSessionTerminating() => Task.CompletedTask;
    [JsonRpcMethod("ClearClientMetadataCache")] public Task ClearClientMetadataCache() => Task.CompletedTask;
    [JsonRpcMethod("SelectionMenu")] public Task SelectionMenu(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("FileActionDialog")] public Task FileActionDialog(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("FeedbackRequested")] public Task FeedbackRequested(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("CreateDotNetHandle")] public Task CreateDotNetHandle(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("GetDotNetObject")] public Task GetDotNetObject(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("DisposeAutomationObject")] public Task DisposeAutomationObject(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("InvokeAutomationMethod")] public Task InvokeAutomationMethod(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("DataSetPageReady")] public Task DataSetPageReady(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("OpenProgressDialog")] public Task OpenProgressDialog(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("CloseProgressDialog")] public Task CloseProgressDialog() => Task.CompletedTask;
    [JsonRpcMethod("UpdateProgressDialog")] public Task UpdateProgressDialog(JToken r) => Task.CompletedTask;
}

class MetadataTokenCapture : System.Diagnostics.TraceListener
{
    public long MetadataToken { get; private set; }
    public override void Write(string? message) { }
    public override void WriteLine(string? message)
    {
        if (message == null) return;
        var idx = message.IndexOf("\"MetadataToken\":", StringComparison.Ordinal);
        if (idx < 0) return;
        var start = idx + "\"MetadataToken\":".Length;
        var end = message.IndexOfAny(new[] { ',', '}', '\n' }, start);
        if (end < 0) end = message.Length;
        if (long.TryParse(message[start..end].Trim(), out var token) && token > 0)
            MetadataToken = token;
    }
}
