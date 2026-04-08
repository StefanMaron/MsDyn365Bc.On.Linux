# Feedback for Microsoft (raw notes)

This document is **raw material for a future report to Microsoft** about
specific things in the BC service tier (NST) and the BC artifact pipeline
that, if changed upstream, would let bc-linux (and other partners working
in non-Windows or non-standard environments) ship simpler, faster, more
maintainable code.

It is not a formal report. It is the running notes that the formal report
will be built from. Each finding includes:

- **What we observed empirically** (so the ask is grounded in evidence,
  not speculation)
- **Why it doesn't show up on Microsoft's own infrastructure** (the
  reason this is invisible to MS internally)
- **The smallest, cheapest upstream change** that would resolve it
- **Why fixing it benefits more than just bc-linux** (the framing
  Microsoft will care about)

When extracting into a formal report, prioritize findings by:

1. Cost of the upstream fix (smaller = easier sell)
2. Number of patches/workarounds it would let bc-linux delete
3. Whether it benefits non-bc-linux partners (Linux is one constituency;
   reproducible builds, deterministic CI, ARM, and lighter Windows
   sandboxes are others)

---

## Finding 1: AL→C# emitter is environment-sensitive, defeats the R2R pass-through optimization

### Severity

**High.** This is the single biggest cold-boot performance cost on Linux
BC and would be ~80–100s of NST startup time recovered per cold boot if
fixed. On a 2-vCPU GitHub Actions runner the saving is closer to 3–4
minutes per CI run because the 5 Base App chunks serialize on parallel
JIT contention.

### What we observed

bc-linux's entrypoint pre-seeds the NST assembly cache from the .app's
`publishedartifacts/` folder before NST starts. This works flawlessly
for **System Application** — the shipped R2R DLLs are byte-identical to
what NST writes, and NST happily picks them up via its fast pass-through
path. Local cold-boot drops by ~30s for that app alone.

It does **not** work for **Base Application**. NST recompiles all 5
Base App chunks on every cold boot, taking ~130 seconds total (~80s of
wall time on multi-core, more on the 2-vCPU GH runner). The "Compiling
the application object assembly" log line fires 5 times per cold boot.

### What's actually going on (decompiled NST source)

The decision happens in `NavAppPackageLoader.ValidatePublishedArtifacts`
inside `Microsoft.Dynamics.Nav.Ncl.dll`. Roughly:

```csharp
flag2 = await ValidatePublishedArtifacts();
if (flag2)
    flag = !(await CopyPublishedArtifacts());     // fast path: System App
else
    flag = true;
...
if (flag)
    generatedAssemblyFilePaths = await DirectFillBlobCacheAsync(...);  // slow path: Base App
```

`ValidatePublishedArtifacts` (with `ClrRetrieverKind = OneApplicationMultipleAssemblies`,
which is the active setting) calls `ValidateOneApplicationMultipleAssemblyScenario`,
which:

1. Re-runs the AL→C# emitter against the .app's AL source
2. Computes SHA-256 of every per-AL-object emitted C# source file
3. Looks each hash up in the proof tree shipped in the .app's
   `Merkle.json` (`SimpleMerkleTree.IsValidInput`)
