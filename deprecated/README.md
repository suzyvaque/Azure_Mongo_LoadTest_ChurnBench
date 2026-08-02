# Deprecated / Superseded Artifacts

This folder holds material **not part of the final benchmark** (see the current
[`../README.md`](../README.md) and
[`../results/REPORT-mongo-vs-documentdb-churn-benchmark.md`](../results/REPORT-mongo-vs-documentdb-churn-benchmark.md)).
Kept for history only — do **not** cite these in current analysis.

## Contents

| Item | What it is |
|---|---|
| `README-previous.md` | The previous README (pre–2026-08 rewrite), which described the older 3-target steady/burst design. |
| `experimental/` | Exploratory shard/rebalance experiments (e.g. resharded-ReqId), superseded by the verified 2-shard reshard in `results/run-20260802-01`. |
| `run-20260619-00` … `run-20260724-*` | Early single-host / steady+burst campaigns before the final open-loop + hold design. |
| `run-20260727-03` | Intermediate mongo hold-fix run, superseded by `results/run-20260727-04` (mongo 4-router). |
| `*-steady-burst-*`, `mongo-vm-*`, `cosmos-ru-*`, `phase1-*` | First-round single-op / phase-1 campaigns (legacy targets `mongo-vm`, `cosmos-ru`). |

## Runs retained as the final evidence base (in `../results/`)

`run-20260727-01`, `run-20260727-02`, `run-20260727-04`, `run-20260731-01`, `run-20260802-01` — these back
the 7-configuration comparison in the final report.
