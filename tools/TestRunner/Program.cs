using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

// BC Test Runner — connects via WebSocket client services to page 130455
// (Command Line Test Tool) and runs tests using RunNextTest + TestResultJson.
//
// Mirrors BcContainerHelper's Run-TestsInBcContainer external behavior with
// renewClientContextBetweenTests: reconnect BEFORE each RunNextTest call so
// that BC's test-isolation session kill (which drops the WebSocket after each
// codeunit) is never treated as an error.  BC tracks test-suite progress in
// the Test Method Line table, so a fresh session always picks up where it
// left off.
//
// Note: The DEFAULT test suite must be pre-created (via SQL in run-tests.sh).
// The suite is opened by filtering TableView to Name=suiteName, which positions
// the page on the correct record so OnOpenPage sets CurrentSuiteName correctly.

var host = "localhost:7085";
var odataHost = "localhost:7052"; // OData API host for suite setup
var company = "CRONUS International Ltd.";
var user = "admin";
var password = "Admin123!";
var timeoutMin = 30;
var codeunitTimeoutMin = 10; // max time for a single RunNextTest call (per codeunit)
var suiteName = "DEFAULT";
var codeunitFilter = ""; // comma-separated codeunit IDs or ranges
var maxIterations = 500;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
    else if (args[i] == "--odata-host" && i + 1 < args.Length) odataHost = args[++i];
    else if (args[i] == "--company" && i + 1 < args.Length) company = args[++i];
    else if (args[i] == "--user" && i + 1 < args.Length) user = args[++i];
    else if (args[i] == "--password" && i + 1 < args.Length) password = args[++i];
    else if (args[i] == "--timeout" && i + 1 < args.Length) timeoutMin = int.Parse(args[++i]);
    else if (args[i] == "--codeunit-timeout" && i + 1 < args.Length) codeunitTimeoutMin = int.Parse(args[++i]);
    else if (args[i] == "--suite" && i + 1 < args.Length) suiteName = args[++i];
    else if (args[i] == "--codeunit-filter" && i + 1 < args.Length) codeunitFilter = args[++i];
    else if (args[i] == "--max-iterations" && i + 1 < args.Length) maxIterations = int.Parse(args[++i]);
    else if (!args[i].StartsWith("--")) host = args[i];
}

int exitCode = 1;
try { exitCode = await RunTests(); }
catch (Exception ex) { Console.Error.WriteLine($"FATAL: {ex.Message}"); }
return exitCode;

