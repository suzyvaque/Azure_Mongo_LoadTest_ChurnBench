# Metrics Reference — MongoDB & DocumentDB

Catalog of every metric captured for both targets — **MongoDB** (`mongo-shard`, self-managed on Azure
VMs) and **DocumentDB** (Cosmos DB for MongoDB **vCore**, cluster `docdb-dbtest-hpc-0`) — with its
source, where it lands, and per-DB availability. Use it when pulling results after a campaign.

**Reading the availability columns:** ✅ = captured; ❌ = not available (reason in the cell).
Two facts explain the ❌s:
- **MongoDB is on VMs we own** → we can read VM host metrics, `serverStatus`/`connPoolStats` (monitor
  credential), and `mongos.log` on the box.
- **DocumentDB is managed PaaS** → no OS/log/`serverStatus` access. It has **no active-connection
  counter** (so concurrency & churn come from the client side), but it *does* expose server-side
  **throughput + error/throttle** via `MongoRequestDurationMs` (§3).

> ### ⏱ Azure Monitor ingestion lag — wait before pulling
> Azure **platform metrics are not queryable until ~1–5 minutes after** the events occur. If you pull
> too early, the **tail of the run window is missing / null**.
> - **DocumentDB `MongoRequestDurationMs`** (request Count + `Operation`/`StatusCodeClass` splits) is the
>   slowest — allow **~5 min**.
> - VM / cluster gauges (CPU, memory, network, IOPS) lag **~1–3 min**.
>
> `Get-AzureMetrics.ps1` **waits `-IngestionWaitSeconds` (default 300s = 5 min) before pulling**, so the
> automated post-run capture is already correct. When **re-pulling an old, already-ingested window**, skip
> it with `-IngestionWaitSeconds 0`. Only §1 (client-side) and §2a/§2c/§2d (read live off the server) have
> **no** lag — those are exact immediately.

### Source labels used in the tables

| Source label | What it is | Vantage point |
|---|---|---|
| **Client (in-proc)** | `Bmt.LoadGen` .NET process — driver SDAM events + in-process counters | Load generator |
| **Client (OS sampler)** | `ClientResourceSampler`, 1 s polling of the client host OS | Load-generator host OS |
| **Server sampler** | `Sample-MongoServerStats.ps1`, 5 s `serverStatus` polling during the run | mongos routers |
| **Azure Monitor (VM)** | `az monitor metrics list` on the Mongo VM host | Azure platform (Mongo VM) |
| **Azure Monitor (cluster)** | `az monitor metrics list` on the DocumentDB cluster | Azure platform (vCore) |
| **serverStatus (post-run)** | One `serverStatus`/`connPoolStats` snapshot after the run | mongos routers |
| **mongos.log** | On-VM scan of `E:\mongo\log\mongos.log` over the window | mongos routers |

---

## 1. Client-side metrics (load-generator VM) — ✅ identical for BOTH DBs

The same generator drives both targets, so **every §1 metric is available for MongoDB and DocumentDB**.
These measure what the *client* observed (connections, latency, churn, client-host pressure) — **not** the
DB server's CPU/RAM. **No ingestion lag** (written locally at run end).
Code: `MetricsCollector.cs`, `ClientResourceSampler.cs`, `ConnectionEventCounters.cs`; serialized by
`RunResult.cs`; CSVs by `CsvWriter.cs`.

### 1a. Connection lifecycle counters

| Metric | Source (event) | Destination | Description |
|---|---|---|---|
| `Connections.Created` | `OnConnectionCreated` | `RunResult.Connections.created` | New physical connections opened |
| `Connections.Ready` | `OnConnectionReady` | `…ready` | Finished handshake+auth, usable |
| `Connections.Closed` | `OnConnectionClosed` | `…closed` | Connections closed |
| `Connections.Failed` | `OnConnectionFailed` | `…failed` | Connection-open failures |
| `Connections.CheckedOut` | `OnConnectionCheckedOut` | `…checkedOut` | Pool check-outs (≈1 per op) |
| `Created/ClosedToTaskRatio` | computed | `…Connections.*` | created÷tasks, closed÷tasks (≈1.0 = no reuse) |
| Reuse verification | derived | `RunResult.ReuseCheck` | `noReuseConfirmed`, `suspectedReuseEvents` |

