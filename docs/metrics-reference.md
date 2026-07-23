# Metrics Reference — MongoDB & DocumentDB

Authoritative catalog of **every metric captured** for both database targets in this benchmark, with
its source, where it's saved, what it means, and whether it is available for **MongoDB**
(`mongo-shard`, self-managed on Azure VMs) and/or **DocumentDB** (Cosmos DB for MongoDB **vCore**,
cluster `docdb-dbtest-hpc-0`). Use it when pulling results after a campaign.

## How to read the availability columns

- **✅** = captured for that database. Where the metric name/resource differs between the two DBs, the
  difference is noted inline.
- **❌** = not captured for that database; the **reason** is given in the cell.

Two facts drive most of the ❌s:
1. **MongoDB runs on Azure VMs we own** → we can read the OS host metrics (Azure Monitor VM), talk to
   `serverStatus`/`connPoolStats` with a monitor credential, and read `mongos.log` on the box.
2. **DocumentDB is a managed PaaS (vCore)** → no OS/VM access, no `serverStatus`/`connPoolStats`, no
   server log file. It exposes a fixed set of **Azure Monitor cluster metrics**, with **no dedicated
   active-connection counter**, so DocumentDB connection **concurrency & churn** come **only from the
   client side**. It *does*, however, expose server-side **throughput** and **error/throttle** signals
   via the `MongoRequestDurationMs` metric's `Count` aggregation and its `Operation` / `StatusCodeClass`
   dimensions (see §3).

## Capture sources (the "Source" column values)

| Source label | What it is | Vantage point |
|---|---|---|
| **Client VM (in-proc)** | `Bmt.LoadGen` .NET process on each load-generator VM (driver SDAM events + in-process counters) | The load generator (client) |
| **Client VM (OS sampler)** | `ClientResourceSampler` reading the client host OS every 1 s | The load generator host OS |
| **Server sampler (serverStatus)** | `Sample-MongoServerStats.ps1`, 5 s polling of each mongos router during the run | The MongoDB mongos routers |
| **Azure Monitor (VM)** | `az monitor metrics list` on the MongoDB VM host | Azure platform (host of the Mongo VM) |
| **Azure Monitor (cluster)** | `az monitor metrics list` on the DocumentDB cluster resource | Azure platform (managed vCore) |
| **mongos serverStatus (post-run)** | Single `serverStatus`/`connPoolStats` snapshot after the run | The MongoDB mongos routers |
| **mongos.log** | On-VM scan of `E:\mongo\log\mongos.log` over the window | The MongoDB mongos routers |

---

## 1. Client-side metrics (measured on the load-generator VM)

These measure **what the client observed** — connections opened, latency, churn, and whether the client
host itself was the bottleneck. Because the same load generator runs against both targets, **all
client-side metrics are available identically for MongoDB and DocumentDB.** They do **not** measure the
DB server's CPU/RAM.

Source code: `src/Bmt.LoadGen/MetricsCollector.cs`, `ClientResourceSampler.cs`,
`src/Bmt.Core/Connections/ConnectionEventCounters.cs`; serialized by `src/Bmt.Core/Metrics/RunResult.cs`;
CSVs by `src/Bmt.LoadGen/Output/CsvWriter.cs`.

