# MongoDB Connection-Churn Benchmark — OPEN-LOOP: documentdb vs mongo-shard

**Campaign:** `run-20260724-openloop`
**TLS enabled on both backends** · sequential one-run-at-a-time · **single generator host** (`HostCount=1`) ·
**3 iterations × 300 s** arrival window per run · **open-loop burst** (seed-deterministic Poisson schedule,
~127.9 Tasks/s offered, injected **ungated**) · full 4-op workload with a fixed **2,000 ms** `taskSleepMs`.
Latency in ms; values are the **mean of the 3 iterations' percentiles** (p50 / p90 / p99).

`mongo-shard` is a **2-shard MongoDB 7.0 cluster fronted by two `mongos` routers** (`10.3.0.4:27016`,
`10.3.0.6:27017`) with server-side TLS (`allowTLS`, self-signed cert + chain-of-trust `CAFile`) and SCRAM
auth; `documentdb` is an **Azure Cosmos DB for MongoDB vCore M80** cluster, always-TLS with SCRAM-SHA-256.
Both perform a **full TLS handshake + SCRAM auth** on every connection, so the connection-establishment cost
is directly comparable. Both runs are same-day (2026-07-24), same code version, fed the **same arrival
schedule**.

To survive no-reuse churn against the sharded topology, each per-Task client is **round-robin pinned to ONE
mongos as a direct single-server connection** (`directConnection=true`) — preserving 2× router fan-out while
avoiding the per-client SDAM monitor-thread explosion (see `INCIDENT-runaway-concurrency-meltdown.md`).

**Raw data:** `results/run-20260724-openloop/docdb/` and `results/run-20260724-openloop/mongo/` — each with
`iter-01..03/` (per-iteration `*.json`, timeseries/latency/target-tcp CSVs) and `aggregate.json`. Layout
follows the grouped `run-{date}/<target>/iter-NN/` convention (Item 3).

## How the metrics are separated (read this first)

Each Task opens a **brand-new** connection (no reuse). The MongoDB driver connects **lazily**, so the first
database operation triggers TCP + TLS + auth. The benchmark records two **independent** measurements:

- **Connection (TCP+TLS+auth)** — `ConnectionOpenMs`, taken from the driver's `ConnectionOpenedEvent`
  duration. This is the **pure handshake** time and the **only** place the TCP+TLS+auth cost is counted.
- **Operation time** — every operation row is reported **with the connection (TCP+TLS+auth) time excluded**,
  so it reflects server execution only.
  - A **cold** op (`find (cold)` = op 1) runs as the first operation on a fresh connection, so its raw
    latency bundles connect+query; the figure shown is `find_input − ConnectionOpenMs` at the same percentile.
  - **warm** ops (`remove`/`insert`/`find` = ops 2–4) run on the already-open socket and are shown as-is.

> **Throughput is offered-load, not capacity.** The load model is **open-loop**: tasks are injected at the
> fixed Poisson arrival schedule (~127.9/s offered) regardless of backend completion speed. Because both
> backends are fed the **same** schedule and neither saturates (< 0.4% errors), completed tasks/s converges
> to the offered rate on both — so the near-identical throughput only confirms neither system fell behind.
> The real differentiators are **latency**, **connection concurrency**, and **resource cost**.

> **Approximation note.** Percentiles are not additive, so `op − connection` at a given percentile is an
> **indicative** decomposition, not an exact per-request subtraction. The raw `ConnectionOpenMs` and
> `OperationLatencyMs` percentiles in each run's `aggregate.json` are exact.

> **Total cycle includes the fixed 2,000 ms `taskSleepMs`** (calc-time substitute) — it is the raw per-Task
> stopwatch (connect → 4 ops + sleep → disconnect), NOT net of the sleep.

Best value per row is **bold**.

---

## 1. Full 4-op workload — OPEN-LOOP burst (~127.9 Tasks/s offered)

<table>
  <thead><tr><th>Metric group</th><th>Pctile</th><th>documentdb</th><th>mongo-shard</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Headline</b></td><td>Throughput (tasks/s)</td><td><u><b style="color:#1a7f37">126.1</b></u></td><td>125.4</td></tr>
    <tr><td>Error rate</td><td><u><b style="color:#1a7f37">0.014%</b></u></td><td>0.371%</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Connection<br>(TCP+TLS+auth)</b></td><td>p90</td><td>1,229.4</td><td><u><b style="color:#1a7f37">758.9</b></u></td></tr>
    <tr><td>p99</td><td>2,879.8</td><td><u><b style="color:#1a7f37">1,626.3</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>find (cold)</b></td><td>p90</td><td><u><b style="color:#1a7f37">1,138.1</b></u></td><td>1,611.0</td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">2,043.5</b></u></td><td>4,578.9</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>remove (warm)</b></td><td>p90</td><td><u><b style="color:#1a7f37">21.2</b></u></td><td>102.1</td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">145.6</b></u></td><td>359.8</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>insert (warm)</b></td><td>p90</td><td><u><b style="color:#1a7f37">18.8</b></u></td><td>111.3</td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">148.0</b></u></td><td>284.6</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>find (warm)</b></td><td>p90</td><td><u><b style="color:#1a7f37">14.0</b></u></td><td>123.0</td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">128.7</b></u></td><td>480.2</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Total cycle<br>incl. fixed 2,000 ms taskSleepMs</b></td><td>p90</td><td><u><b style="color:#1a7f37">4,836.3</b></u></td><td>5,688.4</td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">8,476.1</b></u></td><td>10,853.5</td></tr>
  </tbody>
