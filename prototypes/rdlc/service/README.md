# Original BC Reporting.Service on Mono: real gRPC Render

This is an isolated service integration proof, not a replacement report engine
and not yet an AL `Report.SaveAs` test. All files belong to this session
directory. Nothing in the repository, main native-bridge workspace, or shared
BC/SQL stack was modified.

## Reproduce

The prepared `run/` and `compiler/` directories are private snapshots. With the
existing `bc-rdlc-native-probe:21b74105` image and host Poppler utilities:

```bash
bash run-service-test.sh
```

The script compiles the independently authored adapters and caller, applies
guarded compatibility edits to private copies of two original assemblies,
starts the **original** `Microsoft.BusinessCentral.Reporting.Service.exe`
under Mono, then makes five real gRPC requests over container loopback. The
container uses `--network none`, publishes no host port, and stops its own
service process on exit. The container is removed afterwards.

To stage again from original dependencies and a native-bridge build:

```bash
bash stage.sh /path/to/original/reporting-service-directory /path/to/original-native-bridge \
    ../rdlc-vb-runtime/compatible/Microsoft.VisualBasic.dll
bash run-service-test.sh
```

`stage.sh` copies Microsoft binaries into the session staging area; these
binaries must not be committed with the source. It deliberately retains the
original service dependency set rather than claiming a minimal dependency
closure. A bridge snapshot must include its patched original ReportViewer
Common and DataVisualization, managed/native bridge, VB runtime and `compiler/tasks`. The optional
third argument replaces only the staged VB runtime. The current fixture requires
the full runtime with `Format`, not the earlier portable subset.
Its Roslyn wrapper expects `/work/compiler` and `/work/run`, satisfied by the
container mount here.

`start-service.sh` is the standalone launcher **inside that prepared container**.
`REPORTING_PORT` defaults to 5005. Set `MONO_TRACE=E:all` for the diagnostic
exception log; the test script does this and saves `service.log`.

## Observed results

Five requests run against one service process, reusing its reporting AppDomain.
There is no direct `LocalReport.Render` call in the caller.

| Dataset | Wire representation | PDF | Extracted aggregate |
| --- | --- | --- | --- |
| 3 rows | Noncompact, nondeduplicated, uncompressed; 204 bytes | `service-result.pdf`, 9099 bytes | `rows=3`, `total=7.50` |
| 10,000 rows | Compact, value-deduplicated, compressed; 34,992 bytes | `service-compact-dedup.pdf`, 9224 bytes | `rows=10000`, `total=62506250.00` |
| 7 rows, after the large request | Noncompact, nondeduplicated, uncompressed; 364 bytes | `service-repeat.pdf`, 9100 bytes | `rows=7`, `total=35.00` |
| 120 invoice rows plus a PNG byte-array field | Compact, deduplicated, uncompressed; 58,175 bytes | `service-invoice.pdf`, 25,281 bytes | Four pages, all 120 lines, quantity 7260, total 9075.00, embedded VB and logo |
| Same 120 rows through the filtered chart layout | Compact, deduplicated, uncompressed; 58,175 bytes | `service-chart.pdf`, 65,015 bytes | Six bars (quantities 1 through 6), original 1800 x 1200 image at 300 DPI |

The first fixture is one page and prints aggregates, not 10,000 detail lines.
It now explicitly calls VB `Format(..., "0.00")` around its decimal aggregate.
All three requests also succeeded after the preferred full VB runtime was copied
into this private staging directory; see `stdout-full-vb.log`.
PDF metadata identifies Microsoft's Reporting Services PDF Rendering Extension
15.0.0.0. Page geometry is 612 x 792 points. The current bridge snapshot embeds
a subset CID TrueType Noto Sans with a Unicode map. This is not a typography
fidelity assessment; the main bridge work owns font selection and shaping.