### 1a. Connection lifecycle counters

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| `Connections.Created` | Client VM (in-proc) — driver event `OnConnectionCreated` | `RunResult.Connections.created` (JSON) | Total brand-new physical connections opened | ✅ | ✅ |
| `Connections.Ready` | Client VM (in-proc) — `OnConnectionReady` | `RunResult.Connections.ready` | Connections that finished handshake+auth and became usable | ✅ | ✅ |
| `Connections.Closed` | Client VM (in-proc) — `OnConnectionClosed` | `RunResult.Connections.closed` | Connections closed | ✅ | ✅ |
| `Connections.Failed` | Client VM (in-proc) — `OnConnectionFailed` | `RunResult.Connections.failed` | Connection-open failures | ✅ | ✅ |
| `Connections.CheckedOut` | Client VM (in-proc) — `OnConnectionCheckedOut` | `RunResult.Connections.checkedOut` | Pool check-outs (≈ one per op within a task) | ✅ | ✅ |
| `CreatedToTaskRatio` / `ClosedToTaskRatio` | Client VM (in-proc) — computed in `MetricsCollector.Build` | `RunResult.Connections.*` | created÷tasks and closed÷tasks (≈1.0 in a correct no-reuse run) | ✅ | ✅ |
| **Reuse verification** | Client VM (in-proc) — derived (`created ≥ successfulTasks` AND `created ≈ closed`) | `RunResult.ReuseCheck` (`noReuseConfirmed`, `suspectedReuseEvents`) | Whether any connection was reused across tasks (no-reuse guarantee) | ✅ | ✅ |

### 1b. Concurrency & throughput (per-second time-series)

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| **`InFlightTasks` (concurrent)** | Client VM (in-proc) — `Interlocked` `_inFlight`, peak per second | `Throughput[].inFlightTasks` (JSON) + `in_flight_tasks` (timeseries CSV) | **Concurrent in-flight tasks = concurrent open connections**, per second (max within the second). **Primary source of DocumentDB concurrency** (no server metric exists). | ✅ | ✅ |
| **conn/s (churn)** | Client VM (in-proc) — per-second `ConnectionsCreated` bucket | `Throughput[].connectionsCreated` + `conn_created` (CSV) | Connections opened per second. **Primary source of DocumentDB churn.** | ✅ | ✅ |
| conns closed/s | Client VM (in-proc) — per-second `ConnectionsClosed` bucket | `Throughput[].connectionsClosed` + `conn_closed` (CSV) | Connections closed per second | ✅ | ✅ |
| **per-op QPS** | Client VM (in-proc) — per-second op buckets | `Throughput[].{findInputOps,removeOps,insertOps,findOutputOps}` + CSV cols | Ops/sec for each of the 4 ops (find_input, remove, insert, find_output) | ✅ | ✅ |
| **combined QPS** | Client VM (in-proc) — `ThroughputPoint.CombinedOps` | `combined_ops` (CSV) | Sum of the 4 op rates per second | ✅ | ✅ |
| failed ops/s | Client VM (in-proc) — per-second `FailedOps` bucket | `Throughput[].failedOps` + `failed_ops` (CSV) | Failed ops per second | ✅ | ✅ |

### 1c. Latency (min / mean / p50 / p90 / p95 / p99 / p999 / max)

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| `find_input`, `remove`, `insert`, `find_output` | Client VM (in-proc) — timed in `TaskRunner`, `MetricsCollector.RecordOp` | `RunResult.OperationLatencyMs.*` (JSON) + `*-latency.csv` | Per-operation latency of the 4 ordered ops | ✅ | ✅ |
| `task_cycle` | Client VM (in-proc) — `OnTaskEnd(cycleMs)` | `RunResult.TaskCycleLatencyMs` + `task_cycle` (CSV) | Full per-task cycle: connect → 4 ops → disconnect | ✅ | ✅ |
| `connection_open` | Client VM (in-proc) — `OnConnectionReady(openDuration)` | `RunResult.ConnectionOpenMs` + `connection_open` (CSV) | Driver connection-open (whole handshake) duration | ✅ | ✅ |
| `handshake_hello` | Client VM (in-proc) — `hello`/`isMaster` command hook | `RunResult.HandshakeHelloMs` + `handshake_hello` (CSV) | The `hello`/`isMaster` wire-negotiation command latency | ✅ | ✅ |
| `handshake_auth` | Client VM (in-proc) — SCRAM command hook, `IsAuthCommand` | `RunResult.HandshakeAuthMs` + `handshake_auth` (CSV) | SCRAM `saslStart`/`saslContinue` auth latency | ✅ | ✅ |
| `client_create` | Client VM (in-proc) — `MetricsCollector.RecordClientCreate` | `RunResult.ClientCreateMs` + `client_create` (CSV) | `MongoClient` object-construction time | ✅ | ✅ |

