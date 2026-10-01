# Original NST reporting-client factory contract

Inspected without editing the repository or running the shared stack.
These are API/call-path findings, not evidence of an NST `Report.SaveAs` run.

## Concrete implementation and constructors

Assembly: `Microsoft.BusinessCentral.Reporting.Client.dll`

MVID: `536de3ae329b49f59e1f513338b22cfa`

Type: `Microsoft.BusinessCentral.Reporting.Client.ReportingServiceGrpcClient`

Public constructors:

```csharp
ReportingServiceGrpcClient(
    ReportingServiceClientDiagnosticsInfo diagnosticsInfo,
    ICommunicationFactory connectionFactory);

ReportingServiceGrpcClient(ICommunicationFactory connectionFactory);
```

The two-argument constructor stores diagnostics, obtains a channel from
`connectionFactory.CreateClientChannel()`, and constructs the generated
`ReportingService.ReportingServiceClient` around that channel. It does not
start a process, register a watchdog, configure the remote service, or await
connectivity. The one-argument overload supplies default diagnostics.

`ReportingServiceClientDiagnosticsInfo` is a readonly struct whose full public
constructor takes:

```text
string navTenantId
string environmentName
NavTenantEnvironmentType environmentType
string alternateTenantIds
string aadTenantId
string aadUserId
```

Preserve the diagnostics supplied by NST rather than inventing an empty value.

## Custom factory: one ValueTuple argument

Inspected `Nav.Ncl` MVID: `e34004bb631f46c480755c2a102ffb9d`.

The internal read/write property on `Microsoft.Dynamics.Nav.Runtime.NavEnvironment`
is exactly:

```csharp
Func<
    ValueTuple<ReportingServiceClientDiagnosticsInfo, ICommunicationFactory>,
    IReportingServiceClient
> CustomReportingServiceClient
```

It is a **synchronous delegate with one tuple parameter**, not two parameters
and not a Task-returning factory. Its compiler backing field is
`<CustomReportingServiceClient>k__BackingField`, which Patch 19 already locates.

`NavSession.InitReportingClientAsync`:

1. Constructs `LocalhostCommunicationFactory` using
   `ServerUserSettings.Instance.ReportingServicePort.Value`.
2. Builds diagnostics from the current tenant settings.
3. If the custom factory exists, invokes it with `(diagnostics, factory)` and
   **returns immediately**.
4. Only the noncustom branch creates a default `ReportingServiceGrpcClient` and
   awaits `EnsureConnectivityAsync` through `ExecutionUnit.ExecuteExternalActionAsync`.

`NavSession.GetReportingServiceClientAsync` caches that result in the session's
`reportingClient` field. The same field is referenced from `NavSession.DisposeAsync`.
Use a fresh real client per factory invocation rather than the existing global
no-op proxy singleton: original client disposal shuts down its gRPC channel.

## Communication factory

Reporting.Common has this public interface:

```csharp
interface ICommunicationFactory
{
    Grpc.Core.Channel CreateClientChannel();
    Grpc.Core.ServerPort CreateServerPort();
}
```

Its public `LocalhostCommunicationFactory(int portNumber)` implementation creates
an insecure gRPC channel/server port on `localhost`. It has no process lifecycle
behavior. The client constructor calls only `CreateClientChannel()`.

Conceptually, the opt-in factory can therefore be:

```csharp
input => new ReportingServiceGrpcClient(
    input.Item1,
    new LocalhostCommunicationFactory(supervisedMonoPort))
```

If the explicitly supervised process uses NST's configured ReportingServicePort,
`input.Item2` can be passed unchanged instead. A replacement factory is needed
for an alternate port; a container on a different network also needs an explicit
different implementation because `LocalhostCommunicationFactory` is loopback-only.

The snippet above is a wiring recommendation, not repository code or a completed
configuration/restart implementation.

## What is not automatic on the custom branch

**Connectivity:** `EnsureConnectivityAsync()` exists on the concrete client,
not on `IReportingServiceClient`. It uses `channel.ConnectAsync` with a deadline
derived from `ReportingServiceEstablishConnectionTimeout`. Its implementation
does not launch or restart the server. The custom-factory branch skips this
call, so do not assume selecting a real client proves readiness.

