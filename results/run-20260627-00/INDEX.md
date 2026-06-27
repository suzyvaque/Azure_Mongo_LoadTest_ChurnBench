# Campaign — run-20260627-00 (mongo-vm open-loop full-workload)

This campaign contains MongoDB (`mongo-vm`) full-workload runs executed in sequence:

1. **Steady** full-workload (`config/production/full-workload-steady.json`, scenario `steady`)
2. **Open-loop burst** full-workload (`config/production/full-workload-open-loop.json`, scenario `burst`)

## Run groups

| Run identifier | Target | Workload | Scenario | Duration (3 iters) | Total | OK | Fail | Success |
|---|---|---|---|---:|---:|---:|---:|---:|
| `mongo-vm-steady-full-workload-20260627-093633` | mongo-vm | full-workload | steady | 1,806.040 s | 243,002 | 242,998 | 4 | 99.9984% |
| `mongo-vm-burst-full-workload-20260627-100935` | mongo-vm | full-workload | burst (open-loop) | 1,805.420 s | 268,539 | 268,520 | 19 | 99.9929% |

## Notes

- The burst run was executed in **open-loop** mode (`Scenario.Burst.OpenLoop = true`).
- Burst run execution used `--no-preflight` after repeated preflight dataset-count failures (`calc_input` observed at 99,971 instead of 100,000 immediately after seed verification).
- Per-iteration artifacts and aggregate summaries are stored under each run folder:
  - `iter-01..03/*.json`
  - `iter-01..03/*-timeseries.csv`
  - `iter-01..03/*-latency.csv`
  - `aggregate.json`
- Campaign comparison report: `comparison-mongo-vm-steady-vs-openloop-20260627.html`
