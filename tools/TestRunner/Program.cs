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
var user = "BCRUNNER";
var password = "Admin123!";
var timeoutMin = 30;
var codeunitTimeoutMin = 10; // max time for a single RunNextTest call (per codeunit)
var suiteName = "DEFAULT";
var codeunitFilter = ""; // comma-separated codeunit IDs or ranges
var maxIterations = 500;
var numCodeunitsOverride = 0; // explicit codeunit count for progress display
var verbose = false;
var junitOutput = ""; // path to write JUnit XML — empty = don't emit

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
    else if (args[i] == "--num-codeunits" && i + 1 < args.Length) numCodeunitsOverride = int.Parse(args[++i]);
    else if (args[i] == "--verbose" || args[i] == "-v") verbose = true;
    else if (args[i] == "--junit-output" && i + 1 < args.Length) junitOutput = args[++i];
    else if (!args[i].StartsWith("--")) host = args[i];
}

// Track which codeunits have been printed live (shared between RunTests and PrintLiveResults)
var printedCodeunits = new HashSet<int>();
// Track pass/fail/skip counts during live printing (avoids slow final OData read)
int livePassed = 0, liveFailed = 0, liveSkipped = 0;
// Track how many OData results we've already read (for $skip)
int odataResultsSeen = 0;
// Cached across calls — avoid re-resolving company ID and recreating HttpClient
HttpClient? cachedHttp = null;
string? cachedCompanyId = null;
// Per-method result records, populated by PrintLiveResults as tests complete.
// Used to emit JUnit XML at the end of the run when --junit-output is set.
var recordedResults = new List<RecordedResult>();

