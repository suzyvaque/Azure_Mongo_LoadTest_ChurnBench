# Run 20260624-shard — Sharded MongoDB (`mongo-shard`) connection-churn campaign

**Date:** 2026-06-24
**Target:** `mongo-shard` — 2-shard MongoDB 7.0 cluster, **2× mongos** routers, TLS on.
**Workload:** full-workload (find_input → remove → insert → find_output, `TaskSleepMs=10000`)
plus single-query isolation tests (find-input, insert-output; `TaskSleepMs=0`).
**Each scenario:** 3 iterations × 600 s.
**Client model:** no-reuse churn (1 fresh `MongoClient` per Task), **round-robin direct
single-server connection across the two mongos** (see incident note below).

> **Read the per-Task "cycle latency" (~10 s) as workload think-time, NOT connection cost.**
> `TaskSleepMs=10000` adds a fixed 10 s compute-simulation per Task. The connection-establishment
> metric is **`ConnectionOpenMs`**; the database work is the four operation latencies.

---

## Topology
| Role | Endpoint | Notes |
|------|----------|-------|
| mongos #1 | 10.3.0.6:27017 | TLS allowTLS + CAFile |
| mongos #2 | 10.3.0.4:27016 | TLS allowTLS + CAFile |
| config server (csrs, 1 member) | 10.3.0.6:27019 | |
| shard2 mongod | 10.3.0.6:27018 | |
| shard1 mongod (rs0) | 10.3.0.4:27017 | |
| Load generator | vm-dbtest-hpc-0 (10.2.0.4) | runs the benchmark locally |

`bmt_db` sharded on **hashed `ReqId`**; `calc_input` / `calc_output` ~50/50 across shards.
Connection string: `...@10.3.0.4:27016,10.3.0.6:27017/bmt_db?authSource=admin&tls=true&tlsInsecure=true`.

---

## Headline results

### Steady (135 Tasks/s, 3×600 s) — `mongo-shard-steady-full-workload-20260624-025641/`
| Metric (3-iter mean) | Value |
|----------------------|-------|
| Successful Tasks/s | **132.8** |
| Error rate | **0.005%** (14 failures / 242,993 Tasks) |
| **ConnectionOpen** p50 / p99 | **35 ms / 137 ms** |
| find_input p50 / p99 | 70 ms / 212 ms |
| remove p50 / p99 | 4.1 ms / 80 ms |
| insert p50 / p99 | 4.5 ms / 83 ms |
| find_output p50 / p99 | 1.1 ms / 16 ms |
| Cycle p50 (incl. 10 s think-time) | 10,110 ms |
| Client peak | ~4,090 threads, ~43k handles, ~760 MB, ≤76% CPU |

### Burst (Poisson λ=0.57 Job/s, 14–500 Tasks/Job, 3×600 s) — `mongo-shard-burst-full-workload-20260624-032933/`
| Metric (3-iter mean) | Value |
|----------------------|-------|
| Successful Tasks/s | **128.4** |
| Error rate | **0.16%** (387 failures / 235,228 Tasks) |
| **ConnectionOpen** p50 / p99 | **466 ms / 2,022 ms** |
| find_input p50 / p99 | 1,579 ms / 5,323 ms |
| remove p50 / p99 | 7.9 ms / 268 ms |
| insert p50 / p99 | 18 ms / 309 ms |
| find_output p50 / p99 | 29 ms / 382 ms |
| Cycle p50 (incl. 10 s think-time) | 12,548 ms |
| Client peak | ~5,700 threads, ~75k handles, ~1.37 GB, ≤72% CPU |
| Errors | ServerSelectionTimeout 290, QueryFailure 94, Throttling 3 |

---

## Single-query results (isolated cold-connection cost, `TaskSleepMs=0`)
Each fresh per-Task connection performs exactly ONE op, so here the **cycle latency ≈ the op + its
connection cost** (no think-time). All four campaigns: **0% errors**, generator bounded.