4. If **every** hash is found as a leaf → fast path (`CopyPublishedArtifacts`)
5. If **any** hash misses → slow path (`DirectFillBlobCacheAsync`,
   full Roslyn C# compile + JIT-time R2R regeneration)

The validator emits trace tag `0000POS` ("the AL compiler could not
generate a corresponding c# hash") on the first miss, then returns false.

### Why Base App fails the gate but System App passes

It's not a whitelist, not a size threshold, not signing, not a flag
in the manifest. **It's a content-hash equality check on the AL
emitter's output.** System App ships with 1 Merkle root and a small
number of leaves; Base App ships 5 roots and thousands of leaves. The
validator requires **all** leaves to match across all 5 chunks — a
single drifting symbol in any chunk dooms the whole app.

The drift sources are well-known to anyone who has tried to make a
.NET emitter strictly deterministic across environments:

- Reference assembly metadata differences (parameter names, attribute
  ordering, added members between .NET 8 patch versions)
- `Dictionary<,>` enumeration order between Linux and Windows .NET 8
- Default culture (Linux: `en_US.UTF-8`; Windows: `en-US`)
- File system enumeration order (case sensitivity, modification time
  ordering)
- ServerUserSettings codegen toggles like `DisableAsyncCodeGeneration`
  and `EnableInlinedMethodCodeGeneration` (read at emit time)

Any one of these can shift one byte of the emitted C# text, which
shifts the SHA-256, which fails the Merkle leaf lookup, which forces
the entire app down the slow recompile path.

### Empirical evidence (from 2026-04-08 investigation)

We ruled out bc-linux's own Cecil patches as the cause via a clean
bisect:

| Run | Cecil patches | NST startup | Base App compiles | Time per chunk |
|---|---|---|---|---|
| C | All enabled (production) | 100s | 5 | 41+66+67+72+74s |
| D | All disabled (BC_DISABLE_PATCHES=14,15,15a,15b,checkfile) | 106s | 5 | 43+66+67+73+74s |

Within noise. Patches #14, #15, #15a, #15b, and the Mono.Cecil
CheckFileName patch are NOT the source of the drift — they affect a
different code path (post-NST AL extension publishing via the dev
endpoint, where they remain essential — disabling them produces
`AL0185 DotNet 'GenericDictionary2' is missing` errors).

The NST cold-startup compile path uses the merged reference assemblies
in `Add-Ins/` directly and does not go through the patched Cecil
resolvers. So whatever is causing the drift, it's not us.

We also confirmed empirically that the .app ships valid R2R DLLs:

- `Microsoft_Base Application_27.5.46862.48612.app` contains 5 R2R
  DLLs under `publishedartifacts/file:///S:/.../Ready2RunApps/W1/...`
- The DLL filenames match exactly the hashes that show up in the
  "Compiling the application object assembly" log lines
- The byte content differs from what NST writes by ~10,272 bytes
  (consistently across all 5 chunks): NST strips the Authenticode
  SECURITY directory and re-stamps several PE header fields
- Within `.text`, ~78% of bytes differ across 1,620 separate diff
  clusters → genuine recompilation, not a metadata rewrite pass
- This means: even if we transformed the bytes ourselves to match
  NST's output, we couldn't, because the differences are in the
  R2R native code, which depends on whatever crossgen2 invocation
  NST uses internally

### Why this is invisible from Microsoft's own infrastructure

Microsoft's BC build server runs on Windows. During artifact build,
it runs NST once, populates the assembly cache during that run, and
the cache files persist on the build server's filesystem across
subsequent containers built from the same artifact. **The cold-cache
+ environment-drift path is never hit on Microsoft's own pipeline.**

When a Windows BC container starts cold from a fresh artifact, it
hits the same code path we do — but Windows BC containers tend to be
long-lived and the warm cache survives across restarts, so the
first-boot tax is paid once and forgotten. In a CI environment
(Linux or Windows) where containers are ephemeral and the cache is
in container rootfs (not a volume), the tax is paid on every run.

This is the kind of cost that's invisible from inside Redmond but
significant for everyone running BC in containers. The community pays
hours of runner time per week to recompile assemblies that Microsoft
has already pre-compiled.

### What Microsoft could do (in order of decreasing effort)

**Option 1 (smallest fix, biggest impact): ship the emitted C# source
files inside the .app's `publishedartifacts/` folder, alongside the
R2R DLLs.** Then `ValidatePublishedArtifacts` doesn't need to re-emit
and re-hash at all — it can verify the shipped C# directly against
the Merkle tree, which is byte-stable by construction. The validation
gate becomes "is the file present?" instead of "did my re-emission
produce identical bytes?". Estimated effort: a build pipeline change
on Microsoft's side, no NST code changes. Eliminates the entire
problem class.

**Option 2: change `ValidatePublishedArtifacts` to hash AL bytecode
(the input) instead of C# source (the output).** AL bytecode is in
the .app already, doesn't depend on the emitter, and is byte-stable
across environments. The pre-compiled R2R DLLs are still
deterministic relative to the input AL bytecode under any specific
NST/compiler version, so hashing the input is just as strong a
validation as hashing the output. Estimated effort: a small change
in `ValidateOneApplicationMultipleAssemblyScenario`. Same effect as
Option 1 with slightly more code change.

**Option 3 (most general, most work): make the AL→C# emitter
strictly deterministic.** Audit every code path to enforce sorted
iteration, `InvariantCulture` formatting, no FS-order dependence,
no reference-assembly metadata leakage into emitted text. Hardest
to do retroactively because the determinism contract has to be
preserved against every future emitter change.

### Why this benefits more than just bc-linux

- Any partner running BC in containerized CI (Linux or Windows)
  pays this cost on every cold boot. Faster CI feedback loops for
  every BC ISV.
- Reproducible builds: a deterministic emitter makes BC artifact
  outputs hashable for supply chain attestation (Sigstore, in-toto).
- ARM Windows BC containers (if Microsoft pursues that): same
  drift problem, same fix.
- Bringing BC into developer environments where developers want to
  iterate quickly without per-restart compilation tax.

### Status from bc-linux's side

**Our pre-seed code is correct and forward-compatible.** It already
walks `publishedartifacts/`, extracts to both possible NST cache
paths (we have a known instance-name quirk where NST ignores
`ServerInstance` config and uses a literal default name), and works
flawlessly for any app whose pre-compiled R2R DLLs match what NST
would have produced. **The moment Microsoft fixes the emitter or
ships the C# source, we automatically get the speedup** with no code
changes on our side. We are not blocking ourselves.

In the meantime, bc-linux ships either:
- (a) a workaround that bakes a warm cache into the bc-runner Docker
  image at build-image.yml time, OR
- (b) a StartupHook patch that overrides `UserCodeHash` values to
  bypass the validation gate (medium-risk; needs full BCApps test
  suite validation to confirm the loaded R2R DLL semantics match
  the AL model)

Both are workarounds. The upstream fix removes the need for either.

---

## Finding 2: Add a developer/diagnostic NST mode with feature flags to opt out of Windows-specific subsystems

### Severity

**Medium-high.** Roughly half of bc-linux's `StartupHook.cs` patches
exist to no-op or stub out NST subsystems that have a hard Windows
dependency. These patches are runtime JMP hooks against Microsoft
methods — they break on every NST internal refactor and have to be
re-fixed against each new BC version. If Microsoft exposed
config/env-var flags to disable these subsystems at startup, the
patches could be deleted entirely.

### The general ask

**Ship a "developer" or "non-production" NST configuration mode
where Windows-tied subsystems can be disabled via clean
configuration flags, without requiring binary patching of Microsoft
code.**

The flags would each correspond to one Windows-specific subsystem
that has no Linux equivalent and is not strictly required for AL
test execution, AL extension publishing, or basic OData/API access.
The flags should be safe to enable for sandbox/dev/CI scenarios
and would never be set in production Windows BC deployments.

### Concrete patches that could become flags

| bc-linux patch | What it does | Proposed flag |
|---|---|---|
| #1 (CustomTranslationResolver) | Stops `WindowsIdentity.GetCurrent()` recursion in satellite assembly resolution | `DisableWindowsIdentityFallbacks=true` |
| #2 (NavEnvironment) | Same: bypasses `WindowsIdentity.GetCurrent()` in static initializer | Same flag |
| #4 (EventLogWriter) | No-ops `NavEventLogEntryWriter.WriteEntry` so Windows EventLog calls don't crash | `DisableWindowsEventLog=true` (or "use stdout") |
| #5 (ETW/OpenTelemetry) | No-ops Geneva ETW exporter and `EtwTelemetryLog` ctor | `DisableGenevaETW=true` (or "use console exporter") |
| #13 (Watson) | No-ops Watson crash reporting | `DisableWatsonReporting=true` |
| #19 (CustomReportingServiceClient) | Replaces gRPC client with no-op proxy so the watchdog stops flooding the log | `DisableReportingServiceWatchdog=true` |
| #20 (SideServiceWatchdog) | No-ops `SideServiceProcessClient.EnsureAlive` so it stops trying to start the Windows PE Reporting Service | Same flag |
| #21 (NavOpenTaskPageAction.ShowForm) | No-ops `ShowForm` so headless test sessions don't crash on task page opens | `DisableUIRendering=true` (already implied by headless mode but currently not enforced) |
| #22 (AzureADGraphQuery..ctor) | No-ops the constructor that pulls in MSAL Windows credential APIs | `DisableAzureADGraphIntegration=true` |
| #23 (OpenXml WordDocPictureMerger) | Fixes a recursion bug in Microsoft's Word merger | (Not a flag — this is a real bug in shipped code, would just want it fixed) |
| #16b (NavUser.TryAuthenticate) | Bypasses password hash verification for the bc-linux service user | `AllowPasswordlessSandboxAuth=true` (only valid in sandbox mode) |

The Cecil-related patches (#14, #15, #15a, #15b, CheckFileName) are
trickier — they fix Cecil bugs that Microsoft would have to fix in
the type loader itself rather than expose as flags. Worth mentioning
in the report but they're "please fix this bug" rather than "please
add an opt-out".

### Why this is the right ask

- **Smaller ongoing maintenance burden for bc-linux** — fewer
  patches = fewer breakages on each new BC release
- **Lower runtime risk** — config flags are more stable than JMP
  hooks against private methods
- **Better diagnostic story for Microsoft** — when a customer
  reports an issue with a Windows-specific subsystem, Microsoft
  can ask "did you have `DisableX=true` set?" and have a clean
  isolation answer
- **Faster cold boot** — many of these subsystems do startup work
  that's wasted on Linux (Geneva ETW init, Reporting Service
  probing, AzureAD Graph init). Disabling them via flags lets BC
  skip the work entirely instead of running it and catching the
  exception
- **Already standard pattern in modern .NET hosts** —
  `Microsoft.Extensions.Hosting` services routinely have
  `Disable*` flags to skip optional subsystems. Adding them to
  NST is an idiomatic .NET 8 thing to do, not a bespoke ask

### Why this is invisible from Microsoft's own infrastructure

Microsoft runs NST in production Windows environments where all
these subsystems are present, working, and expected. There is no
internal use case at Microsoft where someone wants to start NST
without Windows EventLog. The need only arises when running NST
outside its native environment (Linux, sandboxed Windows, ARM,
container CI, ...).

### Why this benefits more than just bc-linux

- Sandboxed Windows BC containers (which Microsoft does ship)
  could use these flags to skip subsystems that are pointless in
  a sandbox (Geneva ETW telemetry, Watson, AzureAD Graph) and
  start faster
- Customers running BC in air-gapped environments can disable the
  outbound telemetry subsystems cleanly
- Test/staging environments where Reporting Service isn't
  configured can opt out instead of seeing the watchdog flood
  the event log

---

## Finding 3: Fix `Microsoft.Dynamics.Nav.OpenXml.OfficeWordDocumentPictureMerger.ReplaceMissingImageWithTransparentImage` recursion bug

### Severity

**Low** (one-off bug, not a category) but it's a clear "Microsoft
bug, please fix" item that's easy to include.

### What we observed

In bc-linux Patch #23, there's a real recursion bug in Microsoft's
Word report image merger. When a Word document references a missing
image, `ReplaceMissingImageWithTransparentImage` calls back into
`MergePictureElements` with the transparent placeholder, which
re-enters `ReplaceMissingImageWithTransparentImage` unconditionally
→ unbounded recursion → stack overflow → fatal session crash → BC
container becomes unhealthy.

Triggered by `TestSendToEMailAndPDFVendor` in Tests-Misc; was the
blocker for completing a full sequential Bucket 4 test run. See
`KNOWN-LIMITATIONS.md` and `StartupHook.cs` Patch #23 for the
working around.

### The fix

In `ReplaceMissingImageWithTransparentImage`, do not call
`MergePictureElements` recursively for the placeholder. Either
inline the placeholder substitution or guard the recursion with
a `wasReplaced` flag.

This is OS-independent — the bug exists on Windows BC too, it
just happens to be reproducible by the same `TestSendToEMailAndPDFVendor`
test on every platform.

---

## Findings to add later (placeholder)

Track new findings here as they come up. Each should follow the
same template: severity, observation, why-it's-invisible-internally,
upstream fix, broader benefit.

- [ ] (placeholder for future findings)

---

## Investigation log

- **2026-04-08 (early)** — R2R drift investigation. Cecil patches ruled
  out as cause via empirical bisect (Run C vs Run D, identical Base App
  compile times). Decompiled `NavAppPackageLoader.ValidatePublishedArtifacts`
  in `Microsoft.Dynamics.Nav.Ncl.dll` and identified
  `ValidateOneApplicationMultipleAssemblyScenario` as the validation
  gate. Confirmed via byte comparison that System App pre-seed is
  byte-identical (works) and Base App is not (fails). Documented
  in Finding 1 above.

- **2026-04-08 (later)** — Tier 1 + Tier 2 optimization sweep (no
  caching, no Bypass B). Local cold-boot baseline 160s wall, 105s NST,
  140s entrypoint. Targeted the install-for-tenant tail and the
  healthcheck poll slack.

  Wins:
    - Healthcheck interval 15s → 5s in docker-compose.yml: -10s wall
      (slack between Ready-for-extensions and docker reporting healthy
      collapsed from 20s → 10s). Reliable, no behavioral change.
    - SchemaUpdateMode forcesync → synchronize for install-for-tenant
      POSTs: neutral wall-clock but strictly safer (only sync when
      needed).

  Negative results worth not retrying:
    - Client-side parallel POSTs within dependency layers: NST's dev
      endpoint serializes publishes server-side. Layered concurrency=4
      took the same ~27s as serial. Documented in entrypoint.sh comment
      so future investigators don't waste time.
    - "Skip-if-already-installed" precheck for install-for-tenant: no
      effect in our config because the stuck-publish wipe always runs
      first, leaving 0 keep-set apps tenant-installed at the precheck
      point.
    - Cecil patch bisect (Run D, BC_DISABLE_PATCHES=14,15,15a,15b,checkfile):
      Base App compile times identical with patches off. Cecil patches
      are NOT the source of the AL→C# emission drift that defeats the
      R2R pass-through gate. They affect a different code path (post-NST
      AL extension publishing via dev endpoint, where they remain
      essential — disabling them produces AL0185 errors).
    - .NET runtime tuning experiments (DOTNET_ReadyToRun=1, DOTNET_GCRetainVM=1):
      both individually made things ~5s slower, not faster. Not committed.
      Other tuning vars (GCConserveMemory, GCHeapCount, GCNoAffinitize)
      remain available via the docker-compose.yml passthroughs added
      today, but not yet tested.

  **Cold boot dotnet-trace profile (T2a)** — captured 197 MB nettrace
  via `DOTNET_DiagnosticPorts=...,suspend` so the trace covers from t=0.
  Top inclusive-time consumers across all threads:
    - 5564.77s `UNMANAGED_CODE_TIME` — mostly worker thread idle waiting
      for work, plus SQL/file I/O during DB restore. This is the ~92%
      of 6027s total cumulative thread-time.
    - 463.10s `CPU_TIME` — actual CPU work, the other 8%.
    - 1475s inclusive `Microsoft.CodeAnalysis.CSharp.MethodCompiler.CompileNamespace`
      and ~400s in `AssembliesClrTypeRetriever.PopulateGeneratedAssemblyCacheAsyncImpl`
      and `CSharpCompiler.CompileCSharpFilesAsync` — the Base App Roslyn
      compile path. Dominates everything else by a wide margin.
    - 165s in `SimpleMerkleTree.IsValid` + `SHA256.HashData` — the
      validation gate from Finding 1 actually shows up as a measurable
      CPU consumer (~3% of total). All of this would disappear if
      Microsoft fixed the upstream emitter or shipped the C# source
      with the .app.

  **Critically: ZERO time attributed to Watson, Geneva ETW, EventLog
  WriteEntry, Reporting Service, AzureAD Graph, ShowForm, OpenTelemetry,
  or any of the other Windows-specific subsystems we patch.** The
  existing StartupHook patches are extremely effective. T2b ("skip more
  subsystem init") found nothing actionable to add.

  Implication for the formal Microsoft report: Finding 2's request for
  feature flags would still be valuable for *maintenance burden*
  (patches break on each NST refactor), but it would NOT yield
  meaningful wall-clock improvements on cold boot — the patches we
  already have are already saving the wall time. The lever for further
  cold-boot improvement is exclusively Finding 1 (the AL→C# emitter
  drift), and it's a single bottleneck that swamps everything else.

  Cumulative improvement: **160s → 150s** (-10s, all from T1c). Not
  the dramatic improvement initially hoped for, but the *negative*
  results from this sweep are themselves a strong signal: the cold
  boot is essentially as fast as it can be without addressing the
  R2R/emitter drift upstream or accepting Bypass B's risk.

  Profiling infrastructure committed in the same session (BC_PROFILE_NST
  env var + docker-compose passthrough) so the profile can be reproduced
  by anyone with one env var.

  Raw measurement data at `.snapshots/r2r-investigation-2026-04-08/`
  (gitignored) and the full cold-boot trace at
  `/tmp/bc-linux-measurement/profile/full-cold.speedscope.json` (local
  only).