The later invoice and chart runs use private copies of the parent's
`fixtures/invoice.rdlc` and `fixtures/chart.rdlc`, plus its latest Common,
DataVisualization, managed bridge and native library. See
`stdout-latest-bridge.log`. The chart retains its original 300 DPI raster;
`service-chart.images` records the 1800 x 1200 image and
`service-chart-page1.png` shows the six filtered bars. No downsampling was added.
The invoice's logo travels through the original NavDataSet byte-array serializer.

The original service EXE is unchanged:

```text
SHA256 92f5215ea507a688aa4bc3f3a3f87bab9ea7b08d39117f23f170bdd65e2b6952
```

Original assembly identities used in analysis:

| Assembly | MVID |
| --- | --- |
| Reporting.Service | d524dce1b4c543879880d49f8d4d5edb |
| Reporting.Server | 14bad93bbb5f4017bda6c9e3653863cc |
| Reporting.Common | 192c71fdf4ef4d70afd9f4f0876abc7d |
| Nav.Types from the side-service directory | b4dcebf827404bec9885432b9b6218ec |
| Original ReportViewer.Common | 5b437ccb94874e41a96f8f621f811037 |

## The original execution path

`ServiceRender.cs` creates a real `Microsoft.Dynamics.Nav.Types.Data.NavDataSet`
named `DataSet`, with a `Result` table containing string, Int32 and Decimal
columns, with a PNG byte-array column in invoice mode. It calls the original `NavDataSet.Serialize()` and locally roundtrips
the result as an additional assertion. Reflection only chooses the private
serializer-mode settings and reads the internal compression flag; it does not
implement or fabricate the binary format.

It uses the original generated protobuf/gRPC classes in Reporting.Common:
`ConfigureService` first, then duplex `Render` with `RenderingContext`, the
layout-cache handshake, `LayoutChunk` and `DatasetChunk` messages. It checks
responses for errors, checks the advertised artifact size, and saves only a
complete PDF artifact.

On the server, the original path remains:

```text
ReportingServiceGrpcServer.Render
  -> original layout and dataset chunk receiver
  -> ReportingServiceRenderingEngine.RenderArtifact
  -> RDLCRenderer
  -> LocalReportHandle / original reporting AppDomain
  -> new NavDataSet(new HybridDataStore(bytes, compressed), compact, dedup)
  -> ReportDataSource("DataSet_Result", navDataSet.Tables[0].Rows)
  -> original ReportViewer expression compiler / pagination / PDF writer
  -> original gRPC stats and artifact chunks
```

## Service-specific compatibility adaptations

`PatchServiceCompat.cs`, `HeadlessPageSettings.cs`, and
`MonoRenderingContext.cs` are independently authored. They do not replace
rendering, dataset decoding, layout parsing, expression compilation, pagination,
or artifact transfer.

1. **ETW exporters:** `ConfigureService` originally fails with
   `ArgumentException: ETW cannot be used on non-Windows operating systems`.
   Two exact registration call sites in BC's telemetry assembly are removed:
   the Geneva log and trace exporters configured for a Windows ETW session.
   Other exporters, including the optional file exporter, remain intact.
   This is confined to the side-service copy of the telemetry assembly.
2. **Printer-dependent page getters:** Mono's `PageSettings.Margins` getter
   throws `InvalidPrinterException: No printers are installed` while BC parses
   RDLC page geometry. A managed adapter reads Mono's existing `margins`,
   `paperSize`, and `landscape` fields; BC's original parser and public setters
   still write the actual RDLC values. The patch redirects 42 getter call sites
   in Reporting.Server. It does not invent a printer or replace page dimensions.
   Field names and types are checked; incompatible Mono layouts fail explicitly.
3. **AppDomain counters:** Mono throws `NotImplementedException` from
   `AppDomain.MonitoringIsEnabled = true`. The two enable sites are omitted and
   the local-report proxy's monitoring flag is false, selecting BC's existing
   zero-counter path. The reporting AppDomain and its ordinary rendering-count
   bookkeeping are retained.