### 1d. Task/op totals & error taxonomy

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| `Totals.{TotalTasks,SuccessfulTasks,FailedTasks}` | Client VM (in-proc) — interlocked counters | `RunResult.Totals` | Task counts | ✅ | ✅ |
| `Totals.{TotalOps,SuccessfulOps,FailedOps}` | Client VM (in-proc) — interlocked counters | `RunResult.Totals` | Op counts | ✅ | ✅ |
| `ErrorsByType` | Client VM (in-proc) — `ExceptionClassifier` → `RecordError` | `RunResult.ErrorsByType` (JSON) | Every failure classified into one error bucket | ✅ | ✅ |

### 1e. Client-host resource pressure (the LOAD-GENERATOR VM — not the DB)

Source: `ClientResourceSampler.cs`, sampled every 1000 ms. ⚠️ This CPU/memory is the **client**, not the
DB server. For MongoDB **server** CPU/mem see §2; for DocumentDB **cluster** CPU/mem see §3.

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| `EphemeralPortsInUse` | Client VM (OS sampler) — `GetActiveTcpConnections()` | `ResourceSamples[].ephemeralPortsInUse` + `ephemeral_ports` (CSV) | Active (non-closed) TCP connections — ephemeral-port pressure | ✅ | ✅ |
| `TimeWaitSockets` | Client VM (OS sampler) — TCP table `TimeWait` | `…timeWaitSockets` + `time_wait` (CSV) | Sockets in TIME_WAIT | ✅ | ✅ |
| `HandleCount` / `ThreadCount` | Client VM (OS sampler) — `Process.HandleCount` / `.Threads.Count` | `…handleCount/threadCount` + `handles`/`threads` (CSV) | Process handles / threads | ✅ | ✅ |
| **`CpuPercent`** | Client VM (OS sampler) — `Process.TotalProcessorTime` delta ÷ wall×cores | `…cpuPercent` + `cpu_pct` (CSV) | **Load-generator process CPU %** | ✅ | ✅ |
| **`WorkingSetBytes`** | Client VM (OS sampler) — `Process.WorkingSet64` | `…workingSetBytes` + `working_set_bytes` (CSV) | **Load-generator process memory (working set)** | ✅ | ✅ |
| `Process.Peak*` / `MaxCpuPercent` | Client VM (OS sampler) — `ClientResourceSampler.Peaks()` | `RunResult.Process` | End-of-run peaks of all of the above | ✅ | ✅ |

---

## 2. MongoDB server-side metrics

All rows in this section are **MongoDB-only**. The equivalent DocumentDB column explains why the managed
service can't provide it (and points to the client-side or cluster-metric substitute).

### 2a. In-run server sampler — true server-side concurrency & QPS

Source: `scripts/run/Sample-MongoServerStats.ps1` (auto-started by `Invoke-Campaign.ps1`). One
direct-connection client **per mongos router** using the `bmt_monitor` (clusterMonitor) credential,
polling `serverStatus` every **5 s during the run**. Exists because `connections.current` is a **live
gauge** — the post-run pull reads it after load drains, so this captures the **peak while load is live**.
Destination: `results/_campaign-<RunTag>/server-samples/mongo-serverstats.csv` (one row per router per tick).