### 1b. Concurrency & throughput (per-second series)

| Metric | Source | Destination | Description |
|---|---|---|---|
| **`InFlightTasks`** | `Interlocked` peak/sec | `Throughput[].inFlightTasks` + `in_flight_tasks` (CSV) | **Concurrent open connections/sec — primary source of DocumentDB concurrency** |
| **conn/s (churn)** | per-sec created bucket | `Throughput[].connectionsCreated` + `conn_created` | Connections opened/sec — **primary DocumentDB churn source** |
| conns closed/s | per-sec closed bucket | `…connectionsClosed` + `conn_closed` | Connections closed/sec |
| **per-op QPS** | per-sec op buckets | `Throughput[].{find/remove/insert/findOutput}Ops` + CSV | Ops/sec for each of the 4 ops |
| **combined QPS** | `CombinedOps` | `combined_ops` (CSV) | Sum of the 4 op rates/sec |
| failed ops/s | per-sec failed bucket | `…failedOps` + `failed_ops` | Failed ops/sec |

### 1c. Latency (min/mean/p50/p90/p95/p99/p999/max)

| Metric | Source | Destination | Description |
|---|---|---|---|
| `find_input`,`remove`,`insert`,`find_output` | timed in `TaskRunner` | `RunResult.OperationLatencyMs.*` + `*-latency.csv` | Per-op latency of the 4 ordered ops |
| `task_cycle` | `OnTaskEnd` | `…TaskCycleLatencyMs` + `task_cycle` | Full cycle: connect → 4 ops → disconnect |
| `connection_open` | `OnConnectionReady` | `…ConnectionOpenMs` + `connection_open` | Whole handshake duration |
| `handshake_hello` | `hello`/`isMaster` hook | `…HandshakeHelloMs` + `handshake_hello` | Wire-negotiation latency |
| `handshake_auth` | SCRAM hook | `…HandshakeAuthMs` + `handshake_auth` | SCRAM auth latency |
| `client_create` | `RecordClientCreate` | `…ClientCreateMs` + `client_create` | `MongoClient` construction time |

### 1d. Totals & error taxonomy

| Metric | Source | Destination | Description |
|---|---|---|---|
| `Totals.{Total,Successful,Failed}Tasks` | interlocked | `RunResult.Totals` | Task counts |
| `Totals.{Total,Successful,Failed}Ops` | interlocked | `RunResult.Totals` | Op counts |
| `ErrorsByType` | `ExceptionClassifier` | `RunResult.ErrorsByType` | Every failure bucketed by type |

### 1e. Client-host resource pressure (the LOAD GENERATOR, not the DB)

Source: `ClientResourceSampler.cs`, every 1 s. ⚠️ This CPU/mem is the **client** — for DB server CPU/mem
see §2 (Mongo) / §3 (DocumentDB).

| Metric | Source | Destination | Description |
|---|---|---|---|
| `EphemeralPortsInUse` | `GetActiveTcpConnections()` | `…ephemeralPortsInUse` + `ephemeral_ports` | Active TCP conns — port pressure |
| `TimeWaitSockets` | TCP table `TimeWait` | `…timeWaitSockets` + `time_wait` | Sockets in TIME_WAIT |
| `HandleCount`/`ThreadCount` | `Process.*` | `…handleCount/threadCount` + `handles`/`threads` | Process handles / threads |
| **`CpuPercent`** | `TotalProcessorTime` delta | `…cpuPercent` + `cpu_pct` | **Load-generator process CPU %** |
| **`WorkingSetBytes`** | `WorkingSet64` | `…workingSetBytes` + `working_set_bytes` | **Load-generator memory** |
| `Process.Peak*`/`MaxCpuPercent` | `Peaks()` | `RunResult.Process` | End-of-run peaks of the above |

---

## 2. MongoDB server-side metrics — ✅ Mongo only

DocumentDB (managed) can't provide these; the substitute is noted per row. §2a/§2c/§2d read the server
**live** (no lag); §2b (VM Azure Monitor) lags ~1–3 min.

### 2a. In-run server sampler — true server concurrency & QPS

`Sample-MongoServerStats.ps1` (auto-started by `Invoke-Campaign.ps1`), one client **per mongos router**
via the `bmt_monitor` credential, polling `serverStatus` every **5 s during the run**. Needed because
`connections.current` is a live gauge — this captures the **peak while load is live**.
Destination: `results/_campaign-<RunTag>/server-samples/mongo-serverstats.csv`.