// Verbose logging — only shown with --verbose
void Log(string msg) { if (verbose) Console.Error.WriteLine(msg); }

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
        Log($"Setting up suite '{suiteName}' via OData (codeunits: {codeunitFilter})...");
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
                    Log("Suite setup OK");
                else
                    Log($"SetupSuite: {setupResp.StatusCode} - {await setupResp.Content.ReadAsStringAsync()}");
            }
            else
                Log($"Suite setup FAIL ({resp.StatusCode})");
        }
        catch (Exception ex) { Log($"Suite setup warning: {ex.Message[..Math.Min(80, ex.Message.Length)]}"); }
    }

    // Initial connection — used only for ClearTestResults before the loop.
    var (rpc, ws, _) = await Connect(authBytes, tokenCapture, cts.Token);
    var formState = await OpenTestPage(rpc, tokenCapture, company, cts.Token);
    if (formState == null) return 1;

    // ClearTestResults once, before the loop.
    Log("Clearing previous results...");
    try
    {
        var r = await Invoke(rpc, formState, "ClearTestResults", cts.Token);
        if (r?["DataSetState"] != null) formState = r["DataSetState"];
        Log("Clear OK");
    }
    catch (Exception ex) { Log($"Clear warning: {ex.Message[..Math.Min(80, ex.Message.Length)]}"); }

    rpc.Dispose(); ws.Dispose();

    // RunNextTest loop — reconnect BEFORE every call (mirrors BcContainerHelper's
    // renewClientContextBetweenTests).  Each test-codeunit run will kill the BC
    // session (test isolation), so ConnectionLostException is the normal outcome.
    // BC tracks which codeunit to run next in the Test Method Line table, so a
    // fresh session always advances to the next pending codeunit.
    var startTime = DateTime.UtcNow;

    // Limit iterations to roughly 2x the number of codeunits (each codeunit = 1 run + 1 reconnect)
    // plus extra buffer for the "All tests executed" detection.
    int numCodeunits = numCodeunitsOverride > 0 ? numCodeunitsOverride
        : !string.IsNullOrEmpty(codeunitFilter) ? codeunitFilter.Split(',').Length
        : 100;
    // With test isolation (runner 130451), each codeunit kills the session.
    // We need ~2 iterations per codeunit: one that runs + one empty reconnect.
    int effectiveMaxIterations = Math.Min(maxIterations, numCodeunits * 3 + 10);

    Log($"Running tests via WebSocket ({numCodeunits} codeunits, max {effectiveMaxIterations} iterations)...");
    int codeunitsRun = 0;
    printedCodeunits.Clear();
    for (int iteration = 0; iteration < effectiveMaxIterations; iteration++)
    {
        // Proactive reconnect before each RunNextTest.
        CancellationTokenSource sessionEndedCts;
        try
        {
            (rpc, ws, sessionEndedCts) = await Connect(authBytes, tokenCapture, cts.Token);
            formState = await OpenTestPage(rpc, tokenCapture, company, cts.Token);
            if (formState == null)
            {
                Log("Could not open test page after reconnect");
                break;
            }
        }
        catch (Exception ex)
        {
            Log($"Reconnect failed: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
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
                Log($"Watchdog: aborting hung connection after {codeunitTimeoutMin} min");
                try { capturedWs.Abort(); } catch { }
                try { capturedRpc.Dispose(); } catch { }
            });

            var result = await Invoke(rpc, formState, "RunNextTest", cts.Token);
            if (result?["DataSetState"] != null) formState = result["DataSetState"];

            // Try to read result while the connection is still alive.
            testResultJson = await ReadTestResultJson(rpc, formState!, cts.Token);
            if (testResultJson == "All tests executed.")
                allDone = true;
            else
            {
                // RunNextTest completed without killing the session (no test isolation on Linux).
                // Count the codeunit and fetch results normally.
                codeunitsRun++;
                await PrintLiveResults(authBytes, codeunitsRun, numCodeunits, startTime);
            }
        }
        catch (Exception ex) when (ex is ConnectionLostException || ex is RemoteInvocationException || ex is OperationCanceledException)
        {
            // Expected: BC killed the session after the codeunit finished (test isolation).
            codeunitsRun++;
            // Print live per-function results for the codeunit that just completed
            await PrintLiveResults(authBytes, codeunitsRun, numCodeunits, startTime);
        }

        try { rpc.Dispose(); ws.Dispose(); } catch { }

        if (allDone || codeunitsRun >= numCodeunits)
        {
            if (allDone) Log("All tests executed (page confirmed)");
            else Log($"All {numCodeunits} codeunits executed — stopping");
            break;
        }

        await Task.Delay(500, CancellationToken.None);
    }

    // Print summary from live counts — no final OData read needed (saves ~3.5 minutes)
    var elapsed = DateTime.UtcNow - startTime;
    int total = livePassed + liveFailed + liveSkipped;

    // JUnit XML emit. Off by default; only writes when --junit-output <path> is set.
    // Compatible with GitHub Checks reporters (dorny/test-reporter, EnricoMi/publish-unit-test-result-action),
    // Azure DevOps "Publish Test Results" task with format=JUnit, and AL-Go's AnalyzeTests
    // post-step which expects JUnit XML at .buildartifacts/TestResults.xml.
    if (!string.IsNullOrEmpty(junitOutput))
    {
        try
        {
            JUnitWriter.Write(junitOutput, recordedResults, suiteName, startTime, elapsed);
            Console.Error.WriteLine($"[junit] wrote {recordedResults.Count} test result(s) to {junitOutput}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[junit] WARN: failed to write {junitOutput}: {ex.Message}");
        }
    }

    Console.WriteLine($"\n=== Results ({elapsed.TotalSeconds:F0}s) ===");
    Console.WriteLine($"{total} total, {livePassed} passed, {liveFailed} failed, {liveSkipped} skipped");
    return liveFailed > 0 ? 1 : (livePassed > 0 ? 0 : 1);
}

