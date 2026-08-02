# Connection-Churn Benchmark — MongoDB vs Azure DocumentDB (Cosmos vCore)
### Full-Workload Open-Loop and Hold Tests — Consolidated Report

**Generated:** 2026-08-02
**Scope:** 7 database configurations × 2 test scenarios (open-loop churn, saturation hold), 3 iterations each, three synchronized generator hosts.
**Data provenance:** All figures are aggregated from per-host compact metrics captured directly from the load generators (`results/run-*`). Observed measurements are stated as such; interpretations are labelled.

---

## 1. Test Overview

Both tests use the **same .NET 8 load-generation tool**, the **same 100,000-document dataset**, and a strict **no-connection-reuse model**: every logical task opens a brand-new `MongoClient`/connection, performs its work, and disconnects. This deliberately isolates **per-connection establishment cost** (TCP + TLS + SCRAM auth) — the dimension that dominates connection-churn workloads — from steady-state query performance.

### 1a. Open-Loop Test (throughput/latency under average load)

| Aspect | Detail |
|---|---|
| **Objective** | Determine whether the system can **reliably meet required throughput and latency** under a continuously offered connection-churn load, and characterise per-operation service time. |
| **Traffic model** | **Open-loop** Poisson arrivals (`JobsPerSecondLambda = 4.0`/host, `MinTasksPerJob = 150`, `MaxTasksPerJob = 500`). Arrival rate is **independent of system response** — if the backend slows, work backs up (this is what exposes saturation). |
| **Workload per task** | Full **4-operation cycle**: `find_input` (cold read on fresh socket) → `remove` → `insert` → `find_output`, then `TaskSleepMs = 2900` ms keepalive, then disconnect. |
| **Offered load** | ≈1,300 new connections/s per host → ≈3,900/s combined. Designed to reach ≈11,700 concurrent at ~3 s hold. |
| **Evaluation** | Task completion rate, error rate, throughput (tasks/s), and tail latency (p90/p99) for connection open and each operation. |

### 1b. Hold Test (saturation & bottleneck behaviour)

| Aspect | Detail |
|---|---|
| **Objective** | Determine **how far each system can sustain concentrated concurrent connections**, and — more importantly — **where the bottleneck forms and how the system fails** under saturation. |
| **Traffic model** | **Closed-loop gate** (`Burst.OpenLoop = false`) parking a fixed population of `MaxConcurrentTasks = 4000`/host → **12,000 combined ceiling**. Each parked task holds one connection Ready for the full 5-minute window, issuing a light keepalive `find` every `TaskSleepMs = 10000` ms. |
| **Workload per task** | Single `find_input` keepalive only (no writes) — the test measures *concurrency capacity*, not operation mix. |
| **Evaluation** | Peak/avg concurrent Ready connections, whether ≥10,000 was cleared, establishment latency, and the failure layer (DB server vs router vs client). |

**Shared parameters & assumptions.** Dataset = 100,000 documents (Small 6 KiB ×10k, Medium 16 KiB ×15k, Large 50 KiB ×35k, XL 58 KiB ×40k; ~4.4 GiB total, fixed RNG seed 42 → byte-identical across all targets). Warm-up reads all 100,000 docs untimed before every iteration. Read/write ratio is fixed by the workload definition (open-loop = 2 reads + 1 delete + 1 insert per task; hold = read-only keepalive). Retry-writes forced ON for DocumentDB. All generators, network path (private endpoint), and gate sizing are identical across targets.

> **Concurrency definition (used throughout):** the combined per-second SUM of each host's driver `ActiveReady` gauge (the `report merge` convention). **Max concurrent = the peak of that summed gauge over the run**; the reported value is the best of the 3 iterations.

---

## 2. Database Targets