| Metric (CSV col) | serverStatus field | Description | DocumentDB substitute |
|---|---|---|---|
| `timestampUtc`, `host` | — | Sample time; which router | ❌ no `serverStatus` / per-node access on vCore |
| **`connCurrent`** | `connections.current` | **Concurrent conns held (live peak)** | client `InFlightTasks` §1b |
| `connAvailable` | `connections.available` | Connection headroom | ❌ |
| `connActive` | `connections.active` | Conns executing an op | ❌ |
| `connTotalCreated` | `connections.totalCreated` | Cumulative created (server conn/s = deltas) | client `Connections.Created` §1a |
| **`opInsert/Query/Update/Delete/Getmore/Command`** | `opcounters.*` | **Cumulative op counters (server QPS = deltas)** | DocumentDB `MongoRequestDurationMs` Count by Operation §3 |

### 2b. Mongo VM host metrics — Azure Monitor (VM CPU & memory) · lags ~1–3 min

`az monitor metrics list` per router VM (`vm-dbtest-hpc-1-mongo`, `-mongo-shard`), avg/max over the window.
Destination: `azure-metrics.json → perTarget.<t>.vms.<vm>.host[...]`.

| Metric | Field | Description | DocumentDB equivalent |
|---|---|---|---|
| **VM CPU %** | `Percentage CPU` | **Mongo VM host CPU** | cluster `CpuPercent` §3 |
| **VM available memory** | `Available Memory Bytes` | **Mongo VM free memory** | cluster `MemoryPercent` §3 |
| VM network in | `Network In` (legacy; `Network In Total` is null on these VMs) | NIC bytes received | cluster `NetworkBytesIngress` §3 |
| VM network out | `Network Out` (legacy) | NIC bytes sent | cluster `NetworkBytesEgress` §3 |

### 2c. `serverStatus` + `connPoolStats` — post-run snapshot (live, no lag)

Via the `bmt_monitor` connection, one snapshot at load end.
Destination: `azure-metrics.json → perTarget.<t>.serverStatus.* / .connPoolStats.*` (+ raw text).

| Metric | Field | Description |
|---|---|---|
| `serverStatus.process`/`version` | — | mongos process + version |
| `connectionsCurrent` | `connections.current` | Concurrent conns **at pull time** (≈idle — use §2a for peak) |
| `connectionsAvail` | `connections.available` | Connection headroom |
| **`connectionsCreated`** | `connections.totalCreated` | **Cumulative created (total churn)** |
| `connPoolStats.{totalInUse,totalAvailable,totalCreated}` | `connPoolStats` | mongos→shard back-end pool |

### 2d. Log slice — connection churn from `mongos.log` (live, no lag)

On-VM scan of `E:\mongo\log\mongos.log` (multi-GB → only counts + tiny sample cross `az vm run-command`),
both router VMs, filtered to the window.
Destination: `azure-metrics.json → perTarget.<t>.vms.<vm>.logSlice.*`.

| Metric | Source | Description |
|---|---|---|
| `linesInWindow` | last 500k lines in window | Log lines in `[start..end]` |
| **`connectionAccepted`** | `NETWORK` + `Connection accepted` | **Server-side conns opened in window** |
| **`connectionEnded`** | `NETWORK` + `Connection ended` | **Server-side conns closed in window** |
| `windowCoveredFrom/To` | first/last in-window line | Actual span the tail covered |
| `sample` | first matches | ≤12 sample NETWORK lines |

---

## 3. DocumentDB cluster metrics — Azure Monitor · lags ~1–5 min

Full published set for a Cosmos DB for MongoDB **vCore** cluster (verified via
`az monitor metrics list-definitions`; retained 93 d; each rolled up as avg/max/min/total/count over the
window). Source: `Get-AzureMetrics.ps1` → `perTarget.documentdb.metrics.*` (+ raw
`metrics-raw/documentdb-*.json`). Every metric except `AutoscaleUtilizationPercent` carries a `ServerName`
(per-node) dimension. **Wait ~5 min before pulling** (the request metric is the slowest to ingest).