async Task<int> RunTests()
{
    var authBytes = Encoding.UTF8.GetBytes($"{user}:{password}");
    var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMin));
    var tokenCapture = new MetadataTokenCapture();

    // Step 0: If --codeunit-filter is set, populate the test suite via OData API.
    // This creates the suite, discovers test methods via codeunit 130452, and sets
    // everything up so RunNextTest on page 130455 has work to do.
    if (!string.IsNullOrEmpty(codeunitFilter))
    {
        Console.Write($"Setting up suite '{suiteName}' via OData (codeunits: {codeunitFilter})... ");
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(authBytes));
            // Discover company ID via standard API
            var compResp = await http.GetStringAsync($"http://{odataHost}/BC/api/v2.0/companies");
            var companies = JObject.Parse(compResp)["value"] as JArray ?? new JArray();
            var compId = companies.FirstOrDefault(c => c["name"]?.ToString() == company)?["id"]?.ToString()
                ?? companies.FirstOrDefault()?["id"]?.ToString()
                ?? "dcfa9b8e-552b-f111-9f23-7ced8d3f0294";
            var apiBase = $"http://{odataHost}/BC/api/custom/automation/v1.0";

            // Create a run request with CodeunitIds
            var body = new StringContent(
                $"{{\"CodeunitIds\": \"{codeunitFilter}\"}}",
                Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{apiBase}/companies({compId})/codeunitRunRequests", body);
            if (resp.IsSuccessStatusCode)
            {
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                var reqId = json["Id"]?.ToString();
                // Call SetupSuite (populates suite + methods, does NOT run tests)
                var setupResp = await http.PostAsync(
                    $"{apiBase}/companies({compId})/codeunitRunRequests({reqId})/Microsoft.NAV.setupSuite",
                    null);
                if (setupResp.IsSuccessStatusCode)
                    Console.WriteLine("OK");
                else
                    Console.Error.WriteLine($"SetupSuite: {setupResp.StatusCode} - {await setupResp.Content.ReadAsStringAsync()}");
            }
            else
                Console.Error.WriteLine($"FAIL ({resp.StatusCode})");
        }
        catch (Exception ex) { Console.Error.WriteLine($"warning: {ex.Message[..Math.Min(80, ex.Message.Length)]}"); }
    }

    // Initial connection — used only for ClearTestResults before the loop.
    var (rpc, ws, _) = await Connect(authBytes, tokenCapture, cts.Token);
    var formState = await OpenTestPage(rpc, tokenCapture, company, cts.Token);
    if (formState == null) return 1;

    // ClearTestResults once, before the loop.
    Console.Write("Clearing previous results... ");
    try
    {
        var r = await Invoke(rpc, formState, "ClearTestResults", cts.Token);
        if (r?["DataSetState"] != null) formState = r["DataSetState"];
        Console.WriteLine("OK");
    }
    catch (Exception ex) { Console.Error.WriteLine($"warning: {ex.Message[..Math.Min(80, ex.Message.Length)]}"); }

    rpc.Dispose(); ws.Dispose();

    // RunNextTest loop — reconnect BEFORE every call (mirrors BcContainerHelper's
    // renewClientContextBetweenTests).  Each test-codeunit run will kill the BC
    // session (test isolation), so ConnectionLostException is the normal outcome.
    // BC tracks which codeunit to run next in the Test Method Line table, so a
    // fresh session always advances to the next pending codeunit.
    int totalPassed = 0, totalFailed = 0, totalSkipped = 0;
    var startTime = DateTime.UtcNow;

    Console.WriteLine("\n=== Running Tests ===");
    for (int iteration = 0; iteration < maxIterations; iteration++)
    {
        // Proactive reconnect before each RunNextTest.
        CancellationTokenSource sessionEndedCts;
        try
        {
            (rpc, ws, sessionEndedCts) = await Connect(authBytes, tokenCapture, cts.Token);
            formState = await OpenTestPage(rpc, tokenCapture, company, cts.Token);
            if (formState == null)
            {
                Console.Error.WriteLine("  Could not open test page after reconnect");
                break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Reconnect failed: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            break;
        }

        string testResultJson = "";
        bool allDone = false;
        try
        {
            var capturedRpc = rpc;
            var capturedWs = ws;
            _ = Task.Delay(TimeSpan.FromMinutes(codeunitTimeoutMin)).ContinueWith(_ =>
            {
                Console.Error.WriteLine($"  Watchdog: aborting hung connection after {codeunitTimeoutMin} min (--codeunit-timeout)");
                try { capturedWs.Abort(); } catch { }
                try { capturedRpc.Dispose(); } catch { }
            });

            var result = await Invoke(rpc, formState, "RunNextTest", cts.Token);
            if (result?["DataSetState"] != null) formState = result["DataSetState"];

            // Try to read result while the connection is still alive.
            testResultJson = await ReadTestResultJson(rpc, formState!, cts.Token);
            if (testResultJson == "All tests executed.")
                allDone = true;
        }
        catch (Exception ex) when (ex is ConnectionLostException || ex is RemoteInvocationException || ex is OperationCanceledException)
        {
            // Expected: BC killed the session after the codeunit finished (test isolation).
            Console.Error.WriteLine($"  Session ended after codeunit (expected)");
        }

        try { rpc.Dispose(); ws.Dispose(); } catch { }

        if (allDone)
        {
            Console.WriteLine("  All tests executed (page confirmed).");
            break;
        }

        await Task.Delay(500, CancellationToken.None);
    }

    // Read final results from DB via OData — this is reliable regardless of
    // whether the WebSocket session survived long enough to return TestResultJson.
    var elapsed = DateTime.UtcNow - startTime;
    Console.WriteLine($"\n=== Test Results ({elapsed.TotalSeconds:F0}s) ===");
    return await ReadAndPrintResultsViaOData(authBytes, cts.Token);
}

