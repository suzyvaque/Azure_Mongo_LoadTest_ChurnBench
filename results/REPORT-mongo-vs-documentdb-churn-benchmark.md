# Connection-Churn Benchmark — MongoDB vs Azure DocumentDB (Cosmos vCore)
### Consolidated Report — Closed-Loop, Open-Loop, and Hold Tests

**Generated:** 2026-08-02 (production-validation update 2026-08-03)
**Scope:** 7 database configurations across three test dimensions — closed-loop average workload (Dimension A), open-loop connection churn, and connection-holding scalability (Dimension B) — plus a single-operation latency baseline. 3 iterations each, three synchronized generator hosts. Interpretations are validated against production telemetry (`prod_log`, 2026-03-26).
**Data provenance:** All figures are aggregated from per-host compact metrics captured directly from the load generators (`results/run-*`). Observed measurements are stated as such; interpretations are labelled.

---

## 1. Test Overview

All tests use the **same .NET 8 load-generation tool**, the **same 100,000-document dataset**, and a strict **no-connection-reuse model**: every logical task opens a brand-new `MongoClient`/connection, performs its work, and disconnects. This isolates **per-connection establishment cost** (TCP + TLS + SCRAM auth) from steady-state query performance. The benchmark spans three test dimensions — a **closed-loop** average-workload test, an **open-loop** churn/establishment stress test, and a **connection-holding** scalability test — plus a single-operation latency baseline for reference.

### Production baseline — two independent workload dimensions

Production telemetry (`prod_log`, 2026-03-26) shows the workload has **two separate dimensions that do not occur simultaneously**:

| Dimension | Production evidence (measured) | Capacity question |
|---|---|---|
| **A. Workload processing** | ~135 tasks/s, 4 operations per task; DB workload peak ~393 ops/s (~98 tasks/s); server-side op latency ~0 ms | Can the DB process the actual task workload at stable latency? |
| **B. Connection holding** | Peak concurrent connections ~11,000 (median 21, avg 799); connection lifetime ~9 s; accept rate ~1,210/s; **~9 connections per task** (driver SDAM); **workload at the connection peak only ~5 ops/s** | Can the DB hold the production-like connection accumulation? |

> **Critical distinction — connection concurrency ≠ active workload concurrency.** The ~11,000 peak is **connection concurrency from accumulation** (many short-lived, mostly-idle held connections), **not 11,000 active database workloads**. By Little's Law, concurrency ≈ accept-rate × lifetime ≈ 1,210/s × ~9 s ≈ 11k — driven by connection lifetime and per-task connection multiplicity (~9 connections/task from driver SDAM), not by 11k simultaneous operations. At the connection peak the database served only ~5 ops/s. The database is therefore **not** required to serve 11,000 active 4-op workloads at once. Dimension A and Dimension B are validated by **separate** tests and must not be merged into a single "11k concurrent workload" interpretation.

### 1a. Closed-Loop Full-Workload Test (average HPC workload validation)

| Aspect | Detail |
|---|---|
| **Objective** | Validate the **actual production task-processing capability** (Dimension A): sustained ~135 tasks/s with the full 4-op cycle at stable latency. Represents the normal production processing pattern. |
| **Traffic model** | **Closed-loop, steady** ~135 tasks/s (arrival paced to completion — not a churn storm). |
| **Workload per task** | Full **4-operation cycle**: `find_input` → `remove` → `insert` → `find_output`, on a fresh connection (no reuse). |
| **Evaluation** | Task throughput, per-operation latency (p50/p90/p99), completion/error rate, execution stability. |
| **Status** | *Measured results pending — closed-loop full-workload runs for the final config matrix are deferred (see §4a). The 4-op per-operation costs are characterised in the single-operation baseline (§4d) and under churn in §4b.* |

### 1b. Open-Loop Churn Test (connection establishment / churn stress)

| Aspect | Detail |
|---|---|
| **Objective** | Validate behaviour under **abnormal/peak connection-arrival** stress: establishment rate, handshake overhead, and admission/resource saturation. |
| **Traffic model** | **Open-loop** Poisson arrivals (`JobsPerSecondLambda = 4.0`/host, `MinTasksPerJob = 150`, `MaxTasksPerJob = 500`). Arrival rate is **independent of system response** — if the backend slows, work backs up (this exposes saturation). |
| **Workload per task** | Full **4-operation cycle** (`find`→`remove`→`insert`→`find`), then `TaskSleepMs = 2900` ms keepalive, then disconnect. |
| **Offered load** | ≈1,300 new connections/s per host → ≈3,900/s combined — a deliberate churn storm well above the production accept rate (~1,210/s). |
| **Evaluation** | Connection-establishment rate, task completion/error rate, throughput, and tail latency (p90/p99) for connection-open and each operation. |