| Metric (CSV column) | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| `timestampUtc` | Server sampler | `mongo-serverstats.csv` | Sample wall-clock time (UTC ISO-8601) | ✅ | ❌ managed vCore exposes no `serverStatus`; sampler is not run for DocumentDB |
| `host` | Server sampler | `mongo-serverstats.csv` | Which mongos router (`10.3.0.4:27016` / `10.3.0.6:27017`) | ✅ | ❌ no per-node access on managed vCore |
| **`connCurrent`** | Server sampler (serverStatus) — `connections.current` | `mongo-serverstats.csv` | **Concurrent connections currently held by this router** (the live peak). *DocumentDB substitute: client-side `InFlightTasks`, §1b.* | ✅ | ❌ vCore publishes no active-connection metric |
| `connAvailable` | Server sampler (serverStatus) — `connections.available` | `mongo-serverstats.csv` | Remaining connection headroom | ✅ | ❌ no server connection metric on vCore |
| `connActive` | Server sampler (serverStatus) — `connections.active` | `mongo-serverstats.csv` | Connections currently executing an operation | ✅ | ❌ no server connection metric on vCore |
| `connTotalCreated` | Server sampler (serverStatus) — `connections.totalCreated` | `mongo-serverstats.csv` | Cumulative connections created since mongos start (server-side conn/s = deltas). *DocumentDB substitute: client-side `Connections.Created`, §1a.* | ✅ | ❌ no server connection metric on vCore |
| **`opInsert/opQuery/opUpdate/opDelete/opGetmore/opCommand`** | Server sampler (serverStatus) — `opcounters.*` | `mongo-serverstats.csv` | **Cumulative op counters** (server-side QPS = deltas between rows). *DocumentDB substitute: server-side `MongoRequestDurationMs` Count split by Operation, §3.* | ✅ | ⚠️ no `opcounters`, but server-side RPS + per-op latency come from `MongoRequestDurationMs` (Count / Operation dimension), §3 |

### 2b. MongoDB VM host metrics — Azure Monitor (the Mongo VM's CPU & memory)

Source: **Azure Monitor (VM)** — `az monitor metrics list` per mongos router VM (`vm-dbtest-hpc-1-mongo`,
`vm-dbtest-hpc-1-mongo-shard`), rolled up avg/max over `[start..end]`.
Destination: `azure-metrics.json` → `perTarget.<t>.vms.<vm>.host[...]` (+ raw under `metrics-raw/`).

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| **VM CPU %** | Azure Monitor (VM) — `Percentage CPU` | `azure-metrics.json → …host['Percentage CPU']` | **MongoDB VM host CPU utilization.** *DocumentDB equivalent: cluster `CpuPercent`, §3.* | ✅ | ❌ no VM (PaaS) — see cluster `CpuPercent` instead |
| **VM available memory** | Azure Monitor (VM) — `Available Memory Bytes` | `…host['Available Memory Bytes']` | **MongoDB VM free memory (bytes).** *DocumentDB equivalent: cluster `MemoryPercent`, §3.* | ✅ | ❌ no VM (PaaS) — see cluster `MemoryPercent` instead |
| VM network in | Azure Monitor (VM) — `Network In` (legacy; `Network In Total` is NULL on these D8ds_v5 VMs) | `…host['Network In']` | Host NIC bytes received | ✅ | ❌ no VM (PaaS) — see cluster `NetworkBytesIngress`, §3 |
| VM network out | Azure Monitor (VM) — `Network Out` (legacy) | `…host['Network Out']` | Host NIC bytes sent | ✅ | ❌ no VM (PaaS) — see cluster `NetworkBytesEgress`, §3 |

### 2c. MongoDB server evidence — `serverStatus` + `connPoolStats` (post-run snapshot)