async Task<int> ReadAndPrintResultsViaOData(byte[] authBytes, CancellationToken ct)
{
    int totalPassed = 0, totalFailed = 0, totalSkipped = 0;
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(authBytes));
        var apiBase = $"http://{odataHost}/BC/api/custom/automation/v1.0";

        // Get company ID
        var compResp = await http.GetStringAsync($"http://{odataHost}/BC/api/v2.0/companies");
        var companies = JObject.Parse(compResp)["value"] as JArray ?? new JArray();
        var compId = companies.FirstOrDefault(c => c["name"]?.ToString() == company)?["id"]?.ToString()
            ?? companies.FirstOrDefault()?["id"]?.ToString();
        if (compId == null) { Console.Error.WriteLine("Could not find company via OData"); return 1; }

        // Read test results from Test Method Line table via OData
        var filter = Uri.EscapeDataString($"testSuite eq 'DEFAULT' and lineType eq 'Function'");
        var resultResp = await http.GetStringAsync($"{apiBase}/companies({compId})/testResults?$filter={filter}&$top=5000");
        var results = JObject.Parse(resultResp)["value"] as JArray;
        if (results == null || results.Count == 0)
        {
            Console.Error.WriteLine("No test results found via OData");
            return 1;
        }

        int? lastCu = null;
        foreach (var r in results)
        {
            var cuId = r["testCodeunit"]?.Value<int>() ?? 0;
            var name = r["name"]?.ToString() ?? "";
            var fn = r["functionName"]?.ToString() ?? "";
            var result = r["result"]?.ToString() ?? "";
            var errMsg = r["errorMessagePreview"]?.ToString() ?? "";

            // Print codeunit header on change
            if (lastCu != cuId)
            {
                if (lastCu != null) Console.WriteLine();
                Console.WriteLine($"  Codeunit {cuId}: {name}");
                lastCu = cuId;
            }

            var status = result switch { "Success" => "PASS", "Failure" => "FAIL", _ => "SKIP" };
            // Result enum: " " = 0 (not run), Success = 2, Failure = 1, Skipped = 3
            if (result == "Success" || result == "2") { totalPassed++; status = "PASS"; }
            else if (result == "Failure" || result == "1") { totalFailed++; status = "FAIL"; }
            else { totalSkipped++; status = "SKIP"; }

            Console.Write($"    {status}  {fn}");
            if (status == "FAIL" && errMsg.Length > 0)
                Console.Write($" — {errMsg[..Math.Min(200, errMsg.Length)]}");
            Console.WriteLine();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"OData result read failed: {ex.Message[..Math.Min(120, ex.Message.Length)]}");
    }

    int total = totalPassed + totalFailed + totalSkipped;
    Console.WriteLine($"\nResults: {total} total, {totalPassed} passed, {totalFailed} failed, {totalSkipped} skipped");
    return totalFailed > 0 ? 1 : (totalPassed > 0 ? 0 : 1);
}

async Task<string> ReadTestResultJson(JsonRpc rpc, JToken formState, CancellationToken ct)
{
    try
    {
        var page = await rpc.InvokeWithCancellationAsync<JToken>("GetPage",
            new object[] { new { PageSize = 1, IncludeMoreDataInformation = true, IncludeNonRowData = true }, formState }, ct);
        var s = page?.ToString() ?? "";
        var idx = s.IndexOf("\"TestResultJson\"", StringComparison.Ordinal);
        if (idx < 0) idx = s.IndexOf("\"TestResultsJSONText\"", StringComparison.Ordinal);
        if (idx >= 0)
        {
            // Extract the value after the key
            var valStart = s.IndexOf(":", idx) + 1;
            var valEnd = s.IndexOf("\n", valStart);
            if (valEnd < 0) valEnd = s.Length;
            var val = s[valStart..valEnd].Trim().Trim('"', ',');
            if (val.StartsWith("{")) return val;
            if (val == "All tests executed.") return val;
        }
        return "";
    }
    catch { return ""; }
}

async Task<JToken?> Invoke(JsonRpc rpc, JToken? formState, string action, CancellationToken ct)
{
    return await rpc.InvokeWithCancellationAsync<JToken>("InvokeApplicationMethod",
        new object[] {
            new { ApplicationCodeType = 1, ObjectId = 0, MethodName = action, DataSetState = formState },
            formState!
        }, ct);
}

async Task<(JsonRpc, ClientWebSocket, CancellationTokenSource)> Connect(byte[] authBytes, MetadataTokenCapture tc, CancellationToken ct)
{
    Console.WriteLine($"Connecting to ws://{host}/ws/connect");
    var sessionEndedCts = new CancellationTokenSource();
    var ws = new ClientWebSocket();
    ws.Options.SetRequestHeader("Authorization", $"Basic {Convert.ToBase64String(authBytes)}");
    await ws.ConnectAsync(new Uri($"ws://{host}/ws/connect"), ct);
    var rpc = new JsonRpc(new WebSocketMessageHandler(ws));
    rpc.TraceSource.Switch.Level = System.Diagnostics.SourceLevels.Verbose;
    rpc.TraceSource.Listeners.Add(tc);
    var callbacks = new Callbacks(sessionEndedCts);
    rpc.AddLocalRpcTarget(callbacks);
    callbacks.Rpc = rpc;
    rpc.StartListening();
    await rpc.InvokeWithCancellationAsync<JToken>("OpenConnection",
        new object[] { new { LCID = 1033, DefaultLCID = 1033, TimeZoneId = "UTC", Credentials = new { UserName = user, Password = password } } }, ct);
    Console.WriteLine("Connected.");
    return (rpc, ws, sessionEndedCts);
}

