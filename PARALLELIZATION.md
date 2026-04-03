# Parallelization Strategy

## Why Parallelize

Sequential throughput is at the architectural floor (~0.2s/method). Seven
tuning experiments confirmed this — only SQL overhead removal helped (13%).
The time is distributed evenly across record ops, events, RPC, and page
operations with no single hot spot to optimize.

Microsoft uses 4 parallel runners for Bucket 4. We should do the same.

## Architecture

Each runner = one BC container + one SQL instance (separate database).
This is the only safe approach — shared databases cause test interference
on global tables (User, Permissions, Number Series setup).

## Options (in order of implementation effort)

### 1. GitHub Actions Matrix (easiest)
Split Bucket 4 apps across separate CI jobs. Each job spins up its own
container pair and runs one or two test apps.

```yaml
strategy:
  matrix:
    app: [Tests-ERM, Tests-SCM, Tests-Misc, Tests-Workflow+SCM-Service+SINGLESERVER]
```

Pro: Zero new infrastructure. Directly comparable to Microsoft's pipeline.
Con: Coarse-grained (app-level split, not codeunit-level).

### 2. Docker Compose with Multiple Replicas
Run N (BC+SQL) pairs on the same machine using docker-compose profiles
or a simple orchestration script. Split codeunits evenly across runners.

Pro: Fine-grained splitting, works locally.
Con: Memory hungry (each SQL ~2GB + BC ~1GB = ~3GB per runner).

### 3. CRIU Checkpoint/Restore Pool
Checkpoint a ready-to-test container (apps published, suite configured).
Restore N copies in parallel, each with a different codeunit range.

Pro: 8s restore vs 3-4 min full boot. Best for many short-lived runners.
Con: CRIU setup complexity. Validated but not yet production-ready.

## Expected Performance

| Runners | ERM estimate | Full Bucket 4 |
|---------|-------------|---------------|
| 1 (current) | ~70 min | ~3-4 hours |
| 2 | ~35 min | ~1.5-2 hours |
| 4 (MS parity) | ~18 min | ~50-60 min |
| 8 | ~9 min | ~25-30 min |

Microsoft's Bucket 4: ~170 min with 4 Windows runners.
Our target: match or beat with 4 Linux runners.

## Considered and Rejected

- **Separate companies on shared DB**: Most data is company-scoped, but global
  tables (User, Permissions) cause conflicts. Not worth the complexity.
- **Parallel sessions on single NST**: Same shared-DB problem, plus deadlocks.
- **Multiple NSTs on shared SQL**: Same problem as parallel sessions.
