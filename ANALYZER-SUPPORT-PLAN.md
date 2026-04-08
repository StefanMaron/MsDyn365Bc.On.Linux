# Analyzer support — plan

bc-linux's reusable workflow `bc-test-from-source.yml` currently compiles AL
without passing any analyzer or ruleset flags. This document captures the
plan for adding native analyzer support to the workflow so consumers can
run Microsoft's four cops (CodeCop / AppSourceCop / PerTenantExtensionCop /
UICop), plus any community cop they want, plus a ruleset, as part of the
Linux compile step.

This plan is **scoped to the bc-linux pipeline only**. Reading analyzer
configuration out of AL-Go settings files (so AL-Go consumers don't have to
re-declare their cop setup in workflow inputs) is a separate follow-up
that will build on top of what this plan delivers.

## Goal

When a consumer enables analyzers via the new `bc-test-from-source.yml`
workflow inputs, the AL compile step runs with `/analyzer:` and
`/ruleset:` flags wired correctly: Microsoft's cops load, any custom cop
DLL the consumer points us at (local path or URL) is resolved, ruleset
references are honored including remote `includedRuleSets[].path` URLs,
and a successful compile means the cops actually loaded — not silently
got skipped.

## Non-goals

- **Reading AL-Go settings files.** Future work. This pass takes
  configuration via workflow inputs only.
- **Vendoring or maintaining cop DLLs.** Microsoft's four cops ship inside
  the Linux AL compiler tool already. Custom cops are downloaded fresh per
  workflow run from the URL the consumer supplies.
- **Validating that diagnostics actually fire.** We only validate that
  cops *load* without errors — see Validation below.
- **bc-test-prebuilt.yml**: it doesn't compile, so analyzer flags don't
  apply. Out of scope.

## Workflow inputs

Add these optional `workflow_call` inputs to `bc-test-from-source.yml`.
All default to off / unset, so existing consumers see no behavior change.

