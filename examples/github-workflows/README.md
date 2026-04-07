# GitHub Actions starter workflows for bc-linux

Two copy-paste GitHub Actions workflows that run AL tests against a Business
Central NST on Linux. Both pull the published `ghcr.io/stefanmaron/msdyn365bc.on.linux/bc-runner`
image (publicly available, no auth needed) and use the bc-linux scripts to
boot BC, publish your apps, and execute tests via the bundled
TestRunnerExtension.

## Pick a template

| File | When to use |
|------|-------------|
| [`bc-test-from-source.yml`](./bc-test-from-source.yml) | Your AL source lives in the repo and you want CI to compile it. Installs the Linux AL compiler tool, stages BC symbols from cached artifacts, builds your app + test apps, then publishes and runs them. |
| [`bc-test-prebuilt.yml`](./bc-test-prebuilt.yml) | You already have `.app` files (built by another job, vendor-supplied, or committed to the repo). Skips compilation and goes straight to publish + run. |

Both workflows:

- Boot BC and SQL Server in Linux containers (no Windows runner required)
- Cache BC artifacts between runs (`actions/cache`)
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
| AL compile errors about missing symbols | The "Stage symbols" step couldn't find a dependency. Check the artifact cache structure and add the missing path to that step. |
| Test fails with `serviceConnection` errors | Use the latest `bc-runner` image — `serviceConnection`/TestPage support depends on patches #17–#23 in the startup hook. |

## Reporting issues

If a workflow fails on the bc-linux side (not your AL code), open an issue
at <https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues> with:

- Your `env:` block
- The BC version you targeted
- The full failure log tail