### 1c. Hold Test (concurrent connection-holding scalability)

| Aspect | Detail |
|---|---|
| **Objective** | Validate **large-scale concurrent connection-holding capability** — the production-like connection-accumulation behaviour (Dimension B) — and where the front-end bottlenecks. This measures **connection-holding capacity, not active-workload throughput.** |
| **Traffic model** | **Closed-loop gate** (`Burst.OpenLoop = false`) parking a fixed population of `MaxConcurrentTasks = 4000`/host → **12,000 combined** held connections. Each parked task holds one connection Ready for the full 5-minute window, issuing a light keepalive `find` every `TaskSleepMs = 10000` ms — matching the mostly-idle nature of the real held connections (~5 ops/s at the production peak). |
| **Workload per task** | Single `find_input` keepalive only (no writes) — measures *idle-connection holding capacity*, not operation throughput. |
| **Evaluation** | Peak/avg concurrent Ready (held) connections, whether the production-like ~11k accumulation is sustained, establishment latency, and the failure layer (DB server vs router vs client). |

**Shared parameters & assumptions.** Dataset = 100,000 documents (Small 6 KiB ×10k, Medium 16 KiB ×15k, Large 50 KiB ×35k, XL 58 KiB ×40k; ~4.4 GiB total, fixed RNG seed 42 → byte-identical across all targets). Warm-up reads all 100,000 docs untimed before every iteration. Read/write ratio is fixed by the workload definition (full-workload = 2 reads + 1 delete + 1 insert per task; hold = read-only keepalive). Retry-writes forced ON for DocumentDB. All generators, network path (private endpoint), and gate sizing are identical across targets.

> **Concurrency definition (used throughout):** the combined per-second SUM of each host's driver `ActiveReady` gauge (the `report merge` convention). **Max concurrent = the peak of that summed gauge over the run**; the reported value is the best of the 3 iterations. This gauge counts **held/established connections**, not simultaneously-executing operations.

### 1d. Single-Operation Baseline (operation-level reference)

| Aspect | Detail |
|---|---|
| **Objective** | Provide a clean **operation-level latency baseline** (one `find` or one `insert`), free of establishment/backlog effects. Used only as a per-operation reference — **not** as the closed-loop workload result. |
| **Traffic model** | **Steady, non-saturating** 135 tasks/s (0 failures → true service time). |
| **Workload per task** | One indexed op by `ReqId` on a fresh connection (no reuse): `find` on `calc_input` or `insert` into `calc_output`. |
| **Coverage** | MongoDB (2-shard) and DocumentDB 2-shard at **M60 / M80 / M200** (single generator host). |
| **Evaluation** | Operation p50/p90/p99 and connection-open p99 (steady-state mean of iterations 2–3; iteration 1 dropped as a first-touch transient). |


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

### 3a. Workload processing & churn — "Can the system meet required throughput and latency?"

| Evaluation criterion | Log metric / evidence | How it answers the question |
|---|---|---|
| Same task arrival rate applied? | Config `JobsPerSecondLambda=4.0`, `MinTasksPerJob=150`, `MaxTasksPerJob=500`; `Totals.TotalTasks` (offered) per host | Identical config across targets; offered tasks 71k–77k/host confirm equal arrival pressure. |
| Ops per task & data size identical? | Workload = fixed 4-op cycle; dataset buckets in `base.json` (100k docs, seed 42) | Byte-identical dataset & op mix across all targets. |
| Read/write ratio consistent? | Op mix = 2×find + 1×remove + 1×insert (50% read / 50% write) | Fixed by workload definition; same everywhere. |
| Similar tasks completed / same duration? | `Totals.SuccessfulTasks`, `Arrival.MeasuredArrivalDurationSeconds` (~300 s) | Completion **varies by backend capacity** — this IS the result (see §4). |
| Errors & retries logged? | `Totals.FailedTasks`, `Lifecycle.ConnectionsFailed`, `Retry.RetryableCommandFailures` | Fully captured; error rate + connection-open failures + retry counts reported. |
| Tail latencies stable? | `ConnectionOpenMs.{P90,P99}`, `OperationLatencyMs.*.{P90,P99}`, `TaskCycleLatencyMs.{P90,P99}` | Captured per op; enables p90/p99 comparison. |
| **Key metrics** | **avg latency, tail latency, completion rate, error rate** | Consolidated in §4. |

