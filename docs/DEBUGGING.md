# AL Debugging against the Linux NST

**Status: attach mode AND launch mode (F5) WORKING** (verified 2026-06-12 on
BC 28.1, AL extension 17.0; both need the one-line VS Code-side extension.js
patch described below). The attach-debug cycle works
end-to-end against the Linux service tier: breakpoint set → bind
(`verified:true`) → hit → call stack → variables (Globals `Rec`/`xRec`, Locals) →
watch evaluation → step over → continue. Verified through the **web client**
(`BC_WEBCLIENT=1`): a breakpoint on a Customer List `OnOpenPage` trigger hits
when the page is opened in the browser, locals update correctly across a step,
and the browser session resumes cleanly on continue.

**Real VS Code on Linux additionally needs a one-line patch to the AL
extension's `extension.js`** (see "VS Code hangs on the call stack" below) —
without it, attach takes 15–30 s and the first breakpoint hit shows "Paused on
Step" with a call stack that never loads, even though the server side is fine.

## Recommended setup: attach + breakOnNext

```jsonc
{
  "name": "Attach to Linux BC",
  "type": "al",
  "request": "attach",
  "server": "http://localhost",
  "port": 7049,
  "serverInstance": "BC",
  "tenant": "default",
  "authentication": "UserPassword",      // BCRUNNER / Admin123!
  "environmentType": "OnPrem",
  "breakOnNext": "WebClient"             // or "WebServiceClient" / "Background"
}
```

Workflow: publish with Ctrl+F5 (or `Run without debugging`), start the attach
configuration, then sign in to the web client at `http://localhost:8080` (a
*fresh* sign-in creates the new session the debugger attaches to — an
already-open tab is an old session) and navigate to your page by clicking in
the UI. Breakpoints hit with full call stack and variables.

**The `server` value should be the NST host with `port` 7049 — NOT the web
client's `http://localhost:8080`.** The AL extension talks to the dev endpoint
(7049) for everything (publish, symbols, debugger hub). One subtlety verified
empirically: the **`port` property overrides any port embedded in the
`server` URL** — `"server": "http://localhost:8080"` plus `"port": 7049`
still sends dev requests to 7049, and VS Code's config resolver injects
`port: 7049` (the schema default) whenever launch.json omits it, so a stray
`:8080` in `server` is masked in practice. Hand-rolled DAP clients that omit
`port` get no such rescue: requests go to Kestrel, 404, and the session dies
with "Not Found". Use `http://localhost` + `7049` and don't rely on the
masking.

Nothing container-side needs enabling — the dev endpoint (7049) is already
published and the debugger runs over its SignalR `DebuggerHub`.

## Launch mode (F5): working with the extension.js patch

F5 (`"request": "launch"`) publishes the app, opens the browser at
`<webEndpoint>?page=N&...&debuggingcontext=<hub-connection-id>`, and relies on
the web client passing that `debuggingcontext` to the NST when creating the
session so the session binds to *your* VS Code debug session.
**User-verified working on 2026-06-12 after applying the extension.js cwd
patch** (see "VS Code hangs on the call stack" below). Before that patch the
flow was unreliable: the adapter's multi-second package-cache scan delayed
the debug session while the browser raced ahead, and attempts died in
`ConnectionEstablisher.OpenWebSocket → PromptForCredentials()` with
`NavCancelCredentialPromptException` (visible in `/tmp/webclient.log`),
landing on the role center unbound. If F5 regresses to that behavior after an
AL extension update, reapply the patch first.

Infrastructure that IS in place for launch mode:

- **The NST advertises the web client URL.** The entrypoint sets
  `PublicWebBaseUrl` to `http://localhost:8080/` when `BC_WEBCLIENT=1`
  (override with `BC_WEBCLIENT_PUBLIC_URL` for remapped ports). Without it the
  F5 browser URL is the port-less `http://localhost/?page=N` (lands on port
  80, nothing happens). `GET /BC/dev/metadata` shows the advertised value in
  `webEndpoint`.
- **The AL extension caches the web endpoint** in `ServerInfoCache.dat` next
  to its host binary (`~/.vscode/extensions/ms-dynamics-smb.al-*/bin/linux/`).
  If F5 keeps opening a port-less URL after the container has the fix, delete
  that file to force a re-read.
- **One debug session at a time.** Each F5/attach is its own DebuggerHub
  connection; a second F5 while an earlier one is alive (or a crashed adapter
  process lingering — see Things to know) leaves the break to land on the
  older connection while the new VS Code session shows an empty call stack
  with "Paused on Step" and no frames.

## What was broken on Linux (and is now fixed)

**StartupHook Patch #25.** Every `setBreakpoints` from VS Code failed with
*"An unexpected error occurred invoking 'AddBreakpoint' on the server"*. Root
cause (decompiled from `Microsoft.Dynamics.Nav.Ncl.dll`):
`DebugRuntimeManager.ExecuteClientCall` evaluates