| Input | Type | Default | Effect |
|---|---|---|---|
| `enable_code_cop` | bool | `false` | append `Microsoft.Dynamics.Nav.CodeCop.dll` to `/analyzer:` |
| `enable_ui_cop` | bool | `false` | append `Microsoft.Dynamics.Nav.UICop.dll` to `/analyzer:` |
| `enable_app_source_cop` | bool | `false` | append `Microsoft.Dynamics.Nav.AppSourceCop.dll` to `/analyzer:` |
| `enable_per_tenant_extension_cop` | bool | `false` | append `Microsoft.Dynamics.Nav.PerTenantExtensionCop.dll` to `/analyzer:` |
| `custom_code_cops` | string | `""` | comma- or newline-separated list of additional cop DLLs. Each entry is either a local path (relative to repo) or an `https://` URL — URLs are downloaded to a temp dir at the start of the compile step |
| `ruleset_file` | string | `""` | path or URL to a JSON ruleset file. URLs are downloaded to temp. Passed as `/ruleset:<resolved-path>` |
| `enable_external_rulesets` | bool | `false` | when `true`, add `/enableexternalrulesets` so the ruleset can include remote `includedRuleSets[].path` URLs |
| `enable_code_analyzers_on_test_apps` | bool | `false` | when `false`, none of the analyzer/ruleset flags are passed when compiling test apps (matches AL-Go's default behavior) |

## Helper script

`scripts/resolve-analyzers.py` — small Python helper, same style as
existing `resolve-keep-app-ids.py` and `_bcapp.py`. Single responsibility:
take the resolved input values, do the path resolution + URL downloads,
and emit the final compile flag fragments to stdout for the workflow to
consume.

Inputs (CLI flags or env vars):

- The four `enable_*` cop booleans
- The `customCodeCops` list
- The `rulesetFile` value
- `enableExternalRulesets` bool
- A temp dir for URL downloads
- The path to the installed AL compiler tool's `tools/net8.0/any/`
  directory (so it can find Microsoft's cop DLLs)

Outputs (one line per flag fragment, or a single composed string —
TBD during implementation):

- `/analyzer:<comma-separated DLL list>`
- `/ruleset:<resolved-path>` (when set)
- `/enableexternalrulesets` (when enabled)

The helper handles:

- Resolving Microsoft cop DLL paths from the AL tool dir
- Downloading URL entries in `customCodeCops` to the temp dir, returning
  the local path
- Downloading the `ruleset_file` if it's a URL
- Detecting nupkg downloads (ALCops ships as a nupkg containing 7 cop
  DLLs under `lib/net8.0/`) and extracting all DLLs from `lib/net8.0/`
  into the temp dir, then adding all of them — including
  `ALCops.Common.dll`, which is required for the other ALCops DLLs to
  load without `AD0001` (see gotcha #6 below)

The workflow calls the helper once per app being compiled (test apps
get `enable_code_analyzers_on_test_apps`-aware behavior).

## Gotchas

These bit the investigation phase and are easy to bite again:

1. **Rulesets must be JSON, not XML.** Visual Studio's classic
   `.ruleset` XML format throws `AL1033 Unexpected character encountered
   while parsing value: <`. The Linux AL compiler only accepts the JSON
   format AL-Go and the wider AL community standardize on
   (`{"name", "includedRuleSets", "rules": [{"id", "action", "justification"}]}`).
2. **The filename in the wild is often `.rulset.json`, not
   `.ruleset.json`.** That's a typo in widely-shared example files
   (`StefanMaron/RulesetFiles`) that propagated. The AL compiler doesn't
   care about the extension — it parses by content — so this is just a
   thing to know when consumers ask "why is my ruleset filename
   misspelled?"
3. **`app.json` does not have a `ruleset` property.** Don't try to read
   one — it'll error with `AL0124 The property 'ruleset' cannot be used in
   this context`. Ruleset configuration must come through `/ruleset:`
   on the command line.
4. **`/enableexternalrulesets` is required for any URL-scheme ruleset
   include.** Without it, `includedRuleSets[].path` entries with `http://`
   or `https://` get rejected with `AL0767 ... configuration does not
   permit external rulesets`. There is no protocol restriction (HTTP and
   HTTPS both work, loopback works, public CAs work) — once the flag is
   set, the compiler just fetches the URL.
5. **`AL1033 Could not load the rule set file from '<url>'` is the AL
   compiler's catch-all for ANY HTTP-layer failure** — 404, 500, DNS,
   TLS, server-not-yet-bound, you-name-it. Don't rely on the error
   message to diagnose remote-ruleset problems. When it fails, hit the
   URL with `curl -v` first.
6. **ALCops requires `ALCops.Common.dll` to be passed in the
   `/analyzer:` list alongside the individual cop DLLs.** Otherwise the
   cops load but trip `AD0001` at first analysis. The helper handles
   this for nupkg downloads automatically; consumers passing local paths
   to ALCops DLLs need to include `ALCops.Common.dll` in their list.

## Validation

**Load validation only.** No deliberate-violation planting.

`test-versions.yml` extends its existing matrix so each BC version also
compiles `extensions/smoke-test/` with cops enabled, using a
`.rulset.json` that references a remote include from
`StefanMaron/RulesetFiles` (canonical real-world example). Asserts:

- Compiler exit code is `0`
- Stderr contains none of `AD0001`, `Could not load`,
  `BadImageFormatException`, `PlatformNotSupportedException`

That catches every "cops aren't wired right" failure mode without
needing planted violations. If a future BC version ships a cop that
crashes on Linux, this matrix job tells us empirically and immediately.

## What this unlocks (later, separately)

Once analyzer support exists in the workflow as configurable inputs,
the AL-Go-consumption follow-up becomes straightforward: a small wrapper
that reads the AL-Go settings files (with their precedence chain),
extracts the same eight properties, and translates them into the
workflow inputs above. That's a separate plan and a separate piece of
work — it doesn't need to be designed now.