Source: **mongos serverStatus (post-run)** via the `bmt_monitor` connection (single snapshot at load end).
Destination: `azure-metrics.json` → `perTarget.<t>.serverStatus.*` / `.connPoolStats.*` (+ raw text).

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| `serverStatus.process` / `version` | mongos serverStatus (post-run) | `…serverStatus.*` + `metrics-raw/<t>-serverStatus.txt` | mongos process + version | ✅ | ❌ no `serverStatus` on managed vCore |
| `connectionsCurrent` | mongos serverStatus (post-run) — `connections.current` | `…serverStatus.connectionsCurrent` | Concurrent conns **at pull time** (≈ idle — use §2a for the peak) | ✅ | ❌ no server connection metric on vCore |
| `connectionsAvail` | mongos serverStatus (post-run) — `connections.available` | `…connectionsAvail` | Connection headroom | ✅ | ❌ no server connection metric on vCore |
| **`connectionsCreated`** | mongos serverStatus (post-run) — `connections.totalCreated` | `…connectionsCreated` | **Cumulative connections created** (total churn since start). *DocumentDB substitute: client `Connections.Created`, §1a.* | ✅ | ❌ no server connection metric on vCore |
| `connPoolStats.{totalInUse,totalAvailable,totalCreated}` | mongos serverStatus (post-run) — `connPoolStats` | `…connPoolStats.*` + `metrics-raw/<t>-connPoolStats.txt` | mongos→shard back-end pool stats | ✅ | ❌ no `connPoolStats` on managed vCore (no shard/router topology exposed) |

### 2d. MongoDB log slice — connection churn from `mongos.log`

Source: **mongos.log** — on-VM scan of `E:\mongo\log\mongos.log` (5–31 GB, so only counts + tiny sample
cross `az vm run-command`) on both router VMs, filtered to `[start..end]`.
Destination: `azure-metrics.json` → `perTarget.<t>.vms.<vm>.logSlice.*` (+ raw log-window JSON).

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| `linesInWindow` | mongos.log — on-VM scan of last 500k lines | `…logSlice.linesInWindow` + `metrics-raw/<t>-<vm>-log-window.json` | Log lines within `[start..end]` | ✅ | ❌ managed vCore exposes no server log file |
| **`connectionAccepted`** | mongos.log — grep `NETWORK` + `Connection accepted` | `…logSlice.connectionAccepted` | **"Connection accepted" events in window** (server-side conns opened). *DocumentDB substitute: client `Connections.Created`, §1a.* | ✅ | ❌ no server log on vCore |
| **`connectionEnded`** | mongos.log — grep `NETWORK` + `Connection ended` | `…logSlice.connectionEnded` | **"Connection ended" events in window** (server-side conns closed). *DocumentDB substitute: client `Connections.Closed`, §1a.* | ✅ | ❌ no server log on vCore |
| `windowCoveredFrom/To` | mongos.log — first/last in-window line | `…logSlice.windowCovered*` | Actual timestamp span the tail covered | ✅ | ❌ no server log on vCore |
| `sample` | mongos.log — first matches | `…logSlice.sample` | Up to 12 sample NETWORK log lines (≤300 chars) | ✅ | ❌ no server log on vCore |

---

## 3. DocumentDB cluster metrics — Azure Monitor

Source: **Azure Monitor (cluster)** — `az monitor metrics list` on `docdb-dbtest-hpc-0`, via
`Get-AzureMetrics.ps1`. Destination: `azure-metrics.json` → `perTarget.documentdb.metrics.*` (+ raw
`metrics-raw/documentdb-cluster-metrics.json`). These are the server-side metrics DocumentDB publishes.