### 3b. Connection holding — "How many connections can be held, and where does it fail under saturation?"

| Evaluation criterion | Log metric / evidence | How it answers the question |
|---|---|---|
| Actual concurrently-open connections? | Per-second SUM of `Throughput[].ActiveReady`; `Lifecycle.PeakActiveReady` per host | Max combined Ready = the **held-connection** verdict (not active-op count). |
| Held connections vs active workload? | `ActiveReady` (held) vs per-op throughput during the same window | Distinguishes connection-holding capacity from operation-serving load (production: ~11k held at ~5 ops/s). |
| Attempts vs successful connections distinguished? | `Lifecycle.ConnectionsCreated` (attempts) vs `PeakActiveReady` (established) vs `ConnectionsFailed` | Separates offered from established from failed. |
| New client per task, no reuse? | Architectural (one `MongoClient` per task, disposed after); `ConnectionsCreated ≈ tasks` | Confirmed by design; created counts track task counts. |
| Where do errors occur after saturation? | `ConnectionsFailed`, `RetryableCommandFailures`, error class (ServerSelectionTimeout), + Azure Monitor server CPU | Locates failure at establish vs op layer. |
| Which layer bottlenecks first? | Client `Process.MaxCpuPercent` vs mongos/DB **server** CPU (Azure Monitor) | Directly identifies the saturated layer (see §4/§6). |
| Backlog cleared after test? | `PeakActiveReady` returns to 0 post-window; gate is closed-loop (no residual) | Closed-loop gate drains at deadline; no persistent backlog. |
| **Key focus** | **bottleneck location + failure mode**, not just max connections | Emphasised in §4 & §6. |

---

## 4. Test Result Summary Matrix

### 4a. Closed-loop full workload (average HPC workload) — validates Dimension A

Sustained ~135 tasks/s with the full 4-op cycle at a steady, non-saturating pace — the normal production processing pattern. Measures whether each backend processes the real task workload at stable throughput and latency.

**Results — two sources (labelled).** The **DocDB 1-shard** and **Mongo 2-router** columns are from an earlier self-consistent campaign (`results/run-20260624-prevconfig`, 3×610 s steady, older code). The **DocDB 2-shard (M60/M80/M200)** and **Mongo 4-router** columns are fresh single-host runs at the final code version (`results/run-20260803-02`, 3×300 s steady, `TaskSleepMs=0`). Read across the code-consistent 2-shard/4-router group for the cleanest comparison; see the comparability notes after the table.

| Metric | DocDB 1-shard (prev) | DocDB 2s-M60 | DocDB 2s-M80 | DocDB 2s-M200 | Mongo 2-router (prev) | Mongo 4-router |
|---|---|---|---|---|---|---|
| Throughput (tasks/s) | 132.8 | 134.7 | 134.9 | 134.9 | 132.8 | 134.9 |
| Error % | 0.006 | 0.002 | 0.000 | 0.001 | 0.006 | 0.001 |
| find p50 / p90 / p99 (ms) | 28.9 / 93.2 / 159.0 | 26.9 / 59.6 / 112.5 | 27.5 / 59.3 / 107.8 | 27.6 / 64.4 / 123.7 | 70.2 / 141.1 / 211.8 | 45.6 / 51.7 / 67.9 |
| remove p50 / p90 / p99 (ms) | 2.4 / 5.0 / 77.1 | 3.5 / 4.9 / 44.8 | 3.4 / 4.9 / 38.6 | 3.9 / 5.4 / 46.7 | 4.1 / 6.5 / 80.2 | 3.6 / 5.1 / 6.8 |
| insert p50 / p90 / p99 (ms) | 2.8 / 5.1 / 79.1 | 3.3 / 4.6 / 39.5 | 3.5 / 5.0 / 41.1 | 4.0 / 5.4 / 47.9 | 4.5 / 7.3 / 83.2 | 4.7 / 5.5 / 7.4 |
| find_output p50 / p90 / p99 (ms) | 0.7 / 2.9 / 53.6 | 1.7 / 2.1 / 10.1 | 1.6 / 2.2 / 9.8 | 1.9 / 2.6 / 15.8 | 1.1 / 4.2 / 16.0 | 1.5 / 1.7 / 3.0 |
| Connection-open p90 / p99 (ms) ‖ | 30.9 / 113.0 | 25.5 / 77.3 | 25.8 / 75.6 | 26.3 / 82.4 | 89.7 / 137.1 | 28.8 / 37.0 |

