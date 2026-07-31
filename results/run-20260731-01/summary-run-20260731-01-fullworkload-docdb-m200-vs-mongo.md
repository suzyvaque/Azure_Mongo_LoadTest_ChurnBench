# Full-workload (4-op) comparison — DocumentDB M200 vs mongo-shard

Generated 2026-07-31 17:39. **Full 4-op workload** (`find_input` -> `remove` -> `insert` -> `find_output`), 3-host open-loop churn (`full-workload-open-loop-3host.json`), 3 iterations, warm-up = all 100k docs. This run adds the **per-operation query-time decomposition** (find / remove / insert) from the `run-20260619-00` summary style, now at the M200 tier, keeping the current concurrency + client-CPU metrics.

## Sources & disclosures (read first)

- **DocumentDB M200** — NEW run (`docdb-m200-fw-0731`, `run-20260731-01`), collected fresh with full p50/p90/p99.
- **mongo-shard** — full-workload data from `run-20260727-01` (same open-loop 4-op config). This is the **2-router, pre-scale-out** mongo; the later 4-router scale-out was validated under the **hold** test only (keepalive `find`, no writes), so it has no insert/remove query times. Mongo p50 was not persisted (shows `n/a`); p90/p99 are exact.
- **Single-shard reality (DocumentDB).** The cluster has 2 physical shards by design, but the 100k-doc dataset (~4.4 GiB, evenly stored) physically lives on **ONE shard** — the Cosmos vCore preview rebalancer does not redistribute a small even dataset (confirmed by the 2026-06-27 resharding experiment via `explain()`). So M80->M200 raised the **per-shard connection ceiling / handshake headroom** of the single active shard, not data parallelism. Server CPU stayed **idle (~1.2% avg, 5% peak)** throughout, so the 4-op workload is not compute-bound; the bottleneck is connection establishment + client saturation.
- **Access path.** mongo-shard pins round-robin to `mongos` routers (`directConnection=true`); DocumentDB is a single managed SRV/gateway endpoint. Production access paths, not pure engine internals.

## Per-operation latency — p50 / p90 / p99 (ms)

**Connection (TCP+TLS+auth)** = pure driver `ConnectionOpenMs` handshake; **find (cold)** = first op on the fresh socket, net of the handshake (`op - connection` at each percentile — indicative, percentiles are not additive); **remove / insert / find (warm)** run on the already-open socket (pure server execution); **Total cycle** includes the fixed 10 s keepalive sleep. Under open-loop churn both backends are heavily loaded, so these are **saturation-load** query times, not idle-server times.

| Metric group | Pctile | documentdb (M200) | mongo-shard (2-router) |
|---|---|---|---|
| **Headline** | Throughput (tasks/s) | 154.9 | 29.5 |
| | Error rate (%) | 71.915 | 97.805 |
| **Connection (TCP+TLS+auth)** | p50 | 23929.4 | n/a |
| | p90 | 40194.4 | 99307.2 |
| | p99 | 47411.7 | 240698.7 |
| **find (cold)** | p50 | 7780 | n/a |
| | p90 | 7620 | 135872 |
| | p99 | 11638 | 77732 |
| **remove (warm)** | p50 | 1467.2 | n/a |
| | p90 | 5224.4 | 112921.1 |
| | p99 | 8445.5 | 171148.7 |
| **insert (warm)** | p50 | 679.2 | n/a |
| | p90 | 4124.1 | 14967.7 |
| | p99 | 7149.3 | 113315.7 |
| **find (warm)** | p50 | 643.3 | n/a |
| | p90 | 3301.7 | 14539.7 |
| | p99 | 7239.2 | 16488.4 |
| **Total cycle (incl. 10 s sleep)** | p50 | 9447.1 | n/a |
| | p90 | 51862.1 | 8045 |
| | p99 | 67900.6 | 78512 |

## Concurrency & client resource use

| Metric | documentdb (M200) | mongo-shard (2-router) |
|---|---|---|
| Max concurrent conn (best of 3 iters) | 16035 | 3165 |
| Avg concurrent conn | 2062.9 | 1272.7 |
| Client CPU peak (%) | 89.7 | 79 |
| Client working set peak (MB) | 2500.5 | 1170.5 |
| Warm-up time (s, all docs) | 125.2 (100000 docs) | 138.8 (100000 docs) |
| Connections created / iter | 65803 | 17065 |
| Connection-open failures | 386 | 12132 |

### Per-iteration concurrency

| Target | Iter | Max conn | Avg conn | Throughput (t/s) | Error % |
|---|---|---|---|---|---|
| documentdb | 1 | 8219 | 2643.6 | 177.3 | 67.01 |
| documentdb | 2 | 16035 | 1938.8 | 161.3 | 69.989 |
| documentdb | 3 | 9190 | 1606.4 | 126.1 | 78.747 |
| mongo-shard | 1 | 2061 | 1131.8 | 32.9 | 97.54 |
| mongo-shard | 2 | 2157 | 803.8 | 33.2 | 97.578 |
| mongo-shard | 3 | 3165 | 1882.6 | 22.4 | 98.298 |

## Verdict

Under the full 4-op open-loop churn, DocumentDB M200 serves the warm `remove`/`insert`/`find_output` ops in single-digit-second p99 while its own server CPU stays idle (~1%), confirming the ceiling is connection establishment, not data serving or compute — consistent with the single-physical-shard reality. The 2-router mongo-shard column is the pre-scale-out full-workload run (heavily saturated); the scaled-out 4-router mongo matched DocumentDB on the concurrency **hold** test but was not re-run for the 4-op workload. Net: raising DocumentDB to M200 improves the connection ceiling with no engine-side cost, and the per-op server-execution times remain low even under churn.
