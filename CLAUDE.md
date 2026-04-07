# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

This project runs the **Microsoft Dynamics 365 Business Central service tier on Linux** by patching it at runtime. BC's NST is a .NET 8 application that Microsoft only ships for Windows; we make it run unmodified on Linux via a `DOTNET_STARTUP_HOOKS` assembly that intercepts Win32 P/Invokes, stubs Windows-only services, and rewrites a handful of methods that hard-depend on Windows. SQL Server runs in a separate Linux container (`mssql/server:2022`).

The code here is **not a fork of BC**. We never recompile Microsoft assemblies — the BC service tier DLLs are downloaded fresh from Microsoft artifact storage at container start, then the startup hook patches them in memory (with a few binary patches written to disk for assemblies the JIT can't reach, e.g. `CodeAnalysis.dll`, `Mono.Cecil.dll`).

## Build, run, test

```bash
# Build + start (first boot ~5–10 min: artifact download + DB restore + extension compile)
docker compose up -d --wait

# Override version/country/type
BC_VERSION=28.0 BC_COUNTRY=de docker compose up -d --wait

# Rebuild image after editing src/StartupHook or src/stubs
docker compose build bc

# Logs (BC writes everything to stderr — entrypoint redirects 1>&2 for unbuffered output)
docker compose logs -f bc

# Tear down (keep artifact cache)
docker compose down
# Tear down + wipe cached artifacts and service dir
docker compose down -v
```

Multiple parallel instances: use `-p <project>` with a unique port offset for every published port (see README.md "Running Multiple Instances"). Forgetting one port causes a bind conflict.

### Running AL tests

```bash
# Publish a test app then run a codeunit range (per-method results require --app)
./scripts/run-tests.sh --app MyTestApp.app --codeunit-range 50000..50100

# Single codeunit
./scripts/run-tests.sh --app MyTestApp.app --codeunit-range 50000
```

`run-tests.sh` is a hybrid OData (suite population + result reading) + WebSocket (test execution via a real client session) flow. The WebSocket step is required because TestPage support needs a `serviceConnection`-style session, which OData can't provide. The test runner extension is in `extensions/TestRunnerExtension/` (AL source under `src/`); the prebuilt `.app` is baked into the image and republished automatically on container start.

### Editing the startup hook

The hook is a normal .NET 8 class library:

```bash
cd src/StartupHook && dotnet build -c Release
# Then rebuild the image — nothing on the host runs the hook directly
docker compose build bc && docker compose up -d --wait
```

`kernel32_stubs.c` is compiled to `libwin32_stubs.so` inside the image (gcc is installed in the builder stage). If you add a new exported symbol, also wire it into the `NativeLibrary.SetDllImportResolver` registration in `StartupHook.cs` (Patch #3).

## Architecture

### Layers

1. **`docker-compose.yml`** — two services (`sql`, `bc`). SQL uses a tmpfs for `/var/opt/mssql/data` (4 GB) so first-boot DB restore is fast; the cost is that DB state is wiped on container restart. The `bc` service depends on `sql` being healthy and exposes the dev/OData/API/SOAP/client ports (7045–7089 range).

2. **`src/Dockerfile`** — multi-stage. Builder publishes `StartupHook`, the various stubs (`DrawingStub`, `GenevaStub`, `HttpSysStub`, `PerfCounterStub`, `WindowsPrincipalStub`), and the helper tools (`MergeNetstandard`, `PatchNclTestPage`); also copies the .NET 8 reference assemblies out of the SDK into `/bc/refasm/` (Cecil needs them for type-forward resolution). Runtime stage installs `mssql-tools18` and sets `DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll`.

3. **`scripts/entrypoint.sh`** — the long-running orchestration. Steps:
   - **Step 1**: Download BC artifacts (or wait for them if `BC_ARTIFACT_URL=skip`). Cached in the `bc-artifacts` volume.
   - **Step 2**: Copy service tier into `/bc/service/`, replace the Windows Reporting Service PE binary with our Linux .NET stub (`stubs/reporting-service-stub`), symlink `kernel32.dll`/`user32.dll`/etc. → `libwin32_stubs.so`.
   - **Step 2b**: Apply on-disk binary patches (`CodeAnalysis.dll`, `Mono.Cecil.dll`, `Nav.Ncl.dll` `Assembly.Load`→`LoadFrom`, `TestPageClient.dll` Async fix), copy refasm DLLs, rename `Add-ins` → `Add-Ins` (case-sensitivity fix).
   - **Step 3**: Wait for SQL, restore the demo DB, create BC SQL login.
   - **Step 4**: Start BC, publish the TestRunnerExtension and any apps in `BC_TEST_APPS`, then write `/tmp/bc-ready` (which the healthcheck looks for) and `wait`.

   The script `exec 1>&2`s on entry — stdout is pipe-buffered when PID 1 has no TTY, so all logging goes to stderr. Restart recovery: `.bak` files left by Patch #15 (which renames runtime DLLs after BC has loaded them) are restored at the very top so the next boot finds the real DLLs.

4. **`src/StartupHook/StartupHook.cs`** — single file, ~2,500 lines, numbered patches. Each patch fixes one specific way BC trips on Linux. Read the file header for the canonical list; the high-impact ones:
   - **#3**: `NativeLibrary.SetDllImportResolver` redirects every `kernel32`/`user32`/`advapi32` P/Invoke to `libwin32_stubs.so`. **JMP hooks only work on JIT-compiled BC methods** — BCL methods are ReadyToRun precompiled and cannot be patched this way; that's why some patches are binary edits to disk instead.
   - **#1, #2, #4, #5, #13**: kill Windows-identity / event-log / ETW / Watson code paths that throw `PlatformNotSupportedException` and crash boot.
   - **#14, #15, #15a/b**: server-side AL compiler (Cecil) — strip the Windows .NET runtime probing path and fix type-forward resolution so AL extensions actually compile.
   - **#19, #20**: Reporting Service. The Windows PE binary is replaced with `stubs/reporting-service-stub` (Linux .NET), and `CustomReportingServiceClient` is swapped for a no-op so the watchdog stops flooding the log.
   - **#21**: `NavOpenTaskPageAction.ShowForm` no-op — without it, a single test that opens a task page kills the entire test session.

5. **`extensions/TestRunnerExtension/`** — AL extension (`src/*.al`) exposing the OData/WebSocket pages used by `run-tests.sh`. The compiled `.app` lives in the same dir and is copied into the image at build time (`extensions/TestRunnerExtension/TestRunnerExtension.app` → `/bc/testrunner/TestRunner.app`).

6. **`tools/TestRunner/`** (host-side) and **`src/tools/{MergeNetstandard,PatchNclTestPage}/`** (image-side) — small .NET helpers. `MergeNetstandard` merges netstandard type-forwarding assemblies for Cecil; `PatchNclTestPage` is the disk-side counterpart to a few of the in-memory hook patches.

### Key invariants worth remembering

- **Do NOT enable Server GC** (`DOTNET_gcServer`). It breaks the BC API endpoint. The `docker-compose.yml` comment says so explicitly; the `DOTNET_TieredCompilation` / `DOTNET_TC_QuickJitForLoops` env vars are exposed for tuning but server GC is off-limits.
- **`/bc/service` is a volume**: edits to BC DLLs persist across container restarts, and the entrypoint guards `Step 2` / `Step 2b` with `[ -f ... ]` checks. To force re-patching, `docker compose down -v` (or delete the `bc-service` volume).
- **`Add-ins` vs `Add-Ins`**: Linux is case-sensitive. The entrypoint renames the directory; never refer to the lowercase form in new patches.
- **Patches that depend on assembly load order** (e.g. #18 `SetupSideServices` must run before `Main()` calls it) live in `StartupHook.Initialize()`, not in the per-assembly load callback. Adding a new patch in the wrong place will silently fail because the type isn't loaded yet — or, worse, succeed once and then break on the next BC update because load order shifted.

### Known limitations (see `KNOWN-LIMITATIONS.md`)

- ~142 test failures from "User cannot be deleted because logged on" — Microsoft test cleanup deletes the session user; only fixable by patching the platform "user is logged on" check.
- ~29+ failures from `NSClientCallback.CreateDotNetHandle` NullRef on tests that need a UI session (Camera, Barcode, etc.).
- Bucket 4 sequential test run previously crashed the container after Tests-Misc due to infinite recursion in Microsoft's `OfficeWordDocumentPictureMerger.ReplaceMissingImageWithTransparentImage` (stack overflow in `Nav.OpenXml`, triggered by `TestSendToEMailAndPDFVendor`). **Fixed by Patch #23** — `ReplaceMissingImageWithTransparentImage` is no-op'd via JMP hook so missing images are left in place and the session survives.

When adding a new patch, append it to the numbered list in the `StartupHook.cs` header comment AND `KNOWN-LIMITATIONS.md` if it closes a known failure mode.

## Extension publish/install architecture (consumer-driven, no hand-curated lists)

Significantly hardened during the bc-copilot-blueprint bring-up session in
2026-04. Anyone touching the entrypoint's app management code, the
selective filter, or `resolve-keep-app-ids.py` should read this whole
section first.

### The four shared scripts

| Script | What it does | Used from |
|---|---|---|
| `scripts/_bcapp.py` | Shared helper for reading BC `.app` package files. Parses `NavxManifest.xml`, supports R2R packages and the rare `app.json` fallback. Indexes a whole artifact tree by app id and keeps the highest version per id. | `stage-symbols.py`, the entrypoint's stuck-publish topo sort, any future script that needs to walk artifact manifests |
| `scripts/stage-symbols.py` | Manifest-driven `.alpackages` staging. Walks an artifact tree, indexes every `.app` by id, then copies into the output dir exactly the symbols needed: System.app + Application umbrella + the consumer's transitive dependency closure. Replaces the older glob-based staging that silently missed apps when Microsoft moved files between BC versions. | `bc-test-from-source.yml`, `bc-copilot-blueprint`'s `copilot-setup-steps.yml` |
| `scripts/publish-app.sh` | Sourceable shared helper exposing `bc_publish_app <path> [dev_url] [auth]`. Reads the response body and only treats 422 as success when it actually says "already" (catches missing-dependency / schema-sync / version-conflict failures that the previous duplicated inline `publish_app` functions silently swallowed). | `run-tests.sh`, all three workflows, `bc-copilot-blueprint`'s `iterate.sh` |
| `scripts/wait-for-bc-healthy.sh` | Single canonical "block until docker healthcheck reports `healthy`" loop with progress lines every 60s. Replaces 4 previously-inlined copies across the workflows. | All three workflows, `iterate.sh` (could migrate, currently has its own variant) |

### How extensions get installed for tenant

This is the most important part — and the part that wasted the most time
during the bring-up debugging session. The workflow → entrypoint contract:

1. **Workflow** (`bc-test-from-source.yml` / `bc-test-prebuilt.yml`):
   `resolve-keep-app-ids.py` walks the consumer's `app.json` files
   AND `extensions/TestRunnerExtension/app.json` (so the bc-linux test
   runner extension's own deps are part of the closure), produces a
   GUID list, exports it as `BC_KEEP_APP_IDS`, sets
   `BC_CLEAR_ALL_APPS=selective`. **No hand-curated test framework
   exclusion** — the closure includes whatever the consumer transitively
   needs, including test framework helpers.

2. **Entrypoint, pre-NST**:
   - **Selective filter** (`scripts/entrypoint.sh:391-453`) wipes
     everything in `[Published Application]` not in the keep set.
   - **Stuck-publish wipe** (immediately after): runs a SQL query
     against `[Published Application]` joined with `[NAV App Installed App]`
     and discovers any apps that are PUBLISHED but NOT INSTALLED for any
     tenant. These are the apps that ship in BC's sandbox image as
     "Global, not installed for default tenant" — historically the 5
     core test framework apps (Test Runner, Library Assert, Library
     Variable Storage, Permissions Mock, Any), but the discovery is
     dynamic so a future BC version with a different stuck set just
     works. Wipes them from `[Published Application]` so the
     install-for-tenant pass below can re-POST them cleanly.

3. **Entrypoint, post-NST** (after the dev endpoint is responsive):
   - **Install-for-tenant loop**: iterates `BC_KEEP_APP_IDS` (skipping
     the 5 application stack baseline IDs which BC always installs for
     tenant by default), topologically sorts via `_bcapp.py` so deps
     install before dependents, and POSTs each `.app` to the dev
     endpoint with `?SchemaUpdateMode=forcesync`. This both publishes
     AND installs-for-tenant in one call.
   - **Custom Test Runner Extension publish** (`/bc/testrunner/TestRunner.app`):
     this is bc-linux's own extension and isn't in any artifact, so it's
     baked into the image and POSTed by the entrypoint as a separate
     step. It depends on Microsoft Test Runner, which is in the keep
     set because TestRunnerExtension's app.json is walked by the
     workflow's resolve step (see #1).

4. **Workflow's publish step** (`Publish AL apps to BC`): consumer's
   prod and test apps publish via `bc_publish_app`. All deps are
   already installed-for-tenant by the entrypoint's pass above, so
   publishes succeed first try.

### Things you need to know about BC's dev endpoint

- **`SchemaUpdateMode=forcesync` does both publish AND install-for-tenant**
  IF the app is not already published. Otherwise it returns
  `422 "The extension could not be deployed because it is already
  deployed as a global application or a per tenant application."`
- **`DependencyPublishingOption=Install` is NOT a valid value.**
  Only `Default` / `Strict` / `Ignore` are accepted. The dev endpoint
  cannot promote a Global publish to a tenant install — that's why
  the entrypoint's stuck-publish wipe step exists.
- **A 422 response can mean any of**: "already installed at this
  version" (benign), "already deployed as Global" (benign in our
  context), "missing dependency" (real error, look for `AL1024`),
  "schema sync failure" (real error). `bc_publish_app` reads the
  body to distinguish these.
- **`Published Application` ≠ `[NAV App Installed App]`.** An app
  can be in `[Published Application]` (and visible in the global
  app list) without being installed for any tenant. The keep set
  preserves the published row but doesn't change the installed-for-
  tenant state — that's what the install-for-tenant POST loop is for.

### Workflow reliability invariants

These are the silent-failure modes the bring-up debugging session
hardened. Don't undo them without understanding why they exist.

- **Every `publish_app` call must read the response body before
  treating 422 as success.** The pre-2026 inline versions in
  `bc-test-from-source.yml` and `bc-test-prebuilt.yml` silently
  swallowed missing-dependency 422s and let downstream test runs
  produce "0 total, 0 passed, 0 failed" with no clue. They now
  source `scripts/publish-app.sh`.
- **`./scripts/run-tests.sh ... | tee` hides the real exit code.**
  Both workflows now capture `${PIPESTATUS[0]}` and propagate it.
- **`TESTS_TOTAL == 0` is a hard failure.** Both workflows now
  fail explicitly when no tests ran, instead of accepting empty
  results as success.
- **`run-tests.sh` always passes `--verbose` to TestRunner.dll.**
  Without it, every `Log()` call inside TestRunner is silent and
  failures look like "exit 1 with no diagnostic." See the comment
  block at the docker-exec invocation for why.
- **`verify_suite_populated` must filter by `lineType eq 'Function'`,
  not just count any row.** setupSuite inserts a Codeunit-type stub
  row even when the test app's metadata isn't loaded; only Function
  rows prove that real `[Test]` procedures are enumerable.
- **`build-image.yml`'s `IMAGE_NAME` MUST match what consumers pull**
  (`stefanmaron/msdyn365bc.on.linux/bc-runner`). In an earlier state
  it was `stefanmaron/bc-runner` and every entrypoint fix went into
  an image namespace nobody used. The mismatch was invisible for
  weeks. Don't move this without auditing every consumer's
  `runner_image` default.

## Relationship to `PipelinePerformanceComparison`

The sibling repo `../PipelinePerformanceComparison` is the **primary consumer** of this project and the reason most of the recent patches exist. It is *not* a dependency of bc-linux — the relationship goes the other way:

- **bc-linux** is the runtime platform: it produces the `bc-runner` Docker image and the `run-tests.sh` driver.
- **PipelinePerformanceComparison** uses that image to run **real Microsoft test suites** (BCApps System Application, Base Application, ERM, SCM, Misc, Workflow, SCM-Service, SINGLESERVER — the "Bucket 4" set) on Linux, then compares pipeline timings against Windows BC containers and Windows compile-only runs. Its goal is to make a business case to Microsoft for native Linux BC support.

What this means in practice when working in bc-linux:

- **Most of the "real workload" feedback comes from that repo's benchmark scripts** (`PipelinePerformanceComparison/scripts/benchmark-bucket4.sh`, `benchmark-erm-scm.sh`, `diag-*.sh`). When a patch in `StartupHook.cs` is added or changed, the validation that matters is "does the BCApps / Base App test sweep still pass at the same rate?", run from there.
- **Test results, crash logs, and benchmark output live under `PipelinePerformanceComparison/benchmark-results/`** (e.g. the Bucket 4 Word-merger crash referenced in `KNOWN-LIMITATIONS.md` was captured in `benchmark-results/local-20260404/bucket4-local-full.log`). When investigating a regression, look there before re-running anything locally.
- **The test runner architecture (OData setup + WebSocket execution + OData result read) was driven by what BCApps needed** — TestPage support, real client sessions, callback protocol. Patches #17–#22 in `StartupHook.cs` and the `Nav.Ncl` / `Nav.Types` / `TestPageClient` binary patches in `entrypoint.sh` exist specifically to make Microsoft's stock test apps run unmodified. See `PipelinePerformanceComparison/LINUX-BC-STRATEGY.md` for the canonical history.
- **The `BASE-APP-TEST-HOWTO.md` over there is the recipe** for publishing the System Application Test Library, Base App tests, etc. against a bc-linux container — useful when reproducing a Microsoft-test-only failure that doesn't show up with a custom test app.

If you change behavior here that could plausibly affect test execution (anything touching the test runner extension, the WebSocket session lifecycle, the Cecil/AL compiler patches, or anything that runs during test method execution), check whether the corresponding benchmark scripts in PipelinePerformanceComparison need a re-run, and update the relevant report there if results shift.

## Relationship to `bc-copilot-blueprint`

A second downstream consumer, [`StefanMaron/MsDyn365Bc.Copilot.OnLinux`](https://github.com/StefanMaron/MsDyn365Bc.Copilot.OnLinux),
uses bc-linux's reusable workflow + the bc-runner image to give the
GitHub Copilot Coding Agent a working Business Central environment.
The blueprint is a thin layer (one example AL app, one example test
app, an `iterate.sh` script, and a `copilot-setup-steps.yml` workflow)
on top of this project.

The 2026-04-07 bring-up debugging session for that blueprint produced
most of the hardening described in the "Extension publish/install
architecture" section above. If you change anything in `entrypoint.sh`'s
app management code, `resolve-keep-app-ids.py`, the workflow
`publish_app` loops, or `run-tests.sh`'s setupSuite/verify logic,
**also run a blueprint CI dispatch**
(`gh workflow run bc-test.yml --repo StefanMaron/MsDyn365Bc.Copilot.OnLinux --ref main`)
to make sure the consumer-side path still works end-to-end.

## CI

The bc-linux project ships **three** reusable workflows in
`.github/workflows/`, all driven by the same shared scripts:

- **`bc-test-from-source.yml`** — the canonical reusable workflow.
  Compiles AL source from a calling repo, publishes it to a BC
  Linux container, and runs the tests. Used by both
  `test-versions.yml` (via a matrix over BC versions) and downstream
  consumers like `bc-copilot-blueprint`.
- **`bc-test-prebuilt.yml`** — sibling workflow for consumers that
  already have compiled `.app` files. Same publish/test logic, no
  compile step.
- **`test-versions.yml`** — runs the full container build + test
  sweep across BC versions, calling `bc-test-from-source.yml` from
  the matrix `test` job with `extensions/smoke-test/` as the test
  app. Used to be ~250 lines of inline compile/publish/test logic
  duplicated from the reusable workflow; refactored in 2026-04 to
  use the reusable workflow directly so it gets the same hardening
  (PIPESTATUS, body-checking publish, TESTS_TOTAL guard, shared
  wait-for-bc-healthy.sh) for free.

`build-image.yml` builds and publishes the bc-runner image to
`ghcr.io/stefanmaron/msdyn365bc.on.linux/bc-runner` on every push
that touches `src/`, `scripts/`, or `extensions/`. Both
`build-image.yml` and `test-versions.yml`'s inline build job share
registry layer cache via the same `:cache` tag.

Trigger `test-versions` manually with a `versions: "27.0,27.5"`
input to test specific versions. The default matrix runs every
supported BC version on push/PR. 28.0 will be added once Microsoft
ships an officially-released AL compiler that supports it.
