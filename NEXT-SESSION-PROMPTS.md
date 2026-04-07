# Follow-Up Prompts for Next Sessions

These are self-contained prompts that a fresh Claude session can pick up to
continue specific lines of work.

---

## Prompt 1: Fix the Post-Misc Container Crash

**Problem:** When running the full Bucket 4 test suite sequentially
(ERM → SCM → Misc → Workflow → SCM-Service → SINGLESERVER), the BC container
becomes unhealthy after Tests-Misc completes. The remaining 3 apps fail with
"Failed to create run request" because the API is dead. This blocks us from
producing a complete Bucket 4 number from a single sequential run.

**What we have:**
- Reproduced both locally and on GitHub Actions runner
- ~52MB BC container log saved at:
  `~/Documents/Repos/community/PipelinePerformanceComparison/benchmark-results/local-20260404/bc-container.log`
- Per-app result files in the same directory:
  `Tests-ERM-results.txt`, `Tests-SCM-results.txt`, `Tests-Misc-results.txt`
- Benchmark log: `bucket4-local-full.log`
- The full state at the moment of the crash is in `bc-container.log` (52MB)
- ERM, SCM, Misc all completed successfully — the crash happens AFTER Misc finishes

**What we don't know:**
- Exact crash trigger — whether it's a specific Misc test, cumulative state
  from ERM+SCM+Misc, memory exhaustion, file handle leak, or something else
- Whether running Misc in isolation crashes BC, or only after ERM+SCM precede it
- Whether the crash is reproducible in a smaller workload

**Goal:** Find the root cause and fix it so a fresh container can run all 6
Bucket 4 apps sequentially without crashing. Success = a clean local benchmark
run with all 6 apps producing results.

**Suggested investigation steps:**

1. Read the tail of `bc-container.log` looking for the last entries before
   the unhealthy state. Look for: stack traces, OutOfMemoryException, file
   descriptor errors, NullReference patterns we haven't patched yet, or
   any "fatal" / "abort" / "killed" lines.

2. Check `dotnet-errors.txt` for unique error patterns that may correlate
   with the crash window.

3. Try running just `Tests-Misc.app` in a fresh container (no ERM/SCM
   beforehand) to see if Misc alone is the trigger or if cumulative state
   matters.

4. If it's cumulative, try running ERM → Misc (skipping SCM) and SCM → Misc
   (skipping ERM) to narrow down.

5. Check container memory/CPU stats during the run with `docker stats` —
   look for slow leaks.

6. Look at `KNOWN-LIMITATIONS.md` in this repo for context on the existing
   crash documentation.

**Constraints:**
- Don't break the existing SQL tuning, BCRUNNER user, or any of the
  Patches #17-22 in StartupHook.cs
- Test the fix with a real `bash scripts/benchmark-bucket4.sh` run from the
  PipelinePerformanceComparison repo (it triggers the same crash)
- Commit and push when done in both `bc-linux` (master) and
  `PipelinePerformanceComparison` (main) as relevant

---

## Prompt 2: Get BC 29.0 Insider Artifacts Working

**Problem:** Our benchmarks run on BC 27.5 (publicly available sandbox
artifacts). Microsoft's pipeline runs on BC 29.0 (insider build). Side-by-
side comparison is suggestive but not definitive because of the version
mismatch.

**What we have:**
- Working bc-linux setup on 27.5 with Patches #1-22 in StartupHook.cs
- Cecil binary patches for `CodeAnalysis.dll`, `Mono.Cecil.dll`,
  `Nav.Ncl.dll`, `TestPageClient.dll`, `Nav.Types.dll`
- Download script at `scripts/download-artifacts.sh`
- Microsoft's Bucket 1: 151 min on their self-hosted runners (29.0)
- Our Bucket 1 partial: 19 min local for 6 apps (27.5)

**Goal:** Get a working bc-linux container on BC 29.0 (insider build) and
run the same Bucket 1 / Bucket 4 test apps so the comparison is
version-matched.

**Suggested investigation steps:**

1. Check whether 29.0 sandbox artifacts are publicly downloadable or require
   insider authentication. The MS pipeline uses
   `https://bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net/sandbox/29.0.<build>/base`
   which is the insider feed.

2. If insider auth is needed, check if Stefan has access to download
   manually and stage the artifacts somewhere the script can read.

3. Try `BC_VERSION=29.0 docker compose up -d` and see what breaks. Likely
   suspects:
   - Cecil patches may target methods that have been refactored in 29.0
   - StartupHook patches may target types/methods that have been renamed
     or moved in 29.0
   - The HttpSysStub may need updates for any new request handling

4. Re-apply each patch one by one, fixing as needed. The ALDirectCompile
   PR investigation pattern (per-method fixups) applies here too.

5. Once 29.0 boots cleanly, run the Bucket 1 benchmark on 29.0 and compare
   to Microsoft's 151 min number.

**Constraints:**
- Don't break the 27.5 setup — keep both versions working
- Document any 29.0-specific patches separately so we can compare effort
  needed across versions

---

## Prompt 3: Apply for ISV Partner Validation

**Goal:** Get 3-5 ISV partners running their own test apps on the bc-linux
pipeline. This validates the approach in real-world conditions and provides
adoption signal for the Microsoft pitch.

**Why this matters:** "Microsoft's own tests run faster on our setup" is one
data point. "5 ISVs already use this in their pipelines and saved X hours
per week" is a much stronger signal that the platform is production-ready.

**Targets to approach:**
- Stefan Maron community ISVs with non-trivial test suites
- BC AL community projects on GitHub that publish their own test apps
- AL ecosystem maintainers (1ClickFactory, Continia, etc.) who might be
  interested in cheaper CI

**What to give them:**
- The bc-linux Docker Compose setup
- The benchmark script as a starting point
- Documentation of known limitations (KNOWN-LIMITATIONS.md)
- A simple intake form: "what's your test suite size, what's your current CI
  cost, what would you need from us to try this?"

**Goal output:** A short doc with concrete numbers from real ISV usage that
can be added to the Microsoft pitch.
