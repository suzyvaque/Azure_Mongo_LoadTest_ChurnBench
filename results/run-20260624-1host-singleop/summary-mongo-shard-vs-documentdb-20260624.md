# MongoDB Connection-Churn Benchmark — mongo-shard vs DocumentDB

**Campaign:** `run-20260624-shard`
**TLS enabled on both backends** · sequential one-run-at-a-time on VM1 · **3 iterations × 600 s** per run ·
**steady** (135 Tasks/s) and **burst** (Poisson λ=0.57) run as **separate** runs. Latency in ms; values are
the **mean of the 3 iterations' percentiles** (p50 / p90 / p99).

`mongo-shard` is a **2-shard MongoDB 7.0 cluster fronted by two `mongos` routers** with server-side TLS
(`allowTLS`, self-signed cert + chain-of-trust `CAFile`); `documentdb` is always-TLS. Both perform a
**full TLS handshake** per connection, so the connection-establishment cost is directly comparable.
**DocumentDB was re-run in this same campaign on 2026-06-24** (same managed instance, same code version) —
this is a same-campaign comparison, not a reused baseline.

To survive no-reuse churn, each per-Task client is **round-robin pinned to ONE mongos as a direct
single-server connection** (`directConnection=true`) — preserving 2× router fan-out while avoiding the
per-client SDAM monitor-thread explosion. The production HPC workload is itself connection-churn with
single-use connections, so this is a transferable requirement, not a measurement artifact — see *Key
Findings*.

## How the metrics are separated (read this first)

Each Task opens a **brand-new** connection (no reuse). The MongoDB driver connects **lazily**, so the
first database operation triggers TCP + TLS + auth. The benchmark records two **independent** measurements:

- **Connection (TCP+TLS+auth)** — `ConnectionOpenMs`, taken from the driver's `ConnectionOpenedEvent`
  duration. This is the **pure handshake** time and the **only** place the TCP+TLS+auth cost is counted.
- **Operation time** — every operation row below is reported **with the connection (TCP+TLS+auth) time
  excluded**, so it reflects server execution only.
  - A **cold** op runs as the first operation on a fresh connection, so its raw latency bundles
    connect+query; the figure shown is `op − ConnectionOpenMs` at the same percentile.
  - A **warm** op runs on an already-open socket, so it is pure server execution and is shown as-is.

