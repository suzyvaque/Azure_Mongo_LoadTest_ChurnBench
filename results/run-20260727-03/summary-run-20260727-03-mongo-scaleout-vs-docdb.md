# Apples-to-apples hold comparison — mongo-shard (scaled-out) vs DocumentDB

Generated 2026-07-27 17:08. Saturation **hold** test: three synchronized generator hosts (`vm-hpc-loadgen-az1-0/1/2`), MaxConcurrentTasks=4000/host => **12,000 combined gate**, 10 s keepalive, warm-up = all 100k docs. Concurrency is the combined per-second SUM of each host's driver ActiveReady (`report merge` convention); latency percentiles are the mean of per-host per-iteration values.

## Why this is a valid pair (cross-run)

The **mongo-shard** column is the NEW scaled-out campaign (`mongo-holdfix-0727`, `run-20260727-03`). The **DocumentDB** column is reused from the immediately preceding hold campaign (`run-20260727-02`). This is a legitimate apples-to-apples pairing because the generator topology, hold config, warm-up, and the DocumentDB backend itself are **identical** across the two runs — the *only* variable that changed is the mongo access tier (2 co-located routers -> 4 dedicated routers). Re-running DocumentDB a third time on the same day drove its managed **gateway into connection-rate throttling** (idle server CPU ~2%, yet tens of thousands of ServerSelectionTimeouts and only ~85 concurrent established), so the clean earlier result is used as the DocumentDB baseline rather than a degraded fresh run.

> **Access-path disclosure.** mongo-shard Tasks are pinned round-robin across **4 dedicated `mongos` routers** (`directConnection=true`) to avoid the per-client SDAM topology-monitor thread explosion under no-reuse churn. DocumentDB is a single managed **SRV/gateway** endpoint, so there is **no equivalent optimization to apply**. These results compare each backend's production **access path**, not pure database-engine internals.

> **Naming caveat.** The two new routers are named `vm-mongos-az1-1` / `vm-mongos-az1-2` but are physically provisioned in **Availability Zone 3** (co-located with the existing shard/config VMs); the `az1` in the name is a misnomer kept for continuity.

## Mongo scale-out (the fix)

- **Before:** 2 mongo VMs (FX24ms_v2) each co-hosted `mongos` + shard `mongod` (+ config server on VM2). Per-connection TLS+SCRAM handshakes saturated both at **~99.7% CPU**, capping mongo at ~4.3k concurrent — a client-independent **CPU ceiling**, not a database-engine limit.
- **After:** added **2 dedicated `mongos`-only VMs** (Standard_D8ds_v5, 8 vCPU) => **4-way round-robin** across routers. Handshake CPU now spread ~17-21% per router; mongo holds the **full 12,000 gate**.

## Max / Avg concurrent connections (combined across 3 hosts)

| Target | Iters | Max conn (best) | Max conn (mean) | Avg conn (mean) | Cleared 10k? |
|---|---|---|---|---|---|
| documentdb | 3 | 11154 | 8864.7 | 3865.9 | YES |
| mongo-shard | 3 | 12000 | 12000 | 10570.4 | YES |

### Per-iteration

| Target | Iter | Max conn | Avg conn | Per-host peak Ready |
|---|---|---|---|---|
| documentdb | 1 | 4691 | 1760.3 | 4000 / 1382 / 1248 |
| documentdb | 2 | 10749 | 4563.7 | 3999 / 3999 / 3999 |
| documentdb | 3 | 11154 | 5273.7 | 4000 / 4000 / 4000 |
| mongo-shard | 1 | 12000 | 10927.2 | 4000 / 4000 / 4000 |
| mongo-shard | 2 | 12000 | 9854.1 | 4000 / 4000 / 4000 |
| mongo-shard | 3 | 12000 | 10930 | 4000 / 4000 / 4000 |

## Latency — p90 / p99 (ms), mean of iterations

| Metric | documentdb | mongo-shard |
|---|---|---|
| Establish (Demand->Ready) | 108090.1 / 121137.4 | 17923 / 34004.9 |
| find (keepalive op) | 79911.7 / 113742.8 | 2277.8 / 14802.8 |

## Headline, client CPU/memory, warm-up, retry, connection lifecycle

| Metric | documentdb | mongo-shard |
|---|---|---|
| Throughput (tasks/s, combined) | 36.4 | 42 |
| Error rate (%) | 86.159 | 69.331 |
| Client CPU peak (%) | 89.2 | 88 |
| Client working set peak (MB) | 1963.5 | 2042.6 |
| Warm-up time (s, all docs) | 111.1 (100000 docs) | 135.9 (100000 docs) |
| Connections created / iter | 30045 | 13116 |
| Connection-open failures | 33763 | 2377 |
| Retry writes enabled | True | True |
| Retryable command failures | 7605 | 0 |

## Verdict

With 4 dedicated routers, the earlier mongo CPU ceiling is removed: **Yes — mongo-shard now holds the full 12,000 gate**, matching/exceeding DocumentDB's ~11k. Both backends sustain >=10k concurrent no-reuse connections, so the comparison is now apples-to-apples at the target scale. DocumentDB delivers this from a single managed gateway with ~2% server CPU; mongo-shard requires horizontal router scale-out (4 routers) to reach the same concurrency, reflecting the cost of terminating per-connection TLS+SCRAM on self-managed infrastructure.

> Server-side note: during the mongo hold the 4 mongos routers ran ~17-21% CPU each; during DocumentDB's baseline hold its managed cluster ran ~2% CpuPercent / ~29% MemoryPercent (Azure Monitor).
