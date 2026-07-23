# MongoDB Metrics Reference — what we log, where it comes from, where it lands

This document is the authoritative map of **every metric captured for the self-managed MongoDB
targets** (`mongo-shard`, and the dropped-but-wired `mongo-vm`) in this benchmark. Use it when pulling
results after a campaign so you know exactly which number came from where.

> DocumentDB has its own (smaller) server-side set — summarized in the last section — but the bulk of
> this doc is MongoDB, per request.

There are **two independent capture planes**. Read this first; the tables below are organized by plane.

| Plane | Runs | Vantage point | Written where |
|---|---|---|---|
| **1. Client-side (in-process)** | Continuously *during* the run, inside every `Bmt.LoadGen` process on each load-generator VM | The **load-generator** (client) — the .NET driver + the client host OS | Per-iteration `*.json` + `*-timeseries.csv` + `*-latency.csv` in each host's `results/…/` |
| **2a. Server-side, in-run sampler** | Every **5 s during the run**, from the operator box (az1-0) | The **mongos routers themselves** via `serverStatus` (read-only) | `results/_campaign-<RunTag>/server-samples/mongo-serverstats.csv` |
| **2b. Server-side, post-run pull** | **Once, after** the run over `[start..end]` | **Azure Monitor** (VM host metrics) + **mongos `serverStatus`/`connPoolStats`** + **`mongos.log`** | `results/_campaign-<RunTag>/azure-metrics.json` + `metrics-raw/` |

Key principle: **client-side vs server-side are kept separate on purpose**, so a client-side limit
(e.g. ephemeral-port exhaustion on the load generator) is never misattributed to the database.

---

## Plane 1 — Client-side metrics (measured on the load-generator VM)

Source code: `src/Bmt.LoadGen/MetricsCollector.cs`, `src/Bmt.LoadGen/ClientResourceSampler.cs`,
`src/Bmt.Core/Connections/ConnectionEventCounters.cs`. Serialized by `src/Bmt.Core/Metrics/RunResult.cs`,
CSVs by `src/Bmt.LoadGen/Output/CsvWriter.cs`.

These describe **what the client observed** — how many connections it opened, how long each open/op
took, how fast it churned, and whether the client host itself was the bottleneck. They do **not**
measure the DB server's CPU/RAM.

### 1a. Connection lifecycle counters

| Metric | Meaning | How obtained | Where logged |
|---|---|---|---|
| `Connections.Created` | Total brand-new physical connections opened | MongoDB driver **SDAM event** `OnConnectionCreated` → `ConnectionEventCounters` | `RunResult.Connections.created` (JSON) |
| `Connections.Ready` | Connections that completed handshake+auth and became usable | driver event `OnConnectionReady` | `RunResult.Connections.ready` |
| `Connections.Closed` | Connections closed | driver event `OnConnectionClosed` | `RunResult.Connections.closed` |
| `Connections.Failed` | Connection open failures | driver event `OnConnectionFailed` | `RunResult.Connections.failed` |
| `Connections.CheckedOut` | Pool check-outs (≈ 4× tasks — one per op within a task) | driver event `OnConnectionCheckedOut` | `RunResult.Connections.checkedOut` |
| `CreatedToTaskRatio` / `ClosedToTaskRatio` | created÷tasks and closed÷tasks (≈1.0 in a correct no-reuse run) | computed in `MetricsCollector.Build` | `RunResult.Connections.*` |
| **Reuse verification** | Whether any connection was reused across tasks (the §7.2 no-reuse guarantee) | derived: `created ≥ successfulTasks` AND `created ≈ closed` | `RunResult.ReuseCheck` (`noReuseConfirmed`, `suspectedReuseEvents`, `detail`) |

### 1b. Concurrency & throughput (per-second time-series)

| Metric | Meaning | How obtained | Where logged |
|---|---|---|---|
| **`InFlightTasks` (concurrent)** | **Concurrent in-flight tasks = concurrent open connections**, per second (max within the second) | in-process `Interlocked` counter `_inFlight`, peak-tracked per `SecondBucket` | `RunResult.Throughput[].inFlightTasks` (JSON) + `in_flight_tasks` column (timeseries CSV) |
| **conn/s (churn rate)** | Connections opened per second | per-second `ConnectionsCreated` bucket | `Throughput[].connectionsCreated` + `conn_created` (CSV) |
| conns closed/s | Connections closed per second | per-second `ConnectionsClosed` bucket | `Throughput[].connectionsClosed` + `conn_closed` (CSV) |
| **per-op QPS** | Ops/sec for each of the 4 ops (find_input, remove, insert, find_output) | per-second op buckets | `Throughput[].{findInputOps,removeOps,insertOps,findOutputOps}` + CSV columns |
| **combined QPS** | Sum of the 4 op rates per second | `ThroughputPoint.CombinedOps` (computed) | `combined_ops` (CSV) |
| failed ops/s | Failed ops per second | per-second `FailedOps` bucket | `Throughput[].failedOps` + `failed_ops` (CSV) |

