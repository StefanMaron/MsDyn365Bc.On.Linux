# Sequential Throughput Optimization Ideas

Current state: ~0.33s/method average (Bucket 4, single runner, headless Linux).
Goal: identify where time is spent and reduce irreducible overhead.

## Experiment Results

### Profiling (dotnet-trace, 2 min sample during ERM execution)

Key finding: **47% of time is UNMANAGED_CODE_TIME** (SQL Server processing),
only **2% is CPU_TIME**. This is an I/O-bound workload, not CPU-bound.

Top hot methods:
- `UNMANAGED_CODE_TIME`: 564s (47%) — SQL query processing, locks, transactions
- `AsyncMethodBuilderCore.Start`: 146s — async machinery overhead
- `TestClientProxy.Invoke`: 32s — test page client reflection
- `CallServerSync`: 25s — synchronous client→server RPC
- `BindingManager.DoFill`: 12.7s — page data binding
- `NstDataAccess.GetPage`: 12.5s — server-side page retrieval
- `ActionField` chain: 9.9s — field validation roundtrips

### Experiment 1: SQL Network Co-location (Docker bridge vs host network)

Hypothesis: Docker bridge network adds latency to BC↔SQL communication.
Result: **No measurable difference.**

| Setup | Run 1 | Run 2 | Run 3 | Avg |
|-------|-------|-------|-------|-----|
| Docker bridge (warm) | 76s | 74s | 65s | **72s** |
| Host network (warm) | 71s | 69s | — | **70s** |

Test: 5 codeunits, 301 methods from Tests-SINGLESERVER.
Conclusion: Network hop adds ~0.1ms/call which is negligible. The 47% unmanaged
time is SQL Server's internal processing (query compilation, locking, buffer ops),
not network latency. **Docker bridge is fine.**

### Experiment 2: Server GC (DOTNET_gcServer=1)

Hypothesis: Server GC reduces pause frequency, improving throughput.
Result: **Breaks the API endpoint.** BC's OData/API port (7052) returns 400
errors with Server GC enabled. The HttpSysStub or request handling pipeline
is incompatible with Server GC's thread pool behavior. **Cannot use.**

Baseline (Workstation GC): 62-64s avg (3 runs, warm).
Server GC: API broken, tests cannot run.

### Experiment 3: SQL Server Tuning (MAXDOP, ad hoc, cost threshold)

Hypothesis: BC generates many small queries; MAXDOP=1 and ad hoc optimization
should reduce SQL overhead.

Applied: `max degree of parallelism`=1, `cost threshold for parallelism`=50,
`optimize for ad hoc workloads`=1.

| Setup | Run 1 | Run 2 | Run 3 | Avg |
|-------|-------|-------|-------|-----|
| Baseline (defaults) | 62s | 60s | 61s | **61s** |
| SQL tuned | 65s | 61s | 61s | **62s** |

**No difference.** BC's queries are already single-threaded (small, simple),
plan cache is already warm, and ad hoc optimization doesn't help because the
same queries repeat. The 47% SQL time is irreducible query processing overhead.

### Summary

Three experiments tested, none improved throughput:
1. Network co-location: no effect (Docker bridge overhead negligible)
2. Server GC: breaks BC's API endpoint
3. SQL tuning: no effect (queries already optimal for this workload)

The ~0.2s/method execution speed appears to be the floor for the current
architecture. Further gains require structural changes (RPC short-circuit,
session pooling) or parallelization across multiple BC instances.

## Remaining Ideas to Test

### TCP Tuning (Nagle's Algorithm)
BC↔SQL communication uses TCP. Nagle's algorithm buffers small packets before
sending, adding latency to each of the thousands of tiny SQL round-trips.
Disabling Nagle (`TCP_NODELAY`) could reduce per-call latency. This would NOT
have shown up in bridge-vs-host test since both had Nagle enabled.

### SQL Forced Parameterization
BC may send ad-hoc SQL text for each query. `ALTER DATABASE CRONUS SET
PARAMETERIZATION FORCED` makes SQL Server reuse execution plans more
aggressively, reducing plan compilation overhead per query.

### SQL Packet Size
Default network packet size is 4096 bytes. Tuning this (smaller for many tiny
queries, larger for result sets) could reduce per-round-trip overhead.

### Transaction Log on tmpfs
Data files are on tmpfs but the log file may be on Docker overlay filesystem.
Every commit writes to the log. Check and move if needed.

