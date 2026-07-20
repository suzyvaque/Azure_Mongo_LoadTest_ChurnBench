# `config/production/` — configuration layout

Production configs use an **`Extends` inheritance chain**. A child file names its parent via a top-level
`"Extends": "<file>"` and is deep-merged **over** it (child wins; nested objects merge key-by-key;
scalars/arrays replace). The loader is `src/Bmt.Core/Configuration/BmtConfig.cs` → `LoadMergedObject()`
(max chain depth 10). Comments (`//`) and trailing commas are allowed.

## The chain

```
base.json                         plumbing only — rarely edited
  └── run.json                    OPERATOR HOT KNOBS — edit this to shape a campaign
        ├── full-workload.json          Mode=FullWorkload, TaskSleepMs=2000
        │     ├── full-workload-steady.json          steady on / burst off
        │     ├── full-workload-burst.json           burst on / steady off
        │     ├── full-workload-open-loop.json       burst open-loop
        │     ├── full-workload-open-loop-multihost.json   N-host open-loop (host-count M)
        │     ├── full-workload-open-loop-2host.json       pinned 2-host open-loop
        │     └── full-workload-open-loop-3host.json       pinned 3-host open-loop (added in Package B)
        ├── single-find.json            Mode=SingleOp / FindInput, TaskSleepMs=0
        │     ├── single-find-steady.json
        │     └── single-find-burst.json
        └── single-insert.json          Mode=SingleOp / InsertOutput, TaskSleepMs=0
              ├── single-insert-steady.json
              └── single-insert-burst.json
```

## Where each knob lives

| Concern | File | Notes |
|---------|------|-------|
| Dataset shape, seeder batches, client tuning, preflight gate | `base.json` | Stable plumbing; rarely touched. |
| **Iterations, duration, MaxConcurrentTasks, TaskSleepMs, arrival rates, open/closed loop** | **`run.json`** | The `==== EDIT THESE ====` banner. One place to change every run. |
| Workload mode (full vs single-op) | `full-workload.json` / `single-*.json` | Overrides `Workload` (+ `TaskSleepMs=0` for single-op). |
| Which scenario is active (steady vs burst) | `*-steady.json` / `*-burst.json` | Thin flag flips; pins exactly one scenario. |
| Multi-host open-loop sizing (λ, host count) | `full-workload-open-loop-*.json` | See the sizing math in each file's header comment. |

## Duration model

There is **one** canonical duration: `Scenario.IterationDurationSeconds` in `run.json`. It applies to
every scenario generator per iteration and overrides any per-scenario default. The old
`Steady.DurationSeconds` / `Burst.DurationSeconds` config fields were removed — the C# defaults remain for
validation but are always overridden at runtime. CLI `--duration-sec` still wins for quick smoke runs.

## Secrets

No connection strings live in config. They are read at runtime from env vars:

| Target | Env var |
|--------|---------|
| documentdb | `BMT_CONN` |
| mongo-vm | `BMT_CONN_MONGO` |
| mongo-shard | `BMT_CONN_MONGO_SHARD` |
| cosmos-ru | `BMT_CONN_COSMOS` |
| monitor user (server metrics) | `BMT_CONN_MONGO_MONITOR` |

Azure resource identifiers used for post-run metric pulls live in `config/azure-resources.json` (identifiers
only — no secrets).
