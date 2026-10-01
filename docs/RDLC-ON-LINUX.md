# RDLC rendering on Linux — where the investigation got to

> **Superseded on the `feat/native-rdlc-pdf` branch.** This document ends at a
> `TypeLoadException` it could not identify, and says nothing here is
> implemented. Both statements were true when it was written and are no longer.
> The engine now renders — invoices, charts and RTL text — through Microsoft's
> own ReportViewer under Mono with a native Pango/HarfBuzz font and text bridge,
> and through Microsoft's real reporting service over its real gRPC endpoints.
> See `prototypes/rdlc/README.md` on that branch. What is still accurate below
> is the architecture (why RDLC is a separate process, and why no config flag
> changes that) and the CAS analysis. What is stale is "Where it stops",
> "If it does turn out to work", and the claim that a full Cecil `Write` of
> `Microsoft.ReportViewer.Common.dll` is not possible.
>
> `Report.SaveAs(Pdf)` now works: built with `BC_WITH_RDLC=1` and run with
> `BC_RDLC_RENDERER=mono`, an RDLC layout returns a real four-page PDF, and
> `extensions/rdlc-smoke-test` passes under the normal test runner. The default
> image is unchanged and still returns `false`.

Parked, not abandoned. Written down so the next person starts from the blocker
rather than from the beginning.

