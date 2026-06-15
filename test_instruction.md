# MongoDB Connection-Churn Benchmark — Build & Execution Instructions

## 1. Purpose

Build a .NET (C#) benchmark tool that measures **how well three candidate databases tolerate extreme connection churn** under an HPC workload where **every Task opens a brand-new connection, performs a strict 4-operation sequence (`find` input → `remove` output → `insert` output → `find` output), then fully disconnects**. Connections are **never reused** across Tasks.

This benchmark intentionally does **not** model a long-lived connection-pool application. It models a worst-case "1 Task = 1 process = 1 connection lifecycle" pattern.

---

## 2. Workload Model

### 2.1 Pipeline

**Pre-stage (one-time setup)**
1. Load 100,000 documents into the **Input Collection** (`calc_input`).

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
- Individual document sizes ≈ **6 KB, 16 KB, 50 KB, 58 KB** (including response metadata).
- Estimated `calc_input` total ≈ **4 GB** (derived from logs: observed average response size ≈ 44 KB/doc × 100,000).

> **Identifier semantics:** the document `_id` is just a **sequential** value (row counter) and is **not** used to drive operations. All Task operations (`find` / `remove` / `insert` / `find`) are keyed by **`ReqId`**, which is the logical Request ID passed from the Job. Use `ReqId` — not `_id` — for all reads and writes.

### 3.1 `calc_input` document shape

```jsonc
{
  "results": [
    { "_id": "ReqId",             "value": "string" },
    { "_id": "_id",               "value": "string" },
    { "_id": "CalculatorFileNm",  "value": "string" },
    { "_id": "CalculatorVersion", "value": "string" },
    { "_id": "SkipCalculation",   "value": "boolean" },
    { "_id": "Input",             "value": "string" },
    { "_id": "SuccessExitCodeList","value": "object" },
    { "_id": "ReqClass",          "value": "string" }
  ],
  "ok": 1
}
```

### 3.2 `calc_output` document shape

```jsonc
{
  "results": [
    { "_id": "ReqId",         "value": "string" },
    { "_id": "StartTime",     "value": "string" },
    { "_id": "EndTime",       "value": "string" },
    { "_id": "Output",        "value": "string" },
    { "_id": "_id",           "value": "string" },
    { "_id": "OutputFormatCd","value": "object" }
  ],
  "ok": 1
}
```

---

## 4. Target Resources

| Target key      | Resource |
|-----------------|----------|
| `documentdb`    | Azure DocumentDB (M80, 32 vCore, 128 GB RAM, 512 GB SSD) |
| `cosmos-ru`     | Azure Cosmos DB for MongoDB — **fixed 40,000 RU/s. DO NOT change RU/s.** |
| `mongo-vm`      | MongoDB on Azure VM (Windows Server Datacenter 2025, 32 vCore, 256 GB RAM, 512 GB data disk SSD) |

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
2. **Network path is private** — connectivity to the DB/VM is over a **private or peered** path (Private Endpoint / VNet peering / private DNS), **not** the public internet. Verify the resolved endpoint is a private IP (RFC1918) or the expected private-endpoint FQDN, and fail if it resolves to a public address. This keeps latency results free of internet-path noise and matches the prod topology.
3. **Connectivity & auth smoke test** — open one connection, run a single `find` by `ReqId`, and close it cleanly. Confirms credentials, TLS, server selection, and the `ReqId` query path all work before load begins.
4. **`calc_output` writable** — the output collection exists (or auto-creates) and a `remove`+`insert`+`find` round-trip on a throwaway `ReqId` succeeds; clean up the probe document afterward.
5. **Server/throughput config matches the target spec** — e.g., Cosmos is at the **fixed 40,000 RU/s** (and must not be changed); DocumentDB is the M80 tier; `mongo-vm` mongod is up with the intended `maxIncomingConnections`. Record these in the report's config summary.
6. **Client host headroom** — the test host's **ephemeral port range and `TcpTimedWaitDelay`** are tuned for the expected churn (see Section 7.3), and the process **file-descriptor / handle limit** is high enough for the target concurrent-connection level. Warn (or fail) if the configured headroom is below the scenario's concurrent-connection target.
7. **Clock & time sync** — client clock is NTP-synced (latency and per-second-rate metrics depend on accurate timestamps).
8. **Clean starting state** — no leftover connections from a prior aborted run; results directory writable; sufficient local disk for raw JSON/CSV output.
9. **Data-cache warm-up completed** — the untimed warm-up pass (Section 6.5) has run so the working set is hot before measurement, keeping the comparison apples-to-apples.

### 6.4 Test execution plan (phased)

Run the benchmark in two phases. **Run Phase 1 first for every target; use its results to decide whether a Phase 2 soak is worthwhile for that target** (a candidate that already shows severe instability in Phase 1 can be deprioritized for the longer soak).

**Phase 1 — 1-hour peak behavior**
- Duration: **1 hour per target.**
- Load: hold the **peak-hour profile** uniformly (Scenario A: 135 conn/sec, 540 ops/sec sustained), then apply the **Poisson burst** (Scenario B) within the same hour to exercise the spike envelope.
- Purpose: reproduce the worst single prod hour 1:1 and surface the **fast-onset** behaviors — connection rejection, RU throttling, server-selection/socket timeouts, and client ephemeral-port/TIME_WAIT pressure (these appear within minutes).
- What to observe and compare: connection rejections, whether ops/sec holds during the burst, and **p99/p95/p99.9 latency** relative to the other candidates (no hard SLA; tail latency is the primary comparison metric).

**Phase 2 — N-hour workload recreation (prod-quality soak)**
- Duration: **multi-hour**, two options:
  - **Full-shape replay (preferred):** replay the contiguous **11-hour** prod window (14:00→00:00) following the real hour-by-hour Task curve, including the back-to-back heavy hours near the daily peak. This validates the actual daily peak *shape*, not a synthetic flat hour.
  - **Soak (minimum):** hold the Phase-1 peak load for **≥ 4 hours** continuously.
- Purpose: catch **cumulative / slow-onset** effects that a 1-hour test cannot — file-descriptor/handle leaks, slow client memory growth, sustained TIME_WAIT accumulation, and managed-service background maintenance / RU throttle-smoothing that operate on longer cycles than 1 hour.
- What to observe and compare: whether each target holds the Phase-1 behavior for the full window, and whether there is **upward drift** in latency, error rate, or resource (FD/memory/port) usage over time — stability over the soak is itself a key differentiator between the options.

> **Why 1 hour is sufficient for Phase 1 but not for a final recommendation:** the prod data is bucketed hourly, so the peak hour *is* a complete 1-hour window — a 1-hour test reproduces peak throughput and all fast-onset behaviors exactly. Cumulative leaks and managed-service accounting, however, only become visible over a longer soak, so a **prod-quality comparison needs Phase 2** for the candidates that look viable after Phase 1.

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

These are the likely **first failure point** in a no-reuse churn test (every Task opens a new outbound socket), and they back the Phase-1 fast-onset checks and the Phase-2 no-drift criteria in Section 6.4.
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
