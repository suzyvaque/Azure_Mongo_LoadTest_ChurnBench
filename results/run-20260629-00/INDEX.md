# Campaign — run-20260629-00 (mongo-vm open-loop full-workload)

This campaign contains MongoDB (`mongo-vm`) full-workload runs executed in sequence:

1. **Steady** full-workload (`config/production/full-workload-steady.json`, scenario `steady`)
2. **Open-loop burst** full-workload (`config/production/full-workload-open-loop.json`, scenario `burst`)
3. **Closed-loop burst** full-workload (`config/production/full-workload-burst.json`, scenario `burst`)

## Run groups

| Run identifier | Target | Workload | Scenario | Duration (3 iters) | Total | OK | Fail | Success |
|---|---|---|---|---:|---:|---:|---:|---:|
| `mongo-vm-steady-full-workload-20260628-195309` | mongo-vm | full-workload | steady | 906.050 s | 121,502 | 121,501 | 1 | 99.9992% |
| `mongo-vm-burst-full-workload-20260628-200830` | mongo-vm | full-workload | burst (open-loop) | 910.210 s | 138,072 | 138,063 | 9 | 99.9935% |
| `mongo-vm-burst-full-workload-20260628-203226` | mongo-vm | full-workload | burst (closed-loop) | 904.617 s | 138,072 | 138,058 | 14 | 99.9899% |

## Headline metrics (3-iteration aggregate)

| Metric | Steady | Burst (open-loop) | Burst (closed-loop) |
|---|---:|---:|---:|
| Mean tasks/s (successful) | 134.1 | 151.7 | 152.6 |
| Cycle p99 ms (mean) | 2,274.5 | 3,882.1 | 3,761.4 |
| find_input p99 ms (mean) | 47.0 | 1,485.9 | 1,416.9 |
| remove p99 ms (mean) | 108.6 | 67.2 | 107.8 |
| insert p99 ms (mean) | 108.0 | 67.0 | 116.2 |
| find_output p99 ms (mean) | 5.8 | 70.4 | 61.2 |

No-reuse confirmed on every iteration (`created/task = 1.000`).

## Notes

- The open-loop burst run used `Scenario.Burst.OpenLoop = true` (arrivals injected ungated:
  offered = realized). The closed-loop burst run used the default `OpenLoop = false` (the
  `MaxConcurrentTasks` back-pressure gate is active).
- Before each timed run, `calc_input` was repeatedly found incomplete (87,708 / 94,004 docs) due to
  the shared benchmark environment, so the dataset was re-seeded with `prepare-data --force`
  (deterministic seed 42) to exactly 100,000 docs with the `ReqId` index on both `calc_input`
  (unique) and `calc_output` (non-unique). Each run then passed the preflight dataset check (only
  WARN-level checks remained).
- Per-iteration artifacts and aggregate summaries are stored under each run folder:
  - `iter-01..03/*.json`
  - `iter-01..03/*-timeseries.csv`
  - `iter-01..03/*-latency.csv`
  - `aggregate.json`
- Console logs: `steady-console.log`, `burst-console.log`, `burst-closed-console.log`, `seed-console.log` (git-ignored where applicable).
