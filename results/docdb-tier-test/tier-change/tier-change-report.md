# Tier-Change / Downtime Behavior Report

**Resource:** `docdb-dbtest-hpc-1` (Azure Cosmos DB for MongoDB vCore) · RG `rg-db-test-hpc` · koreacentral
**Fixed architecture (unchanged across every transition):** 2 shards · HA enabled · private connectivity
**Purpose:** measure **customer-visible** downtime during tier changes — i.e. the real connection/query
interruption window seen by a live client — **not** merely how long Azure reports the scaling operation
as running.

> Status: **Scale-up M80 → M200 complete and analyzed below.**
> **Scale-down M200 → M30 is pending** (executed after the M200 workload runs) and will be appended in §2.

---

## Method

A lightweight **client-side probe** runs continuously across each transition from the load-generator VM,
against the private endpoint, using the **same driver and workload** as the main test (config
`config/production/tierchange-probe.json`: steady **30 new connections/s**, no reuse, full 4-op cycle).

To avoid losing data if a segment is interrupted, the probe runs as **back-to-back 300-second segments**
(each writes its own artifact) controlled by a sentinel file, so no segment is ever killed mid-run. Each
segment records: error counts by type, connection lifecycle failures, cold-connection establishment
percentiles, task-cycle percentiles, and per-second timeseries. Coverage gap between segments ≈ 2–3 s.

The scaling command is run in a **separate shell** and **timed** (blocking until the control plane
reaches a terminal `provisioningState`). Command, start/end timestamps, and duration are recorded to
`raw/scaleup-timing.json` (and, later, `raw/scaledown-timing.json`).

---

## 1. Scale-up: M80 → M200

### 1.1 Exact command
```
az cosmosdb mongocluster update -g rg-db-test-hpc -c docdb-dbtest-hpc-1 \
  --shard-node-tier M200 --shard-node-ha true --shard-node-disk-size-gb 512
```
*(HA and shard/disk parameters are re-asserted so the transition changes **only** the compute tier;
shard count = 2 and HA = true are preserved.)*

### 1.2 Control-plane timing (from `raw/scaleup-timing.json`)
| Field | Value |
|-------|-------|
| Command start (UTC) | **2026-08-20T04:08:40.97Z** |
| Command return (UTC) | **2026-08-20T04:34:27.13Z** |
| **Control-plane duration** | **1,546.2 s ≈ 25.8 min** |
| az exit code | 0 (success) |
| Resulting SKU | M200 (verified), HA=true, 2 shards preserved |

### 1.3 Client-visible behavior (probe segments, 30 conn/s throughout)

| Seg | Window (UTC) | Err % | Failed | Error types | Cold-conn open P99 | demand→ready P99 | Task-cycle P99 | Verdict |
|-----|--------------|------:|-------:|-------------|-------------------:|-----------------:|---------------:|---------|
| 01 | 04:07:38 – 04:12:40 | 0.00 | 0 | — | 18.7 ms | 29.5 ms | 2,053 ms | clean (baseline) |
| 02 | 04:12:42 – 04:17:44 | 0.00 | 0 | — | 18.3 ms | 29.0 ms | 2,052 ms | clean |
| 03 | 04:17:50 – 04:23:00 | **2.52** | 227 | ThrottlingOrRateLimit=3, QueryFailure=224 | 21.9 ms | 39.8 ms | 15,420 ms | disruption onset |
| 04 | 04:23:01 – 04:28:04 | **14.48** | 1,303 | **ConnectionFailure=44**, QueryFailure=1,259 | 19.8 ms | **529 ms** (P999 3,079 ms) | 24,338 ms | **acute window** |
| 05 | 04:28:05 – 04:33:08 | 0.00 | 0 | — | 19.0 ms | 28.8 ms | 2,129 ms | recovered |
| 06 | 04:33:09 – 04:38:xx | 0.00 | 0 | — | 18.9 ms | 28.9 ms | 2,113 ms | clean |

Raw per-segment artifacts: `raw/scaleup/seg-01..06/aggregate.json` (+ per-second `iter-01/*.json`).

### 1.4 Interpretation — actual application-visible downtime

- **There was no full connection outage.** The probe kept **establishing new connections at 30/s through
  the entire transition** — cold-connection *open* P99 stayed **~18–22 ms** in every segment, including the
  acute one. New TCP/TLS/auth was serviced normally throughout.
- **The customer-visible impact was a ~10-minute window of elevated errors (04:17:50 – 04:28:04),** with a
  **~5-minute acute sub-window (seg-04, 04:23–04:28)** at **14.5 % errors**. Failures were overwhelmingly
  **`QueryFailure` (in-flight operations dropped)** plus a **brief burst of 44 `ConnectionFailure`** — the
  signature of an **HA failover / replica-set reconfiguration**, where existing sessions are reset and a
  short server-selection blip occurs, rather than the endpoint refusing connections.
- **`demand→ready` P99 rose to 529 ms (P999 3.08 s) only in seg-04** — a transient increase in the time
  for a *new* connection to become usable, consistent with a leader/replica handover — then returned to
  ~29 ms.
- **The data plane recovered by 04:28:05, roughly 6 minutes before the control plane reported `Succeeded`
  (04:34:27).** ⇒ **The 25.8-minute control-plane duration massively overstates customer-visible impact.**
  Reporting the operation runtime as "downtime" would be misleading by ~2.5×.

**Bottom line (scale-up):** application-visible degradation ≈ **~10 min elevated errors / ~5 min acute**,
**no sustained inability to establish new connections**, driven by an HA reconfiguration rather than an
endpoint outage. No `retrywrites` (write retries off); no driver-level write-retry masking occurred.

---

## 2. Scale-down: M200 → M30  *(pending — cost/downtime observation only)*

> To be executed after the three M200 workload runs, using the same probe method. Will record:
> exact CLI, start/end timestamps, control-plane duration, per-segment error/latency table, the actual
> connection-interruption window, and an application-visible-downtime interpretation.
>
> **Note:** M200 → M30 is a large **downgrade** and may exhibit a longer and/or more disruptive window
> than the scale-up; it will be captured fully. **M30 behavior is excluded from the M80-vs-M200
> production-sizing recommendation** and is retained solely as a cost-saving + tier-change-downtime
> observation.

---

## Cross-cutting notes
- Probe rate (30 conn/s) is intentionally modest so the measurement reflects **transition behavior**, not
  load — it isolates connectivity/interruption from throughput effects.
- Timestamps across probe segments and the timed CLI are all UTC and directly comparable.
- The resource was **never deleted**; **no load-generator VM was stopped or deallocated**; shard count,
  HA, private endpoint, and NSG configuration were **not** modified — only `--shard-node-tier` changed.
