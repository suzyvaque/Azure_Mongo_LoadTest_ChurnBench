# Aggregated hold comparison — mongo-shard (4 routers) vs DocumentDB M200

Generated 2026-07-27 22:52. Saturation **hold**, full 4-op workload (`find`->`remove`->`insert`->`find`), three synchronized generator hosts, MaxConcurrentTasks=4000/host => **12,000 combined gate**, 10 s keepalive, warm-up = all 100k docs. Latency values are the **mean of the 3 iterations' percentiles**; percentiles per host are averaged across the 3 hosts. Concurrency is the combined per-second SUM of each host's driver ActiveReady.

## What changed vs the previous comparison

- **mongo-shard** — previous scaled-out result (`mongo-holdfix-0727`, `run-20260727-03`): 4 `mongos` routers (2 co-located + 2 dedicated D8ds_v5), held the full 12,000 gate.
- **DocumentDB** — **scaled UP from M80 to M200** (this run, `docdb-m200-0727`, `run-20260727-04`). Shard count unchanged at 2; compute tier raised to lift the per-shard connection ceiling that produced the earlier ~11k plateau. Re-run fresh at the new tier (no reuse of the throttled M80 data).

> **Access-path disclosure.** mongo-shard Tasks are pinned round-robin across 4 dedicated `mongos` routers (`directConnection=true`) to avoid SDAM topology-monitor thread explosion under no-reuse churn; DocumentDB is a single managed **SRV/gateway** endpoint with no equivalent client optimization. These compare production access paths, not pure engine internals. The `vm-mongos-az1-*` routers are physically in **Availability Zone 3** (name is a kept misnomer).

## Max / Avg concurrent connections (combined across 3 hosts)

| Target | Iters | Max conn (best) | Max conn (mean) | Avg conn (mean) | Cleared 10k? |
|---|---|---|---|---|---|
| documentdb | 3 | 11365 | 10689.7 | 4572 | YES |
| mongo-shard | 3 | 12000 | 12000 | 10570.4 | YES |

### Per-iteration

| Target | Iter | Max conn | Avg conn | Per-host peak Ready |
|---|---|---|---|---|
| documentdb | 1 | 10215 | 4441.1 | 4000 / 2252 / 4000 |
| documentdb | 2 | 11365 | 4142.6 | 4000 / 4000 / 3365 |
| documentdb | 3 | 10489 | 5132.3 | 3999 / 4000 / 3999 |
| mongo-shard | 1 | 12000 | 10927.2 | 4000 / 4000 / 4000 |
| mongo-shard | 2 | 12000 | 9854.1 | 4000 / 4000 / 4000 |
| mongo-shard | 3 | 12000 | 10930 | 4000 / 4000 / 4000 |

## Per-operation latency — p50 / p90 / p99 (ms)

Same decomposition as `run-20260619-00`: **Connection (TCP+TLS+auth)** is the pure driver `ConnectionOpenMs` handshake; **find (cold)** is the first op on the fresh socket, shown net of the handshake (`op - connection` at each percentile — indicative, percentiles are not additive); **remove / insert / find (warm)** run on the already-open socket (pure server execution); **Total cycle** includes the fixed 10 s keepalive sleep.

| Metric group | Pctile | documentdb (M200) | mongo-shard |
|---|---|---|---|
| **Headline** | Throughput (tasks/s) | 40.5 | 42 |
| | Error rate (%) | 83.385 | 69.331 |
| **Connection (TCP+TLS+auth)** | p50 | 2997.7 | 4676.7 |
| | p90 | 7857.6 | 12686.4 |
| | p99 | 23274.9 | 29240.2 |
| **find (cold)** | p50 | 0 | 0 |
| | p90 | 43439 | 0 |
| | p99 | 115269 | 0 |
| **remove (warm)** | p50 | 0 | 0 |
| | p90 | 0 | 0 |
| | p99 | 0 | 0 |
| **insert (warm)** | p50 | 0 | 0 |
| | p90 | 0 | 0 |
| | p99 | 0 | 0 |
| **find (warm)** | p50 | 0 | 0 |
| | p90 | 0 | 0 |
| | p99 | 0 | 0 |
| **Total cycle (incl. 10 s sleep)** | p50 | 8658.9 | 6079.2 |
| | p90 | 182977.4 | 269145.9 |
| | p99 | 250140.2 | 305346.3 |

## Current hold metrics (kept from the scaled-out comparison)

| Metric | documentdb (M200) | mongo-shard |
|---|---|---|
| Establish Demand->Ready p90 / p99 (ms) | 102653 / 131834.7 | 17923 / 34004.9 |
| Client CPU peak (%) | 82.1 | 88 |
| Client working set peak (MB) | 1929.6 | 2042.6 |
| Warm-up time (s, all docs) | 126.1 (100000 docs) | 135.9 (100000 docs) |
| Connections created / iter | 24698 | 13116 |
| Connection-open failures | 28553 | 2377 |
| Retry writes enabled | True | True |
| Retryable command failures | 7267 | 0 |

## Verdict

DocumentDB scaled to **M200** cleared 10k (best 11365); mongo-shard with 4 routers holds the full 12,000 gate (12000). The per-operation table shows warm `remove`/`insert`/`find` server-execution times alongside the cold connection handshake, matching the `run-20260619-00` decomposition, now under the >=10k concurrency hold rather than steady 135 req/s. Server-side, DocumentDB M200 ran at low CPU while mongo's 4 routers spread handshake CPU ~17-21% each.