async Task<JToken?> OpenTestPage(JsonRpc rpc, MetadataTokenCapture tc, string company, CancellationToken ct)
{
    try { await rpc.InvokeWithCancellationAsync<JToken>("OpenCompany", new object[] { company, false }, ct); }
    catch (RemoteInvocationException ex) { Console.Error.WriteLine($"  OpenCompany: {ex.Message[..Math.Min(80, ex.Message.Length)]}"); }

    Console.Write($"Opening page 130455 (suite={suiteName})... ");
    var formCts = CancellationTokenSource.CreateLinkedTokenSource(ct); formCts.CancelAfter(TimeSpan.FromSeconds(30));
    var form = await rpc.InvokeWithCancellationAsync<JToken>("OpenForm",
        new object[] { new { HasMainForm = true, States = new[] { new {
            FormId = 130455, TableView = new { TableId = 130450, View = $"WHERE(Name=CONST({suiteName}))" }
        } }, ControlIds = new string?[] { null }, VersionNumber = tc.MetadataToken, MainFormHandle = Guid.Empty } }, formCts.Token);
    if (form == null || form.Type == JTokenType.Null) { Console.Error.WriteLine("FAIL"); return null; }
    var state = form["States"]?[0];
    Console.WriteLine($"OK ({state?["ServerFormHandle"]})");
    try { var pg = await rpc.InvokeWithCancellationAsync<JToken>("GetPage", new object[] { new { PageSize = 50, IncludeMoreDataInformation = true, IncludeNonRowData = true }, state! }, ct); if (pg?["State"] != null) state = pg["State"]; } catch { }
    return state;
}

class Callbacks
{
    private readonly CancellationTokenSource _sessionEndedCts;
    public JsonRpc? Rpc { get; set; }

    public Callbacks(CancellationTokenSource sessionEndedCts) => _sessionEndedCts = sessionEndedCts;

    // BC's client callback protocol: server sends a JSON-RPC call to the client,
    // then blocks on a ResultWaiter. The client must:
    //   1. Handle the callback (return from the JSON-RPC method)
    //   2. Call EndClientCall on the server to unblock the ResultWaiter
    // Without step 2, the server hangs forever.
    private async Task AckCallback(string name)
    {
        Console.Error.WriteLine($"  [callback] {name} — sending EndClientCall");
        if (Rpc != null)
        {
            try { await Rpc.InvokeAsync("EndClientCall", new object?[] { null }); }
            catch { /* connection may be closing */ }
        }
    }

    [JsonRpcMethod("ClearClientMetadataCache")]
    public async Task ClearClientMetadataCache() => await AckCallback("ClearClientMetadataCache");

    [JsonRpcMethod("OnSessionTerminating")]
    public Task OnSessionTerminating() { Console.Error.WriteLine("  [notification] OnSessionTerminating received"); _sessionEndedCts.Cancel(); return Task.CompletedTask; }

    [JsonRpcMethod("Confirm")] public async Task Confirm(JToken r) => await AckCallback("Confirm");
    [JsonRpcMethod("ProcessServerRequests")] public async Task ProcessServerRequests(JToken r) => await AckCallback("ProcessServerRequests");
    [JsonRpcMethod("FormRunModal")] public async Task FormRunModal(JToken r) => await AckCallback("FormRunModal");
    [JsonRpcMethod("FormClose")] public async Task FormClose(JToken r) => await AckCallback("FormClose");
    [JsonRpcMethod("FormActivate")] public async Task FormActivate(JToken r) => await AckCallback("FormActivate");
    [JsonRpcMethod("SelectionMenu")] public async Task SelectionMenu(JToken r) => await AckCallback("SelectionMenu");
    [JsonRpcMethod("FileActionDialog")] public async Task FileActionDialog(JToken r) => await AckCallback("FileActionDialog");
    [JsonRpcMethod("FeedbackRequested")] public async Task FeedbackRequested(JToken r) => await AckCallback("FeedbackRequested");
    [JsonRpcMethod("CreateDotNetHandle")] public async Task CreateDotNetHandle(JToken r) => await AckCallback("CreateDotNetHandle");
    [JsonRpcMethod("GetDotNetObject")] public async Task GetDotNetObject(JToken r) => await AckCallback("GetDotNetObject");
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
    public override void Write(string? m) { }
    public override void WriteLine(string? m)
    {
        if (m == null) return;
        var i = m.IndexOf("\"MetadataToken\":", StringComparison.Ordinal);
        if (i < 0) return;
        var s = i + "\"MetadataToken\":".Length;
        var e = m.IndexOfAny(new[] { ',', '}', '\n' }, s);
        if (e < 0) e = m.Length;
        if (long.TryParse(m[s..e].Trim(), out var t) && t > 0) MetadataToken = t;
    }
}
