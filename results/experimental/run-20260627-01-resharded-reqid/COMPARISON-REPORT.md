# DocumentDB Sharding Comparison Report - Full Workload (2026-06-27)

**Comparison scope:** `run-20260627-00` (baseline) vs `run-20260627-01-resharded-reqid` (ReqId-resharded), with `mongo-vm` from `run-20260627-00` as reference.

## 1. Test setup

| Item | Value |
|---|---|
| Cluster | `docdb-dbtest-hpc-0` (Azure DocumentDB / Cosmos vCore, M80, server 7.0) |
| Physical shards | 2 (`shard_0`, `shard_1`) |
| Workload | full-workload (4-op cycle: find_input -> remove -> insert -> find_output) |
| Scenarios | Steady (135 tasks/s) and Burst (Poisson, closed-loop) |
| Duration | 3 x 10 min per scenario |
| Dataset | 100,000 docs in `calc_input` (~4.4 GiB, mean ~44 KB/doc) |
| Connection model | one fresh connection per task (no-reuse, churn benchmark) |

## 2. Key findings

- **Sharding the collections on `{ReqId:"hashed"}` had a negligible effect on latency and throughput.** The two DocumentDB configurations are within run-to-run variance of each other.
- **Why:** Azure DocumentDB (Cosmos vCore) defines the logical shard key at `shardCollection` time, but the preview rebalancer (`by_disk_size` strategy) does **not** physically redistribute a small, evenly-stored dataset. `explain()` confirmed the 100k docs remained on a **single physical shard** after resharding + reseed.
- After resharding, `find_input` by ReqId becomes a **targeted single-shard** query (router hashes ReqId), but since all data is on one shard the routing benefit is not observable.
- Connection handling (churn rate, peak concurrency) is **unaffected** by the shard key - it is gated by the client host and the cluster gateway, not by data placement.

## 3. Latency - p90 / p99 only (mean across 3 iterations, ms)

### 3.1 Steady

| Operation | Baseline p90 | Baseline p99 | Resharded p90 | Resharded p99 | p99 delta |
|---|---:|---:|---:|---:|---:|
| Task cycle | 2117.8 | 2229.1 | 2133.1 | 2282 | +2.4% |
| find_input | 78.6 | 150.5 | 89.3 | 185.7 | +23.4% |
| remove | 5 | 59 | 6.4 | 67.7 | +14.7% |
| insert | 4.3 | 54.4 | 6.5 | 65.7 | +20.8% |
| find_output | 1.9 | 24 | 2.5 | 31.1 | +29.6% |
| connection_open | 34.5 | 95.8 | 37.5 | 110 | +14.8% |

### 3.2 Burst

| Operation | Baseline p90 | Baseline p99 | Resharded p90 | Resharded p99 | p99 delta |
|---|---:|---:|---:|---:|---:|
| Task cycle | 5515.4 | 8888.8 | 5071.2 | 7773 | -12.6% |
| find_input | 2787.9 | 5015 | 2363.7 | 4540 | -9.5% |
| remove | 39.4 | 137.9 | 65.7 | 155.5 | +12.8% |
| insert | 44.9 | 132.8 | 74.5 | 148.1 | +11.5% |
| find_output | 33 | 142.2 | 40.1 | 163.3 | +14.8% |
| connection_open | 1497.2 | 2781.1 | 1343.5 | 2437.7 | -12.3% |

## 4. Connections - request rate and concurrency

In this benchmark every task opens exactly one fresh connection and closes it (no-reuse), so **connection requests/sec == task throughput**. *Peak concurrent connection opens* is the maximum number of simultaneously-open client sockets (peak ephemeral ports in use).

| Campaign | Scenario | Throughput (tasks/s) | Connection requests/sec | Peak concurrent connections | Error % |
|---|---|---:|---:|---:|---:|
| DocumentDB Steady (baseline) | Steady | 134.5 | 134.5 | 8699 | 0.005 |
| DocumentDB Steady (ReqId-resharded) | Steady | 134.5 | 134.5 | 8955 | 0.005 |
| DocumentDB Burst (baseline) | Burst | 141.9 | 142 | 13724 | 0.033 |
| DocumentDB Burst (ReqId-resharded) | Burst | 144 | 144.1 | 13654 | 0.04 |
| Mongo-VM Steady (reference) | Steady | 134.5 | 134.5 | 9186 | 0.001 |
| Mongo-VM Burst (reference) | Burst | 148.7 | 148.7 | 15677 | 0.007 |

## 5. Caveats

- The resharded run does **not** represent a truly data-distributed 2-shard topology; data physically stayed on one shard (Cosmos vCore preview rebalancer limitation). To force physical distribution you would need either a storage imbalance large enough to trigger `by_disk_size`, the (non-exposed) `by_shard_count` strategy, or a fresh cluster created with `shardCount=2` so seeding distributes from the start.
- `find_input` p99 differences in Burst are dominated by client-side saturation (CPU ~45%, peak ~13.6k concurrent sockets), not by data placement.
- All runs share the same client host, network path (private endpoint) and M80 cluster tier.

_Generated 2026-06-27 20:56 from aggregate.json + per-iteration JSON._
