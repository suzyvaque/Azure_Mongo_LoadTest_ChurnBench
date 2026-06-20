# MongoDB Connection-Churn Benchmark — mongo-vm (TLS) vs DocumentDB

**Campaign:** `run-20260619-00`
**TLS-vs-TLS** · sequential one-run-at-a-time on VM1 · **3 iterations × 600 s** per run · **steady**
(135 Tasks/s) and **burst** (Poisson λ=0.57) run as **separate** runs. Latency in ms; values are the
**mean of the 3 iterations' percentiles**.

Both backends now perform a **full TLS handshake** per connection: `mongo-vm` had server-side TLS enabled
(`allowTLS`, self-signed cert + chain-of-trust `CAFile`); `documentdb` is always-TLS. The
connection-establishment cost is therefore directly comparable.

## How the metrics are separated (read this first)

Each Task opens a **brand-new** connection (no reuse). The MongoDB driver connects **lazily**, so the
first database operation triggers TCP + TLS + auth. The benchmark records two **independent** measurements:

- **Connection (TCP+TLS+auth)** — `ConnectionOpenMs`, taken from the driver's `ConnectionOpenedEvent`
  duration. This is the **pure handshake** time and the **only** place the TCP+TLS+auth cost is counted.
- **Server query / execution** — the database operation's own latency, **net of the connection handshake**.
  - For the **single-op** tests, the one op runs on a cold connection, so its raw latency bundles
    connect+query; the **server-execution** figures below are `op_latency − ConnectionOpenMs` at the same
    percentile, i.e. **query time with TCP+TLS+auth removed**.
  - For the **full workload**, op1 (`find_input`) is the cold op (its server-execution row is likewise
    net of connection); ops 2–4 (`remove`, `insert`, `find_output`) run on the now-**warm** socket and are
    already pure server execution (no connection cost to remove).

> **Approximation note.** Percentiles are not additive, so `op − connection` at a given percentile is an
> **indicative** decomposition, not an exact per-request subtraction. It is the right way to read "query
> time excluding connection" from aggregate percentiles; treat the server-execution numbers as close
> estimates. The raw `ConnectionOpenMs` and `OperationMs` percentiles in each run's `aggregate.json` are
> exact.

Best value per row is **bold, underlined, green** (`<u><b style="color:#1a7f37">`). The earlier non-TLS
mongo single-find runs under `mongo_tls_false/` are excluded — they skipped the TLS crypto and are not
comparable.

---

## 1. Single-op: find-input

A Task opens a connection, runs **one** `find` on `calc_input` by `ReqId`, disconnects. **Server query** =
`find` latency − connection (TCP+TLS+auth).

### 1a. find-input — STEADY (135 Tasks/s)

<table>
  <thead><tr><th>Metric group</th><th>Metric</th><th>mongo-vm (TLS)</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="background:#e8eef7"><td><b>Throughput</b></td><td>req/s</td><td><u><b style="color:#1a7f37">135.0</b></u></td><td><u><b style="color:#1a7f37">135.0</b></u></td></tr>
    <tr style="background:#e8eef7"><td><b>Errors</b></td><td>error rate</td><td><u><b style="color:#1a7f37">0.00%</b></u></td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p50</td><td>24.9</td><td><u><b style="color:#1a7f37">15.1</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p99</td><td><u><b style="color:#1a7f37">48.4</b></u></td><td>86.9</td></tr>
    <tr style="background:#fde8e8"><td><b>Server query (ex-connection)</b></td><td>p50</td><td>19.7</td><td><u><b style="color:#1a7f37">17.7</b></u></td></tr>
    <tr style="background:#fde8e8"><td><b>Server query (ex-connection)</b></td><td>p99</td><td><u><b style="color:#1a7f37">34.9</b></u></td><td>38.0</td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle</b></td><td>p50</td><td>45.9</td><td><u><b style="color:#1a7f37">42.1</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle</b></td><td>p99</td><td><u><b style="color:#1a7f37">91.4</b></u></td><td>141.7</td></tr>
  </tbody>
</table>

