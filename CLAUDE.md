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

**EXPERIMENTAL altool runner (BC 28+ only):** `scripts/run-tests-altool.py` runs tests through the AL dotnet tool's native `al runtests` command (Microsoft.Dynamics.BusinessCentral.Development.Tools, 18.x prerelease — stable 17.x has no `runtests`), which drives the NST's built-in SignalR hub at `/dev/TestRunnerHub`. No TestRunnerExtension, no OData suite, no WebSocket emulation — the server pushes per-method results (status, output, duration) over the hub. Requires the server to advertise Dev API 7.0 (`GET /BC/dev/metadata`), which only exists in BC 28.0+. Caveats: tests do NOT run under an AL test runner codeunit (no AI tests, no test-runner setup/teardown events, isolation from `RequiredTestIsolation`, default Codeunit) — so Microsoft BCApps suites may behave differently than under `run-tests.sh`; the test app must already be published+installed (the script doesn't publish). The reusable workflow's `test_runner` input defaults to `auto`: after BC is healthy it probes `GET /dev/metadata` (via `run-tests-altool.py --probe`, exit 0 = Dev API ≥ 7.0) and uses the altool runner when supported, falling back to the websocket runner otherwise — so 27.x legs and consumers on older versions keep working unchanged. `websocket` forces the legacy flow; `altool` forces the hub and fails hard when unsupported (the regression-detection mode). `altool_version` pins the dotnet tool. Auth comes from `BC_SERVER_USERNAME`/`BC_SERVER_PASSWORD` env vars, which the script sets from `--auth`. The script's stdout deliberately prints the same `N total, P passed, F failed` and `Test codeunits: ...` lines the workflow parser greps — keep that contract if you touch either side.

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

- **.NET runtime tuning.** The entrypoint sets `DOTNET_gcServer=1` (Server GC, better throughput for the parallel Roslyn compile during NST startup — contrary to older PERFORMANCE-IDEAS.md warnings, this works fine in current BC 27.x) and `DOTNET_TieredCompilation=0` (tier-0 disabled so JMP hooks don't get overwritten by Tier 1 recompilation — the Watson crash handler and several other patches rely on hooks staying in place). Additional tuning knobs (`DOTNET_ReadyToRun`, `DOTNET_GCRetainVM`, `DOTNET_GCConserveMemory`, `DOTNET_GCHeapCount`, `DOTNET_GCNoAffinitize`) are exposed via docker-compose passthroughs for A/B experiments without rebuilding the image — see the `.NET runtime tuning` block in `docker-compose.yml`. Tested 2026-04-08: `DOTNET_ReadyToRun=1` and `DOTNET_GCRetainVM=1` both individually made cold boot ~5s slower on local, not faster. Not adopted.
- **`/bc/service` is a volume**: edits to BC DLLs persist across container restarts, and the entrypoint skips `Step 2` / `Step 2b` when the volume's `.bc-service-stamp` matches this platform version + image. A BC version change or an image rebuild flips the stamp, and the entrypoint wipes and re-patches on its own — so `docker compose build bc && docker compose up -d` is enough to pick up a `StartupHook.cs` change. `docker compose down -v` still works and is the way to force it if you've hand-edited something inside the volume.
- **`Add-ins` vs `Add-Ins`**: Linux is case-sensitive. The entrypoint renames the directory; never refer to the lowercase form in new patches.
- **Patches that depend on assembly load order** (e.g. #18 `SetupSideServices` must run before `Main()` calls it) live in `StartupHook.Initialize()`, not in the per-assembly load callback. Adding a new patch in the wrong place will silently fail because the type isn't loaded yet — or, worse, succeed once and then break on the next BC update because load order shifted.

### Known limitations (see `KNOWN-LIMITATIONS.md`)

- ~142 test failures from "User cannot be deleted because logged on" — Microsoft test cleanup deletes the session user; only fixable by patching the platform "user is logged on" check.
- ~29+ failures from `NSClientCallback.CreateDotNetHandle` NullRef on tests that need a UI session (Camera, Barcode, etc.).
- Bucket 4 sequential test run previously crashed the container after Tests-Misc due to infinite recursion in Microsoft's `OfficeWordDocumentPictureMerger.ReplaceMissingImageWithTransparentImage` (stack overflow in `Nav.OpenXml`, triggered by `TestSendToEMailAndPDFVendor`). **Fixed by Patch #23** — `ReplaceMissingImageWithTransparentImage` is no-op'd via JMP hook so missing images are left in place and the session survives.

- Platform-table captions (All Profile, User, Company, …) came back in Traditional Chinese inside English error messages (issue #52). Not a session-language problem: on Linux/ICU `CultureInfo.GetCultures` never yields plain `zh-TW`, so BC's `LanguageHelper` had no "CHT" entry and the CaptionML parser filed the Chinese text under the English LCID, first in line. **Fixed by Patch #31** (rebuilds the abbreviation table with the ICU-hidden cultures); `extensions/smoke-test` carries a regression guard. Details in `KNOWN-LIMITATIONS.md`.

When adding a new patch, append it to the numbered list in the `StartupHook.cs` header comment AND `KNOWN-LIMITATIONS.md` if it closes a known failure mode.

## Extension publish/install architecture (consumer-driven, no hand-curated lists)

Significantly hardened during the bc-copilot-blueprint bring-up session in
2026-04. Anyone touching the entrypoint's app management code, the
selective filter, or `resolve-keep-app-ids.py` should read this whole
section first.

### The five shared scripts

| Script | What it does | Used from |
|---|---|---|
| `scripts/_bcapp.py` | Shared helper for reading BC `.app` package files. Parses `NavxManifest.xml`, supports R2R packages and the rare `app.json` fallback. Indexes a whole artifact tree by app id and keeps the highest version per id. | `stage-symbols.py`, the entrypoint's stuck-publish topo sort, any future script that needs to walk artifact manifests |
| `scripts/stage-symbols.py` | Manifest-driven `.alpackages` staging. Walks an artifact tree, indexes every `.app` by id, then copies into the output dir exactly the symbols needed: System.app + Application umbrella + the consumer's transitive dependency closure. Replaces the older glob-based staging that silently missed apps when Microsoft moved files between BC versions. | `bc-test-from-source.yml`, `bc-copilot-blueprint`'s `copilot-setup-steps.yml` |
| `scripts/publish-app.sh` | Sourceable shared helper exposing `bc_publish_app <path> [dev_url] [auth]`. Reads the response body and only treats 422 as success when it actually says "already" (catches missing-dependency / schema-sync / version-conflict failures that the previous duplicated inline `publish_app` functions silently swallowed). | `run-tests.sh`, all three workflows, `bc-copilot-blueprint`'s `iterate.sh` |
| `scripts/wait-for-bc-healthy.sh` | Single canonical "block until docker healthcheck reports `healthy`" loop with progress lines every 60s. Replaces 4 previously-inlined copies across the workflows. | All three workflows, `iterate.sh` (could migrate, currently has its own variant) |
| `scripts/ci-lock.sh` | Machine-wide lease around the BC/SQL container lifecycle. Heartbeat-refreshed lease file, stolen once nobody has touched it for `BC_CI_LOCK_STALE_SECONDS`. | Both reusable workflows and all four examples |

### Two jobs on one machine share the containers, and that is issue #24

Compose derives its project name from the working directory's basename —
`bc-linux` for every pipeline everywhere — and `docker-compose.yml` binds
fixed host ports. So *any* two jobs on one docker host address the same
`bc-linux-bc-1` / `bc-linux-sql-1` and the same 7045-7089/8080/11433,
regardless of how their workspaces are laid out. The second job's
`docker compose down --remove-orphans` deletes the first job's BC mid-test.

Two things made this expensive to find, and both are worth remembering:

- **The symptom names the wrong thing.** Both jobs die with GitHub's
  `The runner has received a shutdown signal` / `exit code 143`, which reads
  as an infrastructure problem. Retriggering either PR alone passed with no
  other change, which reads as flakiness. Neither points at containers.
- **The existing `flock` looked like it covered this.** `download-artifacts.sh`
  locks the artifact cache and `CLAUDE.md` said concurrent runners were safe —
  true, and only about the cache. The containers were never locked.

`ci-lock.sh` closes it: acquire before the `down`/`up`, release as the job's
last step. Not flock — a CI job's steps are separate processes, so the lock has
to outlive the step that took it. It is a lease file refreshed by a background
heartbeat, and a lease nobody has touched for 120s is treated as abandoned.
That last part is what makes a killed job self-heal instead of wedging the
machine: the heartbeat is deliberately NOT `setsid`, so it dies with the job's
process group.

Two invariants if you touch it:

- **Release must be the last step, after the log dump**, and must verify the
  token before deleting. It runs `if: always()`, including on paths where
  acquire never ran; a blind `rm` there would hand a running job's stack to
  somebody else.
- **Serializing is the default, not the goal.** `instance_slot: N` on either
  reusable workflow moves the compose project to `bc-linux-N` AND every port by
  N*100, which is what actually buys parallelism. Moving only the project name
  would convert a silent teardown into a port bind conflict — the fixed ports
  are half the collision.

Snapshot mode is the exception: `docker-compose.snapshot.yml` needs
`network_mode: host`, which makes the port variables inert, so slots cannot
isolate it. Serialize it (`bench-selfhosted.yml` uses a `concurrency:` group).

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

## CI wall-clock

Profiled 2026-08-07 against Pageworks PR #27 (private repo, real consumer
workload) and bc-linux's own version matrix. The rationale for each change
lives next to the code — `docker-compose.yml`'s `sql` service,
`scripts/download-artifacts.sh`, `scripts/compose-pull.sh`,
`.github/workflows/mirror-sql-image.yml`. What's recorded here is only the
part you can't read off the code.

**Runner tiers differ by repo visibility.** GitHub gives public repos
4-vCPU/16GB `ubuntu-latest` and private repos 2-vCPU/7GB, same runner image,
nothing in the log saying which you got. That is the answer to "why does
bc-linux's matrix finish the fetch phase in ~30s while a private consumer
repo takes ~60s on identical work" — the same gzip SQL image measured 16s
vs ~45s. It applies to zip extraction, AL compile and BC boot too.
`workflow-summary.sh` records `nproc`/RAM and puts them on every telemetry
event; **durations are only comparable within one tier.**

**Publish an image tag before pointing compose at it.** `test-versions.yml`
has no path filter, so any push to master triggers the matrix immediately
and races `mirror-sql-image.yml`. This cost two runs. The second one went
*green* — the retry loop's last attempt landed after the tag appeared — and
the only symptom was a fetch phase reporting 74s instead of ~14s.

### Do NOT cache BC artifacts (or the SQL image). Ever.

This gets proposed roughly every time someone profiles the pipeline,
including by me. The answer is no, and it isn't close:

- Microsoft moves the revision build constantly — a given `28.0` resolves
  to a new full version many times a day as hotfixes ship. The cache key
  is invalidated about as often as it's written.
- GitHub's Actions cache is capped at 10 GB per repo. One BC version is
  ~3.1 GB extracted / 2.2 GB of zips. Any repo testing against more than
  a couple of versions evicts its own entries before they're ever reused.
- Combined, the cache thrashes: pay the upload cost every run, hit almost
  never.

This is the same reasoning behind the bc-runner image being independent
of any BC version — the image is stable and cacheable, the artifacts are
not. Don't add `actions/cache` around `artifact-cache/`, and don't
propose it in a review.

**Caching the SQL Server image was tried too, and is also worse.** It
looks like the good case — one image, changes a few times a year, exact
digest as the key — and locally `docker load` from a tar took 8s against
a 49s pull. On an actual runner it took **78s**, plus 10s to restore the
cache, against a 13-17s registry pull. The 1.6 GB uncompressed tar is
disk-bound and runner disks are far slower than a dev machine's NVMe.
Measured on run 31155693451; don't re-derive it from a local benchmark.

**Two false leads, so nobody re-chases them:**

- Test startup is ~3s, not the 33s the log implies. That gap was Python
  block-buffering through `| tee`; the workflows run `python3 -u` now.
- BC's per-method test durations are not wall-clock — they summed to 535s
  inside a 90s run. Don't add them up looking for slow tests.

**Where the time actually is,** for a consumer-shaped workload: BC boot
(~3m of a 7m job), then tests, then app publish. The fetch phase is the
dominant cost only for bc-linux's own thin smoke-test matrix.

Before proposing a reordering or parallelization change to the workflows,
read `CI-STEP-ORDERING.md` — it has the step-level critical path for both
workload shapes, the measured noise floor, and the reorderings already
tried and reverted.

### Reusing a warm filesystem is NOT the artifact-cache ban (added 2026-08-08)

The rule above is about pushing artifacts *through GitHub's cache service*:
upload cost every run, 10 GB repo cap, a key Microsoft invalidates several
times a day. All of that still holds. None of it applies to a directory
that is simply **already on the disk** — which is the normal state of
affairs on a self-hosted runner and on every dev box.

So there are now three stamped caches, each keyed on what would actually
invalidate it, each a no-op on a GitHub-hosted runner (fresh VM, nothing
on disk, so every check misses and the code path is the one that always ran):

| what | stamp | key | invalidated by |
|---|---|---|---|
| extracted artifacts | `<dest>/.bc-artifact-cache` | resolved app + platform **URL** | a new hotfix under the same short version, a version/country change |
| same, container side | reads the stamp above | the *request* (type/version/country or URL) | someone changing `BC_VERSION` on a persistent volume |
| patched service tier | `/bc/service/.bc-service-stamp` | platform version + `StartupHook.dll` size+mtime | a BC version change, or any image rebuild |

Three things about this are deliberate and easy to get wrong:

- **The host resolves the version even on a hit; the container never does.**
  Resolving is what lets a hotfix invalidate the cache, which is the whole
  point of tracking short versions. But in CI the host has *already*
  resolved and downloaded, so if the container re-resolved and Microsoft
  published a build in the intervening seconds, it would wipe the host's
  artifacts and re-fetch ~2 GB in the middle of BC boot. The container's
  job is to use what it was handed.
- **A miss clears the directory before downloading.** A torn extraction
  from an interrupted run fails much later and much more confusingly than
  a re-download does.
- **`/bc/service` is stamped with the image, not just the platform.** That
  is what makes rebuilding the image with a changed `StartupHook.cs` take
  effect without `docker compose down -v` — the old "does Nav.Server.dll
  exist" guard happily kept a tier patched by a different build, which is
  why the `down -v` instruction existed in the first place.

`BC_ARTIFACT_REFRESH=1` forces a re-download. `download-artifacts.sh`
`flock`s its destination, so several runners can share one
`artifact_cache_dir` without racing.

**What BC itself writes into the reused volume.** Audited by booting, then
listing `/bc/service` files newer than the stamp: exactly one, the Reporting
Service `.exe`. The entrypoint swaps it for a sleep stub *after* the dev
endpoint answers, because NST's startup probes the real PE's assembly
metadata — so on a warm volume the next boot handed that probe a `/bin/sh`
script, with the real binary parked in `.win`. It survives (BC 28.1 boots and
serves), but a warm boot was not doing what a cold boot does, and nothing
said so. The entrypoint now restores `.win` at the top, next to the
runtime-DLL `.bak` restore, which also fixes the same bug on a plain
container restart. If you add anything else that rewrites the service dir
after NST is up, it needs the same treatment.

### `/bc/patched` is empty, so every patch guarded on it is inert (found 2026-08-08, NOT fixed)

Found while measuring NST restarts. Verified, not inferred:

- `src/Dockerfile` only `mkdir`s `/bc/patched`. `docker run --rm <image> ls
  /bc/patched` returns **0 entries**.
- Nothing writes to it at runtime. `grep -rn 'bc/patched'` across `scripts/`
  and `src/tools/` matches only the entrypoint's own reads.
- `MergeNetstandard` writes to a **different directory**:
  `src/tools/MergeNetstandard/Program.cs:12` is
  `PatchedDir = Path.Combine(BaseDir, "StartupHook/patched")`, so with the
  entrypoint's `BASE_DIR=/bc` its output lands in `/bc/StartupHook/patched`
  (confirmed: 3 files there, `/bc/patched` still empty afterwards).

Two consequences:

1. **The merge re-runs on every boot** (2-3s). Its guard is
   `[ ! -f /bc/patched/netstandard-merged.dll ]`, at a path the producer never
   writes to, so it can never be satisfied.
2. **Every `[ -f /bc/patched/... ]` copy in Step 2b is skipped** — Patch #14's
   `CodeAnalysis.dll` type-forwarding fix, the `Mono.Cecil.dll` CheckFileName
   fix, the Layer 2 `refasm-forwarding` assemblies, and the Layer 3 merged
   assemblies deployed into Add-Ins. The `PatchNclTestPage` patches are
   unaffected — those log "Patched Nav.Ncl.dll" and genuinely run.

The merge is also partly broken on its own terms: it prints
`SKIP: netstandard-merged.dll not found`, so the netstandard merge — the one
Layer 3 is mostly about — is not produced at all.

**Deliberately not fixed here.** Repointing the paths would activate compiler
patches that have evidently been inert, and Patch #14 and the Cecil fix change
how the server-side AL compiler resolves type forwards. Turning them back on
is a behavioural change whose blast radius is AL compilation, and the
validation that matters for that lives in `PipelinePerformanceComparison`'s
BCApps sweeps, not in a boot test. BC 28.1 boots, publishes extensions, and
accepts a freshly compiled app with all of this inert — so whatever these
patches were for is not exercised by that path.

Whoever picks this up: decide first whether these patches are still needed at
all. "Delete the dead guards" and "fix the paths" are both defensible; leaving
a documented patch silently not running is not.

### The assembly cache and the DB snapshot only pay off together

Measured 2026-08-08 on a 4-vCPU box, BC 28.1, five configurations, artifacts
held constant. **Durations are only comparable within this table** — NST
startup here is 80s against the 31s `CI-STEP-ORDERING.md` records on a GitHub
runner, so read the shape, not the seconds.

| configuration | total | NST |
|---|---|---|
| warm service tier only | 148, 149s | 80-85s |
| + persisted assembly cache | 143, 133s | 86, 80s |
| + post-publish DB snapshot | 110, 115s | 95, 96s |
| + snapshot, assembly cache COLD | 105s | 91s |
| + snapshot, assembly cache WARM | **90, 90s** | **80s** |

Either one alone is worth roughly nothing:

- **The assembly cache alone does nothing** (143/133s against a 148s
  baseline — inside the noise, NST unmoved). The boot wipes and republishes
  the test framework, so those apps get fresh `Runtime Package ID`s every
  run and every cache entry keyed to them is orphaned. This is the same
  effect already recorded above under "Why the post-NST publish costs what
  it does", now measured from the other side.
- **The snapshot alone gives 35s but hands 11s back**, because NST goes from
  loading a stripped app set to loading 137 published apps — and compiling
  their assemblies.

Together they are 58s (39%), because the snapshot is what stops the package
IDs churning, which is what makes the assembly cache valid, which is what
removes the compile the snapshot just created. Do not evaluate either in
isolation and conclude it is worthless — that is exactly what the numbers
say if you do.

The snapshot's tenant is real, not synthesized: a freshly compiled app
(`extensions/smoke-test`) published into a snapshot-restored tenant returned
HTTP 200 and landed in both `[Published Application]` and
`[NAV App Installed App]`. That is the check that distinguishes this from
the reverted "synthesize the tenant-install rows in SQL" attempt above, which
wedged the tenant in `OperationalWithSyncPending`.

### The TestRunnerExtension app.json seed is load-bearing

`resolve-keep-app-ids.py` **auto-seeds itself** with
`extensions/TestRunnerExtension/app.json`, resolved relative to the
script. Don't remove that.

Why it has to be in the script and not at the call site: the entrypoint
always publishes `/bc/testrunner/TestRunner.app`, and that extension
depends on Microsoft's **Test Runner**, which consumer test apps generally
do not declare — they declare Library Assert and Tests-TestLibraries.
Without the seed Test Runner falls outside the closure, the selective
filter deletes it in SQL, and the entrypoint's own publish then fails
`AL1024`, taking test execution with it and pointing nowhere near the
cause. Reproduced with a test app declaring only Library Assert +
Tests-TestLibraries: 11 apps without the seed, 12 with it.

It was the caller's job until 2026-08-07. The two reusable workflows
passed it; all four inlined example pipelines did not, so every consumer
who copied an example rather than calling the reusable workflow was
broken. Seeding inside the script fixes those consumers without them
changing anything, because they check bc-linux out at run time. The
explicit `--app-json` in the reusable workflows is now redundant but kept
deliberately — it's idempotent (set union) and survives a future refactor
of the auto-seed.

`extensions/smoke-test` is the regression guard: it deliberately depends on Library Assert and **not** on Test Runner, so a broken seed fails the version matrix. It declared Test Runner until 2026-08-07, which is why the matrix stayed green while every example-pipeline consumer was broken. The reason is written at the top of `SmokeTest1.Codeunit.al` — don't 'fix' that app.json.

`--no-test-runner-seed` opts out. Seed via `--app-json`, **not**
`--extra-ids` with a hardcoded GUID list: the app.json route stays correct
when TestRunnerExtension's dependencies change, and it keeps the "no
hand-curated app lists" property the whole keep-set design rests on.

### Why the post-NST publish costs what it does

Per-app timings from a local boot, and the reason each app is in the list:

| app | ships R2R DLL | publish |
|---|---|---|
| Tests-TestLibraries | **no** | **16.9s** |
| System Application Test Library | **no** | 5.0s |
| Business Foundation Test Libraries | **no** | 0.3s |
| Test Runner | yes | 6.1s |
| Permissions Mock / Library Assert / Library Variable Storage / Any / Performance Toolkit | yes | 0.2-0.8s each |

The three apps that dominate ship **no precompiled DLL at all**, so BC
compiles them from AL source. That ~22s is irreducible; no amount of
R2R or install-vs-republish work touches it. Don't go looking for it again.

The R2R group is the one the stuck-publish wipe deletes and republishes
(~8s), which also **orphans their R2R cache seeds**: the pre-seed keys the
cache on `Runtime Package ID` from `[Published Application]`, the wipe
deleted those rows, and the re-POST assigns fresh ids. Measured on a live
container: 12 seeded entries, only 3 still valid, 11 of 14 published apps
with no usable entry. So the pre-seed currently pays off for the baseline
apps only.

### Don't synthesize the tenant-install rows in SQL — it breaks the tenant

Tried 2026-08-07 and reverted. The idea was to stop the stuck-publish
wipe from deleting keep-set apps, and instead flip them to installed in
place by inserting the two rows that distinguish "published" from
"installed" (`[NAV App Installed App]` and `[Installed Application]`).
Every column that isn't already on `[Published Application]` really is a
constant, and the INSERTs run clean.

BC then refuses to work:

```
Cannot install apps due to the state of the tenant: OperationalWithSyncPending
The tenant 'default' is not accessible.
```

Installing an app does more than write those tables — there's tenant
schema synchronization behind it, and hand-written rows leave the tenant
in sync-pending, which takes every later publish down with it. Whatever
replaces the wipe has to run BC's real install logic. The automation API
below does; raw SQL does not.

### Installing a published app without republishing IS possible

The design assumption that "without the management endpoint there's no way
to install a pre-published app" is **obsolete** on BC 27/28. The automation
API on the API port exposes it:

```
/api/microsoft/automation/v2.0/extensions(<appId>)/Microsoft.NAV.install
Actions: install, uninstall, unpublish, upload   (bound to Microsoft.NAV.extension)
```

Two prerequisites, both discovered the hard way:

- `_Exclude_APIV2_` must be published **and tenant-installed** — it's stuck
  in the same state as everything else, so it needs one bootstrap publish
  (~3s) before its own API can install anything.
- `ServicesDefaultCompany` must be set in `CustomSettings.config`, or the
  action returns `Internal_CompanyNotFound`. A `?company=` query parameter
  does not work — it breaks route resolution entirely. Pre-NST the value is
  available from `SELECT Name FROM [Company] WHERE [Evaluation Company]=1`,
  which is localization-safe.

Not implemented: the round-trip was never proven green, and the ceiling is
~8s minus the ~3s bootstrap. Recorded because the obsolete constraint is
what shaped the wipe-and-republish design, not because the time is worth
chasing.

### Known, not yet acted on: the post-NST extension publish

Local boot splits as 8s prep/restore/R2R-seed → 25s NST startup → **31s
publishing the test framework apps + TestRunnerExtension through the dev
endpoint**. That last third is expensive because dev-endpoint publishes
disable ReadyToRun:

```
Ready to run app Microsoft_Test Runner_… is disabled to run in this
environment. The app will be published as a normal app.
```

so those apps get fully compiled at publish time, bypassing the R2R
pre-seed. Two untaken options: skip the TestRunnerExtension publish when
the altool runner is active (it isn't used there), or write the
`[NAV App Installed App]` tenant rows in SQL pre-NST instead of the
stuck-publish wipe + republish dance, so the apps keep their R2R. The
second is the one that would actually recover the time, and it means
writing to BC's app tables directly.

## JUnit XML test result emission

`tools/TestRunner/Program.cs` accepts `--junit-output <path>` and writes a
JUnit-compliant XML file to that path after the run finishes. `run-tests.sh`
exposes the same flag. The reusable workflows
(`bc-test-from-source.yml`, `bc-test-prebuilt.yml`) always emit per-app
JUnit at `build/junit-<test-app-basename>.xml` and upload it as the
`junit-test-results` workflow artifact (no opt-in needed).

Schema: one `<testsuite>` per BC codeunit, one `<testcase>` per `[Test]`
procedure. Pass cases are self-closing. Failures use `<failure
message="...">` with the BC error message in the attribute and the full
AL call stack in the body. Skipped tests use `<skipped/>`.

### Things not to break

- **`Test Method Line.Name` on Function rows is the function name, not
  the codeunit name.** I expected the table to expose the codeunit name
  on Function rows (since the parent record carries it), but BC stores
  the function name there. Verified empirically by querying
  `testResults?$filter=lineType eq 'Function'` against a live container.
  As a result, `JUnitWriter.Write` uses `Codeunit {id}` as the
  `<testsuite name>` and `<testcase classname>`. **Don't try to "fix"
  it by adding `funcs[0]["name"]` back** — you'll re-introduce the bug
  where every classname looks like a function name. If you want the
  human-readable codeunit name in the JUnit output, the right fix is
  to do a separate OData query for the Codeunit-type row before
  emitting, or extend `TestResultsAPI.Page.al` to expose a
  `codeunitName` field.
- **The TestRunner.dll is baked into the bc-runner image.** A change to
  `Program.cs` requires `docker compose build bc` for `run-tests.sh`'s
  `docker compose exec` path to pick it up. The host-side `dotnet run`
  fallback path picks up source changes automatically, but most
  CI/local users go through the docker exec path.
- **`docker compose cp` is used to extract the XML from the container.**
  TestRunner runs inside the bc service container, writes to
  `/tmp/junit-result.xml` (a fixed in-container path), and `run-tests.sh`
  copies it back to the caller-supplied host path. This avoids needing
  to bind-mount the destination path into the container — important
  because the destination path is consumer-controlled and may not exist
  at container start time.

## `test_runner=auto` splits codeunits between altool and websocket per-codeunit

Added responding to [issue #27](https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues/27).
The altool/TestRunnerHub runner (`run-tests-altool.py`) doesn't run tests
under an AL Test Runner codeunit (see its own docstring), and two distinct
correctness gaps trace back to that:

1. **`[HandlerFunctions]` dispatch, including "unhandled modal → refuse
   with Unhandled UI".** A test that expects BC to refuse an unhandled
   modal page call (`asserterror ... .Invoke(); Assert.ExpectedError
   ('Unhandled UI')`) doesn't get that error under the hub — the call
   just silently returns.
2. **Cross-codeunit `SingleInstance` state leakage under `--transport
   hub`/`auto`.** Tests asserting that a `SingleInstance` codeunit's state
   resets at the per-test-codeunit isolation boundary
   (`RequiredTestIsolation = Codeunit`, the AL default) fail under one
   persistent hub connection for a whole run and pass under websocket —
   the hub apparently doesn't tear down and recreate the isolation scope
   per codeunit the way a fresh session does. This is unrelated to
   `[HandlerFunctions]` and isn't visible from any one codeunit's own
   source.

`scripts/classify-handler-codeunits.py` statically scans a test app's AL
source for `[HandlerFunctions(...)]` usage (and the specific unhandled-
modal-plus-`asserterror` shape) and routes each codeunit to either the fast
path or the classic websocket path — decided ONCE, before either runner
starts, so nothing runs twice even when many codeunits fail. It's a
heuristic tied to the *known* failure shape, not a proof of full
equivalence — see its docstring for the reasoning and its stated limits.
It only works when AL source is available (`bc-test-from-source.yml`); it
cannot recover attribute usage from a compiled `.app` — checked empirically,
`SymbolReference.json` only serializes method signatures, not attributes.

`scripts/run-tests-hybrid.py` is the orchestrator: discovers the full test
codeunit set from the `.app` (same `SymbolReference.json` discovery
`run-tests-altool.py` already does), classifies via the AL source dir,
runs both legs **concurrently** (different BC endpoints, no shared-resource
conflict), and merges JUnit + summary counts into one report. Codeunits
with no matching AL source are conservatively routed to websocket —
unproven safety is treated as unsafe.

**The fast leg defaults to `--altool-transport cli`, not `hub`/`auto`**,
even though hub is ~40x faster per codeunit. `cli` spawns a fresh
`al runtests` process — a fresh connection — per codeunit, which is the
same per-codeunit isolation shape `run-tests.sh` already gets from its own
reconnect-before-every-codeunit design, and is the plausible reason `cli`
doesn't show the `SingleInstance` leak that `hub` does (per the issue's
diagnosis; not independently re-verified here). Don't change this default
back to `hub`/`auto` without first confirming `cli` is actually clean of
gap #2 above — the whole point of this design is that a wrong assumption
here fails silently, the same way the original bug did.

`test_runner=altool` (explicit force, BC 27/28's "catch hub regressions"
mode) is untouched by any of this — it still runs every codeunit through
the hub with no split, on purpose, so a real hub regression can't be
silently rerouted around.

## Custom license override (ISV / developer license)

Added in the 2026-04-08 session. Anyone touching the license import path
in `scripts/entrypoint.sh`, the license mount in `docker-compose.yml`, or
the license staging step in the reusable workflows should read this.

### The problem

By default the entrypoint imports `Cronus.bclicense` from the BC artifact
(`$ARTIFACTS/app/Cronus.bclicense` — path comes from `manifest.json`'s
`licenseFile` field). ISVs need their own developer/partner license,
and the legacy workflow was: boot BC with the default license → connect
to SQL and manually UPDATE `[$ndo$dbproperty].license` → restart NST so
it picks up the new license. That's an extra ~3 minutes per CI run on
cold boot, paid on every container recreation.

### The fix

`BC_LICENSE_FILE` env var: when set and points to a regular file inside
the container, the entrypoint imports THAT file via SQL `OPENROWSET BULK`
during Step 3 (DB setup) — **before NST starts**. NST comes up with the
right license on first boot. Falls back to the manifest default when the
env var is unset or the file doesn't exist (with a WARN log line).

`BC_LICENSE_HOST_PATH` env var + docker-compose bind mount: set this to
the absolute path of a `.bclicense` file on the host, and it gets
bind-mounted at `/bc/custom-license.bclicense` inside the container. The
caller then sets `BC_LICENSE_FILE=/bc/custom-license.bclicense` to wire
the two together. When unset, the mount source defaults to `/dev/null`,
which becomes a character device inside the container — the entrypoint's
`[ -f ]` check correctly skips it and the default Cronus license is used.
No effect when unset.

### CRITICAL: the mount must be on BOTH the bc AND sql services

The license import runs `UPDATE [$ndo$dbproperty] SET [license] =
(SELECT BulkColumn FROM OPENROWSET(BULK '$FILE', SINGLE_BLOB) AS f)`.
`OPENROWSET BULK` reads the file from **SQL Server's** filesystem, not
bc's. This is the reason the default Cronus license works at all: the
`bc-artifacts` named volume is mounted into both services (ro on sql,
rw on bc), so both see `/bc/artifacts/app/Cronus.bclicense`. The custom
license must follow the same pattern. The first implementation mounted
only on bc and hit `Cannot bulk load ... file does not exist or you
don't have file access rights`. Don't make that mistake again — if you
add any new file import via `OPENROWSET BULK`, it needs to be visible
to the sql service, not just bc.

### Workflow integration

The reusable workflows (`bc-test-from-source.yml`, `bc-test-prebuilt.yml`)
declare an optional `secrets.bc_license` on their `workflow_call`
interface. Consumers base64-encode their license and pass it:

```yaml
secrets:
  bc_license: ${{ secrets.BC_LICENSE }}
```

The workflow's "Stage ISV license (if provided)" step is guarded by
`if: ${{ secrets.bc_license != '' }}`. When the secret is set, it
decodes the base64 to `$RUNNER_TEMP/bc-license.bclicense`, `chmod 644`
(so the sql container's mssql uid can read it), and writes both env
vars to `$GITHUB_ENV`. docker-compose sees the two vars in the shell
environment of the next step and does the bind mount accordingly.

The inlined example workflows (both github-workflows/ and
azure-pipelines/) have the same staging pattern inline. Azure Pipelines
uses a secret pipeline variable named `BC_LICENSE_B64` instead of the
GitHub secrets block.

### Things not to break

- The fallback to the manifest default must remain intact — when
  `BC_LICENSE_FILE` is unset the existing Cronus flow continues to
  work. The unified "LICENSE_TO_IMPORT" variable handles both cases.
- The base64-decode path uses `printf '%s' "$BC_LICENSE_B64" | base64 -d`
  — not `echo` (echo adds a trailing newline on some shells, which
  corrupts binary decode). Don't "simplify" this.
- The `chmod 644` on the decoded file is required; without it the mssql
  uid inside sql can't read the bind-mounted file and OPENROWSET fails.
- When adding any further file imports via OPENROWSET BULK, remember
  the sql-container mount requirement (see "CRITICAL" above).

## Web client on Linux (PoC, opt-in)

`BC_WEBCLIENT=1 docker compose up -d --wait` self-hosts Microsoft's real
web client (`Prod.Client.WebCoreApp` from the platform artifact) on Kestrel
at port 8080, pointed at the Linux NST over the existing 7085 client
services channel. Sign-in → role center → list pages → cards all work in a
real browser (verified BC 28.1). The moving parts: `scripts/start-webclient.sh`
(staging + config + case-fix symlinks), `src/WebClientHook/` (a SEPARATE
startup hook — do not reuse the NST's StartupHook in the web client process,
and don't run the WebClientHook in the NST), and two shared-stub tweaks
(HttpSysStub identity injection is env-gated via
`HTTPSYS_STUB_INJECT_IDENTITY=0`; WindowsPrincipalStub gained
`WindowsIdentity.AccessToken`). Two invariants worth remembering:
`DOTNET_TieredCompilation=0` is as load-bearing here as in the NST (Tier-1
recompilation silently undoes JMP hooks), and `hosting.json` overrides
`ASPNETCORE_URLS`. Full details, patch list, and known gaps:
`docs/WEBCLIENT-POC.md`.

One non-obvious cross-cutting fix lives partly in the NST: **time zones.**
`TimeZoneInfo.FromSerializedString(ToSerializedString(tz))` throws on Linux
for most DST-bearing ICU zones, and BC round-trips session/user time zones
through that pair — so anyone whose browser is in a DST zone couldn't sign
in, and the CRONUS demo DB's `Europe/Amsterdam` personalization row broke
even UTC browsers. The fix spans three places that must stay in sync:
StartupHook **Patch #24** (`NSServiceBase.FindClientTimeZone` +
`UserSettings.set_TimeZoneInfo`) and WebClientHook **W6/W6b** both route
zones through a `ZoneForOffset` helper that emits `Etc/GMT±N` (whole-hour,
re-resolvable) or synthetic `UTC±HH:MM` (sub-hour) ids; the entrypoint
normalizes `[User Personalization].[Time Zone]` to `UTC` before NST starts.
If you touch one `ZoneForOffset`, update the other — they're duplicated
across the two hook assemblies on purpose (no shared assembly).

## Two .NET runtimes in one image (BC 27/28 = net8.0, BC 29 = net10.0)

BC 29 is a `net10.0` application: its
`Microsoft.Dynamics.Nav.Server.runtimeconfig.json` asks the host for
`Microsoft.NETCore.App` **and** `Microsoft.AspNetCore.App` 10.0.0, where
BC 27/28 ask for 8.0.0. On a single-runtime image BC 29 dies before the
startup hook ever runs, with "You must install or update .NET to run this
application."

The fix is **additive, not a migration**: the image installs both shared
frameworks side by side and picks per BC version at boot. Nothing about the
.NET 8 path changed.

- **`src/Dockerfile`** — the builder stays on the noble-based `sdk:8.0-noble-amd64`
  and adds the .NET 10 SDK via `dotnet-install.sh`. Do **not** switch the
  base to `sdk:10.0`: that image is trixie, and `libwin32_stubs.so` is
  compiled here with gcc and has to keep running against the noble glibc
  in the `aspnet:8.0-noble-amd64` runtime stage. The runtime stage adds the .NET 10
  ASP.NET Core runtime the same way (which brings `Microsoft.NETCore.App`
  10 with it). A net8.0 app does **not** roll forward to 10 while 8 is
  installed, so BC 27/28 keep resolving 8.0.
- **The stub projects are multi-targeted** (`net8.0;net10.0`).
  `HttpSysStub` and `WindowsPrincipalStub` impersonate BCL assemblies and
  are copied INTO the shared framework directory, so their `AssemblyVersion`
  is conditional on the TFM (8.0.0.0 / 10.0.0.0). The net10 outputs are
  collected into `/bc/hook-net10/`.
- **`scripts/entrypoint.sh` reads the framework major straight off BC's own
  `runtimeconfig.json`** rather than mapping it from the BC major. That file
  IS the contract the host uses to pick a framework, so the selector cannot
  drift when Microsoft moves a version to a new runtime. From it the
  entrypoint derives `NETCORE_RUNTIME_DIR` / `ASPNET_RUNTIME_DIR` (replacing
  the old hardcoded `8.0.*` globs), `REFASM_DIR`, and the Add-Ins
  `System.Drawing.Common` flavour.
- **The net10 stubs are copied ONTO `/bc/hook`,** not pointed at.
  `SetupStubWithResolver` reads stub bytes from the startup hook assembly's
  own directory, so an override path would need a hook code change; the
  overlay is idempotent and re-applied every boot.
- **Reference assemblies for Cecil ship in two sets** — `/bc/refasm`
  (net8.0) and `/bc/refasm-net10`. These feed the server-side AL compiler's
  type-forward resolution (Patch #16's Add-Ins layer 1).
- **Patch #15b is already version-agnostic**: it filters by the path
  substring `/dotnet/shared/Microsoft.NETCore.App/`, which matches both. The
  `Version=8.0.0.0` strings elsewhere in `StartupHook.cs` are inside the
  Patch #15 diagnostic dump only.
- **`Microsoft.Data.SqlClient` is deliberately NOT duplicated.** The 6.0.5
  package's newest asset is `net8.0` and that assembly loads unmodified on
  .NET 10.
- **`scripts/download-artifacts.sh` falls back to the insider storage
  account** when the released index has no match for the requested version
  prefix. BC 29 is insider-only and, unlike the old bcinsider blob endpoint,
  the AFD front door needs no SAS token. The fallback only fires where the
  old code hard-errored.

`src/WebClientHook/` and `tools/TestRunner/` still target `net8.0` and were
not revisited. The web client PoC has not been tried against BC 29.

### BC 29 status: boots and publishes extensions

Verified 2026-08-07 against `sandbox/29.0.53450.0/w1`. BC 27.5, 28.1 and 29.0
all reach `Ready for extensions` on one image, and on all three the
entrypoint's own extension publishes succeed. AL tests have NOT been run on
BC 29.

BC-29-specific problems found on the way; they are compat gaps in BC 29
itself, not .NET 10 problems:

- **Missing OpenTelemetry exporters** — `Nav.Ncl` references
  `OpenTelemetry.Exporter.Console` and `.OpenTelemetryProtocol`; neither DLL
  is anywhere in the platform artifact. `NavEnvironment`'s ctor died with
  `FileNotFoundException`. Staged from NuGet (see above).
- **Patch #29, `NavDirectorySecurity.CreateSecurityForDomainDirectory`** —
  BC 29 reaches it from `TempPathHelper.InitializeFolders` and it constructs
  a `System.Security.AccessControl.DirectorySecurity`, which throws
  `PlatformNotSupportedException` on Linux. On BC 27/28 the call was gated on
  `IServiceTopology.IsServiceRunningInLocalEnvironment`, which Patch #9's
  Linux topology proxy forces false. Now hooked to return null on every
  version — a no-op on 27/28, which never reach it.
- **`SecurityIdentifier` equality operators** — BC 29 calls
  `op_Inequality`, which `WindowsPrincipalStub` didn't define
  (`MissingMethodException`). Added; purely additive for 27/28.

- **A JMP hook must never be applied twice to the same method.** This is what
  segfaulted BC 29, and it is the single most important thing in this
  section. **Patch #14** (`CecilDotNetTypeLoader.IsTypeForwardingCircular`)
  and the Mono.Cecil `Mixin.CheckFileName` hook are the only two patches that
  reached `ApplyJmpHook` twice: once from the `AssemblyLoad` event handler,
  then again from `TryEagerPatch`, whose `Assembly.LoadFrom` is what raised
  that event — so the eager call always landed on an already-hooked method.
  .NET 8 tolerated the redundant re-apply. On .NET 10,
  `RuntimeHelpers.PrepareMethod` against an overwritten entry point dies
  inside `libcoreclr.so` with no managed frames, which is exactly the
  "deterministic SIGSEGV, no managed frames, both hooks must be off" symptom.
  It had nothing to do with the replacements' signatures — a standalone
  repro on .NET 10.0.10 hooking an instance method with parameters to a
  parameterless static works fine. `ApplyJmpHook` now refuses to re-hook:
  `IsAlreadyJmpHooked` reads the entry point (WITHOUT `PrepareMethod`, which
  is the call that crashes) and looks for our own `FF 25 00000000` stub — a
  real StubPrecode also starts `FF 25` but always has a non-zero disp32.
  The check is per method instance, so a second ALC still gets its own hook.
- **`GenevaStub` needs the version-agnostic stub resolver, not just a file
  copy.** BC 29's `Microsoft.BusinessCentral.Telemetry.OpenTelemetry` asks
  for `OpenTelemetry.Exporter.Geneva` **1.15.2.1008**; the stub is 1.9.0.62.
  `ReplaceWithStub` had already overwritten the file, so the default ALC
  found it, rejected the identity, and the load failed. BC reported that as
  a `FileNotFoundException` inside a "LazyEx factory threw an exception"
  **without naming the assembly**, and every dev-endpoint publish returned
  HTTP 500. `Initialize` now also calls `RegisterStubForResolver
  ("OpenTelemetry.Exporter.Geneva")`, which answers any requested version the
  same way `System.Drawing.Common` is handled. Additive for BC 27/28.

`BC_DEBUG_ASSEMBLY_RESOLVE=1` is how the Geneva name was found and is the
tool to reach for next time BC swallows an assembly load failure: it logs
every `AssemblyLoadContext.Default.Resolving` and
`AppDomain.AssemblyResolve` miss with the requesting assembly. Much cheaper
than `BC_DEBUG_FIRSTCHANCE=1`. Both are docker-compose pass-throughs.

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
to master that touches `src/`, `scripts/`, or `extensions/`, tagging
`:latest` and `:<sha>`. It caches layers to the registry `:cache` tag;
`test-versions.yml`'s build job caches to `type=gha` instead, because
it also has to run on fork PRs, which cannot write to the registry.

### test-versions.yml tests the image it just built

`test-versions.yml`'s `build-image` job pushes a throwaway
**`ci-<sha>`** tag and feeds that to every matrix leg, required and
preview. It must never push `:latest` or the bare `:<sha>` —
those are what consumers pull and what `build-image.yml` publishes
from master, and this workflow runs on arbitrary branches.

Until 2026-08 the job built with `push: false` and then handed the
matrix `:latest`. So it verified that the current commit still *built*
and then tested a different image entirely: **no change to
`src/Dockerfile` — or to anything else baked into the image — was
testable on a branch.** The run went green having proven nothing about
the diff.

That is what hid the .NET 10 work on `net10-integration`. The branch
adds the .NET 10 runtime so BC 29's NST can start; the BC 29 preview
leg still died in the host resolver (`Framework:
'Microsoft.NETCore.App', version '10.0.0'` … `The following frameworks
were found: 8.0.29`) because it booted master's .NET 8 `:latest`. Two
things kept it quiet: 27/28 pass fine on the stale image so the run
concluded `success`, and `preview-note` prints "known-expected failure:
BC 29 targets .NET 10 while the image ships .NET 8" — true here, but
for the wrong reason, and it reads as nothing new.

Fork PRs are the one exception: their `GITHUB_TOKEN` is read-only, so
they fall back to building as a compile check and testing `:latest`.
That path emits `::warning::` lines saying the image change is
untested — don't make it silent.

Trigger `test-versions` manually with a `versions: "27.0,28.1"`
input to test specific versions — that input short-circuits discovery
entirely, including the preview legs.

### The version matrix is discovered at run time, not hardcoded

`scripts/discover-bc-versions.py` reads Microsoft's artifact indexes and
emits two matrices:

- **Released (REQUIRED).** Public sandbox index, newest `majors_back`
  majors (dispatch input, default **2** → 27 and 28 as of 2026-08).
  Emitted as SHORT versions ("27.5", "28.3") on purpose:
  `download-artifacts.sh` resolves a short version to the newest build
  itself, so a hotfix landing between matrix computation and download is
  picked up rather than missed. Capping at 2 majors is a cost decision —
  the index carries every major back to 25 and one leg is a full BC boot.
- **Preview (NON-BLOCKING).** Insider index
  (`bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net`, anonymously readable, no
  SAS token, same path shape as the public one). Two legs only: the next
  major and the next minor of the current major. Emitted as FULL versions
  plus a full `bc_artifact_url`, because insider builds move daily and
  nothing in the public-index resolver knows about that host.

**Preview legs must not fail the run.** GitHub does not allow
`continue-on-error:` on a job that `uses:` a reusable workflow, so the
knob lives in `bc-test-from-source.yml` as a `continue_on_error` input
(default false) applied to the job it actually runs. Don't try to move it
back to the caller — it silently does nothing there.

**The next-major preview leg is expected to fail** until the image ships
.NET 10: BC 29 targets .NET 10, `src/Dockerfile` ships the .NET 8 runtime,
so the NST cannot start. `preview-note` writes that framing into the run
summary so a red preview leg isn't misread as a regression.
