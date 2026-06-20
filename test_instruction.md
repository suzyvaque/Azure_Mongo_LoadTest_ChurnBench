# MongoDB Connection-Churn Benchmark — Build & Execution Instructions

## 1. Purpose

Build a .NET (C#) benchmark tool that measures **how well three candidate databases tolerate extreme connection churn** under an HPC workload where **every Task opens a brand-new connection, performs a strict 4-operation sequence (`find` input → `remove` output → `insert` output → `find` output), then fully disconnects**. Connections are **never reused** across Tasks.

This benchmark intentionally does **not** model a long-lived connection-pool application. It models a worst-case "1 Task = 1 process = 1 connection lifecycle" pattern.

---

## 2. Workload Model

### 2.1 Pipeline

**Pre-stage (one-time setup)**
1. Load 100,000 documents into the **Input Collection** (`calc_input`).
2. Create a **`ReqId` index** on **both** `calc_input` and `calc_output` (e.g. `{ ReqId: 1 }`,
   `unique` on `calc_input`). Every Task op keys on `ReqId`, so without this index each op is a full
   collection scan over 100,000 docs — invalidating the latency/RU comparison. Apply identically on
   all three targets (`cosmos-ru`, `documentdb`, `mongo-vm`).

**Job stage (Wrapper)**
1. Fetch Request IDs from `calc_input` in batches of **up to 500** (500 is the batch cap, not a fixed count — see Section 6.2).
2. Group the fetched IDs into a single Job (one command per ID).

**Task stage (per Request ID) — the unit under test**
1. Create a **new DB connection**.
2. `find` the Request ID in `calc_input` (read input).
3. Run calculation. In production this is the HPC calculation time; **in this test it is substituted by a fixed `sleep`** of duration `taskSleepMs` (config-driven, see Section 6.6). This holds the connection open for a deterministic, configurable interval so concurrency is reproducible across targets.
4. `remove` the existing result in `calc_output` (delete by `ReqId`).
5. `insert` the result into `calc_output` (write result).
6. `find` the result in `calc_output` (read back).
7. Release the DB connection. **Do not reuse** the previous connection, client, session, cursor, or pool in the next Task.

> **Operation count per Task = strictly 4 DB ops, in this exact order: `find` (input) → `remove` (output) → `insert` (output) → `find` (output).** All four ops are keyed by **`ReqId`** (the logical Request ID), not the sequential `_id`. Production uses **remove + insert (NOT upsert)** to write results, so the `remove` step is **mandatory** for every Task — never replace it with an upsert and never skip it. The two `find` ops target different collections (`calc_input` then `calc_output`).

> **Full workload vs. single-operation isolation tests.** The 4-op cycle above is the **canonical full
> workload**. The benchmark also runs **single-operation isolation tests** — **find-only** (one `find` on
> `calc_input`) and **insert-only** (one `insert` into `calc_output`) — where each Task performs exactly
> one op with `TaskSleepMs = 0`. These isolate the cold read / cold write halves of the connection-churn
> cost from the combined cycle. See Section 6.4 for how the two test types are run.

### 2.2 No-Reuse Requirements (HARD CONSTRAINTS)

The implementation MUST NOT do any of the following:
- Use a `static` MongoClient.
- Use a singleton MongoClient.
- Register a MongoClient in a DI container for reuse.
- Hold a long-lived MongoClient per worker.
- Warm up the connection pool.
- Set `minPoolSize > 0`.
- Reuse client / cache / connection / session / cursor between requests.
- Share a MongoClient across requests.

> **Connection warm-up vs. data-cache warm-up are different things.** This constraint forbids **connection-layer** warm-up only (no pre-opened pools, no reused clients/sessions/cursors) — the cold connection is exactly what the test measures. It does **not** forbid **data-cache** warm-up of `calc_input`, which is performed before the timed run to equalize starting state across backends (see Section 6.5). Never let data-cache warm-up open or retain reusable connections.

### 2.3 Official MongoDB C# Driver — No-Pool Implementation Rules

The official driver's `MongoClient` always owns an internal connection pool, so a *fully* pool-free connection is not natively possible. When using the official driver:
- Create a **new MongoClient / MongoClientSettings per request**.
- Ensure connection resources are **actually released** after each request.
- Do **not** reuse MongoClient, database, collection, cluster, session, or cursor between requests.
- Constrain the internal pool to **`maxPoolSize = 1`, `minPoolSize = 0`**.
- Collect **connection created / ready / closed** events via the driver's event listener or connection-monitoring API.
- In the report, state explicitly: *"An internal driver pool object may exist, but no pooling/reuse occurs between requests."*
- Document this limitation clearly in **README and code comments**, and implement reuse-avoidance as precisely as the driver allows.

---

## 3. Dataset

Load **100,000 documents** into `calc_input` during the prepare-data stage.
- Four size buckets (including response metadata), with the mix below chosen so the mean matches the
  production trace:

  | Bucket | Doc size | Share | Doc count | Contribution to mean |
  |---|---|---|---|---|
  | Small  | **6 KB**  | **10 %** | 10,000 | 0.6 KB |
  | Medium | **16 KB** | **15 %** | 15,000 | 2.4 KB |
  | Large  | **50 KB** | **35 %** | 35,000 | 17.5 KB |
  | XL     | **58 KB** | **40 %** | 40,000 | 23.2 KB |
  | **Total** | — | **100 %** | **100,000** | **≈ 43.7 KB/doc** |

- Weighted mean ≈ **43.7 KB/doc** (≈ 44 KB), so the `calc_input` total ≈ **43.7 KB × 100,000 ≈ 4.37 GB ≈ 4 GB** —
  consistent with the production-trace observation of ≈ 44 KB/doc. Seed the exact bucket counts above
  (use a fixed RNG seed so the mix is byte-identical across all three targets).

> **Indexing:** create a `ReqId` index on **both** `calc_input` and `calc_output` during `prepare-data`
> (see Sections 2.1 and 5). All Task ops query the `ReqId` field, so the index is mandatory on every
> target before any timed run.

> **Identifier semantics:** the document `_id` is just a **sequential** value (row counter) and is **not** used to drive operations. All Task operations (`find` / `remove` / `insert` / `find`) are keyed by **`ReqId`**, which is the logical Request ID passed from the Job. Use `ReqId` — not `_id` — for all reads and writes.

### 3.1 `calc_input` document shape

```jsonc
{
  "_id": "1653",                    // GENERATED per doc — sequential "1".."100000" (string)
  "ReqId": "1653",                  // GENERATED per doc — == _id; INDEXED (unique), all ops key on this
  "CalculatorFileNm": "PricingEngine.dll",  // RANDOMIZED per doc from a fixed list (seed 42)
  "CalculatorVersion": "2.3.1",     // RANDOMIZED per doc (seed 42)
  "SkipCalculation": false,         // FIXED — keep as-is
  "Input": "<base64>",              // COMPUTED per doc — length sized so WHOLE doc hits its 6/16/50/58 KB bucket
  "SuccessExitCodeList": [0],       // FIXED — keep as-is (real array)
  "ReqClass": "A"                   // RANDOMIZED per doc from a fixed list (seed 42)
}
```

### 3.2 `calc_output` document shape

```jsonc
{
  "_id": "1653",                    // RUNTIME — == ReqId of the Task
  "ReqId": "1653",                  // RUNTIME — INDEXED (non-unique)
  "StartTime": "2026-06-15T09:00:00Z",  // RUNTIME — Task start timestamp (ISO or BSON date)
  "EndTime":   "2026-06-15T09:00:10Z",  // RUNTIME — Task end timestamp
  "Output": "<base64>",             // RUNTIME — result payload produced by the Task
  "OutputFormatCd": { "fmt": "b64" }    // RUNTIME — real object (any shape)
}
```

---

## 4. Target Resources

| Target key      | Resource |
|-----------------|----------|
| `documentdb`    | Azure DocumentDB (M80, 32 vCore, 128 GB RAM, 512 GB SSD) |
| `cosmos-ru`     | Azure Cosmos DB for MongoDB — **fixed 100,000 RU/s (100k RU/s). DO NOT change RU/s.** |
| `mongo-vm`      | MongoDB on Azure VM (Windows Server Datacenter 2025, 32 vCore, 256 GB RAM, 512 GB data disk SSD) |

> **Versions:** MongoDB **Server 7.0** and **wire/API 7.0** for all three targets. Build/run the benchmark tool on **.NET 8 (LTS)** with the **MongoDB C# Driver 2.30** (Core API).

> **`cosmos-ru` scope for the current run:** `cosmos-ru` is **excluded from the current campaign**
> (only `mongo-vm` and `documentdb` are run) but is kept as a first-class target throughout this
> document because it **will be considered in a future round**. When it is re-enabled it runs at the
> **fixed 100,000 RU/s (100k RU/s)** budget above — never auto-raised mid-campaign.

### 4.1 Test topology

| Host | Role |
|------|------|
| **VM1** (Windows Server 2025) | **The single load-generating client.** Runs the benchmark tool (`prepare-data` / `test` / `report`) against **all three** targets, **one target at a time (sequential, never in parallel)**, so the generator's full capacity is available to each and the comparison stays apples-to-apples. |
| **VM2** (Windows Server 2025) | Hosts the **`mongo-vm`** MongoDB Server 7.0 instance. VM1 reaches it over the **private VNet**. |
| `cosmos-ru`, `documentdb` | Managed PaaS — no dedicated VM. VM1 reaches them via **Private Endpoint** in the same region/VNet (see Section 6.3 check 2). |

> **Single-client rule:** using the *same* VM1 host (identical CPU, ephemeral-port range, TCP tuning, egress path) for every target ensures any observed difference is the database, not the client. VM1 must be sized and TCP-tuned (Section 7.3) so it never becomes the bottleneck at ~1,200 conn/sec and ~11K concurrent connections.

---

## 5. CLI Requirements

The tool MUST support exactly these invocations:

```bash
dotnet run -- prepare-data --config config.json --target cosmos-ru
dotnet run -- prepare-data --config config.json --target documentdb
dotnet run -- prepare-data --config config.json --target mongo-vm

dotnet run -- test --config config.json --target cosmos-ru
dotnet run -- test --config config.json --target documentdb
dotnet run -- test --config config.json --target mongo-vm

dotnet run -- report --input results/ --output report.html
```

> **`prepare-data` MUST**: (1) load exactly 100,000 documents into `calc_input`, and (2) create the
> `ReqId` index on **both** `calc_input` and `calc_output` (see Sections 2.1, 3). It must be safe to
> re-run idempotently (creating an existing index is a no-op). The `test` command's preflight
> (Section 6.3) verifies both the count and the indexes before any timed run.

---

## 6. Workload Targets (derived from production measurements)

Basis: **Task = connection**, **4 DB ops per Task**. The targets below are derived from a production trace; use them directly as test inputs (do not re-derive from raw hourly data).

**Derived peaks used to size the test:**
- **Peak Task load** → ≈ **485,000 Tasks/h** → avg **135 conn/sec**, **540 ops/sec** sustained.
- **Peak Job arrival** → ≈ **2,000 Jobs/h** → **0.57 Job/sec** (low Task/Job ratio at this hour).
- **DO NOT use hourly averages as the test target** — they fall under 1/3 of peak and understate the real load.

### 6.1 Burst correction (why averages are insufficient)

Production `mongod.log` showed instantaneous values far above the hourly average:
- Connection open rate ≈ **1,210 conn/sec** (≈ 9× the peak-hour average of 135 conn/sec).
- Concurrent connection peak ≈ **11,012**.

The HPC scheduler injects a Job's Tasks onto available cores **all at once**, so connections arrive in **stepped bursts**, not uniformly. Design tests around a **burst envelope**, not the average.

### 6.2 Test scenarios (1-hour test basis)

| Scenario | conn/sec | ops/sec | Concurrent conn | Job arrival |
|----------|----------|---------|-----------------|-------------|
| **Steady (peak-hour sustained)** | 135 sustained | 540 sustained | ≈ 135 × per-Task hold time (Little's Law; see Section 6.6) | ~1,100 Jobs/h, variable Tasks/Job |
| **Burst (Poisson queue)** | 1,200+ instantaneous | — | 11K+ instantaneous | Poisson λ = 0.57, up to 500 Tasks injected at once |
| **Stress (×2 headroom)** | 2,000+ | 1,100+ | 20K+ | Overload |

**A. Steady-state — "Peak-hour sustained"** (hold the peak-Task profile uniformly for 1 hour)

| Parameter | Target |
|-----------|--------|
| Task throughput | **485,000 Tasks/h** (≈135 conn/sec sustained) — this is the primary driver |
| DB ops target | **≈540 ops/sec sustained** (find:remove:insert:find = 1:1:1:1) |
| ops mix | read 50% (input find + output find) / write 50% (remove + insert) |
| Job submission | ≈ **1,100 Jobs/h**, **variable Tasks/Job** (peak-hour average ≈ 441; batch cap = 500, NOT fixed) |
| What to observe | whether each target **sustains 540 ops/sec for 1 h**, any **connection rejections**, and the **latency distribution** — compared **primarily by p99 (and p95/p99.9), not average** (see note below) |

> **Tasks/Job is not constant.** In production each Job carries a variable number of Tasks (observed range from ~14 to ~491 per Job across hours); 500 is the **batch fetch cap**, not a fixed per-Job count. Drive the Steady scenario by **Task throughput (485,000/h)**, not by multiplying Jobs × 500.

> **No pass/fail thresholds — this is a comparison study.** The goal is to **observe how each DB option behaves under this workload and pick the best fit**, not to pass/fail any candidate against a fixed number. There is no hard latency SLA. Compare the candidates against **each other**, prioritizing **p99 (with p95 and p99.9), not average** — tail latency is what reveals connection-churn cost. The throughput and connection-rejection figures above are **observation targets** (what to measure and compare), not gates.

**B. Burst / Spike — "Poisson queue"** (model Job arrivals as Poisson; each Job injects its Task batch instantly)

| Parameter | Target |
|-----------|--------|
| Job arrival distribution | **Poisson(λ = 0.57 Job/sec)** (peak-Job-hour basis) |
| Tasks per Job | **up to 500** Tasks spawned **simultaneously** (use the 500 batch cap as the worst-case burst) |
| Instantaneous open rate target | **≥ 1,200 conn/sec** (reproduce measured burst) |
| Concurrent connection target | sustain **≥ 11,000** |
| Queue observation | accept backlog, mongod wait queue, client connect timeouts |

> Poisson is used because Job arrivals are independent with a fixed hourly mean; this naturally reproduces burst overlap (the tail where multiple Jobs land in the same instant). Uniform arrival under-estimates worst-case concurrency.

**C. Stress / Headroom — "design-limit validation"** (apply safety factor above measured peaks)

| Metric | Measured peak | Test target (×1.5–2 headroom) |
|--------|---------------|-------------------------------|
| conn/sec (avg) | 135 | **≥ 270** |
| conn/sec (burst) | 1,210 | **≥ 2,000** |
| Concurrent connections | 11,012 | **≥ 20,000** (or up to `maxIncomingConnections`) |
| ops/sec | 540 | **≥ 1,100** |

**Test design summary:** Baseline = the peak-hour sustained **135 conn/sec (540 ops/sec)**, NOT the hourly average. Add a **Poisson-arrival burst of 1,200 conn/sec and 11K concurrent connections** as the spike. For each target, **observe and compare**: connection rejections (count/rate), tail latency (p99/p95/p99.9) relative to the other candidates, and whether ops/sec holds during the burst.

### 6.3 Preflight checks (MUST pass before any timed test)

Before starting a load test against any `--target`, the tool MUST run an automated preflight and **abort with a clear error if any check fails** (do not start the timed run on a failed precondition, or the results are invalid).

**Required checks:**
1. **Dataset present & complete** — `calc_input` exists and contains **exactly 100,000 documents** (`countDocuments` == 100,000, and the collection is non-empty). Fail with `DataSetMissing` if absent, empty, or count mismatched. Optionally spot-check that a sample document has the expected `ReqId`/`Input` fields and is queryable by `ReqId`.
2. **`ReqId` indexes present** — a `ReqId` index exists on **both** `calc_input` and `calc_output` (e.g. `{ ReqId: 1 }`, `unique` on `calc_input`). Verify via the index catalog and fail with `IndexMissing` if either is absent. Without it every `find`/`remove` by `ReqId` is a full collection scan, which inflates latency and (on `cosmos-ru`) burns large RU per op — making the cross-backend comparison invalid.
3. **Network path is private** — connectivity to the DB/VM is over a **private or peered** path (Private Endpoint / VNet peering / private DNS), **not** the public internet. Verify the resolved endpoint is a private IP (RFC1918) or the expected private-endpoint FQDN, and fail if it resolves to a public address. This keeps latency results free of internet-path noise and matches the prod topology.
4. **Connectivity & auth smoke test** — open one connection, run a single `find` by `ReqId`, and close it cleanly. Confirms credentials, TLS, server selection, and the `ReqId` query path all work before load begins.
5. **`calc_output` writable** — the output collection exists (or auto-creates) and a `remove`+`insert`+`find` round-trip on a throwaway `ReqId` succeeds; clean up the probe document afterward.
6. **Server/throughput config matches the target spec** — e.g., Cosmos (when included) is at the **fixed 100,000 RU/s (100k RU/s)** (and must not be changed); DocumentDB is the M80 tier; `mongo-vm` mongod is up with the intended `maxIncomingConnections`. Record these in the report's config summary.
7. **Client host headroom** — the test host's **ephemeral port range and `TcpTimedWaitDelay`** are tuned for the expected churn (see Section 7.3), and the process **file-descriptor / handle limit** is high enough for the target concurrent-connection level. Warn (or fail) if the configured headroom is below the scenario's concurrent-connection target.
8. **Clock & time sync** — client clock is NTP-synced (latency and per-second-rate metrics depend on accurate timestamps).
9. **Clean starting state** — no leftover connections from a prior aborted run; results directory writable; sufficient local disk for raw JSON/CSV output.
10. **Data-cache warm-up completed** — the untimed warm-up pass (Section 6.5) has run so the working set is hot before measurement, keeping the comparison apples-to-apples.

### 6.4 Test execution plan (single-operation isolation + full workload)

The benchmark is run as **two complementary test types**, each executed under both the **Steady**
(Scenario A) and **Burst** (Scenario B) envelopes from Section 6.2. Every run is **3 iterations × 600 s
(10 min) per target** (the production envelope in `config/production/common.json`), and targets are run
**one at a time** (Section 4.1). Run the single-operation tests and the full workload independently —
they answer different questions and are not phases of one another.

**Test type 1 — Single-operation isolation tests** (`TaskSleepMs = 0`, no calc-time sleep)

Each Task opens a brand-new connection, performs exactly **one** DB op, then disconnects. Because the
connection is never reused, op1 absorbs the **entire connection-establishment cost** (TCP + TLS handshake
+ auth + server selection), so the measured latency is dominated by connection setup rather than server
execution. Two variants split the cost by read vs write:

- **find-only** (`config/production/single-find*.json`) — a single `find` on `calc_input` by `ReqId`.
  Isolates the **cold read** path.
- **insert-only** (`config/production/single-insert*.json`) — a single `insert` into `calc_output`.
  Isolates the **cold write** path.

> **Insert-only grows `calc_output`.** A single-op insert never removes, so `calc_output` accumulates
> across iterations. Run `clean-output` to reset to the seeded baseline **before/after every insert-only
> run** so the next run starts from a known state.

**Test type 2 — Full 4-op workload** (`TaskSleepMs = 10,000 ms`; `config/production/full-workload*.json`)

Each Task runs the canonical production cycle `find` (input) → `remove` (output) → `insert` (output) →
`find` (output) on one connection, with the calc-time sleep applied between the input `find` and the
output `remove` (Section 6.6). **Op1 (`find_input`) pays the cold-connection cost; ops 2–4 run on the
now-warm socket and isolate true server execution.** This is the canonical run that reflects the real
HPC workload and exposes the combined per-Task cycle under the realistic 50/50 read/write op mix.

**Purpose & what to observe (both test types).** Reproduce the worst single prod hour and surface the
connection-churn behaviors — connection rejection, RU throttling (`cosmos-ru`, when included),
server-selection/socket timeouts, and client ephemeral-port/TIME_WAIT pressure (these appear within
minutes). Compare candidates by **p99/p95/p99.9 latency** relative to one another (no hard SLA; tail
latency is the primary comparison metric). The single-op tests **separate the connection tax from server
execution** (and split it into read vs write); the full workload shows the **combined per-Task cycle** —
cold connection on op1, warm server execution on ops 2–4.

> **Why 3 × 600 s is enough for this comparison:** the prod data is bucketed hourly, so a steady run at
> peak-hour rate plus the Poisson burst reproduces peak throughput and all fast-onset behaviors
> (connection rejection, throttling, timeouts, port/TIME_WAIT pressure) exactly. Three independent
> 10-minute iterations give a mean-of-3 with visible run-to-run variance without a multi-hour soak.

### 6.5 Starting-state normalization (warm data cache, cold connections)

To keep the cross-DB comparison apples-to-apples, **every measured run starts from a steady-warm data state and a cold connection state.**

**Warm the data cache (all three targets, before the clock starts):**
- Run a separate, **untimed** pre-read pass over `calc_input` (full set or a representative sample) so the working set is hot in cache before measurement begins.
- Managed services (`cosmos-ru`, `documentdb`) are effectively always in this warm state; the pre-read simply matches `mongo-vm` to them. This is fair because it compares each backend at the **steady state that holds for the majority of the prod day**.
- The warm-up pass MUST NOT pre-open or retain reusable connections (see Section 2.2). Use the same per-request connect/disconnect discipline, or warm via a separate process that is fully torn down before the timed run.

**Keep connections cold:** do not pre-open pools or reuse clients/sessions/cursors. Cold connections are the subject of the test.

**Data lifecycle modeled = Model A (immutable input).** The test assumes `calc_input` is loaded once and is **read-only** for the duration of the run. This matches the prod pattern where input is loaded daily and stays consistent through the day.

**Explicitly NOT modeled (report as caveats, see Sections 8.1 and 9):**
- **Post-load cold start:** in prod, the first wave of Tasks right after the daily load hits a partially cold `calc_input` on `mongo-vm`. The warm-up above removes this from the headline numbers, so the report must note that **real daily first-wave latency on Mongo may be higher** than measured.
- **Mid-day input updates (Model B append-only / Model C mutable-in-place):** if input is added or changed during the day, those documents become cold again and (for Model C) could trigger re-computation / `remove`+`insert` contention on the same `ReqId`. These are **possible in prod but not exercised here**, and may affect `mongo-vm` negatively relative to the steady-warm result.

### 6.6 Calculation time substitute & concurrency control

The real HPC calculation step is replaced in this test by a **fixed `sleep`** so runs are deterministic and identical across all three targets.

- **`taskSleepMs` (config-driven):** the per-Task sleep that stands in for HPC calculation time. It is applied at Task step 3 (between the input `find` and the output `remove`). Default **10,000 ms (10 s)**; override in `config.json` to match the prod calc-time profile being modeled.
- **Why it matters \u2014 it sets steady-state concurrency.** By Little's Law, average concurrent connections \u2248 **arrival rate \u00d7 per-Task hold time**, where hold time \u2248 connect + 4 ops + `taskSleepMs` + disconnect (sleep dominates). At 135 conn/sec with `taskSleepMs = 10 s`, expected steady concurrency \u2248 **1,350**. Raising `taskSleepMs` raises concurrency proportionally; set it to reproduce the concurrency you want to exercise.
- **Steady vs. Burst concurrency differ.** The Steady row's concurrency is governed by this formula (not a fixed 11K). The **~11K figure is the measured *burst* peak** (many Tasks injected at once), reproduced by Scenario B, not by steady arrival. Do not conflate the two.
- Keep `taskSleepMs` **identical across all targets** in a given comparison so the cross-DB result stays apples-to-apples.

---

## 7. Metrics to Collect

> **Scope:** all metrics below cover the **per-Task 4-op cycle** (connect → find → remove → insert → find → disconnect). The Job-stage fetch of Request IDs (Section 2.1) is orchestration and is **not** part of the measured per-Task metrics. The `taskSleepMs` interval (Section 6.6) is excluded from DB-operation latencies but is included in the full per-Task cycle latency.

### 7.1 Performance metrics
- Total requests; successful requests; failed requests.
- Connections created per second; connections closed per second.
- **Ops QPS per second, broken down per operation type** (`find` input, `remove`, `insert`, `find` output) and combined.
- Client object creation time.
- TCP/TLS establishment time (or driver-provided connection-open time).
- Authentication / server-selection time.
- **Per-operation execution time** for each of the 4 ops (`find` input, `remove`, `insert`, `find` output).
- Cursor first-result time (for both `find` ops).
- **Full per-Task cycle latency: connect → find → remove → insert → find → disconnect.**
- Connection close / resource-release time.
- Average latency; p50; p90; p95; p99; p99.9.
- Timeout count; connection-error count; authentication-error count; server-selection-timeout count; socket-timeout count.
- Cosmos DB RU throttling / rate-limit error count.
- Azure DocumentDB/Mongo compatibility error count.
- Test-process CPU usage; test-process memory usage.

### 7.2 Connection metrics
- Connections created; connections **ready** (post-handshake/auth); connections closed.
- Connection-created-to-request ratio; connection-closed-to-request ratio.
- Pool checkout event count (if any occurred).
- Suspected-reuse event count.
- Result of client/session/cursor reuse verification between requests.

### 7.3 Client-side resource metrics (host running the test)

These are the likely **first failure point** in a no-reuse churn test (every Task opens a new outbound socket), and they back the fast-onset failure checks in Section 6.4 (connection rejection, port/TIME_WAIT pressure).
- Ephemeral (outbound) ports in use; configured ephemeral port range and % utilization.
- Sockets in **TIME_WAIT** (count over time); configured `TcpTimedWaitDelay`.
- Open socket/file-descriptor (handle) count over time.
- Any client-side socket/port-exhaustion errors (e.g., `SocketException` from port starvation) — **report separately from server-side connection errors** so client limits are not misattributed to the DB.

### 7.4 Exception classification (DO NOT collapse into a single "failure")

Classify every exception by type:
- `Timeout`
- `ConnectionFailure`
- `ServerSelectionTimeout`
- `SocketTimeout`
- `AuthenticationFailure`
- `ThrottlingOrRateLimit` (generic 429 / rate-limit; use for non-Cosmos targets)
- `CosmosRuThrottling` (Cosmos RU 429 specifically; route Cosmos throttling here, do not double-count under the generic bucket)
- `DocumentDbCompatibility`
- `QueryFailure`
- `ClientPortExhaustion`
- `DataSetMissing`
- `IndexMissing` (required `ReqId` index absent on `calc_input` or `calc_output`; preflight failure, do not run the timed test)
- `Unknown`

---

## 8. Output Artifacts

Each `test` run (one `--target`) writes machine-readable results into `results/`:
1. Raw **JSON**.
2. **CSV**.

The `report` command consumes one or more target result sets from `results/` and produces:
3. A single **self-contained HTML** report (openable locally with no external server). To compare all three candidates, run `test` once per `--target`, then run `report --input results/` over the combined directory.

### 8.1 HTML report requirements
- Test-target summary.
- **Masked** connection string.
- Test-configuration summary.
- Dataset size and document count.
- Total success / failure counts.
- Connections-created-per-second graph.
- Connections-closed-per-second graph.
- **Ops QPS-per-second graph** (per operation type: `find` input, `remove`, `insert`, `find` output, plus combined).
- Connection-latency graph.
- **Per-operation latency graph** (`find` input, `remove`, `insert`, `find` output).
- Total-latency graph (full per-Task cycle).
- p50 / p95 / p99 / p99.9 summary.
- Error counts by type.
- Connection-reuse verification result.
- **Starting-state disclosure:** state that results reflect a **warm data cache + cold connection state (Model A, immutable input)**, and include the caveats below.
- **Mongo-VM caveat box:** note that (a) **post-load cold-start** first-wave latency and (b) **mid-day input updates (Model B/C)** are not exercised and may affect `mongo-vm` negatively relative to the reported steady-warm numbers.
- Comparison across the three candidate DBs (when multiple target result sets are present).
- Summary of which candidate is most resilient to connection churn.
- Must be a **self-contained HTML** file (no external dependencies).

---

## 9. README Interpretation Guide

The README MUST explain:
- Why **p95 / p99 / p99.9** latency should be prioritized over average latency.
- How to interpret the **connection created/closed ratio** in a churn test.
- How **Cosmos RU throttling** affects results.
- How to interpret **DocumentDB Mongo compatibility** limitations.
- Cautions when comparing **Mongo-on-VM vs. managed-service** results.
- **Index assumption:** results assume a `ReqId` index is present on **both** `calc_input` and `calc_output` on every target (created by `prepare-data`, verified by preflight). An unindexed run forces full scans and is **not** a valid comparison — the Cosmos RU figures in particular become meaningless.
- **Starting-state assumptions and their limits:** results reflect a **warm data cache + cold connection state** under **Model A (immutable input)**. Explain that (a) Mongo's **post-load cold start** (first wave after the daily load) and (b) **mid-day input updates (Model B append-only / Model C mutable-in-place)** are **not modeled**, and that both could make real `mongo-vm` behavior worse than the steady-warm benchmark suggests. Managed services are effectively always warm, so the warm-cache comparison is fair for the majority of the day but does not capture Mongo's daily cold-edge.
- That this benchmark **does NOT represent typical long-lived connection-pool application performance**.

---

## 10. Deliverables

1. Complete project structure.
2. Runnable C# code.
3. Sample `config.json`.
4. `README.md`.
5. HTML report generation code.
6. Test execution examples.
7. Results interpretation guide.