### SQL Memory
Currently `MSSQL_MEMORY_LIMIT_MB=2048`. More RAM = larger buffer pool and plan
cache. Test with 4096 or unlimited.

### CPU Affinity
Pin BC and SQL to separate CPU cores to avoid context switching overhead.
`--cpuset-cpus` in Docker.

## Experiment Results (continued)

### Experiment 4: SQL Forced Parameterization
`ALTER DATABASE CRONUS SET PARAMETERIZATION FORCED`
Result: **No improvement.** 64s avg vs 62s baseline. May hurt because BC's
queries rely on literal values for optimal plans. *Platform-agnostic.*

### Experiment 5: SQL Memory (4096MB vs default unlimited)
`EXEC sp_configure 'max server memory', 4096`
Result: **No improvement.** 64s avg vs 62s baseline. Working set fits in
default memory allocation. *Platform-agnostic.*

### Experiments blocked by BC compatibility
- **Server GC** (DOTNET_gcServer=1): Breaks API endpoint
- **Tiered compilation off** (DOTNET_TieredCompilation=0): Breaks API endpoint
- **Quick JIT for loops** (DOTNET_TC_QuickJitForLoops=1): Breaks API endpoint
All JIT/GC changes are incompatible with BC's HttpSysStub. *Linux-only issue
(HttpSysStub is the Linux replacement for Windows HttpSys).*

### Experiment 6: TCP Nagle / Low Latency
Could not modify sysctl inside containers (read-only filesystem).
No measurable effect. 68s avg vs 62s baseline (noise). *Platform-agnostic.*

### Experiment 7: CPU Affinity (cpuset-cpus)
Pinned SQL to cores 0,1 and BC to cores 2,3 on a 12-core machine.
Result: **50% slower** (94s warm avg vs 62s). Starving both processes of
cores is worse than letting the OS schedule freely. BC uses many async
threads; SQL Server needs cores for internal task scheduling.
*Platform-agnostic.*

### Experiment 8: Transaction Log Location
Verified: both data (.mdf) and log (.ldf) are on `/var/opt/mssql/data` which
is tmpfs (RAM). Log I/O is not a bottleneck. Combined with DELAYED_DURABILITY
= FORCED, log writes are already minimal. SQL Server does not support disabling
the transaction log entirely — it's fundamental to ACID. *Platform-agnostic.*

### What actually helped
Only SQL overhead removal (Experiment 3b) produced measurable improvement:
- Query store OFF: ~7% *Platform-agnostic*
- Auto statistics OFF: ~4% *Platform-agnostic*
- Page verify NONE: ~1% *Platform-agnostic*
- Delayed durability FORCED: ~1% *Platform-agnostic*
- **Combined: ~13%** *Platform-agnostic*

## 1. Profile First — Find the Bottleneck

Before optimizing, attach `dotnet-trace` or `dotnet-counters` to the BC process
during a test run. Answer: what percentage of wall time is session setup vs AL
execution vs SQL vs GC vs idle/waiting?

```bash
# Collect a trace during a small test run
dotnet-trace collect -p $(pgrep -f Microsoft.Dynamics.Nav.Server) --duration 00:02:00
# Analyze with speedscope or dotnet-trace convert
```

If 40% is session overhead → patches will help a lot.
If 90% is AL execution → not much to squeeze sequentially.

## 2. Session Overhead Reduction

Each codeunit in isolation mode (130450) creates a fresh BC session. This involves:
- Extension loading / validation
- Permission set evaluation
- Feature flag / entitlement checks
- Telemetry context setup
- Azure AD / identity initialization

**Ideas:**
- Patch session init to skip non-essential steps (feature flags, entitlements,
  telemetry context). Similar to existing StartupHook patches but targeting
  `NavSession` or `NavServerSession` initialization.
- CRIU at session level — checkpoint a "warm session" state after first init,
  restore it for subsequent codeunits instead of creating from scratch. This
  is speculative but could eliminate per-codeunit startup cost entirely.
- Pre-warm the extension metadata cache so each new session doesn't re-validate.

## 3. More Runtime Overhead Stripping

Already patched: Watson, SideService, Reporting, AzureAD, ShowForm.

**Candidates to investigate:**
- Telemetry/diagnostics collection during test execution (NavOpenTelemetry,
  TraceWriter). Even with no-op logger, the call sites may do work before
  reaching the no-op.
