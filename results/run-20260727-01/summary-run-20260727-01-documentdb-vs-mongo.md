# Aggregated summary — documentdb vs mongo-shard (run-20260727-01, openloop)

Generated 2026-07-27 12:25. Three synchronized generator hosts (`vm-hpc-loadgen-az1-0/1/2`), 3 iterations x 300s. Concurrency is the combined per-second SUM of each host's driver ActiveReady (the `report merge` convention); latency percentiles are the mean of per-host per-iteration values. Compact per-host artifacts are saved under each `<target>/iter-NN/`.

> **Access-path disclosure.** mongo-shard Tasks are pinned round-robin to a single `mongos` router (`directConnection=true`) to avoid the per-client SDAM topology-monitor thread explosion under no-reuse churn. DocumentDB is a single managed **SRV/gateway** endpoint, so there is **no equivalent optimization to apply**. These results compare each backend's production **access path** (mongo direct-to-router vs DocumentDB SRV gateway), not pure database-engine internals.

> **⚠️ SATURATION / OVERLOAD RESULT — read before the latency tables.** This 3-host open-loop config offers ~1,300 new-connection Tasks/s **per host** (λ=4.0 jobs/s × ~325 Tasks/job), i.e. ~3,900 conn/s combined — **far more than either backend can accept**. Under the no-reuse model with a 5 s client server-selection timeout, ~**96–98% of Tasks fail**, the overwhelming majority as **`ServerSelectionTimeout`** (e.g. one mongo host-iter: 128,953 offered → 3,133 succeeded; 123,789 of 125,820 failures were ServerSelectionTimeout). Consequently:
> - **Max/Avg concurrent connections** and **connections created/failed** below are meaningful — they show each backend's real **connection-ACCEPT ceiling** under overload.
> - The **latency percentiles are dominated by 5 s+ timeout values, NOT clean service time** — treat them as saturation/queueing evidence, not as representative operation latency. For clean latency use the 1-host open-loop run (`run-20260724-openloop`, ~128/s offered, <0.4% errors); for the concurrency ceiling use the hold run (`run-20260727-02`).
> - This is the **overload / rate-ceiling** story by design; it is not a bug.

## Max / Avg concurrent connections (combined across 3 hosts)

| Target | Iters | Max conn (best) | Max conn (mean) | Avg conn (mean) |
|---|---|---|---|---|
| documentdb | 3 | 4485 | 3134 | 275 |
| mongo-shard | 3 | 3165 | 2461 | 1272.7 |

### Per-iteration

| Target | Iter | Max conn | Avg conn | Per-host peak Ready |
|---|---|---|---|---|
| documentdb | 1 | 3240 | 441.5 | 1920 / 2607 / 2274 |
| documentdb | 2 | 1677 | 148.9 | 1159 / 651 / 511 |
| documentdb | 3 | 4485 | 234.6 | 2642 / 888 / 2466 |
| mongo-shard | 1 | 2061 | 1131.8 | 668 / 702 / 692 |
| mongo-shard | 2 | 2157 | 803.8 | 702 / 727 / 738 |
| mongo-shard | 3 | 3165 | 1882.6 | 1114 / 1078 / 978 |

## Latency — p90 / p99 (ms), mean of iterations — ⚠️ saturation-dominated (mostly 5 s+ timeouts; see banner)

| Metric | documentdb | mongo-shard |
|---|---|---|
| Connection (TCP+TLS+auth) | 13409.1 / 20787.8 | 99307.2 / 240698.7 |
| End-to-end cycle | 25200.7 / 65534.2 | 8045 / 78512 |
| find (cold, op1) | 39056 / 50911.6 | 235178.8 / 318431.2 |
| remove (warm) | 3088.7 / 9826.4 | 112921.1 / 171148.7 |
| insert (warm) | 2934.7 / 10341.4 | 14967.7 / 113315.7 |
| find (warm) | 5358.3 / 17585.3 | 14539.7 / 16488.4 |

## Headline, client CPU/memory, warm-up, retry, connection lifecycle

| Metric | documentdb | mongo-shard |
|---|---|---|
| Throughput (tasks/s, combined) | 22 | 29.5 |
| Error rate (%) | 96.489 | 97.805 |
| Client CPU peak (%) | 86.7 | 79 |
| Client working set peak (MB) | 1982.8 | 1170.5 |
| Warm-up time (s, all docs) | 106 (100000 docs) | 138.8 (100000 docs) |
| Connections created / iter | 42675 | 17065 |
| Connection-open failures | 36245 | 12132 |
| Retry writes enabled | True | True |
| Retryable command failures | 7332 | 16 |

> Warm-up (Item 7): all 100000 input documents are read untimed before each timed iteration so both backends start from an identically warm cache. Retry (Item 1): RetryWritesEnabled reflects the driver setting (forced ON for documentdb); retryable-failure counts are best-effort retry triggers to cross-check with throttling/429s.
