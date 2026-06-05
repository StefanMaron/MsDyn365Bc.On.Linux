// Stub for Microsoft.AspNetCore.Server.HttpSys — redirects to Kestrel on Linux.
// BC calls: builder.UseHttpSys(configure) then builder.UseUrls("http://+:7048/BC/api")
// We redirect to Kestrel and use an IStartupFilter to strip URL paths at app startup.
//
// Windows HttpSys supports multiple services sharing a port via URL prefix routing
// (e.g. both /BC/OData and /BC/api on port 7048). Kestrel cannot; SharedPortRouter
// replicates this by assigning the first service its public port (primary) and giving
// subsequent services on the same port internal ports (50100+). The primary's pipeline
// forwards requests whose path belongs to a secondary service, mirroring HttpSys behaviour.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    public class HttpSysOptions
    {
        public bool AllowSynchronousIO { get; set; }
        public long? MaxConnections { get; set; }
        public int MaxAccepts { get; set; }
        public long? MaxRequestBodySize { get; set; }
        public AuthenticationManager Authentication { get; } = new AuthenticationManager();
        public TimeoutManager Timeouts { get; } = new TimeoutManager();
        public bool ThrowWriteExceptions { get; set; }
        public int RequestQueueLimit { get; set; }
        public string? RequestQueueName { get; set; }
        public bool UnsafePreferInlineScheduling { get; set; }
    }

    public class AuthenticationManager
    {
        public bool AllowAnonymous { get; set; } = true;
        public AuthenticationSchemes Schemes { get; set; } = AuthenticationSchemes.None;
        public bool AutomaticAuthentication { get; set; }
    }

    public class TimeoutManager
    {
        public TimeSpan IdleConnection { get; set; }
        public TimeSpan EntityBody { get; set; }
        public TimeSpan DrainEntityBody { get; set; }
        public TimeSpan RequestQueue { get; set; }
        public TimeSpan HeaderWait { get; set; }
        public long MinSendBytesPerSecond { get; set; }
    }

    [Flags]
    public enum AuthenticationSchemes
    {
        None = 0, Basic = 1, Anonymous = 2, NTLM = 4, Negotiate = 8, Kerberos = 16,
    }
}

namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHostBuilderHttpSysExtensions
    {
        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder,
            Action<Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions> configure)
        {
            var opts = new Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions();
            configure?.Invoke(opts);

            builder.UseKestrel(k =>
            {
                k.AllowSynchronousIO = opts.AllowSynchronousIO;
                if (opts.MaxRequestBodySize.HasValue)
                    k.Limits.MaxRequestBodySize = opts.MaxRequestBodySize;
            });

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new UrlStrippingStartupFilter());
            });

            return builder;
        }

        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder)
        {
            return builder.UseKestrel();
        }
    }

    /// <summary>
    /// Replicates Windows HttpSys URL-prefix routing for Kestrel on Linux.
    /// The first service to register a public port becomes primary and listens on that port.
    /// Subsequent services on the same port get internal ports (50100+).
    /// The primary's pipeline forwards requests to the matching secondary based on path prefix.
    /// </summary>
    internal static class SharedPortRouter
    {
        // publicPort → bag of (pathPrefix, listenPort) for all services on that port
        private static readonly ConcurrentDictionary<int, ConcurrentBag<(string prefix, int port)>> _routes = new();
        // publicPort → listenPort of the primary service (always == publicPort)
        private static readonly ConcurrentDictionary<int, int> _primaryPorts = new();
        private static int _nextInternalPort = 50100;

        private static readonly HttpClient _client = new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
        }) { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>
        /// Register a service for <paramref name="publicPort"/> with the given path prefix.
        /// Returns the port the service should actually listen on and whether it is primary.
        /// </summary>
        public static (int listenPort, bool isPrimary) Register(int publicPort, string pathPrefix)
        {
            bool isPrimary = _primaryPorts.TryAdd(publicPort, publicPort);
            int listenPort = isPrimary ? publicPort : Interlocked.Increment(ref _nextInternalPort);
            _routes.GetOrAdd(publicPort, _ => new ConcurrentBag<(string, int)>())
                   .Add((pathPrefix.TrimEnd('/'), listenPort));
            return (listenPort, isPrimary);
        }

        /// <summary>
        /// Find the secondary service whose path prefix best matches <paramref name="requestPath"/>
        /// on <paramref name="publicPort"/>. Returns null if the path belongs to the primary.
        /// </summary>
        public static (string prefix, int port)? FindSecondaryRoute(int publicPort, string requestPath)
        {
            if (!_routes.TryGetValue(publicPort, out var routes)) return null;
            if (!_primaryPorts.TryGetValue(publicPort, out int primaryPort)) return null;

            (string prefix, int port)? best = null;
            foreach (var route in routes)
            {
                if (route.port == primaryPort) continue; // primary handles its own traffic

                var p = route.prefix;
                bool matches = string.Equals(requestPath, p, StringComparison.OrdinalIgnoreCase)
                    || requestPath.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase);

                if (matches && (best == null || p.Length > best.Value.prefix.Length))
                    best = route;
            }
            return best;
        }

        /// <summary>Returns the listen port of the first registered route whose prefix starts with <paramref name="pathPrefix"/>.</summary>
        public static int FindListenPort(int publicPort, string pathPrefix)
        {
            if (!_routes.TryGetValue(publicPort, out var routes)) return -1;
            foreach (var route in routes)
                if (route.prefix.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                    return route.port;
            return -1;
        }

        /// <summary>
        /// Forward the current request to <paramref name="targetPort"/>, copying the response back.
        /// </summary>
        public static async Task ForwardAsync(HttpContext context, int targetPort)
        {
            var path = context.Request.Path.Value ?? "/";
            var qs = context.Request.QueryString.Value ?? "";
            var targetUri = $"http://localhost:{targetPort}{path}{qs}";

            using var req = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

            if (context.Request.ContentLength > 0
                || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                req.Content = new StreamContent(context.Request.Body);
                if (context.Request.Headers.TryGetValue("Content-Type", out var ct))
                    req.Content.Headers.TryAddWithoutValidation("Content-Type", ct.ToArray());
            }

            foreach (var (key, values) in context.Request.Headers)
            {
                // Content-Type was already set on req.Content.Headers above; skip here
                // to avoid adding it twice (which produces "application/json, application/json").
                if (req.Content != null
                    && key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!req.Headers.TryAddWithoutValidation(key, values.ToArray()))
                    req.Content?.Headers.TryAddWithoutValidation(key, values.ToArray());
            }

            HttpResponseMessage resp;
            try
            {
                resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HttpSysStub] Forward to :{targetPort}{path} failed: {ex.Message}");
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = 502;
                return;
            }

            using (resp)
            {
                context.Response.StatusCode = (int)resp.StatusCode;
                foreach (var (key, values) in resp.Headers)
                {
                    if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                        continue;
                    context.Response.Headers[key] = values.ToArray();
                }
                foreach (var (key, values) in resp.Content.Headers)
                    context.Response.Headers[key] = values.ToArray();

                await resp.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        }
    }

    /// <summary>
    /// Startup filter that:
    /// 1. Registers the service with SharedPortRouter (assigning primary or internal port)
    /// 2. For primary services: adds forwarding middleware for secondary path prefixes
    /// 3. Adds UsePathBase() so BC's middleware routes correctly within the service
    /// 4. Sets admin identity on every request (bypasses auth for all endpoints)
    /// </summary>
    internal class UrlStrippingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                var addressFeature = app.ServerFeatures.Get<IServerAddressesFeature>();
                string? pathBase = null;
                int publicPort = -1;
                bool isPrimary = false;

                if (addressFeature != null && addressFeature.Addresses.Count > 0)
                {
                    var original = addressFeature.Addresses.ToList();
                    addressFeature.Addresses.Clear();
                    foreach (var addr in original)
                    {
                        var (port, path, scheme, host) = ParseUrl(addr);
                        if (port < 0) { addressFeature.Addresses.Add(addr); continue; }

                        publicPort = port;
                        pathBase = path;
                        var (listenPort, primary) = SharedPortRouter.Register(port, path ?? "");
                        isPrimary = primary;

                        addressFeature.Addresses.Add($"{scheme}://{host}:{listenPort}");
                        Console.WriteLine($"[HttpSysStub] {addr} → {scheme}://{host}:{listenPort}");
                    }
                }

                // Primary: forward requests whose path belongs to a secondary service
                if (isPrimary && publicPort > 0)
                {
                    int capturedPort = publicPort;
                    app.Use(async (ctx, nextMw) =>
                    {
                        var route = SharedPortRouter.FindSecondaryRoute(
                            capturedPort, ctx.Request.Path.Value ?? "/");
                        if (route.HasValue)
                        {
                            await SharedPortRouter.ForwardAsync(ctx, route.Value.port);
                            return;
                        }
                        await nextMw();
                    });
                }

                if (!string.IsNullOrEmpty(pathBase))
                    app.UsePathBase(pathBase);

                // WebClient shim: intercept /BC/SignIn, /BC/csrf, /BC/csh WebSocket, etc.
                // Only activate when this is the primary service for a WebClient path (ends in /client).
                if (isPrimary && pathBase?.EndsWith("/client", StringComparison.OrdinalIgnoreCase) == true)
                {
                    int capturedPubPort = publicPort;
                    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
                    WebClientShim.Configure(app, capturedPubPort);
                }

                // Set authenticated admin identity on every request
                app.Use(async (context, nextMiddleware) =>
                {
                    var identity = new ClaimsIdentity(new[] {
                        new Claim(ClaimTypes.Name, "admin"),
                        new Claim(ClaimTypes.Role, "SUPER"),
                        new Claim(ClaimTypes.Role, "AdminService"),
                        new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000001"),
                    }, "Passthrough");
                    context.User = new ClaimsPrincipal(identity);
                    await nextMiddleware();
                });

                next(app);
            };
        }

        private static (int port, string? path, string scheme, string host) ParseUrl(string url)
        {
            try
            {
                var parsed = url.Replace("://+:", "://localhost:").Replace("://*:", "://localhost:");
                var uri = new Uri(parsed);
                var scheme = url[..url.IndexOf("://")];
                var host = url.Contains("://+:") ? "+" : url.Contains("://*:") ? "*" : uri.Host;
                var path = uri.AbsolutePath.TrimEnd('/');
                return (uri.Port, string.IsNullOrEmpty(path) || path == "/" ? null : path, scheme, host);
            }
            catch
            {
                return (-1, null, "http", "localhost");
            }
        }
    }

    // ── WebClient shim ──────────────────────────────────────────────────────────
    // Serves the HTTP auth flow and WebSocket endpoint that al-bc-sdk (BcClient)
    // expects, then translates every OpenSession/Invoke interaction into the BC
    // Client Services JSON-RPC protocol (ws://{host}/ws/connect).

    internal static class WebClientShim
    {
        public static void Configure(IApplicationBuilder app, int publicPort)
        {
            app.Use(async (ctx, next) =>
            {
                var path = ctx.Request.Path.Value ?? "";
                var method = ctx.Request.Method;

                if (method == "GET" && path.EndsWith("/SignIn", StringComparison.OrdinalIgnoreCase))
                { await ServeSignIn(ctx); return; }

                if (method == "POST" && path.EndsWith("/SignIn", StringComparison.OrdinalIgnoreCase))
                { ctx.Response.StatusCode = 302; ctx.Response.Headers["Location"] = "/BC"; return; }

                if (method == "POST" && path.EndsWith("/csrf", StringComparison.OrdinalIgnoreCase))
                { ctx.Response.ContentType = "application/json";
                  await ctx.Response.WriteAsync("{\"csrfToken\":\"shim-csrf\"}"); return; }

                if (method == "GET" && path.Contains("/boot/", StringComparison.OrdinalIgnoreCase))
                { ctx.Response.ContentType = "application/javascript";
                  await ctx.Response.WriteAsync("var shimConfig={\"DefaultApplicationId\":\"NAV\"};"); return; }

                if (method == "POST" && path.Contains("/csh/negotiate", StringComparison.OrdinalIgnoreCase))
                { ctx.Response.StatusCode = 404; return; }

                if (ctx.WebSockets.IsWebSocketRequest &&
                    path.Contains("/csh", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains("/negotiate", StringComparison.OrdinalIgnoreCase))
                { await HandleCsh(ctx, publicPort); return; }

                await next();
            });
        }

        private static async Task ServeSignIn(HttpContext ctx)
        {
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync(
                "<html><body><form method=\"post\">" +
                "<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"shim-rvt\"/>" +
                "<input name=\"UserName\"/><input name=\"Password\" type=\"password\"/>" +
                "<button type=\"submit\">Sign in</button></form></body></html>");
        }

        private static async Task HandleCsh(HttpContext ctx, int publicPort)
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            int csPort = SharedPortRouter.FindListenPort(publicPort, "/ws");
            if (csPort <= 0) csPort = publicPort;
            Console.WriteLine($"[WebClientShim] WS session start, csPort={csPort}");
            await using var session = new WebClientSession(ws, csPort);
            await session.RunAsync(ctx.RequestAborted);
        }
    }

    internal sealed class WebClientSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        private readonly int _csPort;
        private CsClient? _cs;

        // Lazily-built state from incoming WebClient messages
        private int _openPageId;
        private string _suiteName = "DEFAULT";
        private string _extensionId = "";
        private bool _testsRun;
        private readonly Queue<string> _resultQueue = new();

        // Headless WebClient test flows drive pages 130451 (populate) and
        // 130455 (run). The shim keeps those synthetic control trees, but uses
        // the extension API for modal-free suite population and opens Microsoft's
        // Command Line Test Tool page for the real Client Services test session.
        private const int CsRunPageId = 130455;
        private string _company = "CRONUS International Ltd.";
        private string _formServerId = "f0";
        private bool _csReady;
        private int _seqNo;

        public WebClientSession(WebSocket ws, int csPort) { _ws = ws; _csPort = csPort; }

        public async Task RunAsync(CancellationToken ct)
        {
            var buf = new byte[256 * 1024];
            using var ms = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await _ws.ReceiveAsync(buf, ct);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);

                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    var response = await HandleMessageAsync(json, ct);
                    if (response != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(response);
                        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[WebClientSession] {ex.Message}"); }
        }

        private async Task<string?> HandleMessageAsync(string json, CancellationToken ct)
        {
            try
            {
                var node = JsonNode.Parse(json);
                if (node == null) return null;
                var id = node["id"]?.GetValue<string>() ?? "0";
                var rpcMethod = node["method"]?.GetValue<string>() ?? "";
                var p = node["params"]?[0];
                return rpcMethod switch
                {
                    "OpenSession" => await HandleOpenSessionAsync(id, p, ct),
                    "Invoke" => await HandleInvokeAsync(id, p, ct),
                    _ => MakeResult(id, new JsonArray())
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebClientSession] HandleMessage error: {ex.Message}");
                return null;
            }
        }

        private Task<string> HandleOpenSessionAsync(string id, JsonNode? p, CancellationToken ct)
        {
            _company = p?["company"]?.GetValue<string>() ?? _company;
            _csReady = false;

            var interactions = p?["interactionsToInvoke"] as JsonArray;
            string? callbackId = null;
            if (interactions != null)
            {
                foreach (var ia in interactions)
                {
                    if (ia?["interactionName"]?.GetValue<string>() != "OpenForm") continue;
                    callbackId = ia?["callbackId"]?.GetValue<string>();
                    var npStr = ia?["namedParameters"]?.GetValue<string>() ?? "{}";
                    try
                    {
                        var np = JsonNode.Parse(npStr);
                        var q = np?["query"]?.GetValue<string>() ?? "";
                        var m = System.Text.RegularExpressions.Regex.Match(q, @"page=(\d+)");
                        if (m.Success) _openPageId = int.Parse(m.Groups[1].Value);
                    }
                    catch { }
                    break;
                }
            }

            _formServerId = $"f{_openPageId}";
            var handlers = BuildOpenSessionHandlers(callbackId);
            return Task.FromResult(MakeResult(id, handlers));
        }

        private async Task<string> HandleInvokeAsync(string id, JsonNode? p, CancellationToken ct)
        {
            var interactions = p?["interactionsToInvoke"] as JsonArray;
            string? lastCallbackId = null;
            string testResultJson = "";
            bool openFormSeen = false;

            if (interactions != null)
            {
                foreach (var ia in interactions)
                {
                    var name = ia?["interactionName"]?.GetValue<string>() ?? "";
                    var controlPath = ia?["controlPath"]?.GetValue<string>() ?? "";
                    var callbackId = ia?["callbackId"]?.GetValue<string>();
                    if (callbackId != null) lastCallbackId = callbackId;

                    switch (name)
                    {
                        case "OpenForm":
                            try
                            {
                                var npStr = ia?["namedParameters"]?.GetValue<string>() ?? "{}";
                                var np = JsonNode.Parse(npStr);
                                var q = np?["query"]?.GetValue<string>() ?? "";
                                var m = System.Text.RegularExpressions.Regex.Match(q, @"page=(\d+)");
                                if (m.Success) _openPageId = int.Parse(m.Groups[1].Value);
                                _formServerId = $"f{_openPageId}";
                                _csReady = false;
                                openFormSeen = true;
                            }
                            catch { }
                            break;

                        case "SaveValue":
                            try
                            {
                                var npStr = ia?["namedParameters"]?.GetValue<string>() ?? "{}";
                                var np = JsonNode.Parse(npStr);
                                var v = np?["newValue"];
                                var val = v?.GetValueKind() == JsonValueKind.String
                                    ? v.GetValue<string>()
                                    : v?.ToJsonString() ?? "";
                                var stripped = StripServerPrefix(controlPath);
                                Console.WriteLine($"[shimdbg] SaveValue alPage={_openPageId} path={controlPath} stripped={stripped} val={Trunc(val, 80)}");
                                if (stripped == "c[0]") _suiteName = val;
                                else if (stripped == "c[1]") _extensionId = val;
                            }
                            catch { }
                            break;

                        case "InvokeAction":
                        {
                            var actionName = ResolveAction(controlPath, _openPageId);
                            Console.WriteLine($"[shimdbg] InvokeAction alPage={_openPageId} path={controlPath} -> logical={actionName ?? "(null)"} suite={_suiteName} ext={_extensionId}");
                            if (actionName != null)
                            {
                                try
                                {
                                    // Translate Microsoft-page actions into modal-free runner calls.
                                    switch (actionName)
                                    {
                                        // Suite population: GetTestCodeunits opens a modal on page
                                        // 130451. Use the extension API instead.
                                        case "GetTestCodeunits":
                                        case "GetTestCodeunitsForSuite":
                                            if (!await SetupSuiteByExtensionApiAsync(ct))
                                                Console.WriteLine("[shimdbg] SetupSuiteByExtension API did not populate a suite");
                                            break;

                                        // WebClient test runners call RunNextTest in a loop, expecting
                                        // one codeunit's result per call. BC test isolation kills the CS
                                        // session on every codeunit run, so run all codeunits once, read
                                        // the result set, and dispense one result per subsequent call.
                                        case "RunNextTest":
                                        {
                                            if (!_testsRun) { await RunAllAndCollectAsync(ct); _testsRun = true; }
                                            testResultJson = _resultQueue.Count > 0
                                                ? _resultQueue.Dequeue()
                                                : "All tests executed.";
                                            Console.WriteLine($"[shimdbg] RunNextTest dispense (remaining {_resultQueue.Count}) -> [{testResultJson.Length}]: {Trunc(testResultJson, 400)}");
                                            break;
                                        }

                                        // DeleteLines / ClearTestResults are implicit in LoadExtension
                                        // (it clears and repopulates a fresh suite), so they're no-ops.
                                        case "DeleteLines":
                                        case "ClearTestResults":
                                            break;

                                        default:
                                            await InvokeCsAsync(actionName, null, ct);
                                            break;
                                    }
                                }
                                catch (Exception ex) { Console.WriteLine($"[WebClientSession] {actionName} error: {ex.Message}"); }
                            }
                            break;
                        }

                        case "CloseForm":
                            if (_cs != null) { try { await _cs.DisposeAsync(); } catch { } _cs = null; }
                            _csReady = false;
                            break;
                    }
                }
            }

            var handlers = BuildInvokeHandlers(lastCallbackId, testResultJson, openFormSeen);
            return MakeResult(id, handlers);
        }

        private async Task EnsureCsReadyAsync(CancellationToken ct)
        {
            if (_csReady) return;
            if (_cs == null) _cs = new CsClient();
            await _cs.ConnectAsync(_csPort, ct);
            await _cs.OpenConnectionAsync(ct);
            await _cs.OpenCompanyAsync(_company, ct);
            // Always open the Microsoft Command Line Test Tool for the real CS
            // session. Page 99905 is useful for the standalone bc-linux runner, but
            // BC 28.1 returns a null form state for that custom Card over Client
            // Services. Page 130455 is the path already exercised by run-tests.sh.
            await _cs.OpenFormAsync(CsRunPageId, GetTableId(CsRunPageId), _suiteName, ct);
            if (_cs.FormState == null)
                throw new InvalidOperationException(
                    $"Page {CsRunPageId} returned no form state for suite '{_suiteName}'");
            _csReady = true;
            var fs = _cs.FormState?.ToJsonString() ?? "(null)";
            Console.WriteLine($"[shimdbg] EnsureCsReady alPage={_openPageId} csPage={CsRunPageId} suite={_suiteName} ext={_extensionId} formState[{fs.Length}]: {Trunc(fs, 800)}");
        }

        private static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

        /// <summary>
        /// Invokes a method on the CS WS Test Runner page, ensuring the session is
        /// ready and reconnecting once if it died (BC tears the session down between
        /// test-isolation runs).
        /// </summary>
        private async Task<string> InvokeCsAsync(string methodName, JsonArray? args, CancellationToken ct)
        {
            await EnsureCsReadyAsync(ct);
            string csResult;
            try { csResult = await _cs!.InvokeMethodAsync(methodName, args, ct); }
            catch
            {
                if (_cs != null) { try { await _cs.DisposeAsync(); } catch { } _cs = null; }
                _csReady = false;
                await EnsureCsReadyAsync(ct);
                csResult = await _cs!.InvokeMethodAsync(methodName, args, ct);
            }
            Console.WriteLine($"[shimdbg] CS {methodName}({args?.ToJsonString() ?? ""}) result[{csResult.Length}]: {Trunc(csResult, 400)}");
            return csResult;
        }

        /// <summary>
        /// Runs every pending codeunit in the suite, then reads the full result set.
        /// BC test isolation drops the CS session after each codeunit run, so we
        /// reconnect before each RunNextTest and treat a thrown invoke as "a
        /// codeunit ran". Results are read afterwards via the TestRunnerExtension
        /// API page and queued for the WebClient caller.
        /// </summary>
        private async Task RunAllAndCollectAsync(CancellationToken ct)
        {
            await RunExternalTestRunnerAsync(ct);
            var objects = await FetchResultObjectsFromApiAsync(ct);
            foreach (var o in objects) _resultQueue.Enqueue(o);
            Console.WriteLine($"[shimdbg] collected {_resultQueue.Count} codeunit result(s)");
        }

        private async Task RunExternalTestRunnerAsync(CancellationToken ct)
        {
            var runnerDll = "/bc/tools/TestRunner/TestRunner.dll";
            if (!File.Exists(runnerDll))
                throw new FileNotFoundException("Bundled TestRunner.dll not found", runnerDll);
            var codeunitCount = await CountCodeunitsInSuiteAsync(ct);

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(runnerDll);
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add("localhost:7085");
            psi.ArgumentList.Add("--odata-host");
            psi.ArgumentList.Add("localhost:7048");
            psi.ArgumentList.Add("--company");
            psi.ArgumentList.Add(_company);
            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add("BCRUNNER");
            psi.ArgumentList.Add("--password");
            psi.ArgumentList.Add("Admin123!");
            psi.ArgumentList.Add("--suite");
            psi.ArgumentList.Add(_suiteName);
            psi.ArgumentList.Add("--num-codeunits");
            psi.ArgumentList.Add(Math.Max(codeunitCount, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--timeout");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("--codeunit-timeout");
            psi.ArgumentList.Add("10");
            psi.ArgumentList.Add("--max-iterations");
            psi.ArgumentList.Add("320");

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start TestRunner.dll");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Console.WriteLine($"[shimdbg] TestRunner exit={proc.ExitCode} stdout={Trunc(stdout, 500)} stderr={Trunc(stderr, 500)}");
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"TestRunner.dll failed with exit code {proc.ExitCode}");
        }

        private async Task<int> CountCodeunitsInSuiteAsync(CancellationToken ct)
        {
            try
            {
                using var http = NewBcHttpClient();
                var apiBase = await GetAutomationApiBaseAsync(http, ct);
                var suite = Uri.EscapeDataString(_suiteName);
                using var resp = await http.GetAsync($"{apiBase}/testResults?$filter=testSuite%20eq%20%27{suite}%27&$top=10000", ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return 0;
                var rows = JsonNode.Parse(text)?["value"]?.AsArray();
                if (rows == null) return 0;
                return rows.Count(row =>
                    string.Equals(row?["lineType"]?.GetValue<string>(), "Codeunit", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[shimdbg] CountCodeunitsInSuite error: {Trunc(ex.Message, 160)}");
                return 0;
            }
        }

        private async Task<bool> SetupSuiteByExtensionApiAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_extensionId)) return false;

            try
            {
                using var http = NewBcHttpClient();
                var apiBase = await GetAutomationApiBaseAsync(http, ct);
                var createBody = new JsonObject { ["CodeunitIds"] = _extensionId }.ToJsonString();
                using var createResp = await http.PostAsync(
                    $"{apiBase}/codeunitRunRequests",
                    new StringContent(createBody, Encoding.UTF8, "application/json"),
                    ct);
                var createText = await createResp.Content.ReadAsStringAsync(ct);
                if (!createResp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[shimdbg] create codeunitRunRequest failed: {(int)createResp.StatusCode} {Trunc(createText, 300)}");
                    return false;
                }

                var requestId = JsonNode.Parse(createText)?["Id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(requestId)) return false;

                using var setupResp = await http.PostAsync(
                    $"{apiBase}/codeunitRunRequests({requestId})/Microsoft.NAV.setupSuiteByExtension",
                    new StringContent("", Encoding.UTF8, "application/json"),
                    ct);
                var setupText = await setupResp.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[shimdbg] SetupSuiteByExtension ext={_extensionId} -> {(int)setupResp.StatusCode} {Trunc(setupText, 200)}");
                return setupResp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[shimdbg] SetupSuiteByExtension API error: {Trunc(ex.Message, 200)}");
                return false;
            }
        }

        private async Task<List<string>> FetchResultObjectsFromApiAsync(CancellationToken ct)
        {
            var byCodeunit = new SortedDictionary<int, (string Name, JsonArray Methods)>();

            try
            {
                using var http = NewBcHttpClient();
                var apiBase = await GetAutomationApiBaseAsync(http, ct);
                var suite = Uri.EscapeDataString(_suiteName);
                var url = $"{apiBase}/testResults?$filter=testSuite%20eq%20%27{suite}%27&$top=10000";
                using var resp = await http.GetAsync(url, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[shimdbg] fetch testResults failed: {(int)resp.StatusCode} {Trunc(text, 300)}");
                    return new List<string>();
                }

                var rows = JsonNode.Parse(text)?["value"]?.AsArray();
                if (rows == null) return new List<string>();

                foreach (var row in rows)
                {
                    if (row == null) continue;
                    var codeunit = row["testCodeunit"]?.GetValue<int>() ?? 0;
                    if (codeunit == 0) continue;

                    var lineType = row["lineType"]?.GetValue<string>() ?? "";
                    if (!byCodeunit.ContainsKey(codeunit))
                        byCodeunit[codeunit] = ($"Codeunit {codeunit}", new JsonArray());

                    if (lineType.Equals("Codeunit", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = row["name"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrWhiteSpace(name))
                            byCodeunit[codeunit] = (name, byCodeunit[codeunit].Methods);
                        continue;
                    }

                    if (!lineType.Equals("Function", StringComparison.OrdinalIgnoreCase)) continue;

                    var resultText = row["result"]?.GetValue<string>() ?? "";
                    var result = resultText.Equals("Failure", StringComparison.OrdinalIgnoreCase) ? 1
                        : resultText.Equals("Success", StringComparison.OrdinalIgnoreCase) ? 2
                        : 0;
                    byCodeunit[codeunit].Methods.Add(new JsonObject
                    {
                        ["method"] = row["functionName"]?.GetValue<string>() ?? row["name"]?.GetValue<string>() ?? "",
                        ["result"] = result,
                        ["message"] = row["errorMessagePreview"]?.GetValue<string>() ?? row["errorMessage"]?.GetValue<string>() ?? "",
                        ["stackTrace"] = row["errorCallStack"]?.GetValue<string>() ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[shimdbg] FetchResultObjectsFromApi error: {Trunc(ex.Message, 200)}");
            }

            var objects = new List<string>();
            foreach (var (codeunit, value) in byCodeunit)
            {
                if (value.Methods.Count == 0) continue;
                var obj = new JsonObject
                {
                    ["codeUnit"] = codeunit,
                    ["name"] = value.Name,
                    ["testResults"] = value.Methods
                };
                objects.Add(obj.ToJsonString());
            }
            return objects;
        }

        private static HttpClient NewBcHttpClient()
        {
            var http = new HttpClient { BaseAddress = new Uri("http://localhost:7048/BC/") };
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("BCRUNNER:Admin123!")));
            return http;
        }

        private async Task<string> GetAutomationApiBaseAsync(HttpClient http, CancellationToken ct)
        {
            using var resp = await http.GetAsync("api/v2.0/companies", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            resp.EnsureSuccessStatusCode();

            var companies = JsonNode.Parse(text)?["value"]?.AsArray();
            var companyId = companies?
                .FirstOrDefault(c => string.Equals(
                    c?["name"]?.GetValue<string>(),
                    _company,
                    StringComparison.OrdinalIgnoreCase))?["id"]?.GetValue<string>()
                ?? companies?.FirstOrDefault()?["id"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(companyId))
                throw new InvalidOperationException("BC API returned no companies");

            return $"api/custom/automation/v1.0/companies({companyId})";
        }

        /// <summary>Forces a fresh CS session + page open (drops any dead session first).</summary>
        private async Task ReconnectCsAsync(CancellationToken ct)
        {
            if (_cs != null) { try { await _cs.DisposeAsync(); } catch { } _cs = null; }
            _csReady = false;
            await EnsureCsReadyAsync(ct);
        }

        /// <summary>Extracts every <c>{"codeUnit":…}</c> result object embedded in a response's NavDataSet buffers.</summary>
        private static List<string> ExtractAllResultObjects(string json)
        {
            var objs = new List<string>();
            foreach (var b64 in ExtractBufferStrings(json))
            {
                string text;
                try { text = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
                catch { continue; }
                int from = 0;
                while (true)
                {
                    int start = text.IndexOf("{\"codeUnit\"", from, StringComparison.OrdinalIgnoreCase);
                    if (start < 0) break;
                    var obj = ExtractEmbeddedResultJson(text.Substring(start));
                    if (string.IsNullOrEmpty(obj)) break;
                    objs.Add(obj);
                    from = start + obj.Length;
                }
                if (objs.Count > 0) break; // results all live in one buffer
            }
            return objs;
        }

        private async Task<string> ExtractTestResultJsonAsync(string csResultJson, CancellationToken ct)
        {
            // The freshest field values are in the InvokeApplicationMethod response
            // (post-run DataSetState); prefer it over a follow-up GetPage which can
            // re-render a stale/empty state.
            DumpBuffers("invokeResult", csResultJson);
            var fromInvoke = SearchTestResultJson(csResultJson);
            if (IsRealResult(fromInvoke)) return fromInvoke;
            try
            {
                var pageJson = await _cs!.GetPageAsync(ct);
                DumpBuffers("getPage", pageJson);
                var v = SearchTestResultJson(pageJson);
                if (IsRealResult(v)) return v;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }
            return fromInvoke;
        }

        private static bool IsRealResult(string s) =>
            !string.IsNullOrEmpty(s) && s.StartsWith("{", StringComparison.Ordinal);

        private static void DumpBuffers(string tag, string json)
        {
            int i = 0;
            foreach (var b64 in ExtractBufferStrings(json))
            {
                string t; try { t = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); } catch { continue; }
                // Strip non-printable bytes for readability.
                var clean = new string(t.Select(c => c < 32 && c != '\n' ? '.' : c).ToArray());
                int idx = clean.IndexOf("codeUnit", StringComparison.OrdinalIgnoreCase);
                int idx2 = clean.IndexOf("All tests", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"[shimdbg-buf] {tag}#{i} len={t.Length} codeUnit@{idx} AllTests@{idx2} :: {Trunc(clean, 300)}");
                i++;
            }
        }

        private static string SearchTestResultJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            // 1) Plain JSON property (in case a page ever exposes it directly).
            try
            {
                using var doc = JsonDocument.Parse(json);
                var direct = FindInElement(doc.RootElement, "TestResultJson")
                    ?? FindInElement(doc.RootElement, "TestResultsJSONText");
                if (!string.IsNullOrEmpty(direct)) return direct;
            }
            catch { }
            // 2) BC encodes page field values inside base64 "buffers" (NavDataSet
            // binary), where string values — including page 99905's TestResultJson —
            // appear as readable UTF-8. Decode each buffer and pull the JSON out.
            foreach (var b64 in ExtractBufferStrings(json))
            {
                string text;
                try { text = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
                catch { continue; }
                var embedded = ExtractEmbeddedResultJson(text);
                if (!string.IsNullOrEmpty(embedded)) return embedded;
                if (text.Contains("All tests executed.", StringComparison.Ordinal))
                    return "All tests executed.";
            }
            return "";
        }

        /// <summary>Collects every string element of every "buffers" array in the JSON.</summary>
        private static IEnumerable<string> ExtractBufferStrings(string json)
        {
            var result = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                CollectBuffers(doc.RootElement, result);
            }
            catch { }
            return result;
        }

        private static void CollectBuffers(JsonElement el, List<string> acc)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in el.EnumerateObject())
                {
                    if (p.NameEquals("buffers") && p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var item in p.Value.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String) acc.Add(item.GetString()!);
                    CollectBuffers(p.Value, acc);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
                foreach (var item in el.EnumerateArray()) CollectBuffers(item, acc);
        }

        /// <summary>
        /// Finds the page 99905 result object (<c>{"codeUnit":…,"testResults":[…]}</c>)
        /// embedded as a UTF-8 substring in a decoded NavDataSet buffer and returns it
        /// as a brace-balanced JSON string.
        /// </summary>
        private static string ExtractEmbeddedResultJson(string text)
        {
            int start = text.IndexOf("{\"codeUnit\"", StringComparison.Ordinal);
            if (start < 0) start = text.IndexOf("{\"codeunit\"", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            int depth = 0; bool inStr = false; bool esc = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                }
                else if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return text.Substring(start, i - start + 1); }
            }
            return "";
        }

        private static string? FindInElement(JsonElement el, string key)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var s = prop.GetString() ?? "";
                    if (s.StartsWith("{") || s.StartsWith("All")) return s;
                }
                foreach (var child in el.EnumerateObject())
                {
                    var found = FindInElement(child.Value, key);
                    if (found != null) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    var found = FindInElement(item, key);
                    if (found != null) return found;
                }
            }
            return null;
        }

        // ── Synthetic handler builders ─────────────────────────────────────────

        private JsonArray BuildOpenSessionHandlers(string? callbackId)
        {
            var arr = new JsonArray
            {
                new JsonObject
                {
                    ["handlerType"] = "DN.SessionInitHandler",
                    ["parameters"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["ServerSessionId"] = "shim-session",
                            ["SessionKey"] = "shim-key",
                            ["CompanyName"] = _company
                        }
                    }
                },
                new JsonObject
                {
                    ["handlerType"] = "DN.LogicalClientInitHandler",
                    ["parameters"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["sc"] = "StringControl", ["ac"] = "ActionControl",
                            ["gc"] = "GroupControl", ["rc"] = "RepeaterControl"
                        }
                    }
                }
            };

            var seqProps = new JsonObject { ["SequenceNumber"] = ++_seqNo };
            if (callbackId != null) seqProps["CallbackId"] = callbackId;
            arr.Add(new JsonObject { ["handlerType"] = "DN.CallbackResponseProperties", ["parameters"] = new JsonArray { seqProps } });
            arr.Add(BuildFormEventHandler());
            return arr;
        }

        private JsonObject BuildFormEventHandler()
        {
            return new JsonObject
            {
                ["handlerType"] = "DN.LogicalClientEventRaisingHandler",
                ["parameters"] = new JsonArray
                {
                    "FormToShow",
                    new JsonObject
                    {
                        ["ServerId"] = _formServerId,
                        ["Caption"] = _openPageId == 130455 ? "Command Line Test Tool"
                                    : _openPageId == 130451 ? "AL Test Tool"
                                    : $"Page {_openPageId}",
                        ["Children"] = GetControlTree(_openPageId)
                    }
                }
            };
        }

        private static JsonArray GetControlTree(int pageId)
        {
            if (pageId == 130455)
            {
                return new JsonArray
                {
                    Field("CurrentSuiteName"), Field("ExtensionId"), Field("CCTrackingType"),
                    Field("CodeCoverageExtensionId"), Field("TestResultJson"),
                    Field("CCResultsCSVText"), Field("CCInfo"),
                    Group("ShimActions",
                        Action("ClearTestResults"), Action("RunNextTest"),
                        Action("GetCodeCoverage"), Action("ClearCodeCoverage"))
                };
            }
            if (pageId == 130451)
            {
                return new JsonArray
                {
                    Field("CurrentSuiteName"), Field("ExtensionId"),
                    new JsonObject { ["t"] = "rc", ["DesignName"] = "Lines", ["Children"] = new JsonArray() },
                    Group("ShimActions2",
                        Action("DeleteLines"), Action("GetTestCodeunits"), Action("GetTestCodeunitsForSuite"))
                };
            }
            return new JsonArray();
        }

        private static JsonObject Field(string name) =>
            new JsonObject { ["t"] = "sc", ["DesignName"] = name, ["Caption"] = name, ["Children"] = new JsonArray() };

        private static JsonObject Action(string name) =>
            new JsonObject { ["t"] = "ac", ["DesignName"] = name, ["Caption"] = name, ["Children"] = new JsonArray() };

        private static JsonObject Group(string name, params JsonNode[] children)
        {
            var arr = new JsonArray();
            foreach (var c in children) arr.Add(c);
            return new JsonObject { ["t"] = "gc", ["DesignName"] = name, ["Caption"] = name, ["Children"] = arr };
        }

        private JsonArray BuildInvokeHandlers(string? callbackId, string testResultJson, bool includeFormToShow = false)
        {
            var seqProps = new JsonObject { ["SequenceNumber"] = ++_seqNo };
            if (callbackId != null) seqProps["CallbackId"] = callbackId;
            var arr = new JsonArray
            {
                new JsonObject { ["handlerType"] = "DN.CallbackResponseProperties", ["parameters"] = new JsonArray { seqProps } }
            };

            if (includeFormToShow)
                arr.Add(BuildFormEventHandler());

            if (!string.IsNullOrEmpty(testResultJson))
            {
                arr.Add(new JsonObject
                {
                    ["handlerType"] = "DN.LogicalClientChangeHandler",
                    ["parameters"] = new JsonArray
                    {
                        _formServerId,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["t"] = "PropertyChanges",
                                ["ControlReference"] = new JsonObject
                                {
                                    ["controlPath"] = $"server:c[4]",
                                    ["formId"] = _formServerId
                                },
                                ["Changes"] = new JsonObject { ["StringValue"] = testResultJson }
                            }
                        }
                    }
                });
            }
            return arr;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static string StripServerPrefix(string path)
        {
            const string p = "server:";
            return path.StartsWith(p, StringComparison.Ordinal) ? path[p.Length..] : path;
        }

        private static string? ResolveAction(string controlPath, int pageId)
        {
            var p = StripServerPrefix(controlPath);
            return (pageId, p) switch
            {
                (130455, "c[7]/c[0]") => "ClearTestResults",
                (130455, "c[7]/c[1]") => "RunNextTest",
                (130455, "c[7]/c[2]") => "GetCodeCoverage",
                (130455, "c[7]/c[3]") => "ClearCodeCoverage",
                (130451, "c[3]/c[0]") => "DeleteLines",
                (130451, "c[3]/c[1]") => "GetTestCodeunits",
                (130451, "c[3]/c[2]") => "GetTestCodeunitsForSuite",
                _ => null
            };
        }

        private static int GetTableId(int pageId) => pageId switch
        {
            130455 => 130450,  // Command Line Test Tool — SourceTable "Test Method Line" (130450)
            130451 => 130450,  // AL Test Tool — SourceTable "Test Method Line" (130450)
            99905 => 130451,   // WS Test Runner — SourceTable "AL Test Suite" (130451)
            _ => 0
        };

        private static string MakeResult(string id, JsonNode result) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

        public async ValueTask DisposeAsync()
        {
            if (_cs != null) { try { await _cs.DisposeAsync(); } catch { } _cs = null; }
        }
    }

    internal sealed class CsClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _readLoop;
        private int _nextId;

        public long MetadataToken { get; private set; }
        public JsonNode? FormState { get; private set; }

        public async Task ConnectAsync(int port, CancellationToken ct)
        {
            _ws.Options.SetRequestHeader("Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("BCRUNNER:Admin123!")));
            await _ws.ConnectAsync(new Uri($"ws://localhost:{port}/ws/connect"), ct);
            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        }

        public async Task OpenConnectionAsync(CancellationToken ct)
        {
            var param = JsonNode.Parse("[{\"LCID\":1033,\"DefaultLCID\":1033,\"TimeZoneId\":\"UTC\",\"Credentials\":{\"UserName\":\"BCRUNNER\",\"Password\":\"Admin123!\"}}]");
            var result = await CallAsync("OpenConnection", param, ct);
            var raw = result?.ToJsonString() ?? "";
            var i = raw.IndexOf("\"MetadataToken\":", StringComparison.Ordinal);
            if (i >= 0)
            {
                var s = i + "\"MetadataToken\":".Length;
                var e = raw.IndexOfAny(new[] { ',', '}' }, s);
                if (e < 0) e = raw.Length;
                if (long.TryParse(raw[s..e].Trim(), out var mt)) MetadataToken = mt;
            }
        }

        public async Task OpenCompanyAsync(string company, CancellationToken ct)
        {
            await CallAsync("OpenCompany",
                JsonNode.Parse($"[\"{company.Replace("\\", "\\\\").Replace("\"", "\\\"")}\", false]"), ct);
        }

        public async Task OpenFormAsync(int pageId, int tableId, string suiteName, CancellationToken ct)
        {
            var param = new JsonArray
            {
                new JsonObject
                {
                    ["HasMainForm"] = true,
                    ["States"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["FormId"] = pageId,
                            ["TableView"] = new JsonObject
                            {
                                ["TableId"] = tableId,
                                // Empty suiteName → no record filter (let the page pick/create
                                // its own record); otherwise filter to the named suite.
                                ["View"] = string.IsNullOrEmpty(suiteName)
                                    ? ""
                                    : $"WHERE(Name=CONST({suiteName}))"
                            }
                        }
                    },
                    ["ControlIds"] = new JsonArray { JsonValue.Create((string?)null) },
                    ["VersionNumber"] = JsonValue.Create(MetadataToken),
                    ["MainFormHandle"] = "00000000-0000-0000-0000-000000000000"
                }
            };
            JsonNode? result = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                result = await CallAsync("OpenForm", param.DeepClone(), ct);
                if (result?["States"]?[0] != null) break;
                // A freshly-reconnected session can briefly return an empty OpenForm
                // (the prior test-isolation session teardown isn't fully settled);
                // back off and retry.
                await Task.Delay(400, ct);
            }
            var rawResult = result?.ToJsonString() ?? "(null)";
            Console.WriteLine($"[shimdbg] OpenForm page={pageId} suiteFilter='{suiteName}' rawResult[{rawResult.Length}]: {(rawResult.Length <= 700 ? rawResult : rawResult.Substring(0, 700) + "…")}");
            FormState = result?["States"]?[0]?.DeepClone();
            if (FormState != null)
            {
                try
                {
                    var pg = await CallAsync("GetPage",
                        new JsonArray
                        {
                            new JsonObject { ["PageSize"] = 50, ["IncludeMoreDataInformation"] = true, ["IncludeNonRowData"] = true },
                            FormState.DeepClone()
                        }, ct);
                    if (pg?["State"] != null) FormState = pg["State"]!.DeepClone();
                }
                catch { }
            }
        }

        public Task<string> InvokeMethodAsync(string methodName, CancellationToken ct) =>
            InvokeMethodAsync(methodName, null, ct);

        public async Task<string> InvokeMethodAsync(string methodName, JsonArray? args, CancellationToken ct)
        {
            var call = new JsonObject
            {
                ["ApplicationCodeType"] = 1,
                ["ObjectId"] = 0,
                ["MethodName"] = methodName,
                ["DataSetState"] = FormState?.DeepClone()
            };
            // BC's InvokeApplicationMethod accepts an "Arguments" array for methods
            // that take parameters (e.g. WS Test Runner LoadExtension(ExtId: Text)).
            if (args != null) call["Arguments"] = args.DeepClone();
            var param = new JsonArray
            {
                call,
                FormState?.DeepClone() ?? JsonValue.Create<string>(null!)
            };
            var result = await CallAsync("InvokeApplicationMethod", param, ct);
            if (result?["DataSetState"] != null) FormState = result["DataSetState"]!.DeepClone();
            return result?.ToJsonString() ?? "";
        }

        public async Task<string> GetPageAsync(CancellationToken ct)
        {
            var param = new JsonArray
            {
                new JsonObject { ["PageSize"] = 1, ["IncludeMoreDataInformation"] = true, ["IncludeNonRowData"] = true },
                FormState?.DeepClone() ?? JsonValue.Create<string>(null!)
            };
            var result = await CallAsync("GetPage", param, ct);
            if (result?["State"] != null) FormState = result["State"]!.DeepClone();
            return result?.ToJsonString() ?? "";
        }

        /// <summary>Reads a JSON-RPC id that may be encoded as a string or a number.</summary>
        private static string? ReadId(JsonNode? idNode)
        {
            if (idNode == null) return null;
            return idNode.GetValueKind() == JsonValueKind.String
                ? idNode.GetValue<string>()
                : idNode.ToJsonString();
        }

        private async Task<JsonNode?> CallAsync(string method, JsonNode? param, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId).ToString();
            var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            using var reg = ct.Register(() => { _pending.TryRemove(id, out _); tcs.TrySetCanceled(); });
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["id"] = id, ["params"] = param }.ToJsonString(), ct);
            return await tcs.Task;
        }

        private async Task SendAsync(string msg, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _sendLock.WaitAsync(ct);
            try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
            finally { _sendLock.Release(); }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buf = new byte[256 * 1024];
            using var ms = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await _ws.ReceiveAsync(buf, ct);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    _ = Task.Run(() => ProcessMsgAsync(json, ct));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[CsClient] ReadLoop: {ex.Message}"); }
            finally
            {
                foreach (var kv in _pending) kv.Value.TrySetCanceled();
                _pending.Clear();
            }
        }

        private async Task ProcessMsgAsync(string json, CancellationToken ct)
        {
            try
            {
                var node = JsonNode.Parse(json);
                if (node == null) return;
                // BC sends the JSON-RPC id as either a string or a number; read it
                // type-tolerantly. Echo the original node verbatim so the type matches.
                var idNode = node["id"];
                var id = ReadId(idNode);
                var cbMethod = node["method"]?.GetValue<string>();

                if (cbMethod == null)
                {
                    // Response to our request
                    if (id != null && _pending.TryRemove(id, out var tcs))
                    {
                        var err = node["error"];
                        if (err != null) tcs.TrySetException(new InvalidOperationException(err.ToJsonString()));
                        else tcs.TrySetResult(node["result"]);
                    }
                    return;
                }

                Console.WriteLine($"[shimdbg-cs] server callback: {cbMethod} id={id}");
                // Server callback: respond with null result to unblock BC's ResultWaiter
                if (id != null)
                    await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode?.DeepClone(), ["result"] = JsonValue.Create((object?)null) }.ToJsonString(), CancellationToken.None);

                if (cbMethod == "OnSessionTerminating") { _cts.Cancel(); return; }

                // No EndClientCall for these
                if (cbMethod is "DisposeAutomationObject" or "InvokeAutomationMethod" or
                    "DataSetPageReady" or "OpenProgressDialog" or "CloseProgressDialog" or "UpdateProgressDialog")
                    return;

                // SelectionMenu: EndClientCall(1)
                if (cbMethod == "SelectionMenu")
                {
                    try { await CallAsync("EndClientCall", JsonNode.Parse("[1]"), CancellationToken.None); } catch { }
                    return;
                }

                // Default: EndClientCall({Result:null})
                try { await CallAsync("EndClientCall", JsonNode.Parse("[{\"Result\":null}]"), CancellationToken.None); } catch { }
            }
            catch (Exception ex) { Console.WriteLine($"[CsClient] ProcessMsg: {ex.Message}"); }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_readLoop != null) try { await _readLoop.ConfigureAwait(false); } catch { }
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
            _ws.Dispose();
            _sendLock.Dispose();
            _cts.Dispose();
        }
    }
}
