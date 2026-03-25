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

## Endpoints

After `docker compose up`, these endpoints are available:

| Endpoint | URL | Purpose |
|----------|-----|---------|
| Dev | `http://localhost:7049/BC/dev` | Publish extensions, download symbols |
| OData | `http://localhost:7048/BC/ODataV4` | Data access |
| API | `http://localhost:7052/BC/api/v2.0` | Business API |

Authentication: `admin` / `Admin123!` (NavUserPassword)

## Publishing Extensions

Publish an `.app` file via the dev endpoint:

```bash
curl -u admin:Admin123! -X POST \
  -F "file=@MyExtension.app;type=application/octet-stream" \
  "http://localhost:7049/BC/dev/apps?SchemaUpdateMode=forcesync"
```

Or use the AL Language extension in VS Code — point `launch.json` to `http://localhost:7049`:

```json
{
    "server": "http://localhost",
    "serverInstance": "BC",
    "port": 7049,
    "authentication": "UserPassword"
}
```

## Downloading Symbols

Download symbol packages from the dev endpoint for use with the AL compiler:

```bash
# System symbols
curl -u admin:Admin123! \
  "http://localhost:7049/BC/dev/packages?publisher=Microsoft&appName=System&appVersion=0.0.0.0" \
  -o System.app

# System Application symbols
curl -u admin:Admin123! \
  "http://localhost:7049/BC/dev/packages?publisher=Microsoft&appName=System%20Application&appVersion=0.0.0.0" \
  -o SystemApplication.app
```

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
