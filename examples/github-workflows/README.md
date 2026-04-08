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
| `codeunit_range` | **yes** | — | IDs of your **test** codeunits to execute. Production app codeunits are published but not run. Accepts `"50000..99999"` (single AL range), `"50000..50100\|130450..130459"` (multiple ranges, pipe-separated), `"50000,50001,50002"` (explicit ids), or any mix. |
| `al_tool_version` | no | `16.2.28.57946` | Linux AL compiler tool version |
| `runner_image` | no | public ghcr.io tag | Override the bc-runner image |
| `bc_linux_ref` | no | `master` | Git ref of `MsDyn365Bc.On.Linux` to check out for scripts |
| `timeout_minutes` | no | `45` | Job timeout |
| `enable_code_cop` | no | `false` | Enable Microsoft CodeCop |
| `enable_ui_cop` | no | `false` | Enable Microsoft UICop |
| `enable_app_source_cop` | no | `false` | Enable Microsoft AppSourceCop |
| `enable_per_tenant_extension_cop` | no | `false` | Enable Microsoft PerTenantExtensionCop |
| `custom_code_cops` | no | `""` | Comma- or newline-separated list of additional cop DLLs (local paths or `https://` URLs). `.nupkg` URLs are unzipped and every DLL under `lib/net8.0/` is added — point at the [ALCops](https://github.com/ALCops/Analyzers) release nupkg to enable all 7 community cops in one shot. |
| `ruleset_file` | no | `""` | Local path or `https://` URL to a JSON ruleset file. Note: AL compiler accepts JSON only, not the classic Visual Studio XML `.ruleset` format. |
| `enable_external_rulesets` | no | `false` | Required when your `ruleset_file` includes remote rulesets via `includedRuleSets[].path` URL entries (e.g. shared org-wide rulesets). |
| `enable_code_analyzers_on_test_apps` | no | `false` | When `false` (default — matches AL-Go), test app compiles skip the analyzer flags entirely. Production apps still get them. |

### Inputs (prebuilt)

Same as above but with `app_files` / `test_app_files` (paths to `.app`
files) instead of `app_dirs` / `test_app_dirs`, and no `al_tool_version`.

### Secrets (both flavours)

| Secret | Required | Description |
|---|---|---|
| `bc_license` | no | Optional ISV/developer BC license, **base64-encoded**. When set, the workflow imports this license BEFORE NST starts — no boot/import/restart cycle. Leave unset to use the public Cronus license. |

To prepare the secret:

```bash
base64 -w0 < your-license.bclicense | gh secret set BC_LICENSE
```

Then call the reusable workflow with:

```yaml
jobs:
  bc-tests:
    uses: StefanMaron/MsDyn365Bc.On.Linux/.github/workflows/bc-test-from-source.yml@master
    with:
      ...
    secrets:
      bc_license: ${{ secrets.BC_LICENSE }}
```


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
   - `CODEUNIT_RANGE` — IDs of your test codeunits. Accepts `70000..70099`
     (single range), `70000..70099|130450..130459` (multiple ranges,
     pipe-separated), `70000,70001,70002` (explicit ids), or any mix.
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

## Custom analyzers and rulesets

The from-source flavours support Microsoft's four cops (CodeCop /
AppSourceCop / PerTenantExtensionCop / UICop), arbitrary custom cops,
and JSON rulesets — including rulesets that include remote ones via
`includedRuleSets[].path` URLs.

The reusable workflow exposes them as inputs (see the table above).
The inlined templates expose the same set as `env:` block variables.
Both default to off, so existing workflows that don't touch these see
zero behaviour change.

When **any** analyzer is configured, the compile step captures
output and fails the workflow if it sees `AD0001`, `Could not load`,
`BadImageFormatException`, or `PlatformNotSupportedException` — so a
silently-broken cop can never produce a green build with disabled
enforcement. This is the load guarantee bc-linux gives you on top of
AL compile's normal exit-code semantics.

A worked example using the reusable flavour:

```yaml
jobs:
  bc-tests:
    uses: StefanMaron/MsDyn365Bc.On.Linux/.github/workflows/bc-test-from-source.yml@master
    with:
      bc_version:     "27.5"
      app_dirs:       "app"
      test_app_dirs:  "test"
      codeunit_range: "50000..99999"
      enable_code_cop: true
      enable_ui_cop: true
      enable_app_source_cop: true
      enable_per_tenant_extension_cop: true
      custom_code_cops: "https://github.com/ALCops/Analyzers/releases/download/v0.6.1/ALCops.Analyzers.0.6.1.nupkg"
      ruleset_file: "rules.rulset.json"
      enable_external_rulesets: true
```

The full design + the six gotchas the empirical investigation surfaced
(JSON-only rulesets, `.rulset.json` typo, no `app.json` ruleset prop,
`/enableexternalrulesets` requirement, `AL1033` is a catch-all error
that hides the real cause, `ALCops.Common.dll` co-load) live in
[`ANALYZER-SUPPORT-PLAN.md`](../../ANALYZER-SUPPORT-PLAN.md).

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