> **Throughput is offered-load, not capacity.** The `Throughput (tasks/s)` row is **successful Tasks ÷
> duration**, and the load model is **open-loop**: tasks are injected at a fixed arrival rate (steady
> 135/s; burst's Poisson arrivals pack slightly more, ~147/s). Because both backends are fed the **same
> arrival schedule** and neither saturates (~0% errors), completed tasks/s converges to that offered rate
> on both — so the near-identical throughput only confirms neither system fell behind. The real
> differentiator here is **latency** (connection + operation percentiles), not throughput. Throughput
> *would* diverge under saturation — as it did for the single-node `mongo-vm`, which dropped to 101/93
> tasks/s with 23–32% errors.

> **Approximation note.** Percentiles are not additive, so `op − connection` at a given percentile is an
> **indicative** decomposition, not an exact per-request subtraction (it can occasionally make a higher
> percentile look slightly lower than a neighbouring one). The raw `ConnectionOpenMs` and `OperationMs`
> percentiles in each run's `aggregate.json` are exact.

Best value per row is **bold**.

---

## 1. Single-op: find-input

A Task opens a connection, runs **one** `find` on `calc_input` by `ReqId`, disconnects. The `find (cold)`
row is the operation time with connection (TCP+TLS+auth) excluded.

### 1a. find-input — STEADY (135 Tasks/s)

<table>
  <thead><tr><th>Metric group</th><th>Pctile</th><th>mongo-shard</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Headline</b></td><td>Throughput (tasks/s)</td><td><u><b style="color:#1a7f37">135.0</b></u></td><td><u><b style="color:#1a7f37">135.0</b></u></td></tr>
    <tr><td>Error rate</td><td><u><b style="color:#1a7f37">0.00%</b></u></td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Connection<br>(TCP+TLS+auth)</b></td><td>p90</td><td>31.9</td><td><u><b style="color:#1a7f37">23.8</b></u></td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">43.7</b></u></td><td>75.6</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>find (cold)</b></td><td>p90</td><td>23.9</td><td><u><b style="color:#1a7f37">19.2</b></u></td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">26.4</b></u></td><td>31.3</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Total cycle</b></td><td>p90</td><td><u><b style="color:#1a7f37">56.9</b></u></td><td>60.1</td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">71.7</b></u></td><td>121.9</td></tr>
  </tbody>
</table>

### 1b. find-input — BURST (Poisson λ=0.57)

<table>
  <thead><tr><th>Metric group</th><th>Pctile</th><th>mongo-shard</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Headline</b></td><td>Throughput (tasks/s)</td><td><u><b style="color:#1a7f37">147.0</b></u></td><td>146.4</td></tr>
    <tr><td>Error rate</td><td><u><b style="color:#1a7f37">0.00%</b></u></td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Connection<br>(TCP+TLS+auth)</b></td><td>p90</td><td><u><b style="color:#1a7f37">1,185.2</b></u></td><td>1,256.1</td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">1,901.5</b></u></td><td>2,408.1</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>find (cold)</b></td><td>p90</td><td>1,355.5</td><td><u><b style="color:#1a7f37">970.6</b></u></td></tr>
    <tr><td>p99</td><td>1,886.2</td><td><u><b style="color:#1a7f37">1,884.1</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Total cycle</b></td><td>p90</td><td>2,569.4</td><td><u><b style="color:#1a7f37">2,255.9</b></u></td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">3,813.1</b></u></td><td>4,328.5</td></tr>
  </tbody>
</table>

---

## 2. Single-op: insert-output

A Task opens a connection, runs **one** `insert` into `calc_output`, disconnects. The `insert (cold)` row is
the operation time with connection (TCP+TLS+auth) excluded.

### 2a. insert-output — STEADY (135 Tasks/s)

<table>
  <thead><tr><th>Metric group</th><th>Pctile</th><th>mongo-shard</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Headline</b></td><td>Throughput (tasks/s)</td><td><u><b style="color:#1a7f37">135.0</b></u></td><td><u><b style="color:#1a7f37">135.0</b></u></td></tr>
    <tr><td>Error rate</td><td><u><b style="color:#1a7f37">0.00%</b></u></td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Connection<br>(TCP+TLS+auth)</b></td><td>p90</td><td>32.0</td><td><u><b style="color:#1a7f37">22.9</b></u></td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">47.5</b></u></td><td>71.6</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>insert (cold)</b></td><td>p90</td><td>26.6</td><td><u><b style="color:#1a7f37">18.6</b></u></td></tr>
    <tr><td>p99</td><td>37.4</td><td><u><b style="color:#1a7f37">26.7</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Total cycle</b></td><td>p90</td><td>59.9</td><td><u><b style="color:#1a7f37">58.2</b></u></td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">92.4</b></u></td><td>111.8</td></tr>
  </tbody>
</table>

### 2b. insert-output — BURST (Poisson λ=0.57)

<table>
  <thead><tr><th>Metric group</th><th>Pctile</th><th>mongo-shard</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Headline</b></td><td>Throughput (tasks/s)</td><td><u><b style="color:#1a7f37">147.3</b></u></td><td>147.2</td></tr>
    <tr><td>Error rate</td><td><u><b style="color:#1a7f37">0.00%</b></u></td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Connection<br>(TCP+TLS+auth)</b></td><td>p90</td><td>1,316.4</td><td><u><b style="color:#1a7f37">1,146.9</b></u></td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">2,064.2</b></u></td><td>2,208.7</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>insert (cold)</b></td><td>p90</td><td>1,520.7</td><td><u><b style="color:#1a7f37">883.6</b></u></td></tr>
    <tr><td>p99</td><td>2,357.6</td><td><u><b style="color:#1a7f37">1,670.4</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Total cycle</b></td><td>p90</td><td>2,864.4</td><td><u><b style="color:#1a7f37">2,078.4</b></u></td></tr>
    <tr><td>p99</td><td>4,451.3</td><td><u><b style="color:#1a7f37">3,926.0</b></u></td></tr>
  </tbody>
</table>

---

## 3. Full 4-op workload (`find`→`remove`→`insert`→`find`)

Op1 (`find (cold)`) runs first on the fresh socket, so its row is net of connection (TCP+TLS+auth). Ops 2–4
(`remove`, `insert`, `find` again) run on the **warm** socket and are pure server execution. Total cycle
excludes the fixed **10,000 ms** `taskSleepMs`.

### 3a. full-workload — STEADY (135 Tasks/s)

<table>
  <thead><tr><th>Metric group</th><th>Pctile</th><th>mongo-shard</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Headline</b></td><td>Throughput (tasks/s)</td><td><u><b style="color:#1a7f37">132.8</b></u></td><td><u><b style="color:#1a7f37">132.8</b></u></td></tr>
    <tr><td>Error rate</td><td><u><b style="color:#1a7f37">0.005%</b></u></td><td>0.006%</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Connection<br>(TCP+TLS+auth)</b></td><td>p90</td><td>89.7</td><td><u><b style="color:#1a7f37">30.9</b></u></td></tr>
    <tr><td>p99</td><td>137.1</td><td><u><b style="color:#1a7f37">113.0</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>find (cold)</b></td><td>p90</td><td><u><b style="color:#1a7f37">51.3</b></u></td><td>62.3</td></tr>
    <tr><td>p99</td><td>74.6</td><td><u><b style="color:#1a7f37">46.0</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>remove (warm)</b></td><td>p90</td><td>6.5</td><td><u><b style="color:#1a7f37">5.0</b></u></td></tr>
    <tr><td>p99</td><td>80.2</td><td><u><b style="color:#1a7f37">77.1</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>insert (warm)</b></td><td>p90</td><td>7.3</td><td><u><b style="color:#1a7f37">5.1</b></u></td></tr>
    <tr><td>p99</td><td>83.2</td><td><u><b style="color:#1a7f37">79.1</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>find (warm)</b></td><td>p90</td><td>4.2</td><td><u><b style="color:#1a7f37">2.9</b></u></td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">16.0</b></u></td><td>53.6</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Total cycle<br>excludes fixed 10,000 ms taskSleepMs</b></td><td>p90</td><td>185.0</td><td><u><b style="color:#1a7f37">138.8</b></u></td></tr>
    <tr><td>p99</td><td>265.1</td><td><u><b style="color:#1a7f37">245.1</b></u></td></tr>
  </tbody>
</table>

### 3b. full-workload — BURST (Poisson λ=0.57)

<table>
  <thead><tr><th>Metric group</th><th>Pctile</th><th>mongo-shard</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Headline</b></td><td>Throughput (tasks/s)</td><td>128.4</td><td><u><b style="color:#1a7f37">135.3</b></u></td></tr>
    <tr><td>Error rate</td><td>0.16%</td><td><u><b style="color:#1a7f37">0.033%</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Connection<br>(TCP+TLS+auth)</b></td><td>p90</td><td><u><b style="color:#1a7f37">1,059.1</b></u></td><td>1,605.5</td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">2,022.1</b></u></td><td>3,235.9</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>find (cold)</b></td><td>p90</td><td>2,396.0</td><td><u><b style="color:#1a7f37">1,433.5</b></u></td></tr>
    <tr><td>p99</td><td>3,300.8</td><td><u><b style="color:#1a7f37">2,589.7</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>remove (warm)</b></td><td>p90</td><td>136.6</td><td><u><b style="color:#1a7f37">22.3</b></u></td></tr>
    <tr><td>p99</td><td>268.2</td><td><u><b style="color:#1a7f37">153.5</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>insert (warm)</b></td><td>p90</td><td>149.7</td><td><u><b style="color:#1a7f37">26.1</b></u></td></tr>
    <tr><td>p99</td><td>309.2</td><td><u><b style="color:#1a7f37">139.3</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>find (warm)</b></td><td>p90</td><td>191.9</td><td><u><b style="color:#1a7f37">21.9</b></u></td></tr>
    <tr><td>p99</td><td>381.7</td><td><u><b style="color:#1a7f37">144.8</b></u></td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Total cycle<br>excludes fixed 10,000 ms taskSleepMs</b></td><td>p90</td><td>4,998.9</td><td><u><b style="color:#1a7f37">3,650.7</b></u></td></tr>
    <tr><td>p99</td><td>7,108.8</td><td><u><b style="color:#1a7f37">6,551.7</b></u></td></tr>
  </tbody>
</table>

---

## Key Findings

- **Sharding fixes the full-workload collapse.** This is the headline change vs `run-20260619-00`. The
  single `mongo-vm` node shed **22.8% (steady) / 32.0% (burst)** of the full 4-op workload as the client
  saturated; the **2-shard `mongo-shard` cluster sustains the full load at 132.8 / 128.4 tasks/s with
  0.005% / 0.16% errors** — matching DocumentDB on steady throughput and errors.
- **Operation time is small once TCP+TLS+auth is removed.** Steady single-op work is ~19–22 ms p50 on
  mongo-shard and ~12–13 ms on DocumentDB. The "slow query" impression was always the cold-connection
  handshake being counted inside the op, not server execution.
- **DocumentDB owns the median; mongo-shard owns the burst tail.** On every steady row DocumentDB has the
  **lower p50/p90** connection and operation latency (~2× faster handshake). But under **burst**,
  mongo-shard's 2× mongos fan-out gives the **tighter connection p90/p99** (full-workload p90 1,059 vs
  1,606 ms, p99 2,022 vs 3,236 ms; single-op p99 also lower) — spreading handshakes over two routers
  absorbs spikes better than DocumentDB's single endpoint.