| Target | Type / deployment | Configuration details |
|---|---|---|
| **DocDB 1-shard M80** | Azure Cosmos DB for MongoDB **vCore**, managed | Tier M80, 2 physical shards provisioned but **data on ONE shard** (never distributed); TLS + SCRAM-SHA-256; single SRV/gateway endpoint; private endpoint. |
| **DocDB 1-shard M200** | Same, managed | Tier M200 (64 vCore / 256 GiB per shard — max tier); still single-shard data. |
| **DocDB 2-shard M60** | Same, managed | Tier M60 (8 vCore); data **genuinely distributed 33/32 chunks** across both physical shards (`{ReqId:hashed}`, sharded-while-empty + reseeded). |
| **DocDB 2-shard M80** | Same, managed | Tier M80; 2-shard distributed. |
| **DocDB 2-shard M200** | Same, managed | Tier M200; 2-shard distributed. |
| **Mongo 2-router** | Self-managed MongoDB 7.0 sharded cluster | 2 shards on 2 VMs (FX24ms_v2, 24 vCore), each co-hosting `mongos` + shard `mongod` (+ config server); Tasks pinned round-robin to **1 router** (`directConnection=true`). TLS (self-signed CA) + SCRAM. |
| **Mongo 4-router** | Same cluster, scaled out | Added **2 dedicated `mongos`-only VMs** (D8ds_v5, 8 vCore) → **4-way round-robin** across routers. Otherwise identical. |

> **Access-path disclosure.** mongo Tasks pin round-robin to `mongos` router(s) with `directConnection=true` to avoid the per-client SDAM topology-monitor thread explosion under no-reuse churn. DocumentDB is a single managed SRV/gateway endpoint with no equivalent client optimisation. These compare **production access paths**, not pure database-engine internals.

> **DocumentDB physical-sharding note.** vCore places all inserts on the primary shard; the preview rebalancer does not redistribute a small, evenly-stored dataset. Distribution was achieved only by **sharding the collection while empty then reseeding** (verified via `config.chunks` = 33/32 split and `explain()` `SHARD_MERGE`). `collStats` on vCore does not expose physical sharding — this is why earlier single-shard runs were mislabelled "2-shard".

---

## 3. Evidence Matrix from Test Logs

The tool records, per host per iteration: task totals (`Totals.*`), per-operation latency percentiles (`OperationLatencyMs.{find_input,remove,insert,find_output}`), connection-open latency (`ConnectionOpenMs`), connection lifecycle (`Lifecycle.{PeakActiveReady, ConnectionsCreated, ConnectionsFailed, DemandToReadyLatencyMs}`), per-second `Throughput[].ActiveReady`, client process CPU/memory (`Process.*`), warm-up (`WarmupSeconds/DocCount`), and retry telemetry (`Retry.*`).

### 3a. Open-loop — "Can the system reliably meet required throughput and latency under average load?"

| Evaluation criterion | Log metric / evidence | How it answers the question |
|---|---|---|
| Same task arrival rate applied? | Config `JobsPerSecondLambda=4.0`, `MinTasksPerJob=150`, `MaxTasksPerJob=500`; `Totals.TotalTasks` (offered) per host | Identical config across targets; offered tasks 71k–77k/host confirm equal arrival pressure. |
| Ops per task & data size identical? | Workload = fixed 4-op cycle; dataset buckets in `base.json` (100k docs, seed 42) | Byte-identical dataset & op mix across all targets. |
| Read/write ratio consistent? | Op mix = 2×find + 1×remove + 1×insert (50% read / 50% write) | Fixed by workload definition; same everywhere. |
| Similar tasks completed / same duration? | `Totals.SuccessfulTasks`, `Arrival.MeasuredArrivalDurationSeconds` (~300 s) | Completion **varies by backend capacity** — this IS the result (see §4). |
| Errors & retries logged? | `Totals.FailedTasks`, `Lifecycle.ConnectionsFailed`, `Retry.RetryableCommandFailures` | Fully captured; error rate + connection-open failures + retry counts reported. |
| Tail latencies stable? | `ConnectionOpenMs.{P90,P99}`, `OperationLatencyMs.*.{P90,P99}`, `TaskCycleLatencyMs.{P90,P99}` | Captured per op; enables p90/p99 comparison. |
| **Key metrics** | **avg latency, tail latency, completion rate, error rate** | Consolidated in §4. |

### 3b. Hold — "How far can each system sustain load, and how does it fail under saturation?"

