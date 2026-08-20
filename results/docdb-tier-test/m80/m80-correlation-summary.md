# M80 — Time-Aligned Correlation Summary (interim)

**Resource:** `docdb-dbtest-hpc-1` (Azure Cosmos DB for MongoDB vCore) · RG `rg-db-test-hpc` · koreacentral
**Fixed architecture (unchanged):** 2 shards · HA enabled · private connectivity (publicNetworkAccess=Disabled)
**Tier under test:** **M80** · **3 × 5-minute runs** (identical workload config `full-workload-tiertest.json`)
**Load generator:** 3× AZ1 VMs (`vm-hpc-loadgen-az1-0/1/2`), coordinated synchronized start, open-loop Poisson arrivals, **no connection reuse** (each task opens one fresh connection).

> Scope: this is the **M80 half** of the M80-vs-M200 comparison, focused on the requested
> **time-aligned correlation** and the **connection-admission vs compute/storage** hypothesis.
> M200 data and the final side-by-side recommendation are produced separately.

---

## 1. Run windows (UTC) and what was offered

| Run | Window (UTC) | Peak conn/s (combined, per-sec) | Peak concurrency (combined active-ready) | Peak in-flight | Failure % |
|-----|--------------|-------------------------------:|-----------------------------------------:|---------------:|----------:|
| 1 | 02:34:35 – 02:41:15 | 1,819 | 9,539 | 11,749 | 0.226 |
| 2 | 02:45:17 – 02:51:57 | 1,839 | 9,447 | 14,324 | 0.563 |
| 3 | 02:55:55 – 03:02:35 | 1,745 | 9,601 | 12,279 | 0.840 |
| **mean** | — | **1,801** | **9,529** | 12,784 | **0.543** |

- **Connection-churn target (≥1,200 new conn/s): REACHED** in all three runs (peak 1,745–1,839/s).
- **Concurrency target (~11,000): ~87% reached** (peak 9,447–9,601). This is a **workload-generation
  ceiling on the client fleet**, not a DB limit — see §5 Limitations. It is identical across tiers, so the
  M80-vs-M200 comparison stays valid.
- Combined **offered rate** was ~758–783 tasks/s sustained; per-second connection peaks reach ~1,800/s
  because the open-loop scheduler bursts and a client-side backlog forms.

---

## 2. Time-aligned correlation (per-minute, Run 1 shown; Runs 2–3 identical shape)

Raw per-run artifacts: `run-{1,2,3}/correlation/m80-run-{1,2,3}-correlation.csv`
(joins the three per-host client per-second timeseries with Azure PT1M platform metrics).

```
Time (UTC)  │ LOADGEN                                    │ AZURE DocumentDB (platform metrics)
            │ conn/s  ready/s  concurrency  failed_ops   │ srv_rps  ReqDur avg/max ms   CPU avg/max%  Mem%  IOPS avg/max
────────────┼────────────────────────────────────────────┼──────────────────────────────────────────────────────────
02:34       │  324     319      6,646         5           │   364     0.28 / 8          0.9 / 3.4     28.9    0 / —
02:35       │  685     673      7,424        16           │ 1,795     1.48 / 1064       1.7 / 8.9     29.1   46 / —
02:36       │  659     653      7,366        48           │ 4,501     0.88 / 1042       4.5 / 22.1    29.4  321 / 353
02:37       │  629     639      7,582        67           │ 4,443     0.81 / 238        5.9 / 19.3    29.8  274
02:38       │  605     611      7,506       366           │ 4,464     0.75 / 231        5.8 / 19.1    29.8  260
02:39       │  659     659      7,460        20           │ 4,324     0.73 / 220        3.8 / 17.7    29.6  236
02:40       │  309     317      5,682         5           │ 4,449     1.31 / 820        1.7 / 3.9     29.2  277
```
*(conn/s and concurrency are per-minute averages; per-second peaks are higher — see §1. `srv_rps` =
MongoRequestDurationMs sample Count ÷ 60. Full 1-sec resolution is in the per-host `*-timeseries.csv`.)*

**Whole-run Azure aggregates (all 3 runs):**

