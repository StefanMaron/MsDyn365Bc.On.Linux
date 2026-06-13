# Container optimization flags (benchmark harness)

All optimizations from the 2026-06 container-optimization analysis live on this
branch, **each behind an independent flag**, so the impact of every one can be
measured in isolation. The caching-related idea (persisting `/bc/patched`) was
deliberately left out as requested.

## How defaults are chosen, and how to measure

Every flag defaults to **current behaviour** (the optimization is *off*). So an
unflagged `docker compose up` / `docker compose build` is byte-for-byte the
pre-optimization baseline, and CI stays green.

To attribute impact, use **add-one-in** (the inverse of leave-one-out):

1. Measure the baseline once with no flags set.
2. Enable exactly **one** flag, rebuild/reboot, measure again.
3. The delta from baseline is that single optimization's contribution.

> Why opt-in and not opt-out: three of these change behaviour in ways that would
> break a normal build or CI run if they were on by default (the platform trim
> removes the AL compiler + test assemblies; `system-only` changes which apps are
> installed; the go-sqlcmd swap is unvalidated — see its note). Opt-in keeps the
> baseline known-good. Flip the defaults in `docker-compose.yml` if you'd rather
> run opt-out.

### What to measure where

| Metric | How |
|---|---|
| **Image size** | `docker compose build bc` then `docker images bc-runner:local --format '{{.Size}}'` |
| **Artifact-volume size** | fresh volume (`docker compose down -v`), boot, then `du -sh` the `bc-artifacts` volume mountpoint (or `docker run --rm -v <proj>_bc-artifacts:/a alpine du -sh /a`) |
| **Cold-boot time** | the entrypoint already logs `[Ns]` per step and a final `Total startup: Ns`; also per-step `Step N (...): Ns` lines |
| **NST-ready time** | entrypoint logs `Dev endpoint ready (HTTP ...) — NST startup: Ns` |
| **Resident memory** | `docker stats --no-stream` (RSS of the `bc` container) once healthy |

## The flags

| Flag | Kind | Default | Bucket | Effect |
|---|---|---|---|---|
| `OPT_GO_SQLCMD` | build arg | `0` | image size | Replace `mssql-tools18` (+unixODBC/msodbcsql18) with the standalone `go-sqlcmd` binary. **Biggest image shrink. Unvalidated — see below.** |
| `OPT_DROP_GNUPG` | build arg | `0` | image size | Read the MS apt key as an armored `.asc` keyring instead of dearmoring with `gpg`, so `gnupg` + deps don't ship. (No effect when `OPT_GO_SQLCMD=1`, which drops the apt repo entirely.) |
| `DOTNET_gcServer` | env | `1` | memory | `0` = Workstation GC (one heap, not one-per-core) → lower NST RSS, possibly lower compile throughput. |
| `BC_MINIMAL_PLATFORM` | env | `0` | volume size | `1` = drop `ModernDev/` (bundled AL compiler) and `Test Assemblies/` from platform extraction. **Breaks platform-bundled AL compile and MS test suites** — use only for run-only containers. Needs a fresh artifact volume to take effect. |
| `BC_CLEAR_ALL_APPS=system-only` | env | (existing var) | boot speed | Boot NST with only the System app installed, then republish the rest from R2R artifacts after NST is up. See below. |
| `BC_SYSTEM_ONLY_KEEP_IDS` | env | (empty) | — | Helper for the above: widen the keep set (comma-separated lowercase app GUIDs) if NST won't boot with System alone. |
| `SQLCMD_TLS` | env | (auto) | — | Helper for go-sqlcmd: override the SQL client TLS flags if the auto-selected ones don't connect. |

## Per-optimization notes

### `OPT_GO_SQLCMD` — go-sqlcmd instead of mssql-tools18 (image size)
```bash
OPT_GO_SQLCMD=1 docker compose build bc
docker images bc-runner:local --format '{{.Repository}} {{.Size}}'   # compare to baseline
```
- mssql-tools18 drags in unixODBC + msodbcsql18 (~200 MB+). go-sqlcmd is a single
  static binary, and using it lets us skip the Microsoft apt repo (and gnupg)
  entirely.