| Evaluation criterion | Log metric / evidence | How it answers the question |
|---|---|---|
| Actual concurrently-open connections? | Per-second SUM of `Throughput[].ActiveReady`; `Lifecycle.PeakActiveReady` per host | Max combined Ready = the concurrency verdict. |
| Attempts vs successful connections distinguished? | `Lifecycle.ConnectionsCreated` (attempts) vs `PeakActiveReady` (established) vs `ConnectionsFailed` | Separates offered from established from failed. |
| New client per task, no reuse? | Architectural (one `MongoClient` per task, disposed after); `ConnectionsCreated ≈ tasks` | Confirmed by design; created counts track task counts. |
| Where do errors occur after saturation? | `ConnectionsFailed`, `RetryableCommandFailures`, error class (ServerSelectionTimeout), + Azure Monitor server CPU | Locates failure at establish vs op layer. |
| Which layer bottlenecks first? | Client `Process.MaxCpuPercent` vs mongos/DB **server** CPU (Azure Monitor) | Directly identifies the saturated layer (see §4/§6). |
| Backlog cleared after test? | `PeakActiveReady` returns to 0 post-window; gate is closed-loop (no residual) | Closed-loop gate drains at deadline; no persistent backlog. |
| **Key focus** | **bottleneck location + failure mode**, not just max connections | Emphasised in §4 & §6. |

---

## 4. Test Result Summary Matrix

### 4a. Open-Loop (full 4-op churn) — measured, mean of 3 iterations

| Config | Throughput (tasks/s) | Conn p90/p99 (ms) | find-cold p90/p99 (ms) | insert-warm p99 (ms) | Cycle p99 (ms) | Completion (succ/3 iters) | Error % | Conn-open fails | Max conc | Client CPU% | DB server CPU |
|---|---|---|---|---|---|---|---|---|---|---|---|
| DocDB 1s-M80 | 22.0 | 13,409 / 20,788 | 39,056 / 50,912 | 10,341 | 65,534 | 19,942 | 96.7 | 36,245 | 4,485 | 87 | ~1.5% |
| DocDB 1s-M200 | **154.7** | 40,194 / 47,412 | 47,814 / 59,049 | 7,149 | 67,901 | **140,620** | **72.1** | 386 | 16,035 | 90 | ~1.2% |
| DocDB 2s-M60 | 48.3 | 35,957 / 45,093 | 50,323 / 59,136 | 11,127 | 73,078 | 44,373 | 92.3 | 516 | 16,420 | 89 | ~1.5% |
| DocDB 2s-M80 | 72.9 | 18,568 / 22,340 | 27,147 / 32,630 | 8,408 | 49,933 | 66,063 | 89.1 | 268 | 9,927 | 85 | ~1.5% |
| DocDB 2s-M200 | 74.9 † | 23,616 / **27,108** | 30,924 / **43,180** | **5,318** | 60,066 | 67,842 † | 88.1 † | 819 | **17,530** | 83 | ~1.2% |
| Mongo 2-router | 29.4 | 99,307 / 240,699 | 235,179 / 318,431 | 113,316 | 78,512 | 26,592 | 97.8 | 12,132 | 3,165 | 79 | **99.7% (sat)** |
| Mongo 4-router | 71.2 | 42,634 / 159,686 | 45,018 / 110,204 | 6,098 | 38,640 | 64,333 | 94.9 | 610 | 2,351 | 66 | ~20%/router |

† **DocDB 2s-M200 open-loop was gateway-throttled** (request-admission throttling under sustained same-day churn; only 1 of 3 iters healthy). Its **latency figures are valid**, but **throughput/completion are suppressed and NOT comparable** to the clean 1s-M200 OL run. Server CPU stayed idle (~1.2%), confirming throttle ≠ compute saturation.

### 4b. Hold (saturation) — measured, mean of 3 iterations

| Config | Max conc (best) | Avg conc | Cleared 10k? | Establish p90/p99 (ms) | Keepalive-find p99 (ms) | Conn attempts / fails | Client CPU% | Server CPU | Bottleneck layer |
|---|---|---|---|---|---|---|---|---|---|
| DocDB 1s-M80 | 11,154 | 3,866 | 2/3 iters | 108,090 / 121,137 | 113,743 | 92,301 / 33,763 | 89 | ~1.5% | Managed gateway (establish) |
| DocDB 1s-M200 | 11,365 | 4,572 | 3/3 | 102,653 / 131,835 | 138,544 | 75,512 / 28,553 | 82 | ~0.8% | Managed gateway (establish) |
| DocDB 2s-M60 | **12,000** | 3,908 | 2/3 | n/a ‡ | 34,212 | 42,893 / 418 | 90 | ~1.5% | Gateway (eased) |
| DocDB 2s-M80 | **12,000** | 4,476 | 2/3 | n/a ‡ | 23,271 | 49,885 / 88 | 88 | ~1.5% | Gateway (eased) |
| DocDB 2s-M200 | **12,000** | 6,019 | 3/3 | n/a ‡ | 28,273 | 47,693 / 44 | 92 | ~1.5% | Gateway (eased) |
| Mongo 2-router | 4,714 | 3,363 | **0/3** | 51,248 / 144,600 | 170,446 | 15,755 / 2,806 | 77 | **99.7% (sat)** | **mongo VM CPU (TLS/SCRAM)** |
| Mongo 4-router | **12,000** | **10,570** | 3/3 | **17,923 / 34,005** | **14,803** | 38,991 / 2,377 | 88 | ~17–21%/router | Client / balanced |