```csharp
bool flag2 = debugRuntime.IsDebuggedSessionClosedOrDisposed
          || (debugRuntime?.DebuggedSession.IsDisposingOrDisposed ?? false);
```

With `breakOnNext`, breakpoints are set before any session is attached, so
`DebuggedSession` is null. The `?.` guards `debugRuntime`, **not**
`DebuggedSession` — the left operand returns `false` for a null session, the
`||` doesn't short-circuit, and the right operand dereferences null →
`NullReferenceException` on every hub call. The hook patches
`BaseDebugRuntime.IsDebuggedSessionClosedOrDisposed` to report `true` for a
null session, so the expression short-circuits and `ExecuteClientCall` takes
its safe early-return path. This is a latent upstream bug (same IL on Windows);
it surfaces in this setup's timing, where breakpoints arrive before the
debuggee session attaches.

## VS Code hangs on the call stack (extension.js patch required)

**Symptom (real VS Code only — headless DAP harnesses don't hit it):** attach
takes 15–30 s instead of <1 s, and when a breakpoint hits, VS Code shows
"Paused on Step" with an empty call stack that never populates. The
`stackTrace` DAP request never gets a response (observed >3 min with no
reply). `DebuggerServices.log` (next to the extension's host binary) fills
with:

```
An exception happened while reading the package cache: '/home/<user>'.
Too many levels of symbolic links : '...'
```

**Root cause (decompiled from
`Microsoft.Dynamics.Nav.EditorServices.Protocol.dll`,
`SettingsExtensions.GetPackageCachePaths`):** when the debug adapter has no
`al.packageCachePath` setting, it falls back to
`Directory.GetCurrentDirectory()` as the package cache root. The debug
adapter is a separate process from the language server and **never receives
workspace settings** — no CLI flag carries `al.packageCachePath` and the
extension never sends `al/setActiveWorkspace` over the DAP channel — so the
fallback *always* fires. The extension spawns the adapter via
`new DebugAdapterExecutable(path, args)` with no `cwd`, so the adapter
inherits the VS Code extension host's working directory, which on Linux is
typically `$HOME`. The first `stackTrace` request compiles the project
(`Workspace.CurrentSolution.GetCompilationAsync`), and that compilation
recursively scans the entire package cache root for `*.app` files — i.e. all
of `$HOME`. Any symlink loop under `$HOME` (e.g. mise's
`~/.local/state/mise/trusted-configs/<id> -> /home/<user>`) makes the scan
effectively endless. The same scan also runs during `attach` processing,
which is where the 15–30 s attach delay comes from — and on a slow attach a
fast sign-in can slip past the `breakOnNext` arm entirely. Windows escapes by
accident: VS Code's CWD there is a small install directory.

**Fix (must be reapplied after every AL extension update):** make the
extension spawn the adapter with the workspace folder as CWD, so the fallback
scans the project directory (where `.alpackages` lives). Idempotent patch:

```bash
cd ~/.vscode/extensions/ms-dynamics-smb.al-*/dist && cp -n extension.js extension.js.bak-dap-cwd && node -e '
const fs=require("fs");
let s=fs.readFileSync("extension.js","utf8");
const o="const r=yield this.getServerPath();return new a.DebugAdapterExecutable(r,n)";
const p="const r=yield this.getServerPath();return new a.DebugAdapterExecutable(r,n,{cwd:t.uri.fsPath})";
if(s.includes(p)){console.log("already patched");process.exit(0);}
if(s.split(o).length-1!==1){console.error("pattern not found exactly once — extension changed, re-derive the patch");process.exit(1);}
fs.writeFileSync("extension.js",s.replace(o,p));console.log("patched OK");'
```

Reload the VS Code window afterwards. After the patch, attach completes in
~1 s and the call stack appears ~3 s after a breakpoint hit. Reported
upstream as [microsoft/AL#8276](https://github.com/microsoft/AL/issues/8276)
(the adapter should never default its package cache to the process CWD,
and/or the extension should pass the cache path / cwd to the adapter); until
that's fixed this patch or an equivalent is required on Linux.

## Capturing VS Code ↔ adapter DAP traffic

Two complementary switches, both usable in any AL project:

- `"traceDap": true` in the launch configuration makes the extension attach
  its `AlDapProtocolMessageLogger`, which logs every DAP message (full JSON)
  to the AL extension's log output channel at *debug* level. To see it:
  Command Palette → "Developer: Set Log Level…" → pick the AL channel →
  Debug. The same content lands in VS Code's extension-host log files on
  disk.
- `"al.editorServicesLogLevel": "Verbose"` (workspace settings) makes the
  spawned host processes log verbosely to `DebuggerServices.log` /
  `EditorServices.log` next to the host binary
  (`~/.vscode/extensions/ms-dynamics-smb.al-*/bin/linux/`). This is also
  where every request error and the package-cache scan exceptions show up,
  with timestamps — the single most useful file when debugging the debugger.

## Things to know

- **`breakOnNext` attaches to the *next* session.** Navigating the web client
  by URL (`/?page=22`) opens a *new* session each time, which is *not* the one
  the debugger attached to. Navigate **inside the UI** (click links/actions) so
  code runs in the attached session — same as on Windows, just easy to trip
  over when testing with scripted URL navigation.
- **Exactly one live debugger connection, please.** Every debug adapter process
  (`Microsoft.Dynamics.Nav.EditorServices.Host … /startDebugging`) holds its own
  SignalR connection to the DebuggerHub, and an armed `breakOnNext` on an *older*
  connection wins the next session. The failure mode looks baffling: the web
  client freezes on a breakpoint (the session really is paused server-side, and
  pages opened from other sessions may fail with "an error occurred"), but the
  VS Code session you're looking at never shows a stop — the break event went to
  the other connection. If this happens, check for stray adapter processes
  (`pgrep -af startDebugging`) left behind by crashed debug sessions or headless
  tooling and kill them, then re-attach.
- **Stopping the debugger while paused aborts the debugged activity.** If you
  hit Stop (Shift+F5) while a session is paused inside `OnOpenPage`, the NST
  throws `NavNCLDebuggerActivityAbortedException` into that activity and the
  web client shows "An error occurred while opening the page." That's the
  documented stop semantics, not a Linux bug — refresh the browser and the
  session continues.
- **Breakpoints bind on session attach.** The DebuggerHub only registers
  breakpoints when a debuggee session exists; the AL debug adapter raises the
  DAP `initialized` event when the session attaches and VS Code (re)sends all
  breakpoints at that point. This is automatic in VS Code; only hand-rolled
  DAP clients need to care.
- **A fresh web-client sign-in attaches the debugger twice.** Sign-in creates
  a transient session and then the real one ~1 s later; the NST re-attaches
  the armed debugger connection and the adapter raises `initialized` twice.
  Harmless: breakpoints are scoped to the debugger connection (verified — a
  client that arms only on the first `initialized` still hits), and VS Code
  re-sends breakpoints on every `initialized` anyway. Don't be confused by
  the doubled events in a DAP trace.
- **Closing the debugged browser tab ends the VS Code debug session.** When
  the debugged session dies, the NST calls
  `OnDetachedFromConnection(terminateSession: true)` and the adapter sends
  DAP `terminated` ~30 s later — VS Code's debug session just disappears.
  Expected lifecycle, not a crash; re-attach and sign in again.
- **Idle SignalR timeout (observed in headless tests, not yet reproduced in
  VS Code):** a debug connection idling for several minutes occasionally died
  with *"Server timeout (30000.00ms) elapsed without receiving a message from
  the server"*, suggesting the NST's hub keep-alive pings stalled. Re-attaching
  recovers. If this shows up in real VS Code sessions, the NST side
  (`DebuggerHub` keep-alive on the Kestrel/HttpSys-stub transport) is the place
  to look.
- **Credential prompts and caching.** UserPassword credentials are cached in
  `UserPasswordCache.dat` next to the AL extension's host binary, protected via
  ASP.NET DataProtection — this works on Linux (verified by decrypting the
  cache). Two things govern how often VS Code prompts:
  - The cache key is the literal `server` string + `serverInstance` (lowercased).
    `"server": "http://localhost"` and `"server": "http://localhost:8080"` are
    *different entries* even when both end up talking to port 7049 — use one
    consistent `server` value across all launch/publish configurations or each
    spelling prompts once.
  - The cache lives inside the extension's install folder, so every AL extension
    update wipes it (and the DataProtection key ring is per host *version*), so
    one re-prompt after an extension update is expected. A server 401 also
    deletes the entry, but this NST never returns 401 (auth is bypassed), so
    that path doesn't fire here.
- **`BC_DEBUG_FIRSTCHANCE=1`** (env var on the `bc` service) prints every
  thrown exception with its full inner chain to the container log — the NST
  counterpart to the web client's `WEBCLIENT_DEBUG_FIRSTCHANCE`. This is what
  surfaced the Patch #25 NRE behind the generic hub error. Very noisy; opt-in
  only.

## How it was verified headlessly

The AL extension's debug adapter
(`Microsoft.Dynamics.Nav.EditorServices.Host /startDebugging /projectRoot:<dir>`)
speaks DAP over stdio and can be driven without VS Code. Two non-obvious
pieces for anyone repeating this:

- **Credentials**: the adapter reads UserPassword credentials from
  `UserPasswordCache.dat` next to the host binary (protected via ASP.NET
  DataProtection, key ring in `~/.config/<EditorServices host assembly name>`).
  VS Code populates it via the `al/saveUsernamePassword` LSP request after
  prompting. Headless, the cache can be written by injecting a
  `DOTNET_STARTUP_HOOKS` assembly into the host process that calls
  `OnPremiseHttpClientFactory.SaveCredentials` via reflection with the same
  `LaunchConfiguration` JSON the attach will use (the cache key is derived
  from server/port/instance/tenant/auth options).
- **Handshake**: send `setBreakpoints` + `configurationDone` in response to
  each `initialized` *event* (which arrives at debuggee-attach time), not
  merely after the `attach` response.