- Permission checks — in test mode, SUPER user runs everything. Can we short-
  circuit the permission evaluation path?
- Event subscriber resolution — BC resolves subscribers dynamically on each
  event raise. Could we cache the resolution table?
- Extension dependency validation — checked on every session but never changes
  during a test run.

## 4. .NET Runtime Tuning

**JIT / Tiered Compilation:**
```bash
# Force aggressive optimization (skip tier 0 interpretation)
export DOTNET_TieredCompilation=1
export DOTNET_TC_QuickJitForLoops=1
# Or disable tiered compilation entirely (immediate full JIT)
export DOTNET_TieredCompilation=0
```

**GC Tuning:**
```bash
# Server GC with larger generations (reduce pause frequency)
export DOTNET_gcServer=1
export DOTNET_GCHeapCount=4
# Reduce GC pressure during test bursts
export DOTNET_GCConserveMemory=0
```

**ReadyToRun:**
- The BC service DLLs ship as R2R (pre-compiled). Test extension DLLs do not.
  Pre-compiling hot test DLLs with crossgen2 could help startup.

## 5. SQL Micro-Optimizations

tmpfs for data files showed no improvement (SQL buffer pool handles caching).
But there may be other angles:
- `OPTIMIZE_FOR_SEQUENTIAL_KEY` on hot test tables
- Increase SQL memory allocation (currently 2GB default)
- Disable SQL telemetry / query store during test runs
- Pre-create temp tables used by test framework

## 6. Test Runner Efficiency

- Batch multiple codeunits per session where isolation isn't strictly needed
  (risky — some tests leave dirty state)
- Reduce WebSocket round-trip overhead between test runner and BC
- Pipeline the next codeunit setup while current one is executing

## 7. Wild Ideas

- **Patch the AL interpreter hot loop** — if profiling shows a specific method
  in Nav.Ncl's AL execution engine is hot, a targeted JMP hook could optimize it
- **Shared memory IPC** instead of WebSocket for test runner ↔ BC communication
- **Snapshot/restore at DB level** between codeunits (SQL Server snapshots are
  near-instant) instead of relying on transaction rollback

## Linux-Exclusive Ideas (Not Yet Tested)

### Microsoft TuneD `mssql` sysctl Profile
Official Microsoft/Red Hat kernel tuning for SQL Server on Linux.
Key parameters: `force_latency=5` (prevents CPU deep sleep between tiny ops),
`vm.transparent_hugepages=always`, `vm.swappiness=1`, `vm.dirty_ratio=80`.
Apply via `docker run --sysctl` or on the Docker host.
Test: apply sysctls, run 5-codeunit benchmark, compare to 56s baseline.
*Linux-exclusive — no Windows equivalent for these kernel parameters.*

### TCP_QUICKACK (Linux-only socket option)
Disables delayed ACKs per-socket. Windows has no equivalent. Our Experiment 6
failed because containers had read-only sysctl. Proper test: run container with
`--sysctl net.ipv4.tcp_low_latency=1` or use LD_PRELOAD shim to set
TCP_NODELAY + TCP_QUICKACK on all sockets created by BC process.
There's a known SqlClient-on-Linux performance issue (dotnet/SqlClient#422)
related to TCP behavior with MARS.
Test: strace/tcpdump to confirm delayed ACKs occur, then apply fix.
*Linux-exclusive — TCP_QUICKACK is a Linux-only socket option.*

### SQL Server Trace Flag 3979 (FUA/Write-Through)
Linux-specific optimization for XFS filesystem. Microsoft's own testing showed
~50% I/O reduction for write-intensive workloads. Our data is on tmpfs so
impact may be minimal, but transaction log writes may still benefit.
Test: `mssql-conf set traceflag 3979 on` + `control.writethrough 1`.
*Linux-exclusive — optimizes XFS FUA path that only exists on Linux.*

## Priority Order

1. **Profile** — without data, everything else is guessing
2. **Session overhead patches** — likely highest ROI if profiling confirms
3. **.NET tuning** — low effort, potentially meaningful
4. **Runtime stripping** — incremental gains, each patch helps a little
5. **SQL tuning** — probably minimal impact but cheap to try
6. **Test runner efficiency** — small gains
7. **Wild ideas** — high risk, explore only if profiling points there