‡ Establish (Demand→Ready) latency was not captured in the 2-shard hold compacts; keepalive-find p99 is the common metric there.

**Resource utilisation & bottleneck (measured).** DocumentDB **server CPU stayed idle (0.8–1.5%) in every scenario** — it is never compute-bound; its ceiling is the managed gateway's connection/request admission. Mongo **2-router saturated at 99.7% VM CPU** (per-connection TLS+SCRAM on the shared shard/router VMs) — a hard client-independent ceiling at ~4.7k. Mongo **4-router** spread that handshake CPU to ~17–21% per router, removing the ceiling. Client CPU peaked 66–92% across all runs (a secondary constraint at extreme churn).

---

## 5. Azure DocumentDB (Cosmos vCore) Tier Comparison

**Published limits (Microsoft Learn, DocumentDB service limits):**

| Property | Value |
|---|---|
| Max cluster tier | **M200 = 64 vCores / 256 GiB RAM per physical shard** |
| Max physical shards | 10 |
| High availability | Available M30+ (standby is failover-oriented; not a readable replica for scale-out) |
| Vector (HNSW/DiskANN) | M30/M40+ |
| Query memory limit | Tier-dependent (~150 MiB at M80); cross-shard query data cap 1 GiB |
| Tiers M10/M20/M25 | Single shard only, no HA (dev/test); **cannot scale back down** once at M30+ |

**Observed tier behaviour in these tests (2-shard, distributed data):**

| Tier | vCore/shard | Hold max concurrent | Hold keepalive p99 | OL warm-insert p99 | Interpretation |
|---|---|---|---|---|---|
| M60 | 8 | 12,000 (2/3) | 34,212 ms | 11,127 ms | Clears full gate; highest op latency of the three. |
| M80 | ~16 | 12,000 (2/3) | 23,271 ms | 8,408 ms | Best hold keepalive latency of the 2-shard set. |
| M200 | 64 | 12,000 (3/3) | 28,273 ms | **5,318 ms** | Only tier to clear 10k on all 3 hold iters; best warm-op latency; **throttle-prone under sustained churn**. |

**Key findings on tier scaling (observed):**
- **Concurrency (hold) is NOT tier-bound once data is 2-shard distributed** — M60, M80, M200 all reach the full 12,000 gate; server CPU idle (~1.5%) throughout. *Interpretation: the ≥10k hold limit is a data-distribution/front-end property, not a compute property.*
- **Data distribution matters far more than tier for the hold ceiling:** single-shard M80 and M200 both plateau at ~11,000 and post 4–6× worse keepalive p99 (113–139 s) than any 2-shard tier (23–34 s). *Interpretation: distributing the held connections across two shard nodes halves each node's serving load.*
- **Tier improves warm-op service latency** (insert p99 11.1 s → 8.4 s → 5.3 s across M60→M80→M200) and **churn throughput** (higher per-shard connection ceiling), but not hold concurrency.
- **M200 exhibits intermittent gateway request-admission throttling** under sustained same-day churn (idle server CPU; recovered after an ~8-min cooldown). *Operational consideration, not a hard limit.*

---

## 6. MongoDB vs Azure DocumentDB — Final Comparison

### 6a. Architecture & operational model
- **Self-managed MongoDB:** full control of shards, routers (`mongos`), config server, TLS certs, and scaling. Scaling connection capacity = **add stateless router VMs** (cheap, fast — 2→4 routers here). You own patching, HA, certificate rotation, capacity planning.
- **Azure DocumentDB (vCore):** managed shards + hidden gateway + hidden config server. Scaling = **change tier** (vertical) or **add physical shards** (horizontal, but requires empty-first-shard + reseed to actually distribute a small dataset). Zero router/patch management; connection front-end is a managed black box.

