# MsDyn365Bc.On.Linux

Run the Microsoft Dynamics 365 Business Central service tier on Linux with
Docker Compose. No fork — the unmodified Microsoft .NET 8 service tier is
patched at runtime so it boots and serves on Linux.

```bash
git clone https://github.com/StefanMaron/MsDyn365Bc.On.Linux.git
cd MsDyn365Bc.On.Linux
docker compose up -d --wait
```

The `--wait` flag returns once BC is healthy. **First boot takes ~5 minutes**
(artifact download + database restore + extension compilation). Subsequent
starts take ~1 minute.

You don't need to build anything: `docker compose up` **pulls the prebuilt,
publicly accessible image** (`ghcr.io/stefanmaron/msdyn365bc.on.linux/bc-runner:latest`)
when it isn't already in your local cache — no GHCR auth required. To grab a
newer published build later, run `docker compose pull`. Only contributors
hacking on the image itself need `docker compose build` (an explicit build
tags the same ref locally, so it then takes precedence over the pull — run
`docker compose build` again after changing `src/` or `scripts/`, or
`docker compose pull` to drop back to the published image).

When the command returns, BC is running with a CRONUS demo database, dev
endpoint, OData, API, and the test toolkit (Test Runner, Library Assert,
Variable Storage, Permissions Mock, Any, System Application Test Library,
Business Foundation Test Libraries, Tests-TestLibraries) all published —
ready for extension development and testing.

**Verify it's up:**

```bash
curl -sf -u BCRUNNER:Admin123! http://localhost:7048/BC/ODataV4/Company \
  | python3 -c "import sys,json; print('OK:', json.load(sys.stdin)['value'][0]['Name'])"
# → OK: CRONUS International Ltd.
```

---

## Requirements

Just to start BC and run AL tests:

- Docker with Compose v2
- `python3`, `curl`, `unzip` (used by `scripts/run-tests.sh` for symbol
  parsing and OData calls)
- ~4 GB RAM (2 GB SQL + 1-2 GB BC)
- ~3 GB disk for artifacts (downloaded once, cached in Docker volumes)

Running on an Apple Silicon Mac (podman + Rosetta)? See [MacOS.md](MacOS.md)
for the extra setup steps and the `docker-compose.macos.yml` overlay.

That's it — **no .NET SDK on the host is required**. The WebSocket test
runner used by `run-tests.sh` is bundled inside the bc-runner image and
invoked via `docker compose exec`, so all the .NET work happens in the
container. (If `run-tests.sh` is pointed at a remote BC instead of a
local docker, it falls back to `dotnet run` from the host source — that
fallback path needs the .NET 8 SDK.)

**Optional — only if you want to compile AL projects from the command line**
without using the VS Code AL extension's F5 build:

- `.NET 8 SDK` plus the Linux AL compiler tool. Pick the version that matches your BC major:

  | BC version | AL runtime (`app.json`) | `al_tool_version` (NuGet) |
  |---|---|---|
  | BC 27.x | `16.0` | `16.2.28.57946` |
  | BC 28.x | `17.0` | `17.0.34.45391` |

  ```bash
  # BC 27.x
  dotnet tool install -g \
    Microsoft.Dynamics.BusinessCentral.Development.Tools.Linux \
    --version 16.2.28.57946
  echo 'export PATH="$HOME/.dotnet/tools:$PATH"' >> ~/.bashrc
  ```

  The reusable workflow (`bc-test-from-source.yml`) auto-derives
  `al_tool_version` from `bc_version` — set it explicitly only to pin a
  specific build. Your app.json `runtime` is respected as-is; the optional
  `runtime_version` input ("auto" or an explicit value) rewrites it before
  compile, but that's opt-in and off by default. If you use VS Code with
  the AL Language extension, F5 / Ctrl+F5 publishes via the dev endpoint
  without the CLI compiler — skip this section.

  **Why does the smoke test use `runtime: "14.0"`?**
  `extensions/smoke-test/app.json` uses a deliberately low runtime value so
  the same committed file compiles cleanly against any supported BC version.
  bc-linux's own `test-versions.yml` passes `runtime_version: auto` to
  rewrite it per matrix leg; consumer apps should declare the runtime
  matching their minimum supported BC version and leave `runtime_version`
  unset.

---

## Endpoints

After `docker compose up`, these are available:

| Endpoint     | URL                                       | Purpose                              |
|--------------|-------------------------------------------|--------------------------------------|
| Dev          | `http://localhost:7049/BC/dev`            | Publish extensions, download symbols |
| OData        | `http://localhost:7048/BC/ODataV4`        | Data access                          |
| API v2.0     | `http://localhost:7052/BC/api/v2.0`       | Business API                         |
| Management   | `http://localhost:7045/BC/Management`     | NAV management endpoint              |
| Client (WS)  | `ws://localhost:7085/BC`                  | WebSocket client services (TestPage) |
| Web client   | `http://localhost:8080`                   | Browser UI (opt-in, `BC_WEBCLIENT=1`) |

**Authentication:** `BCRUNNER` / `Admin123!` (NavUserPassword) by default.
Note the username is *not* `admin` — `BCRUNNER` is used so test code that
needs to delete a user named "ADMIN" doesn't nuke the runner's own session.

Override with `BC_SERVER_USERNAME` / `BC_SERVER_PASSWORD`:

```bash
BC_SERVER_USERNAME=MYUSER BC_SERVER_PASSWORD='MyP@ss1!' docker compose up -d --wait
curl -sf -u MYUSER:MyP@ss1! http://localhost:7048/BC/ODataV4/Company
```

The scripts in `scripts/` (`run-tests.sh`, `run-tests-altool.py`,
`run-tests-hybrid.py`, `publish-app.sh`, etc.) pick the same two env vars up
automatically, so no `--auth` flag is needed once the container was booted
with them. The entrypoint derives Microsoft's V3 password representation from
`BC_SERVER_PASSWORD`, and the unmodified Business Central verifier enforces it:
wrong passwords and disabled, expired, or nonexistent users are rejected.
These remain local dev/CI credentials; do not expose the container's ports to
an untrusted network.

### Web client (browser UI)

Microsoft's real web client (`Prod.Client.WebCoreApp`) can be self-hosted
on Kestrel inside the bc container — sign-in, role center, list pages, and
cards work in a normal browser against the Linux NST:

```bash
BC_WEBCLIENT=1 docker compose up -d --wait
# then open http://localhost:8080  (BCRUNNER / Admin123!)
```

Works with the macOS overlay too
(`BC_WEBCLIENT=1 docker compose -f docker-compose.yml -f docker-compose.macos.yml up -d --wait`).
Change the host port with `BC_WEBCLIENT_PORT`. EXPERIMENTAL — intended for
developers who want to poke at data and pages in a real UI; reports,
printing, and file upload are untested. Details: [docs/WEBCLIENT-POC.md](docs/WEBCLIENT-POC.md).

---

## Local development with VS Code

1. Start BC (from this repo):
   ```bash
   docker compose up -d --wait
   ```

2. In **your AL project**, add a `.vscode/launch.json`:

   ```json
   {
       "version": "0.2.0",
       "configurations": [
           {
               "name": "BC Linux",
               "type": "al",
               "request": "launch",
               "server": "http://localhost",
               "serverInstance": "BC",
               "port": 7049,
               "authentication": "UserPassword",
               "startupObjectId": 22,
               "startupObjectType": "Page",
               "breakOnError": "All",
               "launchBrowser": false,
               "enableLongRunningSqlStatements": true,
               "enableSqlInformationDebugger": true
           }
       ]
   }
   ```

   When the AL extension prompts for credentials on first publish, use
   **`BCRUNNER`** / **`Admin123!`** (not `admin`).

3. **Download symbols** — `Ctrl+Shift+P` → **AL: Download Symbols**.
   Or manually:

   ```bash
   mkdir -p .alpackages
   for app in System "System Application" "Base Application" "Application"; do
     curl -sf -u BCRUNNER:Admin123! \
       "http://localhost:7049/BC/dev/packages?publisher=Microsoft&appName=$(echo $app | sed 's/ /%20/g')&appVersion=0.0.0.0" \
       -o ".alpackages/${app}.app"
   done
   ```

4. **Publish + run** — press `F5` (or `Ctrl+F5`) in VS Code. The AL
   extension uses the `launch.json` settings to publish via the dev
   endpoint and open the configured startup page.