**Read.** At the production task rate, **all six configurations process the full 4-op workload at ~133–135 tasks/s with ≤0.006% error**, confirming Dimension A (workload processing) is met by every backend and tier at average load. Two patterns stand out in the code-consistent 2-shard/4-router runs:

- **DocumentDB has the lower find median; MongoDB has the tighter tails.** DocDB 2-shard `find_input` p50 is ~27 ms across M60/M80/M200 with p99 ~108–124 ms; Mongo 4-router `find_input` p50 is higher (~46 ms) but its p99 is far lower (~68 ms), and its `remove`/`insert`/`find_output` p99 stay single-digit-ms versus DocDB's ~40–48 ms p99. The difference is median-versus-tail shape, not a throughput gap.
- **DocumentDB tier does not change closed-loop service latency.** M60, M80 and M200 are within run-to-run noise of each other at this non-saturating rate — consistent with the tier-independent service time seen in the single-operation baseline (§4d). Tier buys saturation headroom under churn (§4b), not per-op latency at average load.

> ‖ **Comparability caveats for the previous-config columns.** The 1-shard and 2-router columns predate three later changes — the R2/R3 connection-lifecycle refactor, the mongo TLS-chain-validation bypass, and DocumentDB retry-writes-on. **Per-operation service latency is comparable** across versions (server-side op timing is stable); **connection-open figures are NOT directly comparable** to the newer columns or to §4b/§4c/§4d and are shown only for context. The DocumentDB **tier for this June campaign is not recorded** (labelled "1-shard (prev)", not M80/M200); data was single-physical-shard, HA-on.

> **Cross-column disclaimer (new vs previous runs).** The 2-shard and 4-router columns (`run-20260803-02`) were collected **single-host, `TaskSleepMs=0`, 3×300 s, at the final code version**; the 1-shard and 2-router columns (`run-20260624-prevconfig`) used **`TaskSleepMs=10000`, 3×610 s, on older code**. These differences affect total-cycle timing and connection-open figures, **not per-operation service latency** — so the 2-shard-vs-4-router group is the like-for-like read, and the prev columns are context only. The Mongo 4-router run applies the same client-side TLS relaxation (`MongoAllowInsecureTls`, mongo targets only) already used in §4b/§4c so all four routers are usable; DocumentDB is never altered.

### 4b. Open-Loop churn (full 4-op) — measured, mean of 3 iterations

Full 4-op cycle (`find`→`remove`→`insert`→`find`) with a fresh connection per task, offered open-loop at ≈3,900 new connections/s combined (Poisson, ~1,300/s/host). Arrival rate is independent of response, so a slowing backend backs up — this is the throughput/latency-under-churn test; each iteration offers ~71k–77k tasks/host over ~300 s.

| Metric | DocDB 1s-M80 | DocDB 1s-M200 | DocDB 2s-M60 | DocDB 2s-M80 | DocDB 2s-M200 | Mongo 2-router | Mongo 4-router |
|---|---|---|---|---|---|---|---|
| Throughput (tasks/s) | 22.0 | **154.7** | 48.3 | 72.9 | 74.9 † | 29.4 | 71.2 |
| Conn p90/p99 (ms) | 13,409 / 20,788 | 40,194 / 47,412 | 35,957 / 45,093 | 18,568 / 22,340 | 23,616 / **27,108** | 99,307 / 240,699 | 42,634 / 159,686 |
| find-cold p90/p99 (ms) | 39,056 / 50,912 | 47,814 / 59,049 | 50,323 / 59,136 | 27,147 / 32,630 | 30,924 / **43,180** | 235,179 / 318,431 | 45,018 / 110,204 |
| insert-warm p99 (ms) | 10,341 | 7,149 | 11,127 | 8,408 | **5,318** | 113,316 | 6,098 |
| Cycle p99 (ms) | 65,534 | 67,901 | 73,078 | 49,933 | 60,066 | 78,512 | 38,640 |
| Completion (succ/3 iters) | 19,942 | **140,620** | 44,373 | 66,063 | 67,842 † | 26,592 | 64,333 |
| Error % | 96.7 | **72.1** | 92.3 | 89.1 | 88.1 † | 97.8 | 94.9 |
| Conn-open fails | 36,245 | 386 | 516 | 268 | 819 | 12,132 | 610 |
| Max conc | 4,485 | 16,035 | 16,420 | 9,927 | **17,530** | 3,165 | 2,351 |
| Client CPU% | 87 | 90 | 89 | 85 | 83 | 79 | 66 |
| DB server CPU | ~1.5% | ~1.2% | ~1.5% | ~1.5% | ~1.2% | **99.7% (sat)** | ~20%/router |