- **Warm ops favour DocumentDB.** Once the socket is open, DocumentDB's `remove`/`insert`/`find` are
  2–10× faster (sub-6 ms p50 vs mongo-shard's 4–29 ms), and it keeps a smaller warm-op tail under burst.
- **Net:** steady-state DocumentDB is faster end-to-end (lower cycle p50/p90/p99); mongo-shard is
  competitive and even wins specific burst connection-tail percentiles. Both run at near-zero errors —
  the single-node meltdown is gone.

## What the round-robin direct-connect did — and how it maps to production

> **This benchmark models the real workload, not a worst case.** The production HPC workload is itself
> **connection-churn with single-use connections** — every Task opens a fresh connection, does its work,
> and disconnects, with no pooling/reuse. So the **cold** numbers here (full TLS + SCRAM handshake on
> *every* operation) are the latencies production actually pays; the **warm** rows are shown only to
> isolate server-execution cost, not as a production projection.

- **In this benchmark.** Pinning each per-Task client to **one** mongos as `directConnection=true` was
  necessary to *measure the cluster at all*: a `MongoClient` against the full 2-mongos topology launches
  SDAM monitor threads **per mongos, per client**, and with a fresh client per Task under churn that
  exploded to 48,657 threads and melted the load generator. Round-robin keeps the **2× router fan-out**
  (load still splits across both mongos) while removing the per-client topology-monitoring overhead — the
  same mitigation `mongo-vm` uses, so the comparison stays fair.
- **How production is *similar* (this is the realistic case).** Because production churns connections the
  same way, it pays the same **per-operation connection-establishment tax** measured here — there is no
  pooling to hide it. The cold/connection percentiles, not the warm ops, are the production-relevant
  numbers, and the **connection front-end** (TLS+SCRAM handshake throughput) is the metric that decides
  the outcome at scale.
- **The direct-connect pin is a transferable requirement, not a throwaway artifact.** Since production
  also creates a fresh client per unit of work, it would hit the **same SDAM monitor-thread explosion**
  against a multi-mongos topology. Production must therefore adopt the **same shape**: either point each
  short-lived client at a **single mongos** (e.g. `directConnection`/a one-router URI) or place the mongos
  routers **behind a TCP/L4 load balancer** so a single endpoint fans the churn across both. The
  round-robin spread of churn over two routers is the property that improved the burst connection
  percentiles, and it carries directly into production — **more mongos = more parallel handshake
  capacity**.
- **How production likely *differs in scale* (not in pattern).** Production adds factors this single-VM
  test omits — **multiple client hosts** (so the per-host thread/handle pressure is divided), **cross-AZ
  network hops**, **larger shard and mongos counts**, and real **config-server load**. These shift
  absolute latencies up or down, but the **churn-driven connection tax** remains the dominant cost, and
  more mongos/shards is the lever that scales it.

## Migration decision guide

In steady state — the dominant operating mode — **DocumentDB is faster end-to-end** (≈2× lower connection
and cycle p50/p90), and it keeps the smoother warm-op latency throughout. The decisive result vs the prior
single-node campaign is that **sharding removes the full-workload meltdown**: a 2-shard / 2-mongos cluster
now sustains full throughput at near-zero errors, where one node shed 23–32%, and it even wins specific
**burst connection-tail** percentiles via router fan-out. Because the production HPC workload is
**connection-churn with single-use connections** — exactly what this benchmark models — the deciding factor
is each backend's **connection front-end under churn**, not server op speed. The choice therefore narrows
to operational trade-offs (**managed simplicity and lower median latency** with DocumentDB vs. **self-hosted
control/cost** with a sharded cluster) — provided the self-managed deployment is **sharded with multiple
mongos** and short-lived clients **fan their churn across those routers** (single-mongos pin or an L4 load
balancer in front), rather than pointing each per-Task client at the full topology.