> **This is the primary evidence for the "≥11,000 concurrent / ≥1,200 conn/s" headline**, summed
> across hosts by the `merge` step (see §3). Plane 2a independently confirms it server-side.

### 1c. Latency (percentiles: min / mean / p50 / p90 / p95 / p99 / p999 / max)

All latency is recorded into sharded `LatencyDigest`s and summarized at the end.

| Series | Meaning | How obtained | Where logged |
|---|---|---|---|
| `find_input`, `remove`, `insert`, `find_output` | Per-operation latency of the 4 ordered ops | timed in `TaskRunner`, `MetricsCollector.RecordOp` | `RunResult.OperationLatencyMs.*` (JSON) + `*-latency.csv` rows |
| `task_cycle` | Full per-task cycle: connect → 4 ops → disconnect | `MetricsCollector.OnTaskEnd(cycleMs)` | `RunResult.TaskCycleLatencyMs` + CSV row `task_cycle` |
| `connection_open` | Driver connection-open (whole handshake) duration | driver event `OnConnectionReady(openDuration)` | `RunResult.ConnectionOpenMs` + CSV row `connection_open` |
| `handshake_hello` | The `hello`/`isMaster` wire-negotiation command latency | driver `CommandStarted/Succeeded` for hello, `OnHandshakeCommand` | `RunResult.HandshakeHelloMs` + CSV row `handshake_hello` |
| `handshake_auth` | SCRAM `saslStart`/`saslContinue` auth latency | driver handshake command hook, split by `IsAuthCommand` | `RunResult.HandshakeAuthMs` + CSV row `handshake_auth` |
| `client_create` | `MongoClient` object-construction time | `MetricsCollector.RecordClientCreate` | `RunResult.ClientCreateMs` + CSV row `client_create` |

### 1d. Task/op totals & error taxonomy

| Metric | Meaning | How obtained | Where logged |
|---|---|---|---|
| `Totals.{TotalTasks,SuccessfulTasks,FailedTasks}` | Task counts | interlocked counters | `RunResult.Totals` |
| `Totals.{TotalOps,SuccessfulOps,FailedOps}` | Op counts | interlocked counters | `RunResult.Totals` |
| `ErrorsByType` | Every failure classified into exactly one §7.4 bucket | `ExceptionClassifier` → `RecordError` | `RunResult.ErrorsByType` (JSON) |

### 1e. Client-host resource pressure (the LOAD-GENERATOR VM, not the DB)

Source: `ClientResourceSampler.cs`, sampled every `ResourceSampleIntervalMs` (default **1000 ms**).

| Metric | Meaning | How obtained | Where logged |
|---|---|---|---|
| `EphemeralPortsInUse` | Active (non-closed) TCP connections — ephemeral-port pressure | `IPGlobalProperties.GetActiveTcpConnections()` | `RunResult.ResourceSamples[].ephemeralPortsInUse` + `ephemeral_ports` (CSV) |
| `TimeWaitSockets` | Sockets in TIME_WAIT | same TCP table, `TcpState.TimeWait` | `…timeWaitSockets` + `time_wait` (CSV) |
| `HandleCount` / `ThreadCount` | Process handles / threads | `Process.HandleCount` / `.Threads.Count` | `…handleCount/threadCount` + `handles`/`threads` (CSV) |
| **`CpuPercent`** | **Load-generator process CPU %** | `Process.TotalProcessorTime` delta ÷ wall×cores | `…cpuPercent` + `cpu_pct` (CSV) |
| **`WorkingSetBytes`** | **Load-generator process memory (working set)** | `Process.WorkingSet64` | `…workingSetBytes` + `working_set_bytes` (CSV) |
| `Process.Peak*` / `MaxCpuPercent` | End-of-run peaks of all of the above | `ClientResourceSampler.Peaks()` | `RunResult.Process` |

