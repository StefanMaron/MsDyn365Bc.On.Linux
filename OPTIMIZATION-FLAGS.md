# Container optimization flags (benchmark harness)

> **Measured results are at the bottom of this file** ("Measured results"
> section). TL;DR: the big lever is **reducing the installed-app count at boot**
> (cuts NST startup 312s→10s *and* memory 4.6→1.0 GiB). Workstation GC cuts
> memory ~57% for +27% startup. `go-sqlcmd` was *counterproductive* for image
> size (measured) and should not be adopted.

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

> Why opt-in and not opt-out: these change behaviour in ways that would break a
> normal build or CI run if they were on by default (the platform trim removes the
> AL compiler + test assemblies; `system-only` changes which apps are installed).
> Opt-in keeps the baseline known-good. Flip the defaults in `docker-compose.yml`
> if you'd rather run opt-out.

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
| `OPT_GO_SQLCMD` | build arg | `0` | image size | Replace `mssql-tools18` with the standalone `go-sqlcmd` binary. **MEASURED COUNTERPRODUCTIVE (image +12 MB) — do not adopt.** See Measured results. |
| `OPT_DROP_GNUPG` | build arg | `0` | image size | Read the MS apt key as an armored `.asc` keyring instead of dearmoring with `gpg`, so `gnupg` + deps don't ship. (No effect when `OPT_GO_SQLCMD=1`, which drops the apt repo entirely.) |
| `DOTNET_gcServer` | env | `1` | memory | `0` = Workstation GC (one heap, not one-per-core) → lower NST RSS, possibly lower compile throughput. |
| `BC_MINIMAL_PLATFORM` | env | `0` | volume size | `1` = drop `ModernDev/` (bundled AL compiler) and `Test Assemblies/` from platform extraction. **Breaks platform-bundled AL compile and MS test suites** — use only for run-only containers. Needs a fresh artifact volume to take effect. |
| `BC_CLEAR_ALL_APPS=system-only` | env | (existing var) | boot speed | Boot NST with only the System app installed, then republish the rest from R2R artifacts after NST is up. See below. |
| `BC_SYSTEM_ONLY_KEEP_IDS` | env | (empty) | — | Helper for the above: widen the keep set (comma-separated lowercase app GUIDs) if NST won't boot with System alone. |
| `SQLCMD_TLS` | env | (auto) | — | Helper for go-sqlcmd: override the SQL client TLS flags if the auto-selected ones don't connect. |

## Per-optimization notes

### `OPT_GO_SQLCMD` — go-sqlcmd instead of mssql-tools18 (image size) — REJECTED
```bash
OPT_GO_SQLCMD=1 docker compose build bc
docker images bc-runner:local --format '{{.Repository}} {{.Size}}'   # compare to baseline
```
The hypothesis was that mssql-tools18 (+unixODBC/msodbcsql18) was heavy (~200 MB).
**Measured: false.** The whole mssql-tools18 install is ~3 MB here, while the
go-sqlcmd binary is 21.5 MB, so this flag makes the image *bigger* (+12 MB). The
mechanism is sound and kept for completeness (the image records its SQL client in
`/etc/bc-sqlcmd-flavor`; the entrypoint reads it and picks TLS flags — `-C -No` for
ODBC, `-C` for go-sqlcmd; override with `SQLCMD_TLS`, or `GO_SQLCMD_URL` build arg
for a different asset), but **do not adopt it for image size.**

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
Server GC scales heap + GC-thread count with cores. **Measured:** Workstation GC
cut idle BC memory 4.58 GiB → 1.97 GiB (~−57%) for +27% NST startup, and both
OData and the API endpoint stayed healthy — so the old `PERFORMANCE-IDEAS.md`
note that Server GC "breaks the API endpoint" does not reproduce on BC 28.1.

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

---

# Measured results

Measured 2026-06-13 in the sandbox dev environment.
**Host:** 4 vCPU, 15.7 GiB RAM, Docker 29.3 (containerd snapshotter), local
MITM TLS proxy. **Image:** BC 28.1 sandbox w1. Builds used the gitignored
`src/Dockerfile.local-ca` (injects the proxy CA so apt/curl/nuget TLS works in
the build containers — see "Sandbox build note" below). Numbers are single runs,
so treat small deltas as noise; the large ones are the story.

## Boot speed + memory (the headline)

"NST startup" is the download-independent metric (the `Dev endpoint ready —
NST startup: Ns` log line). "Total" is wall-clock to `/tmp/bc-ready`; warm =
artifacts already cached. Memory = `docker stats` RSS of the bc container,
idle just after boot.

| Config | NST startup | Total (warm) | BC memory idle | Apps installed | OData/API |
|---|---|---|---|---|---|
| **Baseline** (Server GC, all apps) | **312 s** | ~379 s | **4.58 GiB** | ~134 | 200 / 200 |
| **`BC_CLEAR_ALL_APPS=system-only`** | **10 s** | **161 s** | **1.03 GiB** | 6 (core stack) | 200, healthy |
| **`DOTNET_gcServer=0`** (Workstation GC, all apps) | 397 s | 465 s | **1.97 GiB** | ~134 | 200 / 200, healthy |