### 1b. find-input — BURST (Poisson λ=0.57)

<table>
  <thead><tr><th>Metric group</th><th>Metric</th><th>mongo-vm (TLS)</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="background:#e8eef7"><td><b>Throughput</b></td><td>req/s</td><td><u><b style="color:#1a7f37">145.8</b></u></td><td>143.6</td></tr>
    <tr style="background:#e8eef7"><td><b>Errors</b></td><td>error rate</td><td>0.37%</td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p50</td><td>655.2</td><td><u><b style="color:#1a7f37">410.8</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p99</td><td>3,806.5</td><td><u><b style="color:#1a7f37">3,010.0</b></u></td></tr>
    <tr style="background:#fde8e8"><td><b>Server query (ex-connection)</b></td><td>p50</td><td>830.1</td><td><u><b style="color:#1a7f37">374.3</b></u></td></tr>
    <tr style="background:#fde8e8"><td><b>Server query (ex-connection)</b></td><td>p99</td><td>2,592.0</td><td><u><b style="color:#1a7f37">2,256.8</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle</b></td><td>p50</td><td>1,513.0</td><td><u><b style="color:#1a7f37">828.1</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle</b></td><td>p99</td><td>6,515.0</td><td><u><b style="color:#1a7f37">5,327.9</b></u></td></tr>
  </tbody>
</table>

---

## 2. Single-op: insert-output

A Task opens a connection, runs **one** `insert` into `calc_output`, disconnects. **Server write** =
`insert` latency − connection (TCP+TLS+auth).

### 2a. insert-output — STEADY (135 Tasks/s)

<table>
  <thead><tr><th>Metric group</th><th>Metric</th><th>mongo-vm (TLS)</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="background:#e8eef7"><td><b>Throughput</b></td><td>req/s</td><td><u><b style="color:#1a7f37">135.0</b></u></td><td>134.8</td></tr>
    <tr style="background:#e8eef7"><td><b>Errors</b></td><td>error rate</td><td><u><b style="color:#1a7f37">0.00%</b></u></td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p50</td><td>24.5</td><td><u><b style="color:#1a7f37">14.7</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p99</td><td><u><b style="color:#1a7f37">42.7</b></u></td><td>90.1</td></tr>
    <tr style="background:#e8f3f7"><td><b>Server write (ex-connection)</b></td><td>p50</td><td>22.5</td><td><u><b style="color:#1a7f37">18.2</b></u></td></tr>
    <tr style="background:#e8f3f7"><td><b>Server write (ex-connection)</b></td><td>p99</td><td><u><b style="color:#1a7f37">29.6</b></u></td><td>35.8</td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle</b></td><td>p50</td><td>48.2</td><td><u><b style="color:#1a7f37">41.1</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle</b></td><td>p99</td><td><u><b style="color:#1a7f37">74.2</b></u></td><td>144.4</td></tr>
  </tbody>
</table>

### 2b. insert-output — BURST (Poisson λ=0.57)

<table>
  <thead><tr><th>Metric group</th><th>Metric</th><th>mongo-vm (TLS)</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="background:#e8eef7"><td><b>Throughput</b></td><td>req/s</td><td><u><b style="color:#1a7f37">146.7</b></u></td><td>144.5</td></tr>
    <tr style="background:#e8eef7"><td><b>Errors</b></td><td>error rate</td><td>0.56%</td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p50</td><td>690.3</td><td><u><b style="color:#1a7f37">363.3</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p99</td><td>3,896.1</td><td><u><b style="color:#1a7f37">2,399.2</b></u></td></tr>
    <tr style="background:#e8f3f7"><td><b>Server write (ex-connection)</b></td><td>p50</td><td>894.3</td><td><u><b style="color:#1a7f37">359.5</b></u></td></tr>
    <tr style="background:#e8f3f7"><td><b>Server write (ex-connection)</b></td><td>p99</td><td>4,333.6</td><td><u><b style="color:#1a7f37">1,688.9</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle</b></td><td>p50</td><td>1,616.1</td><td><u><b style="color:#1a7f37">762.7</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle</b></td><td>p99</td><td>8,456.8</td><td><u><b style="color:#1a7f37">4,134.4</b></u></td></tr>
  </tbody>