| Metric | Azure name | Description | MongoDB equivalent |
|---|---|---|---|
| **Cluster CPU %** | `CpuPercent` | **Cluster CPU (per-node)** | VM `Percentage CPU` §2b |
| **Cluster memory %** | `MemoryPercent` | **Cluster memory (per-node)** | VM `Available Memory Bytes` §2b |
| **Committed memory %** | `CommittedMemoryPercent` | **% of commit-memory limit used — saturation signal** | serverStatus/host mem §2 |
| Autoscale util % | `AutoscaleUtilizationPercent` | % autoscale capacity in use (cluster-wide) | ❌ no autoscale on VM |
| Storage % | `StoragePercent` | % node storage used | VM disk/OS |
| Storage used | `StorageUsed` | Node storage bytes | VM disk/OS |
| IOPS | `IOPS` | Disk IO ops/sec (throughput proxy) | ❌ not published for VM |
| Network ingress | `NetworkBytesIngress` | Bytes into cluster | VM `Network In` §2b |
| Network egress | `NetworkBytesEgress` | Bytes out of cluster | VM `Network Out` §2b |
| **Request latency** | `MongoRequestDurationMs` (Avg/Max/Min) | **Server-side end-to-end request latency** | client op latency §1c |
| **Request count / RPS** | `MongoRequestDurationMs` **Count** | **Requests served (÷ window = server RPS)** — the `opcounters` analogue | `opcounters` §2a |
| **Per-op RPS + latency** | `MongoRequestDurationMs` by **`Operation`** → `requestByOperation.<op>` | Server count + latency per op (find/insert/…) | per-op `opcounters` §2a |
| **Error / throttle counts** | `MongoRequestDurationMs` by **`StatusCodeClass`** → `requestByStatus.<class>` | **2xx/4xx/5xx counts** — throttles = non-2xx | client `ErrorsByType` §1d |

> **Connections caveat:** vCore has **no active-connection counter**, so DocumentDB concurrent/created
> connection counts come only from the client side (§1b/§1a). Everything else above *is* server-side.
> Finer breakdowns are possible by splitting `MongoRequestDurationMs` on `StatusCode`, `ErrorCode`,
> `DatabaseName`, `CollectionName`, `Protocol`, or `Authentication` (same technique as §7).

---

## 4. Cross-host merge — combined concurrency & conn/s (pass/fail gate) — ✅ both DBs

One generator can't reach the envelope, so per-host client series (§1b) are summed on the wall-clock
second. Source: `Merger.cs` via `Merge-Campaign.ps1 -RunTag <tag> -InputDir <dir>`. Client-derived, so
available for both DBs. No lag.

| Metric | Description |
|---|---|
| **`PeakCombinedInFlight`** | Peak per-sec SUM of host concurrency — **combined concurrent**; `ReachedConcurrentTarget` = `≥ 11000` |
| **`PeakCombinedConnPerSec`** | Peak per-sec SUM of host conn/s — **combined churn**; `ReachedChurnTarget` = `≥ 1200` |
| `combined_in_flight/conn_per_sec/ops/failed_ops` | Combined per-second series (`merge-<tag>-…-combined.csv`) |

---

## 5. Artifact map

```
# Operator box (az1-0) — server-side, per campaign. <RunTag> defaults to <db>-<MMdd>-<stamp>.
results/_campaign-<RunTag>/
├── server-samples/mongo-serverstats.csv    # §2a in-run server concurrency + QPS (Mongo only)
├── azure-metrics.json                       # §2b–2d + §3 rollups
└── metrics-raw/
    ├── documentdb-cluster-metrics.json      # §3 the 10 scalar metrics
    ├── documentdb-request-by-operation.json # §3 per-Operation RPS + latency
    ├── documentdb-request-by-status.json    # §3 per-StatusCodeClass counts (throttles/errors)
    ├── <target>-serverStatus.txt            # §2c
    ├── <target>-connPoolStats.txt           # §2c
    ├── <target>-<vm>-host-metrics.json      # §2b
    └── <target>-<vm>-log-window.json        # §2d

# Each load-generator host — client-side (§1). Folder: <db>-<loop>-<workload>-<MMdd>-<stamp>[-hN]
#   db=mongo|docdb|cosmos  loop=open|closed  workload=full|query|insert  stamp=≤3-char base-36
results/mongo-open-full-0723-ti0-h1/
├── aggregate.json                           # cross-iteration aggregate
└── iter-NN/
    ├── <runId>.json                         # full RunResult (all §1)
    ├── <runId>-timeseries.csv               # per-sec concurrency/conn-s/QPS + client CPU/mem/ports
    └── <runId>-latency.csv                  # per-op + lifecycle percentiles

# After merging all hosts (§4):
results/merge-<RunTag>.json                  # combined concurrency + conn/s vs targets (pass/fail)
results/merge-<RunTag>-…-combined.csv        # combined per-second series
```