This is the **full published set** for a Cosmos DB for MongoDB **vCore** cluster, verified with
`az monitor metrics list-definitions` (all retained 93 days at grains PT1M → P1D; each rolled up here as
avg / max / min / total / count over the run window). Each metric except `AutoscaleUtilizationPercent`
carries a `ServerName` dimension (per-node).

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| **Cluster CPU %** | Azure Monitor (cluster) — `CpuPercent` | `…documentdb.metrics.CpuPercent` | **DocumentDB cluster CPU utilization** (per-node). *MongoDB equivalent: VM `Percentage CPU`, §2b.* | ❌ not a cluster resource — MongoDB uses VM `Percentage CPU` | ✅ |
| **Cluster memory %** | Azure Monitor (cluster) — `MemoryPercent` | `…documentdb.metrics.MemoryPercent` | **DocumentDB cluster memory utilization** (per-node). *MongoDB equivalent: VM `Available Memory Bytes`, §2b.* | ❌ MongoDB uses VM `Available Memory Bytes` | ✅ |
| **Committed memory %** | Azure Monitor (cluster) — `CommittedMemoryPercent` | `…documentdb.metrics.CommittedMemoryPercent` | **% of the commit-memory limit allocated by applications on the node** — the "committed memory" saturation signal. | ❌ not published for self-managed VM (use serverStatus mem, §2c/host mem, §2b) | ✅ |
| Autoscale utilization % | Azure Monitor (cluster) — `AutoscaleUtilizationPercent` | `…documentdb.metrics.AutoscaleUtilizationPercent` | % of autoscale capacity in use (cluster-wide; no per-node dimension) | ❌ N/A (no autoscale on self-managed VM) | ✅ |
| Storage % | Azure Monitor (cluster) — `StoragePercent` | `…documentdb.metrics.StoragePercent` | % of available node storage used | ❌ MongoDB uses VM disk metrics / OS | ✅ |
| Storage used (bytes) | Azure Monitor (cluster) — `StorageUsed` | `…documentdb.metrics.StorageUsed` | Quantity of node storage used | ❌ MongoDB uses VM disk metrics / OS | ✅ |
| IOPS | Azure Monitor (cluster) — `IOPS` | `…documentdb.metrics.IOPS` | Disk IO operations per second on the node (throughput proxy) | ❌ not published for self-managed VM | ✅ |
| Network ingress | Azure Monitor (cluster) — `NetworkBytesIngress` | `…documentdb.metrics.NetworkBytesIngress` | Bytes into the cluster. *MongoDB equivalent: VM `Network In`, §2b.* | ❌ MongoDB uses VM `Network In` | ✅ |
| Network egress | Azure Monitor (cluster) — `NetworkBytesEgress` | `…documentdb.metrics.NetworkBytesEgress` | Bytes out of the cluster. *MongoDB equivalent: VM `Network Out`, §2b.* | ❌ MongoDB uses VM `Network Out` | ✅ |
| **Request duration (latency)** | Azure Monitor (cluster) — `MongoRequestDurationMs` (aggregations Avg/Max/Min) | `…documentdb.metrics.MongoRequestDurationMs` | **Server-side end-to-end request latency**, updated every 60 s. *MongoDB equivalent: client op latency §1c / server request latency has no direct mongos gauge.* | ❌ use client op latency (§1c) | ✅ |
| **Request count / RPS** | Azure Monitor (cluster) — `MongoRequestDurationMs` **Count** aggregation | `…documentdb.metrics.MongoRequestDurationMs.count` | **Server-side requests served in the window** (÷ window seconds = server RPS). *This is DocumentDB's server-side throughput signal — the analogue of Mongo `opcounters`.* | ✅ via `opcounters` (§2a) | ✅ |
| **Per-operation RPS + latency** | Azure Monitor (cluster) — `MongoRequestDurationMs` split by **`Operation`** dimension | `…documentdb.requestByOperation.<op>` → `{requestCount, avgMs, maxMs}` | **Server-side request count + latency per op type** (insert/find/update/delete/…). *MongoDB equivalent: per-op `opcounters` (§2a) + client latency (§1c).* | ✅ (opcounters + client latency) | ✅ |
| **Error / throttle counts** | Azure Monitor (cluster) — `MongoRequestDurationMs` split by **`StatusCodeClass`** dimension | `…documentdb.requestByStatus.<class>` → `{requestCount}` | **Server-side 2xx / 4xx / 5xx request counts** — throttles and errors surface here as non-2xx classes. *MongoDB equivalent: client `ErrorsByType` (§1d) + mongos.log (§2d).* | ❌ (use client `ErrorsByType`, §1d) | ✅ |