5. **Debugging works** — breakpoints, call stack, variables, watch,
   stepping, both `launch` and `attach` (`"request": "attach"` with
   `"breakOnNext": "WebClient"` pairs nicely with `BC_WEBCLIENT=1` to
   debug code triggered from the browser). Requires StartupHook Patch
   #25 (included). Details and verified configs:
   [docs/DEBUGGING.md](docs/DEBUGGING.md).

---

## Command-line workflow

For pipelines, scripts, and quick edits without VS Code.

**Compile** (after installing the AL compiler — see [Requirements](#requirements)):

```bash
AL compile "/project:." "/packagecachepath:.alpackages" "/out:MyExtension.app"
```

**Publish via dev endpoint:**

```bash
curl -u BCRUNNER:Admin123! -X POST \
  -F "file=@MyExtension.app;type=application/octet-stream" \
  "http://localhost:7049/BC/dev/apps?SchemaUpdateMode=forcesync"
```

---

## Running AL tests

The test framework (Test Runner, Library Assert, Library Variable Storage,
Permissions Mock, Any) is published automatically on first boot of the BC
container, so a fresh `docker compose up -d --wait` is enough — no extra
setup.

```bash
# Auto-discover test codeunits from the .app's symbols
./scripts/run-tests.sh --app MyTestApp.app

# Same, but limit to specific codeunits. --codeunit-range accepts:
#   50000                                  single id
#   50000..50099                           single AL range
#   "50000..50099|130450..130459"          multiple ranges (pipe-separated)
#   "50000,50001,50002"                    explicit ids
#   "50000..50099,130450,200000..210000"   mixed
./scripts/run-tests.sh --app MyTestApp.app --codeunit-range 50000
./scripts/run-tests.sh --app MyTestApp.app --codeunit-range "50000..50099|130450..130459"
```

When `--app` is provided the script reads `SymbolReference.json` from the
`.app` zip, walks for codeunits with `Subtype = Test`, and intersects with
`--codeunit-range` if also provided. This avoids the SetupSuite call having
to iterate tens of thousands of nonexistent IDs.

**Long codeunit lists are batched automatically.** The websocket runner sets
the suite up by POSTing the ids as one string field, `CodeunitIds:
Text[2048]` on table 99903, so a list past 2048 characters is rejected by BC
outright — HTTP 400 `Application_StringExceededLength`, before any test runs.
Roughly 340 five-digit ids is where that lands. `run-tests.sh` splits the
list, sets the suite up and runs it once per batch, and merges the reports,
so the JUnit XML and the `N total, P passed, F failed` line are the same as
an un-batched run's. Set `BC_CODEUNIT_IDS_MAX_CHARS` to change the per-batch
budget (default 1000). `python3 scripts/test-run-tests-batching.py` exercises
the whole loop against a fake BC — no container needed.

**Which company tests run in:** by default both runners read the OData
`Company` page and pick the evaluation (demo) company. That works on any
localization without knowing the name in advance — the CRONUS company is
called something different in every country, and the demo database ships
a second company ("My Company") alongside it. Pass `--company` to pin it:

```bash
./scripts/run-tests.sh --app MyTestApp.app --company "CRONUS Deutschland GmbH"
```

It has to be the company *name*; neither the AL tool nor the
TestRunnerHub accepts a company id. The CI workflows expose the same
thing as a `test_company` input.

Sample output:

```
=== BC Test Runner ===
Company: CRONUS International Ltd.
Test codeunits: 50000,50004
Setting up test suite... OK

=== Running Tests ===
Executing 2 codeunits via WebSocket (max 26 iterations)...
  [1/2] Codeunit 50000: TestCustomerCreation (0.4s)
    PASS  TestCustomerCreation
    PASS  TestSalesOrderPosting
  [2/2] Codeunit 50004: TestSomethingElse (0.1s)
    PASS  TestSomethingElse

=== Results (2s) ===
3 total, 3 passed, 0 failed, 0 skipped
```

### JUnit XML output

Pass `--junit-output <path>` to also write per-test results as a JUnit
XML file, compatible with GitHub Checks reporters
([dorny/test-reporter](https://github.com/dorny/test-reporter),
[EnricoMi/publish-unit-test-result-action](https://github.com/EnricoMi/publish-unit-test-result-action)),
the Azure DevOps "Publish Test Results" task, and any other CI tool that
ingests JUnit:

```bash
./scripts/run-tests.sh --app MyTestApp.app --junit-output ./test-results.xml
```

Each codeunit becomes one `<testsuite>`, each `[Test]` procedure one
`<testcase>`. Failing tests carry the BC error message in the `message`
attribute and the full AL call stack in the `<failure>` body.

The reusable workflows (`bc-test-from-source.yml`, `bc-test-prebuilt.yml`)
emit JUnit XML automatically (no opt-in needed) and upload it as a
`junit-test-results` workflow artifact.

For end-to-end CI examples (compile + publish + test on every PR), see
[**Templates for your own repo**](#templates-for-your-own-repo) below.

---

## Configuration

Defaults are in `.env`. Override any variable on the command line without
editing files:

```bash
# Change BC version
BC_VERSION=28.0 docker compose up -d

# Change country
BC_VERSION=27.5 BC_COUNTRY=de docker compose up -d

# Change ports (if defaults conflict)
BC_DEV_PORT=17049 docker compose up -d
```

| Variable          | Default          | Description                                                                  |
|-------------------|------------------|------------------------------------------------------------------------------|
| `BC_VERSION`      | `27.5`           | BC version (e.g. `27.5`, `28.0`, or full like `27.5.46862.48612`)            |
| `BC_COUNTRY`      | `w1`             | Country/region code                                                          |
| `BC_TYPE`         | `sandbox`        | `sandbox` or `onprem`                                                        |
| `SA_PASSWORD`     | `Passw0rd123!`   | SQL Server SA password                                                       |
| `SQL_PORT`        | `11433`          | Host port for SQL Server                                                     |
| `BC_DEV_PORT`     | `7049`           | Dev endpoint port (publish, symbols)                                         |
| `BC_ODATA_PORT`   | `7048`           | OData v4 port                                                                |
| `BC_API_PORT`     | `7052`           | API v2.0 port                                                                |
| `BC_MGMT_PORT`    | `7045`           | Management endpoint port                                                     |
| `BC_CLIENT_PORT`  | `7085`           | WebSocket client services port (used by `run-tests.sh`)                      |
| `BC_SERVER_USERNAME` | `BCRUNNER`    | NavUserPassword service user created with SUPER access                       |
| `BC_SERVER_PASSWORD` | `Admin123!`   | Enforced password used to generate the service user's Microsoft V3 value     |
| `BC_LICENSE_HOST_PATH` | unset       | Optional host path to a `.bclicense` file. Mounted into bc + sql containers and imported INSTEAD of the default Cronus license. See "Custom license" below. |
| `BC_LICENSE_FILE` | unset            | Path INSIDE the container of the license file to import. Set to `/bc/custom-license.bclicense` together with `BC_LICENSE_HOST_PATH`. |
| `BC_SQL_IMAGE`    | GHCR mirror      | SQL Server image. See "SQL Server image" below.                              |
| `BC_DL_STREAMS`   | `16`             | Parallel byte-range streams per artifact zip (32 total across the two).       |
| `BC_DL_BIG_SHARE` | `70`             | Percent of the stream budget given to the larger zip so it lands first and its extraction overlaps the other download. `50` restores an even split. |
| `BC_CODEUNIT_IDS_MAX_CHARS` | `1000` | Characters of codeunit ids `run-tests.sh` sends per suite-setup request. BC's `CodeunitIds` field is `Text[2048]`; the default leaves room for the test suite to grow. |

**SQL Server image:** the `sql` service defaults to
`ghcr.io/stefanmaron/msdyn365bc.on.linux/mssql:2022-zstd`, a mirror of
`mcr.microsoft.com/mssql/server:2022-latest` with the layers recompressed
to zstd, refreshed weekly by `.github/workflows/mirror-sql-image.yml`.

The mirror exists because mcr is slow and flaky from GitHub runners — one
consumer run measured 6 MB/s, taking 105s for the 625 MB image, and mcr
intermittently WAF-blocks runner IPs. zstd is on top of that because most
of the pull cost turned out to be decompression rather than transfer: the
same gzip image pulled in ~45s on a 2-vCPU runner against 16s on a 4-vCPU
one. The zstd image is also smaller (488 MB vs 625 MB).

`:2022` is the byte-identical gzip copy, kept as a fallback if your Docker
can't handle zstd layers. Point `BC_SQL_IMAGE` anywhere you like:

```bash
# gzip fallback
BC_SQL_IMAGE=ghcr.io/stefanmaron/msdyn365bc.on.linux/mssql:2022 docker compose up -d

# straight from Microsoft
BC_SQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest docker compose up -d

# your own registry or a pinned CU
BC_SQL_IMAGE=registry.example.com/mssql/server:2022-CU14 docker compose up -d
```

The reusable CI workflows expose the same override as a `sql_image` input.

**Neither the BC artifacts nor the docker images are cached between CI
runs**, and that's deliberate. Microsoft moves the artifact revision build
several times a day, so an artifact cache key would be invalidated about
as often as it's written, and at ~3 GB per version a handful of versions
exhausts the 10 GB repo cache before anything is reused. Caching the SQL
image was measured and is slower than pulling it — `docker load` of the
1.6 GB tar takes ~78s on a runner against a 13-17s pull.

**Custom license (ISVs / developer license):** by default the entrypoint
imports the public Cronus.bclicense that ships with the BC artifact. To
use your own license without the boot/import/restart cycle:

```bash
BC_LICENSE_HOST_PATH=/path/to/your-license.bclicense \
BC_LICENSE_FILE=/bc/custom-license.bclicense \
docker compose up -d
```

The entrypoint imports the override BEFORE NST starts, so the service
tier comes up with the right license on first boot. The reusable CI
workflows (`bc-test-from-source.yml` / `bc-test-prebuilt.yml`) accept
the same license via a `bc_license` secret (base64-encoded).

**Reset state:** `docker compose down -v` removes the containers *and* the
named volumes (`bc-artifacts`, `bc-service`), forcing a fresh artifact
download and BAK restore on the next `up`. Use this when you've changed
something the entrypoint guards on existing files (`/bc/service`,
patched DLLs).

---

## Templates for your own repo

bc-linux ships starter CI/CD templates so downstream projects can run AL
tests against a Linux BC without forking or copy-pasting hundreds of lines
of YAML. The image at `ghcr.io/stefanmaron/msdyn365bc.on.linux/bc-runner:latest`
is publicly accessible — no GHCR auth needed.

| Path                                         | What it is                                                                |
|----------------------------------------------|---------------------------------------------------------------------------|
| `examples/github-workflows/`                 | GitHub Actions starters (inlined templates + reusable workflow examples)  |
| `examples/azure-pipelines/`                  | Azure DevOps starter pipelines (inlined `azure-pipelines.yml` examples)   |
| `.github/workflows/bc-test-from-source.yml`  | Reusable GitHub workflow — compiles AL source from your repo              |
| `.github/workflows/bc-test-prebuilt.yml`     | Reusable GitHub workflow — publishes pre-built `.app` files               |

Cleanest consumer experience (10-line `.github/workflows/bc-test.yml`):

```yaml
name: BC Tests
on: [push, pull_request, workflow_dispatch]
jobs:
  bc-tests:
    uses: StefanMaron/MsDyn365Bc.On.Linux/.github/workflows/bc-test-from-source.yml@master
    with:
      bc_version:     "27.5"
      app_dirs:       "app"
      test_app_dirs:  "test"
      codeunit_range: "50000..99999"
```

See [`examples/github-workflows/README.md`](./examples/github-workflows/README.md)
and [`examples/azure-pipelines/README.md`](./examples/azure-pipelines/README.md)
for full input documentation, troubleshooting, and inlined alternatives.

---

## GitHub Codespaces

This repository includes a devcontainer at `.devcontainer/devcontainer.json`.
Open it in a Codespace and BC starts automatically via Docker-in-Docker.
The AL Language extension is pre-installed.

[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/StefanMaron/MsDyn365Bc.On.Linux)

---

## Running multiple instances

Run multiple BC environments side-by-side by giving each stack a unique
project name and port set. Docker Compose uses the project name to
namespace all containers, networks, and volumes.

```bash
# Instance 1: BC 27.5 on default ports
docker compose -p bc275 up -d --wait

# Instance 2: BC 28.0 on offset ports
COMPOSE_PROJECT_NAME=bc280 \
BC_VERSION=28.0 \
SQL_PORT=21433 \
BC_DEV_PORT=17049 \
BC_ODATA_PORT=17048 \
BC_API_PORT=17052 \
BC_MGMT_PORT=17045 \
BC_CLIENT_PORT=17085 \
  docker compose up -d --wait
```

Each instance gets its own containers (`bc275-bc-1`, `bc280-bc-1`),
volumes, and network. Manage them independently:

```bash
docker compose -p bc275 logs -f     # logs for instance 1
docker compose -p bc280 down        # stop instance 2
docker compose -p bc275 down -v     # stop instance 1 + wipe its volumes
```

**Important:** every port must be unique across instances — you'll get a
bind error if two instances try to map the same host port. The easiest
approach is to pick a port offset (e.g. +10000) for each additional
instance.

For convenience, you can keep a per-instance `.env` file:

```bash
# .env.bc280
BC_VERSION=28.0
SQL_PORT=21433
BC_DEV_PORT=17049
BC_ODATA_PORT=17048
BC_API_PORT=17052
BC_MGMT_PORT=17045
BC_CLIENT_PORT=17085
```

```bash
docker compose -p bc280 --env-file .env.bc280 up -d --wait
```

### …and the same thing in CI

The constraint above — one project name and one port set per BC — is what
makes two CI jobs on one machine dangerous rather than merely slow. Compose
derives the project name from the working directory's basename, which is
`bc-linux` in every pipeline, so two jobs on one docker host address the same
`bc-linux-bc-1` and `bc-linux-sql-1`. The second job's
`docker compose down --remove-orphans` deletes the first job's BC in the
middle of its test run, and both die with GitHub's generic "the runner has
received a shutdown signal" — which points nowhere near the cause
([issue #24](https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues/24)).

Every pipeline in this repo therefore takes a machine-wide lease
(`scripts/ci-lock.sh`) around the container lifecycle, so jobs sharing a
machine queue instead of destroying each other. On a GitHub-hosted runner the
lease is uncontended and costs a single file create.

Queueing is the safe default, not the fast one. To actually run two BCs at
once on one self-hosted machine, give each caller its own slot:

```yaml
jobs:
  bc-tests:
    uses: StefanMaron/MsDyn365Bc.On.Linux/.github/workflows/bc-test-from-source.yml@master
    with:
      instance_slot: 1     # project bc-linux-1; every port +100
```

Slot N gets the compose project `bc-linux-N` and every published port offset
by N\*100 (slot 1: dev 7149, OData 7148, API 7152, SQL 11533, …).
Jobs on different slots run concurrently; jobs on the same slot still queue.
Budget a full BC service tier plus a SQL Server in RAM per slot.

`ci-lock.sh` is usable on its own, too — `acquire`/`release`/`status`, with
`BC_CI_LOCK_DIR` pointing at a path shared by everything that shares the
docker daemon.

---

## How it works

The BC service tier is a .NET 8 application designed for Windows. This
project patches it to run on Linux using a [.NET startup hook](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/debugging-profiling#startup-hooks)
that intercepts and fixes Windows-specific calls at runtime:

- **Win32 P/Invoke stubs** — `kernel32.dll`, `user32.dll`, `advapi32.dll`
  etc. redirected to a shared library with Linux-compatible
  implementations
- **Assembly resolution** — .NET reference assemblies and type-forward
  merging for Cecil-based compilation
- **Service stubs** — `HttpSys` → Kestrel redirect, `PerformanceCounter`,
  `WindowsIdentity`, `Geneva ETW` stubs
- **Binary patches** — `CodeAnalysis.dll` and `Mono.Cecil.dll` fixes for
  type-forwarding resolution on Linux
- **Runtime AL fixes** — patches for Word picture-merger recursion, task
  page UI handler, and ~20 other Windows-only assumptions in the BC
  runtime

The full patch list is at the top of `src/StartupHook/StartupHook.cs`.
Known limitations are in [`KNOWN-LIMITATIONS.md`](./KNOWN-LIMITATIONS.md).
The SQL Server runs as a separate container using the official
`mssql/server:2022` Linux image.

---

## CI / version support

The `Test BC Versions` workflow (`.github/workflows/test-versions.yml`)
runs the full container build + smoke test sweep across multiple BC
versions. Trigger it manually with custom versions:

```
versions: "27.0,27.5,28.0"
```

The published image is `ghcr.io/stefanmaron/msdyn365bc.on.linux/bc-runner`
(public). The `:latest` tag tracks `master`.
