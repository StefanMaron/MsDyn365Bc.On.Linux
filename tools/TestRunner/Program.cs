using System;
using System.Collections.Generic;
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
var company = "CRONUS International Ltd.";
var user = "admin";
var password = "Admin123!";
var timeoutMin = 30;
var suiteName = "DEFAULT";
var maxIterations = 500;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
    else if (args[i] == "--company" && i + 1 < args.Length) company = args[++i];
    else if (args[i] == "--user" && i + 1 < args.Length) user = args[++i];
    else if (args[i] == "--password" && i + 1 < args.Length) password = args[++i];
    else if (args[i] == "--timeout" && i + 1 < args.Length) timeoutMin = int.Parse(args[++i]);
    else if (args[i] == "--suite" && i + 1 < args.Length) suiteName = args[++i];
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
    var allResults = new List<JObject>();
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
        try
        {
            // BC kills the AL session after each test-codeunit run (test isolation).
            // It does NOT send a clean WebSocket close frame, so the pending Invoke hangs
            // indefinitely.  We use a watchdog task that aborts the WebSocket after a
            // deadline — this interrupts the StreamJsonRpc receive loop and causes
            // ConnectionLostException to be thrown on the pending InvokeWithCancellationAsync.
            //
            // IMPORTANT: we do NOT link the watchdog to sessionEndedCts.  BC sends
            // ClearClientMetadataCache for reasons other than session termination (e.g.
            // after app publish), which would immediately cancel a linked token and silently
            // disarm the watchdog before it could fire.
            var capturedRpc = rpc;
            var capturedWs = ws;
            _ = Task.Delay(TimeSpan.FromSeconds(180)).ContinueWith(_ =>
            {
                Console.Error.WriteLine("  Watchdog: aborting hung connection after 3 min");
                try { capturedWs.Abort(); } catch { }
                try { capturedRpc.Dispose(); } catch { }
            });

            var result = await Invoke(rpc, formState, "RunNextTest", cts.Token);
            if (result?["DataSetState"] != null) formState = result["DataSetState"];

            // Try to read result while the connection is still alive.
            testResultJson = await ReadTestResultJson(rpc, formState!, cts.Token);
        }
        catch (Exception ex) when (ex is ConnectionLostException || ex is RemoteInvocationException || ex is OperationCanceledException)
        {
            // Expected: BC killed the session after the codeunit finished (test isolation).
            // The result is committed to the Test Method Line table; the next iteration
            // will reconnect and RunNextTest will advance to the next codeunit.
            Console.Error.WriteLine($"  Session ended after codeunit (expected): {ex.Message[..Math.Min(60, ex.Message.Length)]}");
            try { rpc.Dispose(); ws.Dispose(); } catch { }
            await Task.Delay(500, CancellationToken.None);
            continue;
        }

        try { rpc.Dispose(); ws.Dispose(); } catch { }

        if (testResultJson == "All tests executed.")
        {
            Console.WriteLine("All tests executed.");
            break;
        }

        // Empty means the connection died after RunNextTest returned but before
        // GetPage could read the result.  The codeunit results are already
        // committed to the Test Method Line table.  Continue so the next
        // iteration reconnects and advances to the next codeunit.
        if (string.IsNullOrEmpty(testResultJson))
        {
            Console.Error.WriteLine("  TestResultJson empty (connection dropped after run) — continuing");
            continue;
        }

        try
        {
            var parsed = JObject.Parse(testResultJson);
            allResults.Add(parsed);
            ProcessResult(parsed, ref totalPassed, ref totalFailed, ref totalSkipped);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Could not parse result JSON: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
        }
    }

    var elapsed = DateTime.UtcNow - startTime;
    Console.WriteLine($"\n=== Test Results ({elapsed.TotalSeconds:F0}s) ===");
    foreach (var r in allResults)
    {
        var cu = r["codeUnit"]?.Value<int>() ?? 0;
        var name = r["name"]?.ToString() ?? "";
        var trs = r["testResults"] as JArray;
        if (trs != null)
            foreach (var tr in trs)
            {
                var method = tr["method"]?.ToString() ?? "";
                var res = tr["result"]?.Value<int>() ?? 0;
                var status = res == 2 ? "PASS" : res == 1 ? "FAIL" : "SKIP";
                var msg = tr["message"]?.ToString() ?? "";
                Console.Write($"  {status}  {cu} {name}::{method}");
                if (res == 1 && msg.Length > 0) Console.Write($" — {msg[..Math.Min(120, msg.Length)]}");
                Console.WriteLine();
            }
        else
        {
            var res = r["result"]?.Value<int>() ?? 0;
            Console.WriteLine($"  {(res == 2 ? "PASS" : res == 1 ? "FAIL" : "SKIP")}  {cu} {name}");
        }
    }
    int total = totalPassed + totalFailed + totalSkipped;
    Console.WriteLine($"\nResults: {total} total, {totalPassed} passed, {totalFailed} failed, {totalSkipped} skipped");

    return totalFailed > 0 ? 1 : (totalPassed > 0 ? 0 : 1);
}

void ProcessResult(JObject r, ref int passed, ref int failed, ref int skipped)
{
    var cu = r["codeUnit"]?.Value<int>() ?? 0;
    var name = r["name"]?.ToString() ?? "";
    var trs = r["testResults"] as JArray;
    Console.Write($"  Codeunit {cu} {name} ");
    if (trs != null && trs.Count > 0)
    {
        int p = 0, f = 0, s = 0;
        foreach (var tr in trs) { var res = tr["result"]?.Value<int>() ?? 0; if (res == 2) { p++; passed++; } else if (res == 1) { f++; failed++; } else { s++; skipped++; } }
        Console.WriteLine($"({p} passed, {f} failed, {s} skipped)");
    }
    else { var res = r["result"]?.Value<int>() ?? 0; if (res == 2) { passed++; Console.WriteLine("PASS"); } else if (res == 1) { failed++; Console.WriteLine("FAIL"); } else { skipped++; Console.WriteLine("SKIP"); } }
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
    rpc.AddLocalRpcTarget(new Callbacks(sessionEndedCts));
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

    public Callbacks(CancellationTokenSource sessionEndedCts) => _sessionEndedCts = sessionEndedCts;

    // BC sends ClearClientMetadataCache (and sometimes OnSessionTerminating) right before
    // killing the WebSocket session during test isolation.  We log receipt for diagnostics.
    // NOTE: BC also sends ClearClientMetadataCache for other reasons (e.g. app publish),
    // so receiving it does NOT reliably mean the session is ending.
    [JsonRpcMethod("ClearClientMetadataCache")] public Task ClearClientMetadataCache() { Console.Error.WriteLine("  [notification] ClearClientMetadataCache received"); _sessionEndedCts.Cancel(); return Task.CompletedTask; }
    [JsonRpcMethod("OnSessionTerminating")]     public Task OnSessionTerminating()      { Console.Error.WriteLine("  [notification] OnSessionTerminating received"); _sessionEndedCts.Cancel(); return Task.CompletedTask; }

    [JsonRpcMethod("Confirm")] public Task Confirm(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("ProcessServerRequests")] public Task ProcessServerRequests(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("FormRunModal")] public Task FormRunModal(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("FormClose")] public Task FormClose(JToken r) => Task.CompletedTask;
    [JsonRpcMethod("FormActivate")] public Task FormActivate(JToken r) => Task.CompletedTask;
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