### "Which number came from where" cheat-sheet

| Question | Metric · file | MongoDB | DocumentDB |
|---|---|---|---|
| Hit ≥11,000 concurrent? | `PeakCombinedInFlight` · `merge-<tag>.json` (§4) | ✅ (confirm §2a `connCurrent`) | ✅ (client only) |
| Hit ≥1,200 conn/s? | `PeakCombinedConnPerSec` · `merge-<tag>.json` (§4) | ✅ (confirm §2a `connTotalCreated`) | ✅ (client only) |
| DB server CPU / memory | `Percentage CPU`/`Available Memory Bytes` (§2b) · `CpuPercent`/`MemoryPercent` (§3) | ✅ VM | ✅ cluster |
| Server concurrent conns (peak) | `connCurrent` (§2a) | ✅ | ❌ use client `InFlightTasks` |
| Server QPS / RPS | `opcounters` deltas (§2a) · `MongoRequestDurationMs` Count (§3) | ✅ | ✅ server-side |
| Server per-op throughput+latency | `opcounters` (§2a) · `requestByOperation` (§3) | ✅ | ✅ |
| Server errors / throttles | client `ErrorsByType`/log (§1d/§2d) · `requestByStatus` 4xx/5xx (§3) | ✅ | ✅ |
| Committed-memory saturation | `CommittedMemoryPercent` (§3) | ❌ | ✅ |
| Storage / autoscale headroom | `StoragePercent`/`StorageUsed`/`AutoscaleUtilizationPercent` (§3) | ❌ | ✅ |
| Conns created (total churn) | `connectionsCreated`/log `connectionAccepted` (§2c/§2d) | ✅ | ❌ use client `Connections.Created` |
| Latency (op/handshake/cycle) | `OperationLatencyMs`… (§1c) | ✅ | ✅ + DocDB server `MongoRequestDurationMs` §3 |
| Load-generator CPU/mem/ports | `cpuPercent`/`workingSetBytes`/`ephemeralPortsInUse` (§1e) | ✅ | ✅ |
| DB network throughput | VM `Network In/Out` (§2b) · `NetworkBytes*` (§3) | ✅ VM | ✅ cluster |

---

## 6. Source-file index

| Concern | File |
|---|---|
| Client metric collection | `src/Bmt.LoadGen/MetricsCollector.cs` |
| Client-host resource sampler | `src/Bmt.LoadGen/ClientResourceSampler.cs` |
| Driver connection-event counters | `src/Bmt.Core/Connections/ConnectionEventCounters.cs` |
| Result model (JSON schema) | `src/Bmt.Core/Metrics/RunResult.cs` |
| CSV writers | `src/Bmt.LoadGen/Output/CsvWriter.cs` |
| Orchestration + artifact paths | `src/Bmt.LoadGen/RunOrchestrator.cs` |
| In-run server sampler (Mongo) | `scripts/run/Sample-MongoServerStats.ps1` |
| Post-run Azure Monitor + serverStatus + log pull | `scripts/run/Get-AzureMetrics.ps1` |
| Multi-host campaign driver | `scripts/run/Invoke-Campaign.ps1` |
| Cross-host merge | `src/Bmt.Report/Merger.cs` / `scripts/run/Merge-Campaign.ps1` |
| Azure resource identifiers (no secrets) | `config/azure-resources.json` |

---

## 7. Notebook — pull one metric for one run and see aggregated results

Two ways to inspect a metric. **A** reads the `azure-metrics.json` already saved by a campaign (fast,
offline, no lag). **B** re-queries Azure Monitor live for any metric/window (needs `az login`; mind the
§0 ingestion lag). Paste either cell into a Jupyter notebook (Python 3).

### A. Read the saved rollup from a campaign folder