### find-input (READ) — `mongo-shard-{steady,burst}-find-input-20260624-*`
| Scenario | Tasks/s | Error | find_input p50 / p95 / p99 |
|----------|---------|-------|----------------------------|
| Steady (135/s) | 135.0 | 0.00% | **41 ms / 60 ms / 70 ms** |
| Burst (Poisson) | 147.0 | 0.00% | **1,264 ms / ~3,020 ms / 3,788 ms** |

### insert-output (WRITE) — `mongo-shard-{steady,burst}-insert-output-20260624-*`
| Scenario | Tasks/s | Error | insert p50 / p95 / p99 |
|----------|---------|-------|------------------------|
| Steady (135/s) | 135.0 | 0.00% | **44 ms / 63 ms / 85 ms** |
| Burst (Poisson) | 147.3 | 0.00% | **1,276–1,379 ms / ~3,600 ms / 4,422 ms** |

**Reading:** under **steady** churn a single cold read or write (TLS + SCRAM handshake + one op)
costs ~40–45 ms p50 / ~70–85 ms p99 — fast and flat. Under **burst** spikes (up to 500 Tasks/Job),
the per-op latency rises to ~1.3 s p50 / ~4 s p99: the connection handshake (TLS + SCRAM through
mongos) queues when hundreds of fresh connections arrive simultaneously. Read and write profiles are
nearly identical, confirming the cost is dominated by **connection establishment**, not the DB op
itself. `insert-output` accumulates `calc_output`; cleaned before & after each campaign
(steady drop 243,001 → 0; burst drop 265,522 → 0).

## Interpretation
- **Steady:** the sharded cluster handles sustained churn well — connection open p50 **35 ms**,
  reads/writes single-digit-to-low-hundreds ms, **~0% errors**. With the round-robin direct-connect
  client, the generator stays bounded; no meltdown.
- **Burst:** spiky arrivals (up to 500 Tasks/Job) push connection open to p50 **466 ms** / p99 **2 s**
  and `find_input` to seconds, with a small tail of ServerSelectionTimeouts. The cluster absorbs the
  load but latency degrades markedly under concentrated spikes — consistent with mongos/config-server
  + per-connection SCRAM/TLS handshake cost becoming the bottleneck during bursts.
- **vs. baselines:** compare these `ConnectionOpenMs` values against `mongo-vm` (single node) and the
  managed DocumentDB/Cosmos runs. The connection-establishment metric — not the think-time-inflated
  cycle — is the apples-to-apples comparison.

---

## Files
```
INDEX.md                                   ← this file
INCIDENT-runaway-concurrency-meltdown.md   ← first-attempt client meltdown: cause + fix (evidence)
steady-console.log                         ← full-workload steady run console (valid)
steady-console.aborted.log                 ← FIRST (pre-fix) steady attempt that melted down — invalid
burst-console.log                          ← full-workload burst run console (valid)
single-find-steady-console.log             ← single-op READ steady console
single-find-burst-console.log              ← single-op READ burst console
single-insert-steady-console.log           ← single-op WRITE steady console
single-insert-burst-console.log            ← single-op WRITE burst console
mongo-shard-steady-full-workload-20260624-025641/   (iter-01..03 + aggregate.json)
mongo-shard-burst-full-workload-20260624-032933/
mongo-shard-steady-find-input-20260624-041734/
mongo-shard-burst-find-input-20260624-044801/
mongo-shard-steady-insert-output-20260624-052209/
mongo-shard-burst-insert-output-20260624-055315/
    (each campaign: iter-01/ iter-02/ iter-03/ with json + timeseries.csv + latency.csv, + aggregate.json)
```

## Methodology note (client fix)
The first timed attempt melted the **load-generator** (48,657 threads) because a per-Task
`MongoClient` against the full 2-mongos topology spins up SDAM monitor threads per mongos per client.
Fixed by round-robin pinning each per-Task client to **one** mongos as a **direct single-server**
connection — preserving 2× router fan-out while eliminating per-client topology monitors (the same
mitigation `mongo-vm` uses). Full write-up: `INCIDENT-runaway-concurrency-meltdown.md`.
Post-campaign `calc_output` was emptied (55,576 → 0) via `seeder clean-output --target mongo-shard`.