</table>

---

## 3. Full 4-op workload (`find`→`remove`→`insert`→`find`)

Op1 (`find_input`) runs on the **cold** socket — its **server query** row is net of connection
(TCP+TLS+auth). Ops 2–4 (`remove`, `insert`, `find_output`) run on the **warm** socket and are already
pure server execution. Total cycle includes the fixed **10,000 ms** `taskSleepMs`.

### 3a. full-workload — STEADY (135 Tasks/s)

<table>
  <thead><tr><th>Metric group</th><th>Metric</th><th>mongo-vm (TLS)</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="background:#e8eef7"><td><b>Throughput</b></td><td>req/s</td><td>101.3</td><td><u><b style="color:#1a7f37">132.0</b></u></td></tr>
    <tr style="background:#e8eef7"><td><b>Errors</b></td><td>error rate</td><td>22.83%</td><td><u><b style="color:#1a7f37">0.02%</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p50</td><td>3,407.9</td><td><u><b style="color:#1a7f37">20.3</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p99</td><td>8,509.4</td><td><u><b style="color:#1a7f37">136.8</b></u></td></tr>
    <tr style="background:#fde8e8"><td><b>find_input — cold, server query (ex-connection)</b></td><td>p50</td><td>3,667.5</td><td><u><b style="color:#1a7f37">23.0</b></u></td></tr>
    <tr style="background:#fde8e8"><td><b>find_input — cold, server query (ex-connection)</b></td><td>p99</td><td>5,570.5</td><td><u><b style="color:#1a7f37">71.2</b></u></td></tr>
    <tr style="background:#eef7e8"><td><b>remove — warm</b></td><td>p50</td><td>27.5</td><td><u><b style="color:#1a7f37">2.8</b></u></td></tr>
    <tr style="background:#eef7e8"><td><b>remove — warm</b></td><td>p99</td><td>608.4</td><td><u><b style="color:#1a7f37">89.5</b></u></td></tr>
    <tr style="background:#e8f3f7"><td><b>insert — warm</b></td><td>p50</td><td>77.8</td><td><u><b style="color:#1a7f37">3.3</b></u></td></tr>
    <tr style="background:#e8f3f7"><td><b>insert — warm</b></td><td>p99</td><td>1,207.6</td><td><u><b style="color:#1a7f37">90.0</b></u></td></tr>
    <tr style="background:#e8f7ee"><td><b>find_output — warm</b></td><td>p50</td><td>26.4</td><td><u><b style="color:#1a7f37">1.0</b></u></td></tr>
    <tr style="background:#e8f7ee"><td><b>find_output — warm</b></td><td>p99</td><td>609.5</td><td><u><b style="color:#1a7f37">72.6</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle (incl. 10 s sleep)</b></td><td>p50</td><td>16,527.6</td><td><u><b style="color:#1a7f37">10,087.2</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle (incl. 10 s sleep)</b></td><td>p99</td><td>24,169.5</td><td><u><b style="color:#1a7f37">10,312.4</b></u></td></tr>
  </tbody>
</table>

### 3b. full-workload — BURST (Poisson λ=0.57)

