# Azure DevOps starter pipelines for bc-linux

Two copy-paste Azure DevOps pipelines that run AL tests against a Business
Central NST on Linux. Both pull the public
`ghcr.io/stefanmaron/msdyn365bc.on.linux/bc-runner` image (no GHCR auth
needed) and use the bc-linux scripts to boot BC, publish your apps, and
execute tests via the bundled TestRunnerExtension.

## Pick a template

| File | When to use |
|------|-------------|
| [`bc-test-from-source.yml`](./bc-test-from-source.yml) | Your AL source lives in the repo and you want CI to compile it. Installs the Linux AL compiler tool, downloads BC artifacts, stages symbols, builds your app + test apps, then publishes and runs them. |
| [`bc-test-prebuilt.yml`](./bc-test-prebuilt.yml) | You already have `.app` files (built by another job, vendor-supplied, or committed to the repo). Skips compilation and goes straight to publish + run. |

## Setup

1. **Copy** one of the YAML files into your repo as
   `azure-pipelines.yml` (or any name).
2. **Edit the `variables:` block** at the top:
   - `BC_VERSION`, `BC_COUNTRY`, `BC_TYPE` — defaults `27.5` / `w1` / `sandbox`.
   - **From-source**: `APP_DIRS` and `TEST_APP_DIRS` — space-separated paths
     to directories containing `app.json`.
   - **Pre-built**: `APP_FILES` and `TEST_APP_FILES` — space-separated paths
     to `.app` files in your repo.
   - `CODEUNIT_RANGE` — IDs of your test codeunits. Accepts `70000..70099`
     (single range), `70000..70099|130450..130459` (multiple ranges,
     pipe-separated), `70000,70001,70002` (explicit ids), or any mix.
3. **Optional ISV license**: if you need your own developer / partner
   license instead of the public Cronus one, base64-encode it and add
   it to your pipeline's variable group as **secret variable**
   `BC_LICENSE_B64`:
   ```bash
   base64 -w0 < your-license.bclicense
   # paste the output as a secret variable in Azure DevOps
   ```
   The pipeline imports the license BEFORE the BC service tier starts,
   so no boot-import-restart cycle is needed. Leave the variable unset
   to use the default Cronus license.
4. In Azure DevOps: **Pipelines → New pipeline → Existing Azure Pipelines
   YAML file**, point at the file you just committed, save and run.

The pipelines use the **Microsoft-hosted `ubuntu-latest` agent**, which
already has Docker, Docker Compose, .NET 8 SDK, curl, and git
preinstalled — no service connection or self-hosted agent required.

## What's running under the hood

- **Image**: `ghcr.io/stefanmaron/msdyn365bc.on.linux/bc-runner:latest` —
  multi-stage Docker image that downloads Microsoft BC artifacts at boot,
  copies the .NET 8 service tier into place, applies a startup-hook patch
  set, restores the CRONUS demo DB, and exposes BC on the standard
  7045–7089 ports.
- **`git clone` of MsDyn365Bc.On.Linux**: brings in `docker-compose.yml`,
  `run-tests.sh`, the bundled `TestRunnerExtension.app`, and
  `download-artifacts.sh`. We use a plain `git clone` (rather than the
  Azure DevOps repository resource) so the pipeline works without any
  service connection setup.
- **TestRunnerExtension**: an AL extension shipped with the image. Exposes
  the OData/WebSocket endpoints `run-tests.sh` uses to populate test suites,
  execute methods, and read results.
- **No artifact caching** — artifacts are downloaded fresh each run from
  Microsoft's CDN. With the HTTP/1.1 download path in
  `download-artifacts.sh` this lands at ~50s for a full BC platform on a
  Microsoft-hosted Ubuntu agent (~88 MB/s observed in practice), which is
  not worth the complexity of a cache step.

## Customising further

- **Multiple BC versions**: convert the single job into a matrix using a
  `strategy.matrix:` block. Make `BC_VERSION` a matrix variable.
- **Different artifact source**: pre-populate
  `$(Pipeline.Workspace)/artifact-cache/$(BC_VERSION)` yourself before the
  download step (or skip the download step altogether).
- **Multiple test apps with shared symbols**: build production apps first,
  copy their `.app` outputs into each test app's `.alpackages/` directory
  (the from-source template already does this).
- **Custom BC user / company**: pass `--auth user:pass` and `--company "..."`
  to `run-tests.sh`.
- **Self-hosted agent**: works the same way as long as Docker, .NET 8, curl,
  and git are available.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `BC unhealthy` after several minutes | Artifact download timed out, or first-boot DB restore is still running. Look at the failure log tail in the pipeline output. |
| `publish failed: 422` | App schema conflict — bump your app version. |
| `Could not get company ID` | BC isn't reachable on `localhost:7048`. Check that the container reached `healthy` and that the OData port is mapped. |
| AL compile errors about missing symbols | The "Stage symbols" step couldn't find a dependency. Add the missing path to that step. |
| Test fails with `serviceConnection` errors | Use the latest `bc-runner` image — `serviceConnection`/TestPage support depends on patches #17–#23 in the startup hook. |
| `docker: command not found` | You're on a self-hosted agent without Docker installed, or `vmImage:` is not `ubuntu-latest`. |

## Reporting issues

If a pipeline fails on the bc-linux side (not your AL code), open an issue
at <https://github.com/StefanMaron/MsDyn365Bc.On.Linux/issues> with:

- Your `variables:` block
- The BC version you targeted
- The full failure log tail
