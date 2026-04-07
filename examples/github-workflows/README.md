# GitHub Actions starter workflows for bc-linux

Run AL tests against a Business Central NST on Linux from your own GitHub
repo. All flavours pull the public
`ghcr.io/stefanmaron/msdyn365bc.on.linux/bc-runner` image (no auth needed)
and use the bc-linux scripts to boot BC, publish your apps, and execute
tests via the bundled TestRunnerExtension.

## ✨ Recommended: reusable workflow (10-line consumer file)

`bc-linux` ships two **reusable workflows** in its own `.github/workflows/`
that you can call from your repo. The consumer file is tiny:

```yaml
# .github/workflows/bc-test.yml
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

Two flavours are available — pick whichever fits:

| Reusable workflow | When to use |
|---|---|
| `bc-test-from-source.yml` | Compile AL source from your repo, stage symbols from BC artifacts, publish, run tests. |
| `bc-test-prebuilt.yml`    | Skip compilation; publish and run pre-built `.app` files. |

A copy-paste consumer example is in
[`bc-test-using-reusable.yml`](./bc-test-using-reusable.yml).

**Pin to a tag in production.** `@master` is great while iterating, but for
reproducible CI runs swap it for a release tag once one exists
(`@v1`, `@v2.1`, etc.) so an upstream change can't break your pipeline.

### Inputs (from-source)

| Input | Required | Default | Description |
|---|---|---|---|
| `bc_version` | no | `27.5` | BC platform version |
| `bc_country` | no | `w1` | BC country code |
| `bc_type` | no | `sandbox` | `sandbox` or `onprem` |
| `app_dirs` | no | `""` | Space-separated dirs containing `app.json` for production apps |
| `test_app_dirs` | **yes** | — | Space-separated dirs containing `app.json` for test apps |
| `codeunit_range` | **yes** | — | ID range of your **test** codeunits to execute (e.g. `50000..99999`). Production app codeunits are published but not run. |
| `keep_app_ids` | no | `""` | Comma-separated extra app GUIDs to always keep in the BC database, on top of the baseline + your apps' transitive dependencies. Set to `"all"` to opt out of selective keeping and preserve every stock extension. See "Faster startup via selective extension keeping" below. |
| `al_tool_version` | no | `16.2.28.57946` | Linux AL compiler tool version |
| `runner_image` | no | public ghcr.io tag | Override the bc-runner image |
| `bc_linux_ref` | no | `master` | Git ref of `MsDyn365Bc.On.Linux` to check out for scripts |
| `timeout_minutes` | no | `45` | Job timeout |

### Inputs (prebuilt)

Same as above but with `app_files` / `test_app_files` (paths to `.app`
files) instead of `app_dirs` / `test_app_dirs`, and no `al_tool_version`.

### Faster startup via selective extension keeping

By default the templates analyse your `app.json` (or `.app`) files,
walk the transitive dependency closure against the downloaded BC
artifacts, and tell the container to **uninstall every stock extension
that's not actually needed** before NST starts. Result: BC boots faster
because there are far fewer apps to install/upgrade on first run.

Always-kept baseline (regardless of dependency analysis):

| App | GUID |
|---|---|
| System (AL platform) | `8874ed3a-0643-4247-9ced-7a7002f7135d` |
| System Application | `63ca2fa4-4f03-4f2b-a480-172fef340d3f` |
| Business Foundation | `f3552374-a1f2-4356-848e-196002525837` |
| Base Application | `437dbf0e-84ff-417a-965d-ed2bb9650972` |
| Application (umbrella) | `c1335042-3002-4257-bf8a-75c898ccb1b8` |

If your project depends on additional stock extensions that the
resolver can't infer (rare — would mean your `app.json` is missing a
dependency it actually needs at runtime), pass them via `keep_app_ids`
as a comma-separated GUID list. Or set `keep_app_ids: all` to disable
selective keeping entirely and preserve every stock extension —
useful as an escape hatch if you suspect a missing dep is causing
test failures.

## Alternative: inlined templates (paste into your repo)

If you'd rather see exactly what's happening — or you want to fork-and-tweak
the steps — copy one of these files into `.github/workflows/bc-test.yml`
in your repo and edit the `env:` block at the top.

| File | When to use |
|------|-------------|
| [`bc-test-from-source.yml`](./bc-test-from-source.yml) | Your AL source lives in the repo and you want CI to compile it. |
| [`bc-test-prebuilt.yml`](./bc-test-prebuilt.yml) | You already have `.app` files; skips compilation. |

These are functionally equivalent to the reusable workflows above — just
copied into your repo so you can edit them freely. Trade-off: when bc-linux
ships an improvement, you'll have to re-copy it.

All flavours:

- Boot BC and SQL Server in Linux containers (no Windows runner required)
- Download BC artifacts on demand (~50s on a hosted runner thanks to the
  HTTP/1.1 fix in `download-artifacts.sh`)
- Publish via the BC dev endpoint
- Execute tests via `bc-linux/scripts/run-tests.sh` (hybrid OData + WebSocket)
- Print the BC log tail on failure

## Setup

1. **Copy** one of the YAML files into your repo at
   `.github/workflows/bc-test.yml` (or any name you like).
2. **Edit the `env:` block** at the top:
   - `BC_VERSION`, `BC_COUNTRY`, `BC_TYPE` — which Microsoft BC build to test
     against. Defaults: `27.5` / `w1` / `sandbox`.
   - **From-source**: `APP_DIRS` and `TEST_APP_DIRS` — space-separated paths
     to directories containing `app.json`.
   - **Pre-built**: `APP_FILES` and `TEST_APP_FILES` — space-separated paths
     to `.app` files in your repo.
   - `CODEUNIT_RANGE` — AL codeunit ID range that covers your test codeunits
     (e.g. `70000..70099`).
3. **Commit & push**. The workflow runs on every push and PR to `main`/`master`,
   plus manually via the Actions tab.

## What's running under the hood

- **Image**: `ghcr.io/stefanmaron/msdyn365bc.on.linux/bc-runner:latest` — multi-stage Docker
  image that downloads Microsoft BC artifacts at boot, copies the .NET 8
  service tier into place, applies a startup-hook patch set, restores the
  CRONUS demo DB, and exposes BC on the standard 7045–7089 ports.
- **bc-linux repo checkout**: brings in `docker-compose.yml`, `run-tests.sh`,
  the `TestRunnerExtension.app` (bundled in the image, but the script also
  exists on the host for orchestration), and `download-artifacts.sh`.
- **TestRunnerExtension**: an AL extension shipped with the image. Exposes
  the OData/WebSocket endpoints `run-tests.sh` uses to populate test suites,
  execute methods, and read results.

## Customising further

- **Multiple BC versions**: turn the single job into a matrix on `BC_VERSION`.
  See `.github/workflows/test-versions.yml` in the bc-linux repo for a
  worked example.
- **Different artifact source**: pass `BC_ARTIFACT_URL=skip` to the BC
  container env and pre-populate `BC_ARTIFACTS_DIR` yourself.
- **Multiple test apps with shared symbols**: build production apps first,
  copy their `.app` outputs into each test app's `.alpackages/` directory
  (the from-source template already does this).
- **Custom BC user / company**: pass `--auth user:pass` and `--company "..."`
  to `run-tests.sh`.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `BC unhealthy` after several minutes | Artifact download timed out, or first-boot DB restore is still running. Look at the failure log tail in the workflow output. |
| `publish failed: 422` | App schema conflict — make sure your version number bumps between runs, or set `SchemaUpdateMode=ForceSync` (already the default in these templates). |
| `Could not get company ID` | BC isn't reachable on `localhost:7048`. Check that the container is `healthy` and that the OData port is mapped. |
| AL compile errors about missing symbols | The "Stage symbols" step couldn't find a dependency. Check the downloaded artifact structure and add the missing path to that step. |
| Test fails with `serviceConnection` errors | Use the latest `bc-runner` image — `serviceConnection`/TestPage support depends on patches #17–#23 in the startup hook. |

## Reporting issues

If a workflow fails on the bc-linux side (not your AL code), open an issue
at <https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues> with:

- Your `env:` block
- The BC version you targeted
- The full failure log tail
