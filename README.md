# MsDyn365Bc.On.Linux

Run the Business Central service tier on Linux with Docker Compose.

```bash
git clone https://github.com/StefanMaron/MsDyn365Bc.On.Linux.git
cd MsDyn365Bc.On.Linux
docker compose up -d --wait
```

The `--wait` flag returns once BC is healthy (OData endpoint responding). First boot takes 5-10 minutes (artifact download + database restore + extension compilation). Subsequent starts take ~1 minute.

BC starts with a CRONUS demo database, dev endpoint, OData, and API — ready for extension development and testing. The test framework (Library Assert, Variable Storage, etc.) is published automatically.

## Configuration

Defaults are in `.env`. Override any variable without editing files:

```bash
# Change BC version
BC_VERSION=28.0 docker compose up -d

# Change country
BC_VERSION=27.5 BC_COUNTRY=de docker compose up -d

# Change ports (if defaults conflict)
BC_DEV_PORT=17049 docker compose up -d
```

| Variable | Default | Description |
|----------|---------|-------------|
| `BC_VERSION` | `27.5` | BC version (e.g. `27.0`, `27.5`, `28.0`, or full like `27.5.46862.48004`) |
| `BC_COUNTRY` | `w1` | Country/region code |
| `BC_TYPE` | `sandbox` | `sandbox` or `onprem` |
| `SA_PASSWORD` | `Passw0rd123!` | SQL Server SA password |
| `BC_DEV_PORT` | `7049` | Dev endpoint port |
| `BC_ODATA_PORT` | `7048` | OData port |
| `BC_API_PORT` | `7052` | API port |

## Running Multiple Instances

You can run multiple BC environments side-by-side by giving each stack a unique project name and port set. Docker Compose uses the project name to namespace all containers, networks, and volumes, so they don't collide.

Use the `-p` flag (or `COMPOSE_PROJECT_NAME` env var) together with different ports:

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

Each instance gets its own containers (`bc275-bc-1`, `bc280-bc-1`), volumes, and network. Manage them independently:

```bash
docker compose -p bc275 logs -f     # logs for instance 1
docker compose -p bc280 down        # stop instance 2
docker compose -p bc275 down -v     # stop instance 1 and remove its volumes
```

**Important:** Every port must be unique across instances — you'll get a bind error if two instances try to map the same host port. The easiest approach is to pick a port offset (e.g. +10000) for each additional instance.

For convenience, you can create a separate `.env` file per instance:

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

## Endpoints

After `docker compose up`, these endpoints are available:

| Endpoint | URL | Purpose |
|----------|-----|---------|
| Dev | `http://localhost:7049/BC/dev` | Publish extensions, download symbols |
| OData | `http://localhost:7048/BC/ODataV4` | Data access |
| API | `http://localhost:7052/BC/api/v2.0` | Business API |

Authentication: `admin` / `Admin123!` (NavUserPassword)

## Local Development with VS Code

### Setup

1. Start BC:
   ```bash
   docker compose up -d --wait
   ```

2. Add a `launch.json` to your AL project's `.vscode/` folder:
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

3. Download symbols — press `Ctrl+Shift+P` → **AL: Download Symbols**, or manually:
   ```bash
   mkdir -p .alpackages
   for app in System "System Application" "Base Application" "Application"; do
     curl -sf -u admin:Admin123! \
       "http://localhost:7049/BC/dev/packages?publisher=Microsoft&appName=$(echo $app | sed 's/ /%20/g')&appVersion=0.0.0.0" \
       -o ".alpackages/${app}.app"
   done
   ```

4. Publish — press `F5` or `Ctrl+F5` in VS Code. The AL extension uses the `launch.json` settings to publish via the dev endpoint.

### Publishing from the command line

```bash
# Compile
AL compile "/project:." "/packagecachepath:.alpackages" "/out:MyExtension.app"

# Publish
curl -u admin:Admin123! -X POST \
  -F "file=@MyExtension.app;type=application/octet-stream" \
  "http://localhost:7049/BC/dev/apps?SchemaUpdateMode=forcesync"
```

### Running Tests

The test framework (Test Runner, Library Assert, Library Variable Storage, Any) is published automatically on first boot.

1. **Publish your test app** (separate from running tests):
   ```bash
   curl -u admin:Admin123! -X POST \
     -F "file=@MyTestApp.app;type=application/octet-stream" \
     "http://localhost:7049/BC/dev/apps?SchemaUpdateMode=forcesync"
   ```

2. **Run tests**:
   ```bash
   # Run all test codeunits from your app
   ./scripts/run-tests.sh --app MyTestApp.app --codeunit-range 50000..50100

   # Run a single test codeunit
   ./scripts/run-tests.sh --app MyTestApp.app --codeunit-range 50000

   # Run without .app (codeunit-level results only, no per-method detail)
   ./scripts/run-tests.sh --codeunit-range 50000..50100
   ```

   Output:
   ```
   === BC Test Runner ===
   Company: CRONUS International Ltd.
   Populating test suite...
     Codeunit 50000: My Tests
       - TestCustomerCreation
       - TestSalesOrderPosting
     Test codeunits: 1, Test methods: 2

   === Test Results ===
     PASS TestCustomerCreation        50000
     PASS TestSalesOrderPosting       50000

   Results: 2 total, 2 passed, 0 failed, 0 skipped
   ```

   The `--app` flag enables per-method results by parsing the `.app` file's symbol metadata. Without it, you get codeunit-level results only.

## GitHub Codespaces

This repository includes a devcontainer configuration. Open it in a Codespace and BC starts automatically via Docker-in-Docker. The AL Language extension is pre-installed.

[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/StefanMaron/MsDyn365Bc.On.Linux)

## How It Works

The BC service tier is a .NET 8 application designed for Windows. This project patches it to run on Linux using a [.NET startup hook](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/debugging-profiling#startup-hooks) that intercepts and fixes Windows-specific calls at runtime:

- **Win32 P/Invoke stubs** — `kernel32.dll`, `user32.dll`, `advapi32.dll` etc. redirected to a shared library with Linux-compatible implementations
- **Assembly resolution** — .NET reference assemblies and type-forward merging for Cecil-based compilation
- **Service stubs** — `HttpSys` → Kestrel redirect, `PerformanceCounter`, `WindowsIdentity`, `Geneva ETW` stubs
- **Binary patches** — `CodeAnalysis.dll` and `Mono.Cecil.dll` fixes for type-forwarding resolution on Linux

The SQL Server runs as a separate container using the official `mssql/server:2022` Linux image.

## Version Support

The CI workflow tests across multiple BC versions. Trigger it manually with custom versions:

```
versions: "27.0,27.5,28.0"
```

## Requirements

- Docker with Compose v2
- ~4 GB RAM (2 GB for SQL, 1-2 GB for BC)
- ~3 GB disk for artifacts (downloaded once, cached in Docker volumes)

First boot takes 2-5 minutes (artifact download + database restore). Subsequent starts take ~20 seconds.
