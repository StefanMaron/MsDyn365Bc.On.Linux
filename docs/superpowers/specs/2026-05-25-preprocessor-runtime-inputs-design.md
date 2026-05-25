# Design: preprocessor_symbols + runtime_version inputs for bc-test-from-source.yml

**Date:** 2026-05-25  
**Status:** Approved  
**Scope:** `bc-test-from-source.yml`, `examples/github-workflows/`, `examples/azure-pipelines/`, `README.md`

---

## Problem

Two gaps surfaced when wiring up a `bc_version` matrix for
`BusinessCentral.AL.Language.Tests`:

1. **No preprocessor symbol support.** The AL compile step has no way to pass
   `/preprocessorsymbols` to `alc`. Consumer repos that guard version-specific
   code behind `#if BC28PLUS` cannot compile correctly in a matrix.

2. **No runtime version management.** The `runtime` field in `app.json` must be
   compatible with the AL compiler tool version being used. Currently consumers
   must hand-edit `app.json` per BC version, or leave it at a low value
   indefinitely. Neither `runtime_version` nor `al_tool_version` is auto-derived
   from `bc_version`, so the mapping is entirely manual and undocumented.

---

## Design

### New inputs

Added to `workflow_call.inputs` in `bc-test-from-source.yml`:

```yaml
preprocessor_symbols:
  description: >
    Comma-separated preprocessor symbols passed to /preprocessorsymbols
    (e.g. "BC27PLUS,BC28PLUS"). Empty = no flag passed to the compiler.
  required: false
  type: string
  default: ""

runtime_version:
  description: >
    Override app.json "runtime" field before compile (e.g. "16.0" for BC 27,
    "17.0" for BC 28). When blank, auto-derived from the bc_version major via
    the lookup table. Set explicitly to suppress auto-derive for edge cases.
  required: false
  type: string
  default: ""
```

`al_tool_version` keeps its existing input signature but its **default value
changes from the hardcoded `16.2.28.57946` to auto-derived** — implemented
inline in the "Install AL compiler" step so the explicit-override path still
works unchanged.

### bc_version → runtime + al_tool lookup table

Hardcoded in the workflow. The same table is used in two places: the "Patch
app.json runtime" step and the "Install AL compiler" step.

| BC major | runtime | al_tool_version |
|---|---|---|
| 27 | 16.0 | 16.2.28.57946 |
| 28 | 17.0 | 17.0.34.45391 |
| *(unknown)* | 14.0 | 16.2.28.57946 |