> **Correction vs earlier notes:** vCore has **no dedicated active-connection counter**, so DocumentDB
> **concurrent** and **created** connection counts still come **only from the client side** (§1b
> `InFlightTasks`, §1a `Connections.Created`), and the in-run server sampler (§2a) does **not** run for
> DocumentDB. **However**, DocumentDB *does* expose **server-side throughput and error/throttle
> visibility** through the `MongoRequestDurationMs` metric: its **`Count`** aggregation = requests served
> (RPS), its **`Operation`** dimension = per-op RPS + latency, and its **`StatusCodeClass`** dimension =
> 2xx/4xx/5xx counts (throttles show up as 4xx/5xx). Other available dimensions on that metric
> (`StatusCode`, `ErrorCode`, `DatabaseName`, `CollectionName`, `Protocol`, `Authentication`) can be
> split the same way if a finer breakdown is needed.

---

## 4. Cross-host merge — combined concurrency & conn/s (pass/fail gate)

A single generator can't reach the envelope, so per-host client-side series (§1b) are summed on the
absolute wall-clock second. Source: `src/Bmt.Report/Merger.cs` via
`scripts/run/Merge-Campaign.ps1 -RunTag <tag> -InputDir <dir>`. Because these are built from **client-side**
series, they are available for **both** DBs.

| Metric | Source | Destination | Description | MongoDB | DocumentDB |
|---|---|---|---|---|---|
| **`PeakCombinedInFlight`** | Merge of client `InFlightTasks` across hosts | `merge-<tag>.json` | Peak per-second SUM of each host's concurrency — **combined concurrent**; `ReachedConcurrentTarget` = `≥ 11000` | ✅ | ✅ |
| **`PeakCombinedConnPerSec`** | Merge of client conn/s across hosts | `merge-<tag>.json` | Peak per-second SUM of each host's conn/s — **combined churn**; `ReachedChurnTarget` = `≥ 1200` | ✅ | ✅ |
| `combined_in_flight`, `combined_conn_per_sec`, `combined_ops`, `combined_failed_ops` | Merge of client series | `merge-<tag>-…-combined.csv` | Combined per-second series | ✅ | ✅ |

---

## 5. Artifact map — where to look when pulling results

```
# Operator box (az1-0) — server-side artifacts per campaign:
# <RunTag> defaults to <db>-<MMdd>-<stamp> (e.g. mongo-0723-ti0), sharing <MMdd>-<stamp> with the
# per-host folders below so operator + client artifacts correlate at a glance.
results/_campaign-<RunTag>/
├── server-samples/mongo-serverstats.csv   # §2a in-run server concurrency + QPS (MongoDB only)
├── azure-metrics.json                      # §2b–2d + §3: VM CPU/mem/net, serverStatus, log, DocumentDB cluster
└── metrics-raw/
    ├── documentdb-cluster-metrics.json     # §3 DocumentDB scalar metrics (all 10)
    ├── documentdb-request-by-operation.json # §3 DocumentDB per-Operation RPS + latency
    ├── documentdb-request-by-status.json   # §3 DocumentDB per-StatusCodeClass counts (throttles/errors)
    ├── <target>-serverStatus.txt           # §2c MongoDB
    ├── <target>-connPoolStats.txt          # §2c MongoDB
    ├── <target>-<vm>-host-metrics.json     # §2b MongoDB VM
    └── <target>-<vm>-log-window.json       # §2d MongoDB log

# Each load-generator host — client-side (§1), under results/<campaignId>/:
# Compact folder name: <db>-<loop>-<workload>-<MMdd>-<stamp>[-hN]
#   db=mongo|docdb|cosmos  loop=open|closed  workload=full|query|insert
#   <stamp> = ≤3-char base-36 of the shared start instant (same across hosts); -hN only when multi-host
results/mongo-open-full-0723-ti0-h1/
├── aggregate.json                          # cross-iteration aggregate for that host
└── iter-NN/
    ├── <runId>.json                        # full RunResult (all §1 metrics)
    ├── <runId>-timeseries.csv              # per-second concurrency/conn-s/QPS + client CPU/mem/ports
    └── <runId>-latency.csv                 # per-op + lifecycle latency percentiles

# After merging all hosts (§4):
results/merge-<RunTag>.json                 # combined concurrency + conn/s vs targets (pass/fail)
results/merge-<RunTag>-…-combined.csv       # combined per-second series
```