### 6b. Performance characteristics observed
- **Connection establishment under churn (open-loop):** DocumentDB is markedly better than mongo at the same scale. Connection p99: DocDB 2s-M200 = **27 s** vs mongo 4-router = 160 s vs mongo 2-router = **241 s**. DocumentDB (1s-M200) also completed **140,620 tasks** vs mongo 4-router's 64,333.
- **Warm operation service time:** mongo's `mongod` serves an already-open socket fastest (find-warm p99 = **1.2 s** on 4-router) — once past the handshake, the self-managed engine is very fast. DocumentDB warm ops are single-digit seconds under load.
- **Hold latency:** mongo 4-router is best (establish p99 **34 s**, keepalive **15 s**); DocDB 2-shard is next (keepalive 23–34 s); DocDB 1-shard is worst (114–139 s).

### 6c. Connection & scaling behaviour
- **Both reach 12,000 concurrent** in their best configs (mongo 4-router; DocDB 2-shard any tier). 
- **DocDB 1-shard plateaus at ~11,000** regardless of tier — a single connection front-end.
- **Mongo 2-router caps at 4,714** — the CPU-saturation baseline.
- **Scaling lever differs fundamentally:** mongo adds cheap stateless routers; DocumentDB adds shards (stateful, data-moving) or tier (vertical). For pure connection-front-end capacity, mongo's router scale-out is lighter-weight.

### 6d. Bottleneck patterns (measured)
| Config | First bottleneck | Evidence |
|---|---|---|
| Mongo 2-router | **mongo VM CPU** (TLS+SCRAM on shared shard/router VMs) | 99.7% server CPU at ~4.7k connections |
| Mongo 4-router | Client / balanced | Routers ~20% CPU; holds 12,000 |
| DocDB 1-shard (any tier) | **Managed gateway, single front-end** | ~11k plateau, idle server CPU, 28–34k conn-open failures |
| DocDB 2-shard (any tier) | Gateway (eased by 2nd front-end) | 12,000 gate, conn-open failures collapse to 44–518 |

*Common root cause (interpretation):* in every configuration the binding constraint is **per-connection TLS + SCRAM establishment**, never the database engine — DocumentDB server CPU is idle (~1.5%) at all times, and mongo's fix was CPU headroom (more routers), not engine tuning.

### 6e. Operational considerations
- **DocumentDB:** minimal ops, idle server CPU, but (a) **M200 throttles under sustained churn** (needs cooldown/backoff), (b) getting real 2-shard distribution required a non-obvious empty-first reshard, (c) HA standby is not a readable scale-out replica.
- **Mongo:** no gateway throttling observed, fastest warm ops, cheap router scale-out — but you own TLS/SCRAM CPU capacity planning, patching, and the 2-router config fails outright at this scale.

### 6f. When each is more suitable
| Scenario | Recommended |
|---|---|
| **Connection-churn-heavy, minimal ops effort** | **DocumentDB** — best establishment throughput, managed, idle server CPU (add shards / raise tier for capacity). |
| **Sustained high concurrency with lowest hold latency** | **Mongo 4-router** — best hold latency, holds full gate — if you can operate the router fleet. |
| **Realistic apps with connection reuse/pooling** | **Either** — the handshake bottleneck largely disappears; pick on ops model & cost. This no-reuse benchmark is a worst-case stress test. |
| **Small managed footprint, ≥10k concurrency required** | **DocumentDB 2-shard M200** — clears the full gate at ~1.5% server CPU from a single managed endpoint. |

---

### Appendix — Data caveats (explicit)
1. **DocDB 2s-M200 open-loop:** throttled; latency valid, throughput/completion suppressed (1/3 healthy iters).
2. **Mongo 2-router:** 97–99.7% error is the genuine saturation result, not a data artefact.
3. **Hold remove/insert p99 = 0:** by design (keepalive-find only).
4. **Establish p99 absent for 2-shard hold:** compact schema difference; keepalive-find p99 used as the common metric.
5. **Server CPU** figures are from Azure Monitor (DocumentDB `CpuPercent`; mongo VM `Percentage CPU`) over each run window; **client CPU** from the tool's `Process.MaxCpuPercent`.
6. **One cold/anomalous hold iteration** occurred per 2-shard tier (first-touch); "cleared 10k" counts the sustained iterations.
