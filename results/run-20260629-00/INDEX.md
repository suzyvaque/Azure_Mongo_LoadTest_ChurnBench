# Campaign — run-20260629-00 (documentdb full-workload: steady + open/closed-loop burst)

This campaign contains Azure DocumentDB (`documentdb`, Cosmos vCore) full-workload runs executed in sequence on VM `vm-dbtest-hpc-0`:

1. **Steady** full-workload (`config/production/full-workload-steady.json`, scenario `steady`)
2. **Open-loop burst** full-workload (`config/production/full-workload-open-loop.json`, scenario `burst`)
3. **Closed-loop burst** full-workload (`config/production/full-workload-burst.json`, scenario `burst`)

All runs use the updated envelope: **5 minutes × 3 iterations** (`IterationDurationSeconds = 300`, `Iterations = 3`).

## Run groups

| Run identifier | Target | Workload | Scenario | Duration (3 iters) | Total | OK | Fail | Success |
|---|---|---|---|---:|---:|---:|---:|---:|
| `documentdb-steady-full-workload-20260628-192222` | documentdb | full-workload | steady | 906.225 s | 121,501 | 121,496 | 5 | 99.9959% |
| `documentdb-burst-full-workload-20260628-193747` | documentdb | full-workload | burst (open-loop) | 913.593 s | 132,011 | 131,979 | 32 | 99.9758% |
| `documentdb-burst-full-workload-20260628-202911` | documentdb | full-workload | burst (closed-loop) | 914.375 s | 131,806 | 131,767 | 39 | 99.9704% |

## Aggregate metrics (mean across 3 iterations)

| Metric | Steady | Burst (open-loop) | Burst (closed-loop) |
|---|---:|---:|---:|
| Mean successful tasks/s | 134.07 | 144.46 | 144.11 |
| Mean error % | 0.004% | 0.024% | 0.030% |
| Cycle p50 ms | 2,047.8 | 2,853.1 | 2,923.4 |
| Cycle p99 ms | 2,215.4 | 6,536.2 | 6,403.8 |
| find_input p99 ms | 137.6 | 3,601.3 | 3,485.4 |
| remove p99 ms | 45.2 | 162.6 | 160.7 |
| insert p99 ms | 48.5 | 131.8 | 144.1 |
| find_output p99 ms | 20.2 | 135.9 | 151.2 |
| Connection open p99 ms | 92.3 | 2,110.7 | 2,047.6 |

## Notes

- The burst run was executed in **open-loop** mode (`Scenario.Burst.OpenLoop = true` via `full-workload-open-loop.json`): the seed-deterministic Poisson arrival schedule (λ = 0.57 Job/s, 14–500 Tasks/Job) is injected **ungated** (bypassing the `MaxConcurrentTasks` back-pressure cap), so realized conn/sec equals the offered schedule for a fair cross-target comparison.
- The steady run injects a fixed 135 Tasks/s for 300 s per iteration.
- Preflight passed both runs (warnings only: leftover TIME_WAIT connections and the warm-up advisory; the timed run performs its own untimed cache warm-up).
- The open-loop burst overload is visible in the connection-open and `find_input` (cold-connection) percentiles: cycle p99 rises from ~2.2 s (steady) to ~6.5 s (burst) because op 1 pays the cold-connection cost under bursty arrivals while ops 2–4 reuse the open connection.
- The **closed-loop burst** run (`full-workload-burst.json`, `Scenario.Burst.OpenLoop = false`) keeps the `MaxConcurrentTasks` back-pressure gate. On DocumentDB it tracks the open-loop run almost identically (cycle p99 6.40 s vs 6.54 s; tasks/s 144.1 vs 144.5), indicating the client host's concurrency cap was not the binding constraint here — the offered Poisson schedule stayed within the gate, so gated and ungated injection produced effectively the same realized load.
- No-reuse model confirmed in every iteration (`created/task = 1.000`).
- Per-iteration artifacts and aggregate summaries are stored under each run folder:
  - `iter-01..03/*.json`
  - `iter-01..03/*-timeseries.csv`
  - `iter-01..03/*-latency.csv`
  - `aggregate.json`
- Captured console logs: `_steady-console.log`, `_burst-console.log`.