**Configuration:** The constructor does not call ConfigureService. Our real
service probe had to send ConfigureService before Render; the server's
`ReportingServiceSettings.GetInstanceAsync()` otherwise waits and ultimately
reports that the reporting service is not configured. With SetupSideServices
disabled, explicitly configure each newly started Mono process as part of the
supervisor's readiness handshake, or provide equivalent lazy initialization
with proper async/error handling.

Configuration must agree with NST's dataset serializer, especially
`EnableCompactSerialization`; copying the probe's hardcoded `false` into the
real NST path would be incorrect. Dataset compression and value deduplication
are flags on each RenderingContext. Preserve original NST configuration instead
of using the fixture's settings.

**Restart:** A replacement Mono process loses its in-memory ReportingServiceSettings.
Reconnecting a surviving channel alone is insufficient: the supervisor must
repeat configuration before marking the process ready.

**Disposal:** The concrete client's DisposeChannelAsync calls channel.ShutdownAsync
once and marks the client disposed; it does not terminate the rendering process.
A separately supervised process is compatible with that ownership model.

## Interface operations

`IReportingServiceClient : IAsyncDisposable` exposes:

```csharp
ValueTask ConfigureServiceAsync(ReportingServiceSettings settings);
ValueTask<ReportingServiceSettings> GetServiceConfigurationAsync();
ValueTask<Stream> RenderAsync(
    byte[][] dataset, byte[] layout,
    RenderingContext renderingContext, NavCancellationToken token);
ValueTask PrintReportAsync(
    byte[][] dataset, byte[] layout, PrintRequestDetails printDetails,
    RenderingContext renderingContext, NavCancellationToken token);
```

No Windows process/watchdog method is part of this interface. Keeping Patch 18
SetupSideServices disabled and the Windows watchdog stubbed while using this
real transport is consistent with the inspected client construction path.
That does not by itself prove every later NST reporting call is Linux-safe.

## Root-only scope and current bridge notes

The successful service probes ran as the image's default UID 0. They do **not**
prove non-root compatibility. Parent reports a known non-root failure from
ReportViewer `RevertImpersonationContext.Impersonate(IntPtr.Zero)`; keep that as
an explicit remaining gate rather than removing impersonation indiscriminately.

Parent's current bridge uses actual Mono ProtectedData and its Roslyn wrapper
has `/noconfig`, `/nowarn:40000`, `/vbruntime:portableVB`,
`/define:_MYTYPE="Empty"` and the netstandard facade reference. No changes to
those parent-owned binaries, wrapper or data-protection behavior were made here.
This integration directory is an earlier private snapshot, not an implicit
upgrade to whichever bridge binaries the parent produces later.

The one explicit later upgrade is the preferred full VB runtime copied from
`../rdlc-vb-runtime/compatible/Microsoft.VisualBasic.dll`; its hash and remaining
culture limitations are recorded in README.md. The real gRPC fixture's previously
failing `Format` expression now succeeds. No parent-owned files were modified.

## Source lookup anchors

Use these explicit context aliases and metadata identities:

| Context | Member token | Finding |
| --- | --- | --- |
| `rdlc-nst-client` | `0600001B` | Two-argument real client constructor |
| `rdlc-nst-client` | `0600001C` | Default-diagnostics constructor |
| `rdlc-nst-client` | `02000003` | IReportingServiceClient |
| `rdlc-nst-client` | `02000004` | Diagnostics struct |
| `rdlc-nst-client` | `06000064` | EnsureConnectivityAsync state-machine body |
| `rdlc-nst-client` | `06000062` | DisposeChannelAsync state-machine body |
| `ncl` | `1700157C` | CustomReportingServiceClient property |
| `ncl` | `06006FC0` | NavSession.InitReportingClientAsync |
| `ncl` | `0600712D` | Session client caching |
| `rdlc-service-common` | `02000007` | ICommunicationFactory |
| `rdlc-service-common` | `02000008` | LocalhostCommunicationFactory |

`rdlc-service-common` is the side-service Reporting.Common MVID
`192c71fdf4ef4d70afd9f4f0876abc7d`. Resolve equivalent members in the target
artifact version before hardcoding constructor or delegate assumptions.
