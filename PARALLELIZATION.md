# Parallelization Strategy (Future Work)

## Status: Not Yet Implemented

Sequential throughput is at the architectural floor (~0.2s/method). Eight
tuning experiments confirmed this — only SQL overhead removal helped (~13-23%
depending on workload). The time is distributed evenly across record ops,
events, RPC, and page operations with no single hot spot to optimize.

The path to dramatic speedups is parallelization. This document captures the
strategy for future work — it is not implemented yet.

## What Microsoft Does Today (Wasteful)

Microsoft splits the Base Application test suite into 4 buckets and runs them
on 4 separate self-hosted runners. Each runner:

1. Spins up a fresh Windows container (multi-minute boot)
2. Spins up a fresh SQL Server instance
3. Compiles its assigned test apps from source
4. Publishes them
5. Runs its share of tests
6. Tears everything down

This is **embarrassingly parallel at the pipeline level** — there's no
coordination, no shared state, no resource pooling. Each runner pays the full
container + SQL boot cost. Each runner uses the same RAM/CPU footprint as a
solo run. The "parallelization" is "spawn N independent pipelines."

It works, but it's expensive: 4 runners worth of compute and storage to do
work that, if smarter, could share substantial overhead.

## Smarter Approaches (Future Investigation)

The interesting question: **how do we share the expensive bits across parallel
test runs without losing test isolation?**

### Option A: Shared Base, Multiple BC Instances per SQL

One SQL Server, multiple databases, multiple BC instances (each with its own
ServerInstance and port set). Each runner gets its own database — full
isolation — but they share SQL Server's buffer pool and process space.

- **Pro**: One SQL instance instead of N → big memory savings
- **Pro**: Buffer pool shared → cumulative RAM efficiency
- **Pro**: Single boot cost for SQL across all runners
- **Con**: Adds NST coordination complexity (multiple `MicrosoftDynamicsNavServer$X` processes)
- **Con**: SQL becomes a contention point under heavy parallel load

### Option B: CRIU Container Pool

Boot one BC+SQL pair to a known state (test framework published, demo data
initialized), checkpoint it with CRIU, then restore N copies in parallel —
each one already at second-of-test-execution rather than minute-of-boot.

- **Pro**: Skip the 3-5 minute boot cost per runner
- **Pro**: Validated separately — CRIU restore works on BC in 8.4s
- **Con**: Each restored copy still needs its own SQL data (memory hungry)
- **Con**: CRIU integration with Docker Compose is non-trivial

### Option C: Codeunit-Level Distribution Within Fewer Containers

Run, say, 2 fat containers (each with BC+SQL) instead of 6 small ones. The
test runner becomes smarter: it pulls codeunit IDs from a shared work queue,
each container processes whatever it grabs. Containers can hold significantly
more codeunits each because the per-container overhead is amortized.

- **Pro**: Better resource utilization (fewer processes, more work each)
- **Pro**: Dynamic load balancing (slow codeunits don't block fast ones)
- **Con**: Requires a shared work queue (Redis, file, or DB)
- **Con**: Test interference still requires separate databases per container

### Option D: GitHub Actions Matrix (Naive Baseline)

For comparison/validation: just split test apps across CI jobs. Same as
Microsoft's approach but on Linux. Useful as the "obvious thing" to compare
the smarter approaches against.

- **Pro**: Zero new infrastructure
- **Pro**: Directly comparable to Microsoft's pipeline shape
- **Con**: Wastes the same overhead Microsoft wastes — doesn't show the
  Linux density advantage

## Why This Matters for the Microsoft Pitch

Linux's real win isn't faster sequential test execution — that's only ~15-25%.
The real win is **container density**: a Linux BC container is ~500MB RAM
vs ~2-3GB for Windows. That means:

- 4 Linux runners on a 16GB machine vs 4 Windows runners on a 64GB machine
- More parallelism per dollar of cloud spend
- Faster CI feedback loops because more runs in flight

A smart parallelization strategy that shares overhead amplifies this further.
"Run the entire Base Application test suite in 30 minutes on a single beefy
Linux box" is a much more compelling pitch than "we're 1.5x faster
sequentially."

## Outreach Goal: Real ISV Validation

Beyond Microsoft's own code, we want ISV partners running their test apps on
this pipeline. That validates the approach in production-like conditions and
gives concrete adoption signal for the Microsoft pitch ("here are 5 ISVs who
already saved time using this — please make it official").

Targets:
- Stefan Maron's own production extensions (already validated to compile/run)
- ISV partners in the Stefan Maron community who maintain large test suites
- BC AL community projects on GitHub with non-trivial test apps

## Considered and Rejected

- **Separate companies on shared DB**: Most data is company-scoped, but global
  tables (User, Permissions, server config) cause test interference. The
  cleanup pass that filters out global-table tests would be larger than the
  parallelization gain. Not worth it.
- **Parallel sessions on single NST**: Same shared-DB problem, plus deadlocks
  on hot tables. Won't work without modifying BC test isolation semantics.
- **Multiple NSTs on shared SQL with shared database**: Same problem. The
  smart variant is **separate databases per NST** (Option A above).
