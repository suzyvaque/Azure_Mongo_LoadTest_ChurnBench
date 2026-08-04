# run-20260624-prevconfig — Previous-Config Closed-Loop Baseline

**Renamed from** `run-20260624-shard` and promoted from `deprecated/` because its **closed-loop (steady)
full-workload** runs are reused as the interim baseline for **§4a** of
[`../REPORT-mongo-vs-documentdb-churn-benchmark.md`](../REPORT-mongo-vs-documentdb-churn-benchmark.md).

## What is reused

| Run | Role in report |
|---|---|
| `documentdb-steady-full-workload-20260624-022440` | §4a "DocDB 1-shard (prev)" column |
| `mongo-shard-steady-full-workload-20260624-025641` | §4a "Mongo 2-router (prev)" column |

Both: 100k dataset (seed 42), full 4-op cycle, steady ~132.8 tasks/s, ~0.006% error, 3×610 s.

## Provenance & comparability caveats

- **Code version:** predates the R2/R3 connection-lifecycle refactor (Jul 23), the mongo TLS-chain-validation
  bypass (Jul 25, `879195f`), and DocumentDB retry-writes-on (Jul 27, `6ee1cc8`). **Per-operation service
  latency is comparable** to the final runs; **connection-establishment figures are NOT** directly comparable.
- **DocumentDB tier not recorded** for this June campaign (single-physical-shard, HA-on) — labelled
  "1-shard (prev)", not M80/M200.
- **Mongo = `mongo-shard` with 2× co-located mongos routers** (the "2-router" architecture), before the
  dedicated-router scale-out.
- Iterations are **610 s** with `TaskSleepMs=10000` (vs 300 s / 2,900 ms in the final campaigns).

Other runs in this folder (single-op, burst variants, the concurrency-meltdown incident note) are retained
for historical context and are **not** part of the final report.