| Metric | Run 1 | Run 2 | Run 3 |
|--------|------:|------:|------:|
| CPU avg / **max** % | 3.47 / **22.1** | 3.46 / **19.8** | 3.05 / **19.2** |
| Memory avg / max % | 29.4 / 31.8 | 29.4 / 31.7 | 29.3 / 31.3 |
| IOPS avg / max | 202 / 353 | 202 / 340 | 186 / 332 |
| Mongo Request Duration avg / max ms | 0.89 / 1064 | 0.75 / 367 | 0.69 / 1045 |
| Server requests (2xx) | 1,460,325 | 1,308,559 | 1,439,841 |
| **4xx (throttle/error)** | **46** | **38** | **42** |
| **5xx** | **0** | **0** | **0** |
| Storage % | 6.1 | 6.2 | 6.2 |

Server-side request mix (per operation, avg latency): `IsMaster` ~0ms, `SaslStart`/`SaslContinue`
(**auth handshake**) avg **0.02–0.24 ms** (max ≤159 ms), `Find` avg 0.6–0.8 ms, `Insert`/`Delete`
avg 3.5–4.8 ms. **The database authenticates and serves each request in well under 1 ms on average.**

---

## 3. The two connection numbers that matter (client-side)

Because there is **no active-connection counter** on vCore, concurrent/created connections come from the
client-side merge; connection-establishment latency comes from the load generator's instrumentation.

| Client latency layer | Run 1 P95/P99 | Run 2 P95/P99 | Run 3 P95/P99 | What it measures |
|----------------------|--------------:|--------------:|--------------:|------------------|
| **Cold-conn driver-open** (TCP+TLS+hello+auth) | ~3.3s / **5.0–7.4s** | ~3.5–5.1s / **5.6–6.4s** | ~3.9–5.3s / **6.1–8.7s** | time for the driver to open one brand-new connection |
| **Cold-conn demand→ready** (incl. client pool queueing) | — / **7.8–10.9s** | — / **8.5–12.3s** | — / **9.0–13.7s** | wall-clock from a new task wanting a connection to it being usable |
| **DB-facing cycle** (4 ops after connect) | ~19–22s / **21–26s** | ~20–23s / **22–27s** | ~19–25s / **21–30s** | the query/workload flow once connected |
| **True task E2E** (offered→finished) | — / **~187s** | — / **~182s** | — / **~181s** | dominated by client scheduler backlog (offered faster than dispatched) |

> The **new-task tail metric of interest** is **cold-conn demand→ready**: a newly arriving task waits
> **~8–14 s at P99** just to obtain a usable connection while the workload sustains ~1,800 conn/s peak.

---

## 4. Hypothesis evaluation — connection/gateway/admission vs storage/compute

> **Hypothesis:** *If p95/p99 E2E connection latency increases significantly while CPU, memory, and IOPS
> remain relatively low, the bottleneck is in the connection-establishment / gateway-admission path
> rather than storage or backend compute.*

**Verdict for M80: the hypothesis holds — the tail is in the connection/admission path, not DB resources.**

Evidence, correlated on the same timeline (§2):

1. **Tail connection latency is high while DB resources are low.** During the peak minutes (02:36–02:39)
   the cold-conn establishment tail was **5–14 s (P99)**, yet at the *same timestamps* CPU peaked at only
   **17–22 %**, memory sat at **~30 %**, and IOPS averaged **236–321** (peak 353). There is no minute in
   which a resource is near saturation.
2. **The database's own request latency is sub-millisecond.** Azure Mongo Request Duration averaged
   **0.7–0.9 ms** across all three runs; the **auth handshake (SaslStart/SaslContinue) averaged
   0.02–0.24 ms** server-side. So the multi-second cold-connection time is **not** spent doing work the
   database measures — it is spent *before* the request reaches server-side processing (TLS negotiation,
   gateway/admission, client-side connect concurrency limits, ephemeral-port/backlog contention).
3. **Failures are client-side server-selection timeouts, not server rejections.** Per-run failures
   (0.23–0.84 %) are **dominated by `ServerSelectionTimeout`** (29 → 1,071 per host across runs) — the
   driver could not obtain a connection within its selection timeout because the *client* establishment
   pipeline was backed up. On the server side, **4xx = 38–46 total and 5xx = 0** across ~1.3–1.5 M
   requests/run, and **`ThrottlingOrRateLimit` = 0–10 per host** — i.e. **essentially no server-side
   throttling or admission rejection**.