### Quick "which number came from where" cheat-sheet

| Question | Metric | Source · file | MongoDB | DocumentDB |
|---|---|---|---|---|
| Did we hit ≥11,000 concurrent? | `PeakCombinedInFlight` | §4 client merge · `merge-<tag>.json` | ✅ (confirm w/ §2a `connCurrent`) | ✅ (client only) |
| Did we hit ≥1,200 conn/s? | `PeakCombinedConnPerSec` | §4 client merge · `merge-<tag>.json` | ✅ (confirm w/ §2a `connTotalCreated`) | ✅ (client only) |
| DB server CPU / memory | Mongo: `Percentage CPU` / `Available Memory Bytes`; DocDB: `CpuPercent` / `MemoryPercent` | §2b / §3 · `azure-metrics.json` | ✅ VM | ✅ cluster |
| Server concurrent conns (true peak) | `connCurrent` summed across routers | §2a · `mongo-serverstats.csv` | ✅ | ❌ no server metric — use client `InFlightTasks` |
| Server QPS / RPS | Mongo: `opQuery/opCommand/…` deltas; DocDB: `MongoRequestDurationMs` Count (÷ window) | §2a `mongo-serverstats.csv` / §3 `azure-metrics.json` | ✅ | ✅ (server-side, via request Count) |
| Server per-op throughput + latency | Mongo: `opcounters` + client latency; DocDB: `MongoRequestDurationMs` by `Operation` | §2a / §3 `requestByOperation` | ✅ | ✅ |
| Server errors / throttles | Mongo: client `ErrorsByType` + mongos.log; DocDB: `MongoRequestDurationMs` by `StatusCodeClass` (4xx/5xx) | §1d / §2d / §3 `requestByStatus` | ✅ (client + log) | ✅ (server-side status classes) |
| Committed memory saturation | DocDB: `CommittedMemoryPercent` | §3 · `azure-metrics.json` | ❌ (use serverStatus/host mem) | ✅ |
| Storage / autoscale headroom | DocDB: `StoragePercent`, `StorageUsed`, `AutoscaleUtilizationPercent` | §3 · `azure-metrics.json` | ❌ (VM disk/OS) | ✅ |
| Conns created (total churn) | `connectionsCreated` / log `connectionAccepted` | §2c/§2d · `azure-metrics.json` | ✅ | ❌ use client `Connections.Created` |
| Latency (op / handshake / cycle) | `OperationLatencyMs`, `ConnectionOpenMs`, … | §1c · `<runId>.json` + `-latency.csv` | ✅ | ✅ (client) + DocDB server `MongoRequestDurationMs` §3 |
| Load-generator CPU/mem/ports | `cpuPercent`, `workingSetBytes`, `ephemeralPortsInUse` | §1e · `-timeseries.csv` | ✅ | ✅ |
| DB network throughput | Mongo: VM `Network In/Out`; DocDB: `NetworkBytesIngress/Egress` | §2b / §3 · `azure-metrics.json` | ✅ VM | ✅ cluster |

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
| **In-run server-side sampler** (MongoDB) | `scripts/run/Sample-MongoServerStats.ps1` |
| **Post-run Azure Monitor + serverStatus + log pull** | `scripts/run/Get-AzureMetrics.ps1` |
| Multi-host campaign driver (starts sampler + auto-pull) | `scripts/run/Invoke-Campaign.ps1` |
| Cross-host merge (combined concurrency/conn-s) | `src/Bmt.Report/Merger.cs` / `scripts/run/Merge-Campaign.ps1` |
| Azure resource identifiers (no secrets) | `config/azure-resources.json` |