> ⚠️ **This CPU/memory is the client (load generator), NOT the MongoDB server.** For the **MongoDB VM's**
> CPU/memory, see Plane 2b (Azure Monitor).

---

## Plane 2a — Server-side in-run sampler (the true server-side concurrency/QPS)

Source: `scripts/run/Sample-MongoServerStats.ps1`, launched automatically by
`scripts/run/Invoke-Campaign.ps1` for `mongo-shard`/`mongo-vm` targets.

- **How:** opens ONE direct-connection MongoDB client **per mongos router** using the `bmt_monitor`
  (clusterMonitor) credential (`BMT_CONN_MONGO_MONITOR`), and runs read-only `serverStatus` every **5 s**
  **during the run**. Runs from the operator box (az1-0) — **zero load-gen impact**, and the secret never
  crosses `az vm run-command`.
- **Why it exists:** `serverStatus.connections.current` is a **live gauge**. The post-run pull (2b) reads
  it after load has drained, so it shows ~idle. This sampler captures the **peak while load is live** —
  the independent server-side confirmation of the client-side `InFlightTasks` concurrency claim.
- **Where:** appends one CSV row **per router per tick** to
  `results/_campaign-<RunTag>/server-samples/mongo-serverstats.csv`.

| CSV column | Meaning | Source field |
|---|---|---|
| `timestampUtc` | Sample wall-clock time (UTC, ISO-8601) | sampler clock |
| `host` | Which mongos router (`10.3.0.4:27016` or `10.3.0.6:27017`) | per-client seed |
| **`connCurrent`** | **Concurrent connections currently held by this router** (the live gauge) | `serverStatus.connections.current` |
| `connAvailable` | Remaining connection headroom | `serverStatus.connections.available` |
| `connActive` | Connections currently executing an operation | `serverStatus.connections.active` |
| `connTotalCreated` | Cumulative connections created since mongos start | `serverStatus.connections.totalCreated` |
| **`opInsert/opQuery/opUpdate/opDelete/opGetmore/opCommand`** | **Cumulative op counters** (server-side QPS = difference between adjacent rows) | `serverStatus.opcounters.*` |

**Deriving cluster-wide numbers when pulling results:**
- **Peak concurrent (server-side):** for each `timestampUtc`, sum `connCurrent` across the two routers,
  then take the max over time. (`Invoke-Campaign.ps1` also prints a per-router peak at the end.)
- **Server-side conn/s:** difference `connTotalCreated` between adjacent ticks (per router), ÷ interval,
  then sum routers.
- **Server-side QPS:** difference `opQuery`/`opInsert`/… between adjacent ticks ÷ interval, summed.

---

## Plane 2b — Server-side post-run pull (Azure Monitor + serverStatus + logs)

Source: `scripts/run/Get-AzureMetrics.ps1`, auto-invoked by `Invoke-Campaign.ps1` at load end over
`[startAt .. loadEnd]`. Config identifiers: `config/azure-resources.json`.

Output: consolidated `results/_campaign-<RunTag>/azure-metrics.json`; raw payloads under
`results/_campaign-<RunTag>/metrics-raw/`.

### 2b-i. MongoDB VM host metrics — **this is the MongoDB Azure VM's CPU & memory**

Pulled from **Azure Monitor** for **each** mongos router VM (`vm-dbtest-hpc-1-mongo`,
`vm-dbtest-hpc-1-mongo-shard`) via `az monitor metrics list`, rolled up avg/max over the window.

| Metric | Meaning | Azure Monitor metric name | Where logged |
|---|---|---|---|
| **VM CPU %** | **MongoDB VM host CPU utilization** | `Percentage CPU` | `azure-metrics.json` → `perTarget.<t>.vms.<vm>.host['Percentage CPU']` |
| **VM available memory** | **MongoDB VM free memory (bytes)** | `Available Memory Bytes` | `…host['Available Memory Bytes']` |
| VM network in | Host NIC bytes received | `Network In` (legacy; the `Network In Total` variant is NULL on these D8ds_v5 VMs) | `…host['Network In']` |
| VM network out | Host NIC bytes sent | `Network Out` (legacy, see above) | `…host['Network Out']` |

> Raw `az` JSON is also saved to `metrics-raw/<target>-<vm>-host-metrics.json`.

### 2b-ii. MongoDB server evidence — `serverStatus` + `connPoolStats` (point-in-time, post-run)

