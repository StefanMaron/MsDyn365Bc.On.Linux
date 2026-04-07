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

When the command returns, BC is running with a CRONUS demo database, dev
endpoint, OData, API, and the test framework (Test Runner, Library Assert,
Variable Storage, Permissions Mock, Any) all published — ready for extension
development and testing.

**Verify it's up:**

```bash
curl -sf -u BCRUNNER:Admin123! http://localhost:7048/BC/ODataV4/Company \
  | python3 -c "import sys,json; print('OK:', json.load(sys.stdin)['value'][0]['Name'])"
# → OK: CRONUS International Ltd.
```

---

## Requirements

Just to start BC:

- Docker with Compose v2
- ~4 GB RAM (2 GB SQL + 1-2 GB BC)
- ~3 GB disk for artifacts (downloaded once, cached in Docker volumes)

To **also run AL tests** via `scripts/run-tests.sh` from the host:

- `python3` (JSON parsing of test symbols + suite responses)
- `.NET 8 SDK` (the WebSocket test runner is a small `dotnet run` project)
- `curl`, `unzip`

To **also compile AL projects from the command line**:

- `.NET 8 SDK` plus the Linux AL compiler tool:

  ```bash
  dotnet tool install -g \
    Microsoft.Dynamics.BusinessCentral.Development.Tools.Linux \
    --version 16.2.28.57946
  echo 'export PATH="$HOME/.dotnet/tools:$PATH"' >> ~/.bashrc
  ```

  (Bump the version for newer BC majors. The version above is known to
  work with BC 27.x.)

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

**Authentication:** `BCRUNNER` / `Admin123!` (NavUserPassword).
Note the username is *not* `admin` — `BCRUNNER` is used so test code that
needs to delete a user named "ADMIN" doesn't nuke the runner's own session.

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

# Same, but limit to a specific codeunit (or range)
./scripts/run-tests.sh --app MyTestApp.app --codeunit-range 50000
./scripts/run-tests.sh --app MyTestApp.app --codeunit-range 50000..50099
```

When `--app` is provided the script reads `SymbolReference.json` from the
`.app` zip, walks for codeunits with `Subtype = Test`, and intersects with
`--codeunit-range` if also provided. This avoids the SetupSuite call having
to iterate tens of thousands of nonexistent IDs.

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