Cold baseline total was **813 s**, of which **434 s** was the one-time artifact
download (899 MB app + 1.3 GB platform at the proxy's ~5 MB/s) + 54 s extract.
That download is cached after the first boot, so it's excluded from the warm
comparisons above.

### `system-only` — the standout, with a caveat
Booting NST with only the System app installed cut **NST startup 312 s → 10 s
(~30×)**, total warm boot to **161 s**, and idle memory to **1.03 GiB**. The
resulting container is **functional**: OData `/Company` returns 200 and the
healthcheck passes, because the 5 apps that republished successfully are the
core application stack (System Application, Business Foundation, Base
Application, Application umbrella).

Caveat that matters: the post-NST step republishes *every* snapshotted app, and
**129 of 134 stock apps failed with HTTP 422** (the `_Exclude_*` platform
markers and optional feature/Agent/AI/API apps — many can't be re-deployed via
the dev endpoint, and once a core dep 422s its dependents cascade). So
`system-only` as written gives you the **base stack only**, fast and light. If
your workload needs specific feature apps, widen `BC_SYSTEM_ONLY_KEEP_IDS`, or
better use the existing **`BC_CLEAR_ALL_APPS=selective` + `BC_KEEP_APP_IDS`**
flow, which keeps exactly the closure you need and gets the same fast-boot
benefit while staying functionally complete. The republish-all loop inherited
from `BC_CLEAR_ALL_APPS=true` is the wrong post-step for `system-only`; a
targeted republish (consumer app + transitive deps only) is the right one.

### Workstation GC — real memory win, modest startup cost
`DOTNET_gcServer=0` cut idle BC memory **4.58 GiB → 1.97 GiB (~−57%)** at the
cost of **+27% NST startup** (312 → 397 s) and +23% total. Both OData (7048) and
the API endpoint (7052) returned 200 and the container went healthy — so the old
`PERFORMANCE-IDEAS.md` note that "Server GC breaks the API endpoint" does **not**
reproduce on BC 28.1; both GC modes serve the API fine. Use Workstation GC for
memory-constrained hosts, Server GC (default) for fastest boot/throughput.

## Image size

Measured with the containerd image store. `docker images` SIZE (inflated by
per-image attestation/shared-layer accounting) and `docker image inspect .Size`
disagree in magnitude but agree in direction; the unambiguous facts are the
SQL-client footprints.

| Image | `docker images` | Note |
|---|---|---|
| baseline (mssql-tools18 + gnupg) | 465 MB | — |
| `OPT_DROP_GNUPG=1` | 455 MB | **−10 MB** — small win, no downside |
| `OPT_GO_SQLCMD=1` | 477 MB | **+12 MB — counterproductive** |

Why go-sqlcmd loses: the go-sqlcmd binary is **21.5 MB**, while the *entire*
`mssql-tools18` install (msodbcsql18 + tools) is only **~3 MB** here with
`--no-install-recommends`. My pre-measurement estimate of "~200 MB saved" was
simply wrong — unixODBC/msodbcsql18 is far leaner than assumed. **Do not adopt
`OPT_GO_SQLCMD`.** It builds and runs correctly (binary executes, flavor marker +
TLS-flag selection work), it's just bigger. `OPT_DROP_GNUPG` is a free small win.

## Artifact-volume size

Baseline extracted artifact volume = **3.1 GB** (app 1.3 GB + platform 1.9 GB).
`BC_MINIMAL_PLATFORM=1` drops:

| Tree | Size | 
|---|---|
| `ModernDev/` | **305 MB** |
| `Test Assemblies/` | 6.6 MB |
| **total saved** | **~312 MB (~10% of the volume)** |

Nearly all the saving is `ModernDev` (the bundled AL compiler); `Test Assemblies`
is negligible. Worth it only for run-only containers that neither compile AL with
the platform's bundled compiler nor run Microsoft's stock test suites.

## Ranking by impact (this environment)

1. **Reduce installed-app count at boot** (`system-only` / `selective`+keep-set)
   — by far the biggest lever: cuts boot time *and* memory dramatically. This is
   the answer to "speed of usage."
2. **Workstation GC** (`DOTNET_gcServer=0`) — large memory win if you can spend
   the startup time.
3. **`BC_MINIMAL_PLATFORM`** — ~312 MB volume, run-only containers only.
4. **`OPT_DROP_GNUPG`** — small, free image win.
5. **`OPT_GO_SQLCMD`** — rejected; makes the image larger.

## Sandbox build note (not for upstream)

Builds here require trusting the environment's MITM-proxy CA inside the build
containers. The mechanism (all gitignored, never committed): `.local-ca/`
holds the host CA bundle, `src/Dockerfile.local-ca` is generated from
`src/Dockerfile` by injecting `COPY .local-ca/ca-certificates.crt
/etc/ssl/certs/ca-certificates.crt` after each `FROM`, and
`docker-compose.override.yml` points the build at it. Regenerate the local
Dockerfile after editing `src/Dockerfile`. On a normal network none of this is
needed — build `src/Dockerfile` directly.