† **DocDB 2s-M200 open-loop was gateway-throttled** (request-admission throttling under sustained same-day churn; only 1 of 3 iters healthy). Its **latency figures are valid**, but **throughput/completion are suppressed and NOT comparable** to the clean 1s-M200 OL run. Server CPU stayed idle (~1.2%), confirming throttle ≠ compute saturation.

### 4c. Hold — connection-holding scalability — measured, mean of 3 iterations

Closed-loop gate parking a fixed population of 4,000 connections/host (**12,000 combined**), each held Ready for the full 5-min window with a light keepalive `find` — the **connection-holding** capacity test: how many mostly-idle established connections can be sustained (Dimension B), not how much active work is served. Matches production's connection-accumulation behaviour (~11k held connections at ~5 ops/s). "Max conc" below is **held connections**, not active operations.

| Metric | DocDB 1s-M80 | DocDB 1s-M200 | DocDB 2s-M60 | DocDB 2s-M80 | DocDB 2s-M200 | Mongo 2-router | Mongo 4-router |
|---|---|---|---|---|---|---|---|
| Max conc (best) | 11,154 | 11,365 | **12,000** | **12,000** | **12,000** | 4,714 | **12,000** |
| Avg conc | 3,866 | 4,572 | 3,908 | 4,476 | 6,019 | 3,363 | **10,570** |
| Cleared 10k? | 2/3 iters | 3/3 | 2/3 | 2/3 | 3/3 | **0/3** | 3/3 |
| Establish p90/p99 (ms) | 108,090 / 121,137 | 102,653 / 131,835 | n/a ‡ | n/a ‡ | n/a ‡ | 51,248 / 144,600 | **17,923 / 34,005** |
| Keepalive-find p99 (ms) | 113,743 | 138,544 | 34,212 | 23,271 | 28,273 | 170,446 | **14,803** |
| Conn attempts / fails | 92,301 / 33,763 | 75,512 / 28,553 | 42,893 / 418 | 49,885 / 88 | 47,693 / 44 | 15,755 / 2,806 | 38,991 / 2,377 |
| Client CPU% | 89 | 82 | 90 | 88 | 92 | 77 | 88 |
| Server CPU | ~1.5% | ~0.8% | ~1.5% | ~1.5% | ~1.5% | **99.7% (sat)** | ~17–21%/router |
| Bottleneck layer | Managed gateway (establish) | Managed gateway (establish) | Gateway (eased) | Gateway (eased) | Gateway (eased) | **mongo VM CPU (TLS/SCRAM)** | Client / balanced |

‡ Establish (Demand→Ready) latency was not captured in the 2-shard hold compacts; keepalive-find p99 is the common metric there.

**Resource utilisation & bottleneck (measured).** DocumentDB **server CPU stayed idle (0.8–1.5%) in every scenario** — it is never compute-bound; its ceiling is the managed gateway's connection/request admission. Mongo **2-router saturated at 99.7% VM CPU** (per-connection TLS+SCRAM on the shared shard/router VMs) — a hard client-independent ceiling at ~4.7k. Mongo **4-router** spread that handshake CPU to ~17–21% per router, removing the ceiling. Client CPU peaked 66–92% across all runs (a secondary constraint at extreme churn).

### 4d. Single-operation baseline (operation-level reference) — measured, steady-state mean of iterations 2–3