- The image records its SQL client in `/etc/bc-sqlcmd-flavor`; the entrypoint
  reads it and picks TLS flags (`-C -No` for ODBC, `-C` for go-sqlcmd).
- **Unvalidated.** There is no Docker daemon in the analysis environment, so the
  go-sqlcmd flag compatibility and the release-asset URL could not be smoke-tested.
  Before relying on it: build with the flag, boot, and confirm Step 3 (DB setup)
  succeeds. If the client can't connect, set `SQLCMD_TLS` (e.g. `-C -N true`) and/or
  override `GO_SQLCMD_URL` (build arg) with the correct release asset for your
  arch/version. `OPENROWSET BULK` (license import) is server-side and client-agnostic.

### `OPT_DROP_GNUPG` — armored apt keyring (image size)
```bash
OPT_DROP_GNUPG=1 docker compose build bc
```
Only meaningful with `OPT_GO_SQLCMD=0` (the mssql-tools18 path). Small win; pairs
naturally with measuring the ODBC image size without gnupg.

### `DOTNET_gcServer=0` — Workstation GC (memory)
```bash
DOTNET_gcServer=0 docker compose up -d --wait
docker stats --no-stream   # compare RSS to a default (Server GC) boot
```
Server GC scales heap + GC-thread count with cores. Note: `PERFORMANCE-IDEAS.md`
once recorded Server GC "breaking the API endpoint", while the current entrypoint
ships Server GC and `CLAUDE.md` says it's fine — this flag is also the cheap way to
re-settle that contradiction. Watch the API/OData health on the `=0` run.

### `BC_MINIMAL_PLATFORM=1` — trim platform extraction (volume size)
```bash
docker compose down -v                       # extraction is guarded; needs a fresh volume
BC_MINIMAL_PLATFORM=1 docker compose up -d --wait
# then: du -sh the bc-artifacts volume vs a baseline extraction
```
Drops `ModernDev/` and `Test Assemblies/`. Do **not** combine with the smoke-test /
test-versions CI or any AL compile that uses the platform's bundled compiler — they
will fail with these trees missing. Pure run / pure-API containers are unaffected.

### `BC_CLEAR_ALL_APPS=system-only` — minimal-boot + R2R republish (boot speed)
```bash
BC_CLEAR_ALL_APPS=system-only docker compose up -d --wait
# compare entrypoint 'Total startup' and 'NST startup' vs a default (false) boot,
# and vs BC_CLEAR_ALL_APPS=true (full clear + republish-all)
```
Tests the hypothesis that booting NST with the full installed app set (and letting
it re-emit/validate them) is slower than booting with a minimal set and republishing
from the ready-to-run packages, whose binaries are already pre-seeded into the
assembly cache before NST starts.

Caveats to keep in mind when reading the numbers:
- The existing **R2R pre-seed** step already puts the compiled binaries on disk for
  *all* apps before NST starts, so part of this benefit may already be realized in
  the default path. The honest question the timing answers is whether *fewer
  installed apps at boot* (less startup metadata/validation) beats the cost of
  republishing afterward.
- Republish uses `SchemaUpdateMode=forcesync`; for Base Application that still runs a
  schema sync (the binaries being pre-shipped doesn't skip schema work).
- **NST may not boot with only the System app.** If the dev endpoint never comes up,
  widen the keep set, e.g. the full application-stack baseline:
  ```bash
  BC_CLEAR_ALL_APPS=system-only \
  BC_SYSTEM_ONLY_KEEP_IDS=8874ed3a-0643-4247-9ced-7a7002f7135d,63ca2fa4-4f03-4f2b-a480-172fef340d3f,f3552374-a1f2-4356-848e-196002525837,437dbf0e-84ff-417a-965d-ed2bb9650972,c1335042-3002-4257-bf8a-75c898ccb1b8 \
  docker compose up -d --wait
  ```
  (System, System Application, Business Foundation, Base Application, Application
  umbrella). Bisect downward from there to find the minimal bootable set.