</table>

---

## 2. CPU & Memory usage

Two independent tiers are reported: the **client (load generator)** process, sampled once per second and
captured in each run's JSON (`Process` peaks + `ResourceSamples` means); and the **server (database)** tier,
pulled post-run from Azure Monitor over the run window. Lower is better (**bold**).

### 2a. Client / load-generator host (per-run, mean of 3 iterations)

The generator is a single 32-vCore / 256 GB VM. Under no-reuse churn it is **CPU- and handle-bound**, not
RAM-bound — so these figures show which backend's handshake path costs the *client* more.

<table>
  <thead><tr><th>Resource</th><th>Aggregation</th><th>documentdb</th><th>mongo-shard</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>CPU %</b></td><td>peak</td><td><u><b style="color:#1a7f37">46.5</b></u></td><td>63.1</td></tr>
    <tr><td>mean</td><td><u><b style="color:#1a7f37">22.7</b></u></td><td>32.5</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Working set (MB)</b></td><td>peak</td><td>1,111</td><td><u><b style="color:#1a7f37">923</b></u></td></tr>
    <tr><td>mean</td><td>773</td><td><u><b style="color:#1a7f37">425</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td><b>Handles</b></td><td>peak</td><td>54,105</td><td><u><b style="color:#1a7f37">51,105</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td><b>Threads</b></td><td>peak</td><td>12,178</td><td><u><b style="color:#1a7f37">6,334</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td><b>Ephemeral ports in use</b></td><td>peak</td><td><u><b style="color:#1a7f37">11,077</b></u></td><td>13,046</td></tr>
    <tr style="border-top:2px solid #555"><td><b>TIME_WAIT sockets</b></td><td>peak</td><td><u><b style="color:#1a7f37">10,342</b></u></td><td>10,646</td></tr>
  </tbody>
</table>

- **mongo-shard costs the client more CPU** at the same offered load (peak 63.1% vs 46.5%; mean 32.5% vs
  22.7%) — the round-robin direct-connect path plus per-connection SCRAM validation is heavier on the client.
- **DocumentDB spins ~2× more threads** (12.2k vs 6.3k) — its SRV/gateway driver keeps more in-flight
  bookkeeping per connection — and carries a **larger working set** (773 vs 425 MB mean). mongo-shard is the
  leaner-footprint client despite its higher CPU.
- Both stay well under the ~55k ephemeral-port ceiling (11–13k peak), so the single host was **not**
  port-exhausted at this offered rate.

### 2b. Server / database tier (Azure Monitor, over the run window)

<table>
  <thead><tr><th>Backend</th><th>Resource</th><th>avg</th><th>peak</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>documentdb</b><br>(Cosmos vCore M80)</td><td>CPU %</td><td>2.0</td><td>5.1</td></tr>
    <tr><td>Memory %</td><td>29.0</td><td>29.7</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>mongo-shard VM1</b><br>(mongos + rs0 shard)</td><td>CPU %</td><td>22.0</td><td>38.2</td></tr>
    <tr><td>Memory used %</td><td>~2</td><td>~2</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>mongo-shard VM2</b><br>(mongos + shard2 + configsvr)</td><td>CPU %</td><td>18.5</td><td>33.3</td></tr>
    <tr><td>Memory used %</td><td>~2</td><td>~2</td></tr>
  </tbody>
</table>

- **Both database tiers are far from saturation** during the open-loop churn. DocumentDB's managed backend
  sits near-idle (CPU ~2%, peak ~5%); the self-managed mongo VMs run hotter (CPU ~18–22%, peak ~38%) because
  the **TLS handshake + SCRAM auth for every fresh connection is performed on the mongos/mongod VMs
  themselves**, whereas DocumentDB terminates that on its gateway fleet outside this metric.