async Task PrintLiveResults(byte[] authBytes, int codeunitsRun, int numCodeunits, DateTime startTime)
{
    try
    {
        // Reuse HttpClient and company ID across calls
        if (cachedHttp == null)
        {
            cachedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            cachedHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(authBytes));
        }
        if (cachedCompanyId == null)
        {
            var compResp = await cachedHttp.GetStringAsync($"http://{odataHost}/BC/api/v2.0/companies");
            cachedCompanyId = JObject.Parse(compResp)["value"]?.FirstOrDefault(c => c["name"]?.ToString() == company)?["id"]?.ToString()
                ?? JObject.Parse(compResp)["value"]?.FirstOrDefault()?["id"]?.ToString();
            if (cachedCompanyId == null) return;
        }

        var apiBase = $"http://{odataHost}/BC/api/custom/automation/v1.0/companies({cachedCompanyId})";
        // Only fetch new results using $skip — results are ordered by primary key (insertion order)
        var filter = Uri.EscapeDataString("testSuite eq 'DEFAULT' and lineType eq 'Function' and result ne ' '");
        var url = $"{apiBase}/testResults?$filter={filter}&$top=500&$skip={odataResultsSeen}";
        var resp = await cachedHttp.GetStringAsync(url);
        var results = JObject.Parse(resp)["value"] as JArray;
        if (results == null || results.Count == 0) return;

        odataResultsSeen += results.Count;

        // Group new results by codeunit for per-codeunit duration header
        var byCu = new Dictionary<int, List<JToken>>();
        var cuOrder = new List<int>();
        foreach (var r in results)
        {
            var cuId = r["testCodeunit"]?.Value<int>() ?? 0;
            if (!byCu.ContainsKey(cuId)) { byCu[cuId] = new List<JToken>(); cuOrder.Add(cuId); }
            byCu[cuId].Add(r);
        }

        foreach (var cuId in cuOrder)
        {
            var funcs = byCu[cuId];
            var cuName = funcs[0]["name"]?.ToString() ?? "";

            // Compute per-codeunit duration from min(startTime) to max(finishTime)
            DateTime? cuStart = null, cuEnd = null;
            foreach (var r in funcs)
            {
                var s = r["startTime"]?.Value<DateTime?>();
                var e = r["finishTime"]?.Value<DateTime?>();
                if (s.HasValue && (!cuStart.HasValue || s < cuStart)) cuStart = s;
                if (e.HasValue && (!cuEnd.HasValue || e > cuEnd)) cuEnd = e;
            }
            var durationStr = "";
            if (cuStart.HasValue && cuEnd.HasValue)
            {
                var secs = (cuEnd.Value - cuStart.Value).TotalSeconds;
                durationStr = $" ({secs:F1}s)";
            }

            Console.WriteLine($"  [{codeunitsRun}/{numCodeunits}] Codeunit {cuId}: {cuName}{durationStr}");

            foreach (var r in funcs)
            {
                var fn = r["functionName"]?.ToString() ?? "";
                var result = r["result"]?.ToString() ?? "";
                var errMsg = r["errorMessage"]?.ToString() ?? r["errorMessagePreview"]?.ToString() ?? "";
                var callStack = r["errorCallStack"]?.ToString() ?? "";

                // Count for summary
                if (result == "Success" || result == "2") livePassed++;
                else if (result == "Failure" || result == "1") liveFailed++;
                else liveSkipped++;

                var status = (result == "Success" || result == "2") ? "PASS"
                    : (result == "Failure" || result == "1") ? "FAIL" : "SKIP";

                // Capture per-method duration and timestamps for JUnit emit.
                // Both fields are optional in BC's response — fall back to 0/now.
                var fnStart = r["startTime"]?.Value<DateTime?>();
                var fnEnd = r["finishTime"]?.Value<DateTime?>();
                double fnSeconds = 0.0;
                if (fnStart.HasValue && fnEnd.HasValue)
                    fnSeconds = Math.Max(0.0, (fnEnd.Value - fnStart.Value).TotalSeconds);

                recordedResults.Add(new RecordedResult(
                    CodeunitId: cuId,
                    CodeunitName: cuName,
                    FunctionName: fn,
                    Status: status,
                    DurationSeconds: fnSeconds,
                    StartTime: fnStart ?? DateTime.UtcNow,
                    ErrorMessage: status == "FAIL" ? errMsg : "",
                    CallStack: status == "FAIL" ? callStack : ""));

                Console.Write($"    {status}  {fn}");
                if (status == "FAIL" && errMsg.Length > 0)
                    Console.Write($" — {errMsg[..Math.Min(200, errMsg.Length)]}");
                Console.WriteLine();
                if (status == "FAIL" && callStack.Length > 0)
                {
                    // BC uses backslash as call stack line separator
                    foreach (var line in callStack.Split('\\').Take(10))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0)
                            Console.WriteLine($"           {trimmed[..Math.Min(120, trimmed.Length)]}");
                    }
                }
            }

            printedCodeunits.Add(cuId);
        }
        Console.Out.Flush();
    }
    catch { /* OData read failed — silent */ }
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
    Log($"Connecting to ws://{host}/ws/connect");
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
    Log("Connected.");
    return (rpc, ws, sessionEndedCts);
}