**Fallback behaviour:** when `bc_version`'s major is not in the table, the
workflow emits a `::warning::` annotation ("bc_version major N is not in the
runtime/al_tool lookup table — using fallback 14.0 / 16.2.28.57946") and
continues. Silent fallback is not acceptable; the consumer must know they are
hitting an unmapped version.

Extending the table when a new BC major ships requires a one-line addition in
the workflow. No external config file is needed at this scale.

### New step: "Patch app.json runtime" (before compile, after symbol staging)

```bash
RUNTIME_VERSION="${{ inputs.runtime_version }}"
BC_MAJOR=$(echo "${{ inputs.bc_version }}" | cut -d. -f1)

if [ -z "$RUNTIME_VERSION" ]; then
  case "$BC_MAJOR" in
    27) RUNTIME_VERSION="16.0" ;;
    28) RUNTIME_VERSION="17.0" ;;
    *)
      echo "::warning::bc_version major $BC_MAJOR not in runtime lookup table — using fallback 14.0"
      RUNTIME_VERSION="14.0"
      ;;
  esac
fi

for d in $APP_DIRS $TEST_APP_DIRS; do
  [ -z "$d" ] && continue
  F="project/$d/app.json"
  [ -f "$F" ] || continue
  python3 - "$F" "$RUNTIME_VERSION" <<'PYEOF'
import sys, json, pathlib
p = pathlib.Path(sys.argv[1])
d = json.loads(p.read_text())
if d.get("runtime") != sys.argv[2]:
    d["runtime"] = sys.argv[2]
    p.write_text(json.dumps(d, indent=2) + "\n")
    print(f"  patched {p}: runtime → {sys.argv[2]}")
else:
    print(f"  {p}: runtime already {sys.argv[2]}, skip")
PYEOF
done
```

**Scope:** `project/` only (the consumer checkout). The bc-linux smoke test
(`extensions/smoke-test/app.json`) is never touched — it lives in `bc-linux/`
and uses `runtime: "14.0"` deliberately so a single committed file compiles
with both 27.x and 28.x tools.

**Idempotent:** skips files where the existing `runtime` value already matches.

### Change: compile_dir() — preprocessor symbols flag

`PP_FLAG` is derived once before the `compile_dir` function definition:

```bash
PP_FLAG=()
[ -n "$PREPROCESSOR_SYMBOLS" ] && PP_FLAG=("/preprocessorsymbols:$PREPROCESSOR_SYMBOLS")
```

The flag is injected in **both** code paths inside `compile_dir`:

```bash
# analyzer-capture branch:
AL compile "/project:$src" "/packagecachepath:..." "/out:$out" \
  "${PP_FLAG[@]}" "$@" 2>&1 | tee "$tmplog"

# plain branch:
AL compile "/project:$src" "/packagecachepath:..." "/out:$out" \
  "${PP_FLAG[@]}"
```

`PP_FLAG` is positional-expanded as an array so an empty array adds no
arguments. The existing `$@` (analyzer flags) remain last.

### Change: "Install AL compiler" step — auto-derive al_tool_version

```bash
AL_TOOL="${{ inputs.al_tool_version }}"
BC_MAJOR=$(echo "${{ inputs.bc_version }}" | cut -d. -f1)

if [ -z "$AL_TOOL" ]; then
  case "$BC_MAJOR" in
    27) AL_TOOL="16.2.28.57946" ;;
    28) AL_TOOL="17.0.34.45391" ;;
    *)
      echo "::warning::bc_version major $BC_MAJOR not in al_tool lookup table — using fallback 16.2.28.57946"
      AL_TOOL="16.2.28.57946"
      ;;
  esac
fi

dotnet tool install -g \
  Microsoft.Dynamics.BusinessCentral.Development.Tools.Linux \
  --version "$AL_TOOL" 2>/dev/null || true
```

The existing `al_tool_version` input default value changes from
`"16.2.28.57946"` to `""` so that an unset input triggers auto-derive.
Consumers who pin an explicit version are unaffected. This is
backward-compatible for BC 27 consumers (auto-derive produces the same
`16.2.28.57946` value), and automatically correct for BC 28 consumers
who previously had to set it manually.

### Smoke test (test-versions.yml) — no changes

The smoke test matrix does not set `runtime_version` or `preprocessor_symbols`.
`extensions/smoke-test/app.json` keeps `"runtime": "14.0"`. The patch step
only walks `project/$d/` dirs, which are the consumer's checkout — the smoke
test has no `project/` checkout, so it is never touched.

---

## Consumer matrix example (after this change)

```yaml
strategy:
  matrix:
    include:
      - bc_version: "27.5"
        preprocessor_symbols: "BC27PLUS"
      - bc_version: "28.1"
        preprocessor_symbols: "BC27PLUS,BC28PLUS"

uses: StefanMaron/MsDyn365Bc.On.Linux/.github/workflows/bc-test-from-source.yml@master
with:
  bc_version:           ${{ matrix.bc_version }}
  preprocessor_symbols: ${{ matrix.preprocessor_symbols }}
  test_app_dirs:        test
  codeunit_range:       50000..99999
```

`runtime_version` and `al_tool_version` are auto-derived and need not appear
in the matrix at all for standard BC 27/28 versions.

---

## Documentation changes

### README.md

New subsection under the AL compiler section:

```
### bc_version → AL runtime → al_tool_version mapping

| BC version | AL runtime | al_tool_version (Linux NuGet) |
|---|---|---|
| BC 27.x | 16.0 | 16.2.28.57946 |
| BC 28.x | 17.0 | 17.0.34.45391 |

The reusable workflow auto-derives both values from bc_version. Set
runtime_version or al_tool_version explicitly to override for edge cases.

**Why does the smoke test use runtime: "14.0"?**
bc-linux's internal smoke test uses a deliberately low runtime value so the
same committed app.json compiles cleanly against any supported BC version
without patching. This is specific to the multi-version smoke test; consumer
apps should use the runtime that matches their minimum supported BC version.
```

### examples/github-workflows/README.md input table

Add two rows:

| `preprocessor_symbols` | no | `""` | Comma-separated preprocessor symbols for `/preprocessorsymbols` (e.g. `"BC27PLUS,BC28PLUS"`) |
| `runtime_version` | no | *(auto-derived from bc_version)* | Override `app.json` runtime before compile. Auto-derived when blank. |

Update `al_tool_version` row default from `16.2.28.57946` to *(auto-derived from bc_version)*.

---

## Files touched

| File | Change |
|---|---|
| `.github/workflows/bc-test-from-source.yml` | Add inputs, patch step, PP_FLAG, al_tool auto-derive |
| `examples/github-workflows/README.md` | New input rows, updated al_tool_version default note |
| `examples/github-workflows/bc-test-from-source.yml` | Add new inputs to the consumer template |
| `examples/azure-pipelines/bc-test-from-source.yml` | Add equivalent variables + patch step |
| `README.md` | New bc_version → runtime → al_tool mapping section |

`bc-test-prebuilt.yml` is **not** touched — it has no compile step, so
preprocessor symbols and runtime patching don't apply.

---

## Out of scope

- Patching `bc-linux/extensions/smoke-test/app.json` (intentionally left at 14.0)
- Azure Pipelines matrix example (consumers can adapt from the GitHub example)
- Any change to the test runner, entrypoint, or Docker image