4. **Cross-domain rendering context:** Mono's remoted `CultureInfo` loses its
   internal nonserialized culture data, causing a NullReferenceException in
   `TextInfo` during expression processing. Eight original worker-context setter
   sites now use named cultures re-resolved in the receiving domain and represent
   a null thread principal as an unauthenticated `GenericPrincipal`. This also
   avoids Mono's null-principal serialization crash. BC's RPC carries LCIDs
   rather than custom CultureInfo overrides. The original worker scheduling,
   restore logic and timezone handling remain.

The patcher asserts the expected ETW, monitoring and context-site counts and
unexpected IL shapes fail rather than producing a nominally successful patch.

## Dependencies and remaining limitations

- Ubuntu noble probe image: Mono 6.8, libgdiplus, Pango/HarfBuzz/FreeType and the
  native bridge's font files. No Wine or Windows process.
- Original BC Reporting.Service EXE/config, Reporting.Server/Common, Nav.Types
  and the side-service dependency directory. `Microsoft.SqlServer.Types.dll`
  is required even for this simple scalar dataset because ReportViewer's
  serializer resolves its types.
- Matching **Grpc.Core 2.46.6** managed assembly and
  `libgrpc_csharp_ext.x64.so`; do not substitute a random native version.
- Original ReportViewer Common/WebForms/DataVisualization/ProcessingObjectModel,
  the main prototype's patched Common and DataVisualization, and managed/native
  font/drawing bridge.
- Roslyn 4.8 net472 `vbc.exe`, Mono framework reference assemblies, and the
  independently built full `Microsoft.VisualBasic.dll` from
  `../rdlc-vb-runtime/compatible/`. SHA-256:
  `6b742ce1c7a6844ed51b6eac5ab82453ab6c5dbf87552613a8b4c678268d7e07`.
  It is the complete MIT Mono runtime with the official MIT Microsoft legacy
  StringType implementation and helper plumbing, **delay-signed**, not
  Microsoft-signed. The existing compiler wrapper's
  `/vbruntime:/work/run/Microsoft.VisualBasic.dll` already points to this copy.
  The earlier portable subset failed `Format` compilation with BC30451;
  the restored Format expression now renders through the service in all three
  requests. Full culture parity is not claimed: two Turkish I comparisons still
  fail in Mono's CompareInfo. Provenance and broader API limitations are in the
  VB-runtime workspace's README.
- This image has no CUPS. Mono logs `libcups not found`; PDF output does not
  require a printer after the page-state adaptation. **Physical printing has
  not been implemented or claimed.**
- With first-chance tracing enabled, original OpenTelemetry HTTP instrumentation
  reports its unsupported .NET Framework reflection probe, and Mono certificate
  probes report caught import exceptions. They do not prevent rendering.
- Some real render errors were obscured by a separate Mono
  `StackTrace.AddFrames` NullReferenceException inside BC's exception telemetry,
  yielding gRPC `Unknown`. `service.log` with `MONO_TRACE=E:all` retains the
  original root exceptions. The successful path does not fix that error-reporting
  defect.
- No attempt was made to enable unsafe external assemblies, external images,
  hyperlinks, network access or authenticated remote clients. Keep this service
  isolated and only accept trusted layouts; Mono is not a modern CAS sandbox.
- Not demonstrated here: AL `Report.SaveAs`, NST's original reporting client,
  the service watchdog and restart lifecycle, BC report parameters/labels from
  a real AL report, AL Media/MediaSet values beyond the demonstrated PNG byte array,
  concurrency, cancellation recovery or production memory limits.

The repository integration gates (NST patches 18/19/20, service launcher/stamp,
and image/runtime wiring) intentionally remain the main task's responsibility.

The follow-up `NST-WIRING.md` records the original NST client's constructors,
exact custom-factory delegate, configuration/readiness obligations and
root-only scope of these probes.
