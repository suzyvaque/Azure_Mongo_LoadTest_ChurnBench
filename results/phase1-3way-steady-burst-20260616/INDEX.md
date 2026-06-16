# Campaign — phase1-3way-steady-burst-20260616

One **benchmark campaign** = one higher folder under `results/`, holding every target run from the
**same code version**, plus the comparison report and summary. Runs were executed **sequentially**
(mongo-vm → documentdb → cosmos-ru) on VM1; TIME_WAIT was drained to a clean baseline between targets.

Scenario token **`steady-burst`** = Scenario A steady (135 Tasks/s) + Scenario B Poisson burst
(λ=0.57) applied together in one 1-hour window (formerly labelled "both").

## Run groups

| Run identifier | Target | Finished (UTC) | Duration | Total | OK | Fail | Success | No-reuse |
|---|---|---|---|---|---|---|---|---|
| `mongo-vm-steady-burst-20260616-031555` | mongo-vm | 2026-06-16 03:15:54 | 3,610 s | 523,085 | 464,519 | 58,566 | 88.80% | ✅ |
| `documentdb-steady-burst-20260616-042846` | documentdb | 2026-06-16 04:28:45 | 3,610 s | 506,086 | 480,440 | 25,646 | 94.93% | ✅ |
| `cosmos-ru-steady-burst-20260616-053319` | cosmos-ru | 2026-06-16 05:33:18 | 3,677 s | 336,546 | 139,629 | 196,917 | 41.49% | ✅ |

## Folder layout

```
results/
  phase1-3way-steady-burst-20260616/                       <- campaign (this folder)
    mongo-vm-steady-burst-20260616-031555/                 <- per-target run
      *.json  *-timeseries.csv  *-latency.csv  *.log
    documentdb-steady-burst-20260616-042846/
    cosmos-ru-steady-burst-20260616-053319/
    comparison-3way-steady-burst-20260616-145757.html      <- self-contained 3-way report
    summary-3way-steady-burst-20260616-145757.md           <- concise metrics summary
    INDEX.md                                               <- this manifest
```

Per-target folder contents:

- `<run-id>.json` — full machine-readable `RunResult` (totals, per-op + cycle + connection latency
  percentiles, errors-by-type, no-reuse verification, embedded preflight gate info).
- `<run-id>-timeseries.csv` — one row per second (connection open/close, per-op QPS, in-flight tasks,
  ephemeral ports, TIME_WAIT, handles, threads, CPU%, working set).
- `<run-id>-latency.csv` — per-op + cycle + connection latency percentiles.
- `<run-id>.log` — captured console log for that run (**git-ignored**, see below).

## Reproduce / regenerate

```
# Each target run writes results/<run-id>/ automatically. Point --results at the campaign folder:
dotnet run --project src/Bmt.LoadGen -c Release -- test --config config/config.json \
  --target <key> --scenario both --results results/<campaign>
# Then build the comparison from the campaign folder (loader scans recursively):
dotnet run --project src/Bmt.Report -c Release -- report \
  --input results/<campaign> --output results/<campaign>/comparison-3way-steady-burst-<ts>.html
```

## Publishing / confidentiality

- **Published** (no confidential data): `*.json`, `*.csv`, `*.html`, `*.md`, `INDEX.md`. Connection
  strings in these are masked for **credentials *and* host/IP/appName** — internal Azure hostnames and
  private IPs are redacted (`mongodb://****:****@****:10255/...`).
- **Git-ignored**: `*.log` only. The captured console logs include preflight check-3 output that prints
  resolved **private IPs** (to prove the network path is private), so the raw logs are kept out of the
  repo. The same information, masked, is preserved in the published artifacts.
- cosmos-ru ran with RU pinned at the fixed **40,000 RU/s**; its 429s are bucketed as
  `CosmosRuThrottling`. Its ReqId index is non-unique (platform constraint) — distinct ReqId still
  guaranteed via the system-unique `_id` (ReqId == _id). Recorded in the run's preflight gate info and
  the comparison report.