Via the `bmt_monitor` connection through the driver (same mechanism as 2a, but a single snapshot).

| Metric | Meaning | Source | Where logged |
|---|---|---|---|
| `serverStatus.process` / `version` | mongos process + version | `serverStatus` | `azure-metrics.json` → `perTarget.<t>.serverStatus.*` + raw `metrics-raw/<t>-serverStatus.txt` |
| `connectionsCurrent` | Concurrent conns **at pull time** (≈ idle — use 2a for the peak) | `serverStatus.connections.current` | `…serverStatus.connectionsCurrent` |
| `connectionsAvail` | Connection headroom | `serverStatus.connections.available` | `…connectionsAvail` |
| **`connectionsCreated`** | **Cumulative connections created** (meaningful post-run: total churn since start) | `serverStatus.connections.totalCreated` | `…connectionsCreated` |
| `connPoolStats.{totalInUse,totalAvailable,totalCreated}` | mongos→shard pool stats | `connPoolStats` | `…connPoolStats.*` + raw `metrics-raw/<t>-connPoolStats.txt` |

### 2b-iii. MongoDB log slice — connection churn from `mongos.log`

The client connection churn lands on the **mongos routers**, so `mongos.log`
(`E:\mongo\log\mongos.log`) on **both** router VMs is summarized **on the VM** (logs are 5–31 GB, so
only counts + a tiny sample cross `az vm run-command`).

| Metric | Meaning | Source | Where logged |
|---|---|---|---|
| `linesInWindow` | Log lines within `[start..end]` | on-VM scan of last 500k lines, filtered by `"t":{"$date"}` | `azure-metrics.json` → `perTarget.<t>.vms.<vm>.logSlice.*` + raw `metrics-raw/<t>-<vm>-log-window.json` |
| **`connectionAccepted`** | **"Connection accepted" events in window** (server-side conns opened) | grep `NETWORK` + `Connection accepted` | `…logSlice.connectionAccepted` |
| **`connectionEnded`** | **"Connection ended" events in window** (server-side conns closed) | grep `NETWORK` + `Connection ended` | `…logSlice.connectionEnded` |
| `windowCoveredFrom/To` | Actual timestamp span the tail covered | first/last in-window line | `…logSlice.windowCovered*` |
| `sample` | Up to 12 sample NETWORK log lines (≤300 chars) | first matches | `…logSlice.sample` |

---

## 3. Cross-host merge — combined concurrency & conn/s (the pass/fail gate)

A single generator can't reach the envelope, so per-host client-side series (Plane 1b) are summed on
the **absolute wall-clock second** (`StartedUnixSeconds + Second`).

- **Command:** `scripts/run/Merge-Campaign.ps1 -RunTag <tag> -InputDir <dir-with-all-hosts-results>`
  (wraps `Bmt.Report merge`). Source: `src/Bmt.Report/Merger.cs`.
- **Output:** `results/merge-<RunTag>.json` + a combined per-second CSV (`…-combined.csv`).

| Combined metric | Meaning | Where |
|---|---|---|
| **`PeakCombinedInFlight`** | Peak of per-second SUM of each host's `InFlightTasks` — **combined concurrent** | `merge-<tag>.json` → per group; `ReachedConcurrentTarget` = `≥ 11000` |
| **`PeakCombinedConnPerSec`** | Peak of per-second SUM of each host's conn/s — **combined churn** | same; `ReachedChurnTarget` = `≥ 1200` |
| `combined_in_flight`, `combined_conn_per_sec`, `combined_ops`, `combined_failed_ops` | Combined per-second series | `…-combined.csv` (columns) |

---

## 4. DocumentDB (for contrast) — server-side set

DocumentDB (Cosmos DB for MongoDB **vCore**, cluster `docdb-dbtest-hpc-0`) via **Azure Monitor**
(`Get-AzureMetrics.ps1`, `perTarget.documentdb.metrics.*`):

| Metric | Azure Monitor name |
|---|---|
| **Cluster CPU %** | `CpuPercent` |
| **Cluster memory %** | `MemoryPercent` |
| IOPS (throughput proxy) | `IOPS` |
| Network ingress/egress | `NetworkBytesIngress` / `NetworkBytesEgress` |
| Request duration | `MongoRequestDurationMs` (null when idle) |

> ⚠️ **vCore publishes NO active-connection or 429/throttle metric.** DocumentDB **concurrent** and
> **created** connection counts therefore come **only from the client side** (Plane 1b `InFlightTasks`
> and `Connections.Created`). The in-run sampler (Plane 2a) does **not** run for DocumentDB.