Tracking issue: [#73](https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues/73).
Measured 2026-09-07 against BC 28.4.

## Why RDLC is a separate process, and why no config flag changes that

`ReportingServiceIsSideService` exists and `scripts/entrypoint.sh` already sets it
to `false`. It controls how the reporting process is *managed*, not whether
rendering happens in-process, and nothing could — the split is a runtime
boundary:

| assembly in `/bc/service/SideServices/` | target framework | size |
|---|---|---|
| `Microsoft.ReportViewer.Common.dll` | **.NET Framework 4.6** | 9.0 MB |
| `Microsoft.BusinessCentral.Reporting.Server.dll` | **.NET Framework 4.8** | 102 KB |
| `Microsoft.BusinessCentral.Reporting.Service.exe` | **.NET Framework 4.8** | 117 KB |
| `Microsoft.Dynamics.Nav.Types.Report.dll` | .NET Standard 2.0 | 37 KB |

The NST is .NET 8. .NET Framework 4.6 assemblies cannot load into it, so
Microsoft hosts the RDLC renderer in its own executable and talks to it over
gRPC (`Grpc.Core` 2.46.6, on `ReportingServicePort`). The `.exe` is a thin host;
all the rendering is in the managed `Microsoft.ReportViewer.*` assemblies.

This is also why **Word and Excel layouts already render and RDLC does not**.
Those go through Aspose.Words *inside* the NST, which is why supplying
`libSkiaSharp.so` and harfbuzz (commit `9679545`) was enough for them. RDLC never
enters that process.

What the image does today: the entrypoint replaces the `.exe` with a `sleep
infinity` stub (it is a Windows PE and cannot be executed), keeping the real
binary as `.exe.win`; Patch #18 no-ops `SetupSideServices`; Patch #20 no-ops the
watchdog's `EnsureAlive()`; Patch #19 swaps `CustomReportingServiceClient` for a
proxy whose `RenderAsync` / `PrintReportAsync` throw `NavReportException`.

## What was proven to work

Mono 6.12 runs the real service on Linux. Staged `SideServices` into a scratch
directory, added the Linux gRPC native, restored the real `.exe`, ran it in a
stock `mono:latest` container.

- **Mono loads Microsoft's assemblies unmodified** — `monop` reports
  `Microsoft.ReportViewer.Common, Version=15.0.0.0`.
- **`libgrpc_csharp_ext.x64.so` resolves.** The artifact ships only
  `grpc_csharp_ext.x64.dll` (Windows). `Grpc.Core` **2.46.6** — the exact version
  the artifact carries — has `runtimes/linux-x64/native/libgrpc_csharp_ext.x64.so`
  in its NuGet package. Same version-matched-native pattern as `libSkiaSharp.so`.
- **`libgdiplus` is present** in the Mono image, so `System.Drawing` is backed.
- **The service starts and serves.** `Program.Main` needs `-PortNumber`; without
  it the failure is `No port was provided to the server.`, which reads like a
  platform error and is not one. With it:

  ```bash
  mono ./Microsoft.BusinessCentral.Reporting.Service.exe -PortNumber 5005 -Name ReportingService
  ```

  the process stays up and binds 12 gRPC listener sockets on `::1` and
  `127.0.0.1` (`0x138D` = 5005 in `/proc/net/tcp6`).

## The CAS blocker, and the patch that clears it

An actual render first failed with `System.NotImplementedException` at
`System.AppDomain.get_ApplicationTrust()` — Mono does not implement it.
ReportViewer reads it on every render, even for an RDL with no expressions:

```
at System.AppDomain.get_ApplicationTrust ()
at Microsoft.Reporting.ReportRuntimeSetupHandler.get_IsAppDomainCasPolicyEnabled ()
at Microsoft.Reporting.ReportRuntimeSetupHandler.get_ExecuteInSandbox ()
at Microsoft.Reporting.ReportRuntimeSetupHandler.GetReportRuntimeSetup ()
at Microsoft.Reporting.LocalService.GetCompiledReport (...)
```

The relevant Microsoft code:

```csharp
private ReportRuntimeSetup GetReportRuntimeSetup()
{
    if (m_reportRuntimeSetup == null)
    {
        if (ExecuteInSandbox) ExecuteReportInSandboxAppDomain();
        else                  ExecuteReportInCurrentAppDomain();
    }
    return m_reportRuntimeSetup;
}

internal void ExecuteReportInCurrentAppDomain()
{
    if (!IsAppDomainCasPolicyEnabled)
        throw new InvalidOperationException(ProcessingStrings.CasPolicyUnavailableForCurrentAppDomain);
    SetAppDomain(useSandBoxAppDomain: false);
    m_reportRuntimeSetup = ReportRuntimeSetup.CreateForCurrentAppDomainExecution();
}
```

**On Windows `IsAppDomainCasPolicyEnabled` is true**, because BC's own
`Microsoft.BusinessCentral.Reporting.Service.exe.config` turns legacy CAS back
on:

```xml
<runtime>
  <NetFx40_LegacySecurityPolicy enabled="true" />
</runtime>
```

So the faithful patch reproduces the Windows configuration rather than changing
behaviour: `get_IsAppDomainCasPolicyEnabled` → `return true`, and
`get_ExecuteInSandbox` → `return false`. Getting the first one *backwards*
(returning false) does not help — `ExecuteReportInCurrentAppDomain` then throws
`InvalidOperationException` on purpose.

Both are tiny-format property getters, so this is a two-instruction body
overwrite: set the tiny header to `(2 << 2) | 2`, then `ldc.i4.1; ret`
(`17 2A`) or `ldc.i4.0; ret` (`16 2A`).

**A full Cecil `Write` of `Microsoft.ReportViewer.Common.dll` is not an option**
on a machine with no .NET Framework GAC: it dies resolving
`System.IO.Packaging.CompressionOption` for a field constant, and a lenient
assembly resolver does not help because `MetadataBuilder.GetConstantType` calls
`CheckedResolve`. Read the method RVA with Cecil, convert RVA to file offset via
the PE section headers, and patch the bytes in place.

With those two edits the CAS exceptions are gone and ReportViewer enters report
compilation. That it is genuinely compiling is not an inference — a deliberately
invalid hand-written RDL was rejected with the real schema error:

```
Deserialization failed: The element 'Report' ... has invalid child element 'Body' ...
List of possible elements expected: ... ReportSections ...  Line 4, position 4.
```

and a corrected RDL 2010 (`ReportSections/ReportSection`) parsed.

## Where it stops

```
ReportProcessingException: An unexpected error occurred in Report Processing.
  at ReportProcessing.CreateIntermediateFormat (...)
  inner: System.TypeLoadException: Failure has occurred while loading a type.
  at ReportIntermediateFormat.Persistence.IntermediateFormatWriter.Write (System.Object obj)
```

A type fails to load in ReportViewer's intermediate-format serializer.
`MONO_LOG_LEVEL=info MONO_LOG_MASK=type,dll` did not name it.

**The gating question is whether that is one missing type or a class of them.**
Answering it needs better type-load diagnostics than the stock Mono image gives
— a debug Mono build, or stepping the serializer. Until that is known, the size
of this project is unknown, which is why it is parked rather than scheduled.

## If it does turn out to work

The remaining work would be: add Mono to the image (opt-in — it is a few hundred
MB for a feature many consumers never use), stage the version-matched
`libgrpc_csharp_ext.x64.so` the way `src/Dockerfile` already stages
`libSkiaSharp.so`, apply the two-getter byte patch to
`Microsoft.ReportViewer.Common.dll` in Step 2b, and gate off Patches #18, #19 and
#20 and the `.exe` stub swap so BC starts the process and uses the real client.
None of those three patches is load-bearing for anything else — they exist only
because the process could not run.

Two things to keep in mind if it is picked up:

- `Grpc.Core` is the deprecated C-core gRPC stack. 2.46.6 is near the end of that
  line. The Linux native exists, so this works today; it is not something to
  build on for years.
- That would be a third managed runtime in the image, on top of the .NET 8 and
  .NET 10 pair already there for BC 27/28 and BC 29.