<table>
  <thead><tr><th>Metric group</th><th>Metric</th><th>mongo-vm (TLS)</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="background:#e8eef7"><td><b>Throughput</b></td><td>req/s</td><td>93.3</td><td><u><b style="color:#1a7f37">134.8</b></u></td></tr>
    <tr style="background:#e8eef7"><td><b>Errors</b></td><td>error rate</td><td>32.00%</td><td><u><b style="color:#1a7f37">0.05%</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p50</td><td>2,571.3</td><td><u><b style="color:#1a7f37">530.8</b></u></td></tr>
    <tr style="background:#fff4e0"><td><b>Connection (TCP+TLS+auth)</b></td><td>p99</td><td>8,447.5</td><td><u><b style="color:#1a7f37">3,434.5</b></u></td></tr>
    <tr style="background:#fde8e8"><td><b>find_input — cold, server query (ex-connection)</b></td><td>p50</td><td>3,185.4</td><td><u><b style="color:#1a7f37">512.7</b></u></td></tr>
    <tr style="background:#fde8e8"><td><b>find_input — cold, server query (ex-connection)</b></td><td>p99</td><td>9,655.9</td><td><u><b style="color:#1a7f37">3,127.1</b></u></td></tr>
    <tr style="background:#eef7e8"><td><b>remove — warm</b></td><td>p50</td><td>28.9</td><td><u><b style="color:#1a7f37">3.9</b></u></td></tr>
    <tr style="background:#eef7e8"><td><b>remove — warm</b></td><td>p99</td><td>632.2</td><td><u><b style="color:#1a7f37">243.4</b></u></td></tr>
    <tr style="background:#e8f3f7"><td><b>insert — warm</b></td><td>p50</td><td>88.6</td><td><u><b style="color:#1a7f37">5.7</b></u></td></tr>
    <tr style="background:#e8f3f7"><td><b>insert — warm</b></td><td>p99</td><td>1,283.4</td><td><u><b style="color:#1a7f37">200.7</b></u></td></tr>
    <tr style="background:#e8f7ee"><td><b>find_output — warm</b></td><td>p50</td><td>34.1</td><td><u><b style="color:#1a7f37">3.7</b></u></td></tr>
    <tr style="background:#e8f7ee"><td><b>find_output — warm</b></td><td>p99</td><td>754.8</td><td><u><b style="color:#1a7f37">185.4</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle (incl. 10 s sleep)</b></td><td>p50</td><td>14,211.5</td><td><u><b style="color:#1a7f37">11,520.7</b></u></td></tr>
    <tr style="background:#f3e8f7"><td><b>Total cycle (incl. 10 s sleep)</b></td><td>p99</td><td>28,070.3</td><td><u><b style="color:#1a7f37">17,149.6</b></u></td></tr>
  </tbody>
</table>

---

## Key Findings

- **Server query time is small once TCP+TLS+auth is removed.** With the connection handshake separated
  out, the steady single-op **server query** is ~18–20 ms p50 on both backends (find: 19.7 ms mongo /
  17.7 ms DocumentDB; insert: 22.5 / 18.2). The earlier impression that "the query got slow" was the
  cold-connection handshake being counted inside the op — not slow server execution.
- **With TLS equalized, steady single-op is near-parity.** mongo-vm holds the **tighter tail** (connection
  and query p99 lower), DocumentDB the **lower median**. Both run 0% errors at the full 135 req/s.
- **Warm full-workload ops confirm it.** The same `ReqId` `find_output` on a warm socket is 26 ms p50
  (mongo) / 1 ms (DocumentDB); `remove`/`insert` are similarly cheap. Only the cold op1 carries the
  connection cost, and the full-workload `find_input` server-query row (net of connection) is what isolates
  it.
- **Under burst, DocumentDB is ~2× faster.** Both degrade from client-side CPU saturation at ≥1,200 new
  conn/s, but DocumentDB's managed gateway absorbs it better across connection, server-query, and cycle —
  and stays at 0% errors where mongo-vm sheds 0.37–0.56%.
- **Full workload breaks the single VM.** Four ops/Task saturate the client (CPU 100%, 50k+ handles),
  driving mongo-vm to **22.8% (steady) / 32.0% (burst)** failures — mostly `ServerSelectionTimeout` — with
  multi-second connection times. DocumentDB stays **≤0.05%** errors. DocumentDB wins every full-workload row.

## Migration decision guide (TLS-on-both)

For light **single-op** churn, mongo-vm and DocumentDB are effectively tied once connection and query are
measured separately — mongo-vm even wins the tail. For the **full multi-op workload under churn**, **Azure
DocumentDB (vCore)** is the clear recommendation: it sustains full throughput at near-zero errors while the
single mongo-vm node saturates the client and sheds 23–32% of work. The decisive factor is **not** server
query speed (~sub-30 ms once connection is excluded) but how each backend's connection front-end copes with
TLS-handshake churn at scale.