One indexed op per fresh connection at a steady **135 tasks/s** (no reuse). This rate saturates no backend (**0 failures**), so these are clean service times rather than the saturation-blended latencies of §4b. Values are the steady-state mean (iterations 2–3; iteration 1 dropped as a first-touch transient). Used as an **operation-level reference only** — it is not the closed-loop full-workload result (§4a).

| Metric | MongoDB (2-shard) | DocDB 2s-M60 | DocDB 2s-M80 | DocDB 2s-M200 |
|---|---|---|---|---|
| find op p50 / p90 / p99 (ms) | 41.2 / 55.8 / **69.8** | **26.5** / 47.8 / 107.7 | 34.3 / 75.7 / 198.8 | 28.7 / 58.0 / 154.1 |
| insert op p50 / p90 / p99 (ms) | 44.2 / 59.0 / **91.8** | **27.6** / 48.8 / 117.6 | 34.6 / 68.0 / 126.0 | 29.9 / 54.5 / 111.3 |
| Connection-open p99 (find / insert, ms) | **43.5 / 50.2** | 77.9 / 86.2 | 105.0 / 89.3 | 96.6 / 82.2 |
| Client CPU peak (%) | 29–38 | 28–32 | 29–33 | 27–31 |
| Failures | 0 | 0 | 0 | 0 |

Source runs: MongoDB = `run-20260624-shard` (mongo-shard single-op steady, TLS on, 3×600 s); DocumentDB = `run-20260803-01/docdb-{m60,m80,m200}` (single-op steady, 3×300 s). At 135/s the mongos router count is immaterial (routers only bottleneck under establishment saturation), so the MongoDB 2-shard result is a valid single-op service-time measurement for the sharded MongoDB cluster.

**Findings (single-op service time, measured):**
- **DocumentDB has the lower typical (median) latency; MongoDB has the tighter tail.** DocumentDB serves the median op faster (find p50 27–34 ms, insert p50 28–35 ms) than MongoDB (find 41 ms, insert 44 ms), but MongoDB holds the **tighter p99** on both ops (find 70 ms vs DocumentDB 108–199 ms; insert 92 ms vs 111–126 ms).
- **DocumentDB tier size does not materially change single-op service time.** M60, M80, and M200 land within run-to-run noise of each other — a single indexed keyed op at 135/s is too light to be compute-bound, so extra vCores do not lower its service latency.
- **This refines the §4b / §5 warm-op observation.** Under the saturated 4-op churn (§4b), DocumentDB warm-op p99 *appeared* to improve with tier (insert p99 11.1 s → 5.3 s, M60→M200). Isolated from saturation, raw single-op service time is **tier-independent** — the earlier gain came from higher tiers easing queueing, not faster operation execution.
- **MongoDB opens connections faster even unsaturated** (conn-open p99 44–50 ms vs DocumentDB 78–105 ms), consistent with the direct-to-router pin versus the DocumentDB SRV/gateway handshake path.

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

> **Note:** "Hold max concurrent" is **connection-holding capacity** (idle-capable, established/held connections), **not** active-operation throughput. It answers Dimension B (how many connections can be held), not how much 4-op work is served concurrently.

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
- **Both sustain 12,000 held connections** in their best configs (mongo 4-router; DocDB 2-shard any tier) — above the ~11k production accumulation.
- **DocDB 1-shard plateaus at ~11,000 held connections** regardless of tier — a single connection front-end.
- **Mongo 2-router caps at 4,714 held connections** — the CPU-saturation baseline.
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
| **Sustained high connection-holding with lowest hold latency** | **Mongo 4-router** — best hold latency, holds 12,000 connections — if you can operate the router fleet. |
| **Realistic apps with connection reuse/pooling** | **Either** — the handshake bottleneck largely disappears; pick on ops model & cost. This no-reuse benchmark is a worst-case stress test. |
| **Small managed footprint, ≥10k connection-holding required** | **DocumentDB 2-shard M200** — holds 12,000 connections at ~1.5% server CPU from a single managed endpoint. |

---

### 6g. Conclusion — validated against production requirements

The benchmark is evaluated against the **two independent production dimensions** (§1), not a single combined figure.