---

## 5. Artifact map — where to look when pulling results

Per campaign (RunTag), after `Invoke-Campaign.ps1`:

```
# On the operator box (az1-0), server-side artifacts:
results/_campaign-<RunTag>/
├── server-samples/mongo-serverstats.csv     # Plane 2a: in-run server concurrency + QPS timeseries
├── azure-metrics.json                        # Plane 2b: consolidated (VM CPU/mem/net, serverStatus, log)
└── metrics-raw/                              # Plane 2b: raw az JSON + serverStatus/connPoolStats/log slices
    ├── documentdb-cluster-metrics.json
    ├── <target>-serverStatus.txt
    ├── <target>-connPoolStats.txt
    ├── <target>-<vm>-host-metrics.json
    └── <target>-<vm>-log-window.json

# On EACH load-generator host (client-side, Plane 1), under results/<campaignId>/:
results/<RunTag>-test-burst-<workload>-hNNofMM-<stamp>/
├── aggregate.json                            # cross-iteration aggregate for that host
└── iter-NN/
    ├── <runId>.json                          # full RunResult (all Plane 1 metrics)
    ├── <runId>-timeseries.csv                # per-second concurrency/conn-s/QPS + client CPU/mem/ports
    └── <runId>-latency.csv                   # per-op + lifecycle latency percentiles

# After merging all hosts:
results/merge-<RunTag>.json                   # combined concurrency + conn/s vs targets (pass/fail)
results/merge-<RunTag>-…-combined.csv         # combined per-second series
```

### Quick "which number came from where" cheat-sheet

| Question | Metric | Plane / file |
|---|---|---|
| Did we hit ≥11,000 concurrent? | `PeakCombinedInFlight` | 3 · `merge-<tag>.json` (client) — confirm with 2a `connCurrent` sum |
| Did we hit ≥1,200 conn/s? | `PeakCombinedConnPerSec` | 3 · `merge-<tag>.json` (client) — confirm with 2a `connTotalCreated` deltas |
| **MongoDB VM CPU / memory** | `Percentage CPU` / `Available Memory Bytes` | **2b · `azure-metrics.json` (Azure Monitor)** |
| MongoDB server concurrent conns (true peak) | `connCurrent` summed across routers | 2a · `mongo-serverstats.csv` |
| MongoDB server QPS | `opQuery/opCommand/…` deltas | 2a · `mongo-serverstats.csv` |
| MongoDB conns created (total churn) | `connectionsCreated` / log `connectionAccepted` | 2b · `azure-metrics.json` |
| Latency (op / handshake / cycle) | `OperationLatencyMs`, `ConnectionOpenMs`, … | 1c · `<runId>.json` + `-latency.csv` (client) |
| Load-generator CPU/mem/ports | `cpuPercent`, `workingSetBytes`, `ephemeralPortsInUse` | 1e · `-timeseries.csv` (client) |
| DocumentDB CPU / memory | `CpuPercent` / `MemoryPercent` | 4 · `azure-metrics.json` (Azure Monitor) |

---

## 6. Source-file index

| Concern | File |
|---|---|
| Client metric collection (counters, latency, throughput) | `src/Bmt.LoadGen/MetricsCollector.cs` |
| Client-host resource sampler (CPU/mem/ports) | `src/Bmt.LoadGen/ClientResourceSampler.cs` |
| Driver connection-event counters | `src/Bmt.Core/Connections/ConnectionEventCounters.cs` |
| Result model (JSON schema) | `src/Bmt.Core/Metrics/RunResult.cs` |
| CSV writers (timeseries + latency) | `src/Bmt.LoadGen/Output/CsvWriter.cs` |
| Orchestration + artifact paths | `src/Bmt.LoadGen/RunOrchestrator.cs` |
| **In-run server-side sampler** | `scripts/run/Sample-MongoServerStats.ps1` |
| **Post-run Azure Monitor + serverStatus + log pull** | `scripts/run/Get-AzureMetrics.ps1` |
| Multi-host campaign driver (starts sampler + auto-pull) | `scripts/run/Invoke-Campaign.ps1` |
| Cross-host merge (combined concurrency/conn-s) | `src/Bmt.Report/Merger.cs` / `scripts/run/Merge-Campaign.ps1` |
| Azure resource identifiers (no secrets) | `config/azure-resources.json` |