async Task<JToken?> OpenTestPage(JsonRpc rpc, MetadataTokenCapture tc, string company, CancellationToken ct)
{
    try { await rpc.InvokeWithCancellationAsync<JToken>("OpenCompany", new object[] { company, false }, ct); }
    catch (RemoteInvocationException ex) { Log($"OpenCompany: {ex.Message[..Math.Min(80, ex.Message.Length)]}"); }

    Log($"Opening page 130455 (suite={suiteName})...");
    var formCts = CancellationTokenSource.CreateLinkedTokenSource(ct); formCts.CancelAfter(TimeSpan.FromSeconds(30));
    var form = await rpc.InvokeWithCancellationAsync<JToken>("OpenForm",
        new object[] { new { HasMainForm = true, States = new[] { new {
            FormId = 130455, TableView = new { TableId = 130450, View = $"WHERE(Name=CONST({suiteName}))" }
        } }, ControlIds = new string?[] { null }, VersionNumber = tc.MetadataToken, MainFormHandle = Guid.Empty } }, formCts.Token);
    if (form == null || form.Type == JTokenType.Null) { Log("OpenForm failed"); return null; }
    var state = form["States"]?[0];
    Log($"Page opened ({state?["ServerFormHandle"]})");
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
    // ClientResponse DTO matching Microsoft.Dynamics.Nav.Types.ClientResponse.
    // The server's IClientApi.EndClientCall(ClientResponse response) accesses response.Result,
    // so we must send an object (not null) to avoid NullReferenceException in EndClientCall.
    private static readonly JObject EmptyClientResponse = new JObject { ["Result"] = null };

    private async Task AckCallback(string name)
    {
        // EndClientCall is silent by default — it fires frequently during test execution
        if (Rpc != null)
        {
            try { await Rpc.InvokeAsync("EndClientCall", new object?[] { EmptyClientResponse }); }
            catch { /* connection may be closing */ }
        }
    }

    [JsonRpcMethod("ClearClientMetadataCache")]
    public async Task ClearClientMetadataCache() => await AckCallback("ClearClientMetadataCache");

    [JsonRpcMethod("OnSessionTerminating")]
    public Task OnSessionTerminating() { _sessionEndedCts.Cancel(); return Task.CompletedTask; }

    [JsonRpcMethod("Confirm")] public async Task Confirm(JToken r) => await AckCallback("Confirm");
    [JsonRpcMethod("ProcessServerRequests")] public async Task ProcessServerRequests(JToken r) => await AckCallback("ProcessServerRequests");
    [JsonRpcMethod("FormRunModal")] public async Task FormRunModal(JToken r) => await AckCallback("FormRunModal");
    [JsonRpcMethod("FormClose")] public async Task FormClose(JToken r) => await AckCallback("FormClose");
    [JsonRpcMethod("FormActivate")] public async Task FormActivate(JToken r) => await AckCallback("FormActivate");
    // StrMenu: respond with option 1 (first choice). Many tests trigger privacy consent
    // dialogs — selecting the first option ("Allow Always") lets them proceed.
    [JsonRpcMethod("SelectionMenu")] public async Task SelectionMenu(JToken r)
    {
        if (Rpc != null)
            try { await Rpc.InvokeAsync("EndClientCall", new object?[] { 1 }); } catch { }
    }
    [JsonRpcMethod("FileActionDialog")] public async Task FileActionDialog(JToken r) => await AckCallback("FileActionDialog");
    [JsonRpcMethod("FeedbackRequested")] public async Task FeedbackRequested(JToken r) => await AckCallback("FeedbackRequested");
    [JsonRpcMethod("CreateDotNetHandle")] public async Task CreateDotNetHandle(JToken r) => await AckCallback("CreateDotNetHandle");
    [JsonRpcMethod("GetDotNetObject")] public async Task GetDotNetObject(JToken r) => await AckCallback("GetDotNetObject");
    // Dialog callbacks — BC sends these when AL code opens/closes dialogs
    [JsonRpcMethod("CloseDialog")] public async Task CloseDialog(JToken r) => await AckCallback("CloseDialog");
    [JsonRpcMethod("OpenDialog")] public async Task OpenDialog(JToken r) => await AckCallback("OpenDialog");
    [JsonRpcMethod("UpdateDialog")] public async Task UpdateDialog(JToken r) => await AckCallback("UpdateDialog");
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

// Per-method record captured during PrintLiveResults. Populated as test methods
// complete; consumed by WriteJUnitXml at the end of the run when --junit-output
// is set.
record RecordedResult(
    int CodeunitId,
    string CodeunitName,
    string FunctionName,
    string Status,             // "PASS" | "FAIL" | "SKIP"
    double DurationSeconds,
    DateTime StartTime,
    string ErrorMessage,
    string CallStack);

static class JUnitWriter
{
    // Emits a JUnit XML file compatible with:
    //   - GitHub Checks reporters (dorny/test-reporter, EnricoMi/publish-unit-test-result-action)
    //   - Azure DevOps "Publish Test Results" task with format=JUnit
    //   - AL-Go's AnalyzeTests post-step (expects .buildartifacts/TestResults.xml)
    //
    // Schema: one <testsuite> per BC codeunit. Each <testcase> is one [Test]
    // procedure. <failure> elements carry the BC error message + call stack
    // for failing tests. <skipped/> for SKIP. PASS has no children.
    public static void Write(string path, IReadOnlyList<RecordedResult> results, string suiteName, DateTime runStart, TimeSpan totalElapsed)
    {
        // Group by codeunit so each gets one <testsuite>.
        var byCu = results
            .GroupBy(r => r.CodeunitId)
            .OrderBy(g => g.Key)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");

        int totalTests = results.Count;
        int totalFailures = results.Count(r => r.Status == "FAIL");
        int totalSkipped = results.Count(r => r.Status == "SKIP");

        sb.Append("<testsuites name=\"").Append(XmlEscape(suiteName)).Append("\"");
        sb.Append(" tests=\"").Append(totalTests).Append("\"");
        sb.Append(" failures=\"").Append(totalFailures).Append("\"");
        sb.Append(" errors=\"0\"");
        sb.Append(" skipped=\"").Append(totalSkipped).Append("\"");
        sb.Append(" time=\"").Append(totalElapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append("\"");
        sb.Append(" timestamp=\"").Append(runStart.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("\"");
        sb.AppendLine(">");

        foreach (var g in byCu)
        {
            // Classname uses just the codeunit id. BC's Test Method Line table
            // doesn't expose the codeunit name on Function rows (Rec.Name on a
            // Function row holds the function name, not the codeunit name —
            // verified empirically against testResults). The codeunit id is
            // unambiguous and matches the typical "ClassName.testName" pattern
            // that JUnit consumers (GitHub Checks, Azure DevOps, dorny) display.
            // RecordedResult.CodeunitName is preserved as a hint for diagnostic
            // logs but intentionally not used here.
            var className = $"Codeunit {g.Key}";
            int suiteFailures = g.Count(r => r.Status == "FAIL");
            int suiteSkipped = g.Count(r => r.Status == "SKIP");
            double suiteSeconds = g.Sum(r => r.DurationSeconds);
            var suiteStart = g.Min(r => r.StartTime);

            sb.Append("  <testsuite name=\"").Append(XmlEscape(className)).Append("\"");
            sb.Append(" tests=\"").Append(g.Count()).Append("\"");
            sb.Append(" failures=\"").Append(suiteFailures).Append("\"");
            sb.Append(" errors=\"0\"");
            sb.Append(" skipped=\"").Append(suiteSkipped).Append("\"");
            sb.Append(" time=\"").Append(suiteSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append("\"");
            sb.Append(" timestamp=\"").Append(suiteStart.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("\"");
            sb.AppendLine(">");

            foreach (var r in g)
            {
                sb.Append("    <testcase classname=\"").Append(XmlEscape(className)).Append("\"");
                sb.Append(" name=\"").Append(XmlEscape(r.FunctionName)).Append("\"");
                sb.Append(" time=\"").Append(r.DurationSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append("\"");

                if (r.Status == "FAIL")
                {
                    sb.AppendLine(">");
                    sb.Append("      <failure message=\"").Append(XmlEscape(Truncate(r.ErrorMessage, 500))).Append("\" type=\"AssertionFailure\">");
                    // Body: full message + call stack (BC uses backslash as separator)
                    var body = new StringBuilder();
                    if (!string.IsNullOrEmpty(r.ErrorMessage))
                    {
                        body.AppendLine(r.ErrorMessage);
                    }
                    if (!string.IsNullOrEmpty(r.CallStack))
                    {
                        body.AppendLine();
                        body.AppendLine("Call stack:");
                        foreach (var line in r.CallStack.Split('\\'))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length > 0) body.AppendLine("  " + trimmed);
                        }
                    }
                    sb.Append(XmlEscape(body.ToString()));
                    sb.AppendLine("</failure>");
                    sb.AppendLine("    </testcase>");
                }
                else if (r.Status == "SKIP")
                {
                    sb.AppendLine(">");
                    sb.AppendLine("      <skipped/>");
                    sb.AppendLine("    </testcase>");
                }
                else
                {
                    // PASS — self-closing testcase, no body
                    sb.AppendLine("/>");
                }
            }

            sb.AppendLine("  </testsuite>");
        }

        sb.AppendLine("</testsuites>");

        // Make sure the parent directory exists; AL-Go expects .buildartifacts/
        // and similar nested paths.
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);

        System.IO.File.WriteAllText(path, sb.ToString());
    }

    private static string XmlEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Replace XML metacharacters. Order matters: & first, then the others.
        return s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