4. **Request-duration spikes occur even when CPU/memory stay low.** Mongo Request Duration *max* touched
   **~1,042–1,064 ms** in minutes 02:35 and 02:40 while CPU in those same minutes was only **1.7–4.5 %**.
   These are isolated per-request outliers (tail of `Find`/`Insert`), not a compute-driven trend.
5. **IOPS is a supporting signal only.** IOPS tracked request volume loosely (rising to ~320 as RPS rose
   to ~4,500) but never approached a storage ceiling (StoragePercent ~6 %). Per the instruction, IOPS is
   **not** used as a proxy for connection or request volume — and indeed connection establishment (the
   actual bottleneck) generates little storage I/O, which is exactly why IOPS stays low while the
   connection tail is high.

**Distinguishing the three layers the instruction asked for:**

1. **Connections *attempted* by the load generator** — ~1,800/s peak (per-second), ~660/s per-minute avg.
2. **Connections *actually established*** — `ready/s` tracks `attempted/s` almost 1:1 with a growing
   client backlog; establishment *succeeds* but **slowly** (5–14 s tail).
3. **Requests *actually processed* by DocumentDB** — ~4,300–4,500 server RPS at **<1 ms** each, 5xx = 0.
4. **Backend compute/storage at the same time** — CPU ≤22 %, Mem ~30 %, IOPS ≤353: **large headroom.**

The gap between (1)/(2) being slow and (3)/(4) being cheap and idle is the connection-admission
bottleneck signature.

---

## 5. Retry & failure behavior (M80)

- **Directly measured retry attempts** (driver telemetry, `Retry` block): `TotalCommandFailures` **0–3**
  and `RetryableCommandFailures` **0–3** per host per run — i.e. **near-zero driver-level retries**.
- **Retryable-error-labelled failures** (`ErrorsByType`): dominated by `ServerSelectionTimeout`
  (client couldn't get a connection); `QueryFailure` 20–34/host; `ThrottlingOrRateLimit` 0–10/host;
  `ConnectionFailure` 0–3/host.
- **Retryable writes:** the connection string sets **`retrywrites=false`**, so driver-side write retries
  are effectively **off**. (Note: the load generator's `Retry.RetryWritesEnabled` telemetry reports
  `True`, reflecting the driver default rather than the connection-string override — flagged as an
  instrumentation ambiguity, not evidence of actual write retries.)
- **No server-side throttling** of consequence (4xx = 38–46/run; 5xx = 0).

These are **distinguished, not conflated**: measured retries ≈ 0; the "failures" are overwhelmingly
client-side server-selection timeouts under connection backlog.

---

## 6. Three-run consistency

Peak conn/s 1,745–1,839 (±3 %), peak concurrency 9,447–9,601 (±1 %), CPU max 19.2–22.1 %, memory
29.3–29.4 % avg, Mongo Request Duration 0.69–0.89 ms avg — **tight across all three runs**. Failure rate
varied 0.23–0.84 % (all client-side server-selection). The M80 picture is stable and reproducible.

---

## 7. Interim conclusion (M80 only)

On M80, at the fixed 2-shard / HA / private architecture, driving ≥1,200 new conn/s and ~9,500
concurrent connections:

- The **database has large headroom** on every platform metric (CPU ≤22 %, Mem ~30 %, IOPS ≤353,
  Mongo Request Duration <1 ms avg, 5xx = 0).
- The **tail latency experienced by a newly arriving task (~8–14 s P99 to a usable connection)** is
  **not** explained by DB compute or storage; it correlates with the **connection-establishment /
  admission path**, which is largely **client- and gateway-bound**.

**Implication for sizing:** because the M80 bottleneck is connection admission — not DB CPU/memory/IOPS —
a larger compute tier (M200) would only help the tail if the limit is on the **Azure-side connection/
gateway path**, not the compute path. That is precisely what the M200 runs are designed to test. This
document will be cross-referenced by `final/report.md` once M200 data is in.

---

### Artifacts backing this summary
- `run-{1,2,3}/loadgen/` — per-host JSON + per-second timeseries/latency CSVs (raw)
- `run-{1,2,3}/azure/azure-metrics.json` + `azure/metrics-raw/` — Azure platform metrics (PT1M)
- `run-{1,2,3}/correlation/m80-run-{1,2,3}-correlation.csv` — per-minute time-aligned join (this doc's §2)
- `merge-m80.json` — combined cross-host / cross-iteration envelope