```python
import json, pathlib, pandas as pd

# --- point these at your run ---
CAMPAIGN = pathlib.Path(r"C:\bmt\results\_campaign-docdb-0723-ti0")  # campaign folder with azure-metrics.json
TARGET   = "documentdb"          # "documentdb" | "mongo-shard"
METRIC   = "CpuPercent"          # e.g. CpuPercent, CommittedMemoryPercent, IOPS, MongoRequestDurationMs

data = json.loads((CAMPAIGN / "azure-metrics.json").read_text())
print("window:", data["runWindowStartUtc"], "->", data["runWindowEndUtc"])
t = data["perTarget"][TARGET]

if TARGET == "documentdb":
    row = t["metrics"][METRIC]                       # avg / max / min / total / count / samples
    print(f"\n{METRIC}  ({row['unit']})")
    print(pd.Series(row))
    if METRIC == "MongoRequestDurationMs":
        print("\nRPS split by Operation:")
        print(pd.DataFrame(t["requestByOperation"]).T.sort_values("requestCount", ascending=False))
        print("\nCounts by StatusCodeClass (throttles = non-2xx):")
        print(pd.DataFrame(t["requestByStatus"]).T)
else:  # mongo-shard: metric lives per-VM under host[...]
    for vm, blk in t["vms"].items():
        print(f"\n{vm} · {METRIC}")
        print(pd.Series(blk["host"][METRIC]))
```

### B. Re-query Azure Monitor live (any metric / dimension / window)

```python
import json, subprocess, pandas as pd

RID   = ("/subscriptions/01c04f52-ad2b-4cc0-b77b-61508ec58f51/resourceGroups/rg-db-test-hpc"
         "/providers/Microsoft.DocumentDB/mongoClusters/docdb-dbtest-hpc-0")   # DocumentDB cluster
METRIC = "MongoRequestDurationMs"
START, END = "2026-07-23T03:11:00Z", "2026-07-23T03:15:00Z"   # your run window (UTC)
AGGS   = ["Average", "Maximum", "Minimum", "Total", "Count"]

def az_metric(rid, metric, start, end, aggs, dimension=None, interval="PT1M"):
    cmd = ["az", "monitor", "metrics", "list", "--resource", rid, "--metric", metric,
           "--start-time", start, "--end-time", end, "--interval", interval,
           "--aggregation", *aggs, "-o", "json"]
    if dimension:
        cmd += ["--filter", f"{dimension} eq '*'", "--top", "50"]
    return json.loads(subprocess.run(cmd, capture_output=True, text=True, shell=True).stdout)

# scalar rollup over the window
j = az_metric(RID, METRIC, START, END, AGGS)
pts = j["value"][0]["timeseries"][0]["data"]
df = pd.DataFrame(pts)
print(f"{METRIC}: avg={df.get('average').mean():.2f}  max={df.get('maximum').max():.2f}  "
      f"count(total requests)={df.get('count').sum():.0f}")

# same metric split by a dimension (Operation / StatusCodeClass / StatusCode / ErrorCode ...)
jd = az_metric(RID, METRIC, START, END, ["Count", "Average", "Maximum"], dimension="Operation")
rows = []
for ts in jd["value"][0]["timeseries"]:
    op = ts["metadatavalues"][0]["value"]
    d  = pd.DataFrame(ts["data"])
    rows.append({"Operation": op, "requestCount": d.get("count").sum(),
                 "avgMs": d.get("average").mean(), "maxMs": d.get("maximum").max()})
print(pd.DataFrame(rows).sort_values("requestCount", ascending=False).to_string(index=False))
```

> For a **Mongo VM** metric in cell B, set `RID` to the VM resource id
> (`az vm show -g rg-db-test-hpc -n vm-dbtest-hpc-1-mongo --query id -o tsv`) and `METRIC` to
> `"Percentage CPU"`, `"Available Memory Bytes"`, `"Network In"`, or `"Network Out"`.

---

## §8 Benchmark-correctness & open-loop telemetry (client-side, exact, no lag)

Added to make the churn benchmark answer *where* a bottleneck is, not just *how slow*. All of these are
**Client (in-proc)** or **Client (OS sampler)** — captured live, no Azure Monitor ingestion lag. They live
in each per-host, per-iteration `RunResult` JSON (and its `-timeseries.csv` / `-latency.csv` /
`-target-tcp.csv` companions), and are merged per synchronized iteration by `report merge`.