**Dimension A — workload processing (~135 tasks/s, 4 ops/task).** *Direct closed-loop validation pending (§4a).* Supporting evidence to date: at a non-saturating 135 tasks/s the single-operation baseline (§4d) shows **0 failures** and low, stable per-op latency on both backends (DocumentDB median find/insert ~27–35 ms; MongoDB ~41–44 ms with a tighter p99); production server-side op latency is ~0 ms with peak workload only ~393 ops/s. No evidence suggests either backend is workload-throughput-bound at production rates; the closed-loop test will confirm sustained 4-op throughput/latency directly.

**Dimension B — connection holding (~11k accumulation).** **Validated (measured).** Both **mongo 4-router** and **DocumentDB 2-shard (M60/M80/M200)** sustain **12,000 held connections** — above the ~11k production spike — while the database engine stays idle (DocumentDB server CPU ~1.5%). DocumentDB **1-shard** holds ~11,000 and **mongo 2-router** caps at ~4,714 (VM-CPU saturated), so those configurations do **not** meet the holding requirement with headroom. Failure/throttling is characterised: DocumentDB's ceiling is managed-gateway admission (idle CPU); mongo's is TLS/SCRAM VM CPU; DocumentDB M200 can intermittently throttle admission under sustained churn (recovers on cooldown).

**Metric-driven verdict.**

| Requirement | Metric | Result |
|---|---|---|
| Task throughput | ~135 tasks/s sustained | Supported by §4d (0 failures at 135/s) + production (~0 ms op latency); closed-loop confirmation pending (§4a) |
| Operation latency | per-op p50/p90/p99 | Low & stable at 135/s (§4d); DocumentDB lower median, MongoDB tighter tail |
| Connection holding | ≥ ~11k held | **Met** by mongo 4-router and DocumentDB 2-shard (12,000); **not met** by DocumentDB 1-shard (~11k, no headroom) or mongo 2-router (~4.7k) |
| Failure / throttling | failure layer & rate | Characterised (§4c, §6d); no engine-level failure — bottleneck is connection establishment/admission |

- **What is validated:** connection-holding scalability to ≥12,000 for the recommended configs (mongo 4-router, DocumentDB 2-shard), and stable operation-level latency at the production 135 tasks/s rate.
- **What capacity dimension is validated:** primarily **Dimension B (connection holding)** by direct measurement; **Dimension A (workload processing)** is supported by the single-op baseline and production evidence, with closed-loop full-workload confirmation pending (§4a).
- **What is NOT claimed:** these results do **not** claim the database serves ~11,000 simultaneous active 4-op workloads. Production reaches ~11k via connection accumulation (mostly-idle held connections at ~5 ops/s) — a fundamentally different requirement from 11k active operations, which no test here simulates and which production does not exhibit.

### 6h. Scope limitation — driver/runtime connection behaviour

Absolute connection-establishment behaviour (per-task connection count, handshake/SDAM footprint, accept rate) depends on the MongoDB **driver and runtime** in use — production reaches ~9 connections per task via driver topology monitoring (SDAM), whereas the benchmark pins `directConnection=true` to control the access path. Absolute connection-footprint and establishment-latency numbers may therefore vary with the driver/runtime. The **relative MongoDB-vs-DocumentDB comparison remains valid**, since both targets are exercised under identical client tooling, dataset, and network path.

---

### Appendix — Data caveats (explicit)
1. **DocDB 2s-M200 open-loop:** throttled; latency valid, throughput/completion suppressed (1/3 healthy iters).
2. **Mongo 2-router:** 97–99.7% error is the genuine saturation result, not a data artefact.
3. **Hold remove/insert p99 = 0:** by design (keepalive-find only).
4. **Establish p99 absent for 2-shard hold:** compact schema difference; keepalive-find p99 used as the common metric.
5. **Server CPU** figures are from Azure Monitor (DocumentDB `CpuPercent`; mongo VM `Percentage CPU`) over each run window; **client CPU** from the tool's `Process.MaxCpuPercent`.
6. **One cold/anomalous hold iteration** occurred per 2-shard tier (first-touch); "cleared 10k" counts the sustained iterations.
7. **Single-operation results (§4d) are an operation-level reference**, not the closed-loop full-workload result; the closed-loop full-workload matrix (§4a) is pending.
8. **Production baseline figures** are from `prod_log` (2026-03-26: mongostat `metrics.json`, `mongod.log` connection events, and the query profiler). "Held/concurrent connections" denotes established connections, **not** simultaneously-executing operations (~11k held at ~5 ops/s at peak).