- Mongo VM memory is negligible (~10 GB of the FX24ms_v2's 500 GB used ≈ 2%); DocumentDB memory is a steady
  ~29% of the M80 allowance. Neither backend is memory-bound.
- **Takeaway:** at ~128 conn/s per host the connection cost is paid on the *client* and the *mongo VMs*, not
  as a database compute limit — the headroom that the later 3-host and saturation-hold campaigns exploited.

---

## 3. Connection lifecycle logs (mean of 3 iterations)

Driver connection-monitoring events (`ConnectionEventCounters`) are the authoritative connection log: every
connection's create → ready → close is counted, and concurrency gauges are sampled per second. Under the
no-reuse model **1 Task = 1 connection lifecycle**, so created ≈ closed ≈ successful Tasks confirms no
pooling/reuse and no leak.

<table>
  <thead><tr><th>Metric group</th><th>Field</th><th>documentdb</th><th>mongo-shard</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="4"><b>Connection counts</b><br>(per iteration)</td><td>created</td><td>38,275</td><td>38,245</td></tr>
    <tr><td>ready (reached Ready)</td><td>38,275</td><td>38,245</td></tr>
    <tr><td>failed to open</td><td><u><b style="color:#1a7f37">0</b></u></td><td><u><b style="color:#1a7f37">0</b></u></td></tr>
    <tr><td>closed</td><td>38,275</td><td>38,245</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="3"><b>Peak concurrency</b><br>(driver gauges)</td><td>peak active Ready</td><td>1,164</td><td>1,300</td></tr>
    <tr><td>peak active Connecting</td><td>1,097</td><td><u><b style="color:#1a7f37">514</b></u></td></tr>
    <tr><td>peak Waiting-for-server</td><td><u><b style="color:#1a7f37">1,086</b></u></td><td>1,386</td></tr>
    <tr style="border-top:2px solid #555"><td><b>Mean concurrency</b></td><td>mean active Ready</td><td>398</td><td>452</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="3"><b>Demand→Ready</b><br>(cold-connection open: DNS/TCP/TLS/auth)</td><td>p50</td><td><u><b style="color:#1a7f37">472</b></u></td><td>854</td></tr>
    <tr><td>p90</td><td>2,349</td><td><u><b style="color:#1a7f37">2,304</b></u></td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">4,900</b></u></td><td>6,146</td></tr>
    <tr style="border-top:2px solid #555"><td><b>Reconciliation</b></td><td>created = closed (no leak)</td><td><u><b style="color:#1a7f37">yes</b></u></td><td><u><b style="color:#1a7f37">yes</b></u></td></tr>
  </tbody>
</table>

- **Zero connection-open failures on both** — every one of ~38k connections per iteration reached Ready and
  was cleanly closed; `created = ready = closed`, so the no-reuse model held with no socket leak.
- **mongo-shard sustains a higher mean/peak Ready concurrency** (452 mean / 1,300 peak vs 398 / 1,164),
  but with a **higher Waiting-for-server peak** (1,386 vs 1,086) and a **lower peak Connecting** (514 vs
  1,097) — mongo Tasks spend more time in server-selection (routing through the two mongos) while DocumentDB
  spends more in the physical connect phase.
- **DocumentDB opens cold connections faster at the median** (Demand→Ready p50 472 vs 854 ms) but the two
  converge in the p90 tail; mongo-shard's p99 is worse (6,146 vs 4,900 ms), consistent with the mongos
  server-selection queueing under burst.

---

## Key Findings

- **Neither backend saturated at ~128 conn/s per host** — < 0.4% errors, zero connection-open failures, and
  database CPU well under 40% throughout. The open-loop schedule is a controlled *input*, so throughput on
  both simply tracks the offered ~126 tasks/s; the story is in latency and resource cost.
- **DocumentDB wins the median and every warm/cold operation percentile.** Once TCP+TLS+auth is excluded,
  DocumentDB's ops are 2–8× faster (warm find p90 14 vs 123 ms), it has the faster cold-connection median
  (Demand→Ready p50 472 vs 854 ms), the lower error rate (0.014% vs 0.371%), and the lower end-to-end cycle
  (p99 8.5 s vs 10.9 s).
- **mongo-shard wins the connection (handshake) percentiles.** Its 2× mongos fan-out gives a tighter
  `ConnectionOpenMs` (p90 759 vs 1,229 ms; p99 1,626 vs 2,880 ms) — spreading handshakes over two routers
  absorbs the burst better than DocumentDB's single SRV endpoint.
- **The connection cost lands on the client and the mongo VMs, not DocumentDB's compute.** DocumentDB's
  server sits ~2% CPU because handshake/auth terminates on its managed gateway; the self-managed mongos/mongod
  VMs run 18–22% CPU doing per-connection TLS+SCRAM themselves. On the *client*, mongo-shard is the heavier
  path (peak CPU 63% vs 47%) though it holds a smaller working set and half the threads.
- **No leaks, clean no-reuse.** created = ready = closed on both, every iteration — the churn model is
  faithful and the generator stayed within port/handle limits.

## Notes & caveats

- **Single-host scope.** These runs use one generator (`HostCount=1`), so peak concurrency (~1.2–1.3k Ready)
  reflects one host's share, not the full campaign envelope. The multi-host (11k+) and saturation-hold
  (≥10k) results are separate campaigns.
- **Cold vs warm mapping to production.** The production HPC workload is itself **connection-churn with
  single-use connections**, so the **cold/connection** percentiles are the production-relevant numbers; the
  warm rows isolate server-execution cost only.
- **Server memory for mongo** is derived from the VM `Available Memory Bytes` guest metric against the
  FX24ms_v2's 500 GB (≈2% used); DocumentDB memory is the M80 `MemoryPercent` counter. These are different
  instruments and are shown for order-of-magnitude context, not exact parity.
- **Percentiles are per-run means**, not pooled across iterations; `op − connection` decompositions are
  indicative (see approximation note). Exact per-metric percentiles live in each run's `aggregate.json`.