### 8.1 Iteration synchronization (R1)

The coordinator (`Invoke-Campaign.ps1`) owns the iteration loop: each iteration computes one shared
`--start-at` UTC instant, launches exactly one iteration on hosts 1/2/3, waits for full completion incl.
drain, validates all three hosts reported, and reruns the whole three-host iteration on any failure.
`report merge` groups **per (target, scenario, iteration)**, requires the exact host set `{1..N}`, dedupes
retries to the latest attempt per host, and reports **start-time skew**, superseded artifacts, and
entirely-missing iterations. Cross-iteration mean/min/max is computed only over **valid** iterations.

### 8.2 Arrival vs drain (R2) — the 300 s arrival window is the denominator

| Metric | Meaning |
|---|---|
| `Totals.TasksScheduled` / `TasksStarted` | Tasks offered to the runtime / that began executing |
| `OpenLoop.ScheduledTasksPerSec` / `StartedTasksPerSec` | rate = count ÷ **arrival window** (not total duration) |
| `OpenLoop.SchedulerQueueLatencyMs` | `TaskStartedUtc − ScheduledUtc` (generator-runtime dispatch delay) |
| `OpenLoop.TaskExecutionLatencyMs` | `TaskFinishedUtc − TaskStartedUtc` |
| `OpenLoop.OfferedToFinishedLatencyMs` | `TaskFinishedUtc − ScheduledUtc` — **authoritative** open-loop e2e (p50/p95/p99) |
| `OpenLoop.*ArrivalMs` | same three, but only for Tasks that also **completed during arrival** (secondary view) |
| `Arrival.ArrivalStarted/Stopped/DrainStarted/DrainFinished*` | explicit window bounds |
| `Arrival.TasksOutstandingAtArrivalStop` / `MaximumDrainBacklog` | backlog carried into / peak during drain |

The authoritative set includes **every Task offered during arrival, even if it completes during drain** —
excluding drain completions would drop the slowest requests and make an overloaded backend look fast.

### 8.3 Connection lifecycle (R3) — driver events are authoritative

Driver connection-monitoring events (not Task counts, not raw sockets) drive `Lifecycle.*`:
`ConnectionsCreated/Ready/Failed/Closed`, gauges `PeakActiveConnecting/PeakActiveReady/PeakWaitingForServer`,
and the two cold-connection latencies `DemandToReadyLatencyMs` (user-observed) and `DriverOpenLatencyMs`
(physical open only). `LifecycleReconciled` checks created≈closed after drain; `CreatedMinusDemand` checks
~one connection per Task that reached demand. **Threshold verdicts** (in `report merge`): peak combined
`ConnectionsCreated/s ≥ 1,200`, peak combined `ConnectionsReady/s ≥ 1,200`, and peak combined
**`ActiveReady ≥ 11,000`** — the authoritative concurrency verdict. In-flight Task count is a generator
diagnostic only and is **never** used as proof of established connections.

### 8.4 Target-specific TCP (R4)

`TargetTcp` resolves the database destination IP/port set (SRV + A/AAAA), refreshes it periodically, and the
sampler counts only sockets to those endpoints: `Target{SynSent,Established,TimeWait,CloseWait,FinWait1,
FinWait2,TotalSockets,DistinctLocalPorts}` (per-second sub-second peaks, raw cadence 250–500 ms). Host-wide
totals (`HostTotalTcpSockets`, `HostTotalTimeWait`, ephemeral-port utilization) are kept as **general
VM-pressure context only** — never as database-specific evidence. `DroppedSamples` reports telemetry
integrity. Interpretation: high `TargetSynSent` = TCP/accept delay; high `TargetEstablished` with low driver
ready = TLS/auth/handshake delay; high `TargetTimeWait` = ephemeral-port churn; high `TargetCloseWait` =
delayed cleanup.

### 8.5 Merged report (R5)

Each synchronized iteration merges to: required host IDs present, start-time skew, combined offered/started
rates, peak combined created/s + ready/s + **active-ready** (authoritative), failure rate, true e2e p99 and
drain duration (worst host bounds the iteration), plus a per-host breakdown (`Hosts[]`). The campaign-level
`CrossIteration` summary reports mean/min/max across the **valid** iterations only.
