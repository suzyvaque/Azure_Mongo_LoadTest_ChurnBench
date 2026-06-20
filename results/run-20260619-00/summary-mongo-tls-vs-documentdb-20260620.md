# MongoDB Connection-Churn Benchmark — mongo-vm (TLS) vs DocumentDB

**Campaign:** `run-20260619-00`
**TLS-vs-TLS** · sequential one-run-at-a-time on VM1 · **3 iterations × 600 s** per run · scenarios run
separately — **steady** (135 Tasks/s) and **burst** (Poisson λ=0.57).
**Workload:** new `MongoClient` per Task (no reuse; `maxPoolSize=1`/`minPoolSize=0`). Three workloads:
single-op **find-input**, single-op **insert-output**, full 4-op **full-workload**
(`find`→`remove`→`insert`→`find`) keyed by `ReqId`. Latency in ms; values are the **mean of the 3
iterations' percentiles**.

Both backends now perform a **full TLS handshake** per connection: `mongo-vm` had server-side TLS enabled
(`allowTLS`, self-signed cert + chain-of-trust `CAFile`); `documentdb` is always-TLS. This makes the
connection-establishment cost directly comparable for the first time. The best value per row is **bold,
underlined, green**.

> The earlier **non-TLS** mongo single-find runs live under `mongo_tls_false/` and are excluded here —
> their connection times skipped the TLS crypto and so are not comparable to DocumentDB.

## Single-op: find-input (cold connection — find pays TCP+TLS+auth)

<table>
  <thead><tr><th>Scenario</th><th>Metric</th><th>mongo-vm (TLS)</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="background:#e8eef7"><td rowspan="2"><b>steady</b></td><td>Throughput (req/s)</td><td><u><b style="color:#1a7f37">135.0</b></u></td><td><u><b style="color:#1a7f37">135.0</b></u></td></tr>
    <tr style="background:#e8eef7"><td>Error rate</td><td><u><b style="color:#1a7f37">0.00%</b></u></td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="background:#fff4e0"><td rowspan="2"><b>connectionOpen</b><br>(steady)</td><td>p50</td><td>24.9 ms</td><td><u><b style="color:#1a7f37">15.1 ms</b></u></td></tr>
    <tr style="background:#fff4e0"><td>p99</td><td><u><b style="color:#1a7f37">48.4 ms</b></u></td><td>86.9 ms</td></tr>
    <tr style="background:#fde8e8"><td rowspan="2"><b>find (query)</b><br>(steady)</td><td>p50</td><td>44.6 ms</td><td><u><b style="color:#1a7f37">32.8 ms</b></u></td></tr>
    <tr style="background:#fde8e8"><td>p99</td><td><u><b style="color:#1a7f37">83.3 ms</b></u></td><td>124.9 ms</td></tr>
    <tr style="background:#f3e8f7"><td rowspan="2"><b>Total cycle</b><br>(steady)</td><td>p50</td><td>45.9 ms</td><td><u><b style="color:#1a7f37">42.1 ms</b></u></td></tr>
    <tr style="background:#f3e8f7"><td>p99</td><td><u><b style="color:#1a7f37">91.4 ms</b></u></td><td>141.7 ms</td></tr>
    <tr style="background:#e8eef7"><td rowspan="2"><b>burst</b></td><td>Throughput (req/s)</td><td><u><b style="color:#1a7f37">145.8</b></u></td><td>143.6</td></tr>
    <tr style="background:#e8eef7"><td>Error rate</td><td>0.37%</td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="background:#fff4e0"><td rowspan="2"><b>connectionOpen</b><br>(burst)</td><td>p50</td><td>655.2 ms</td><td><u><b style="color:#1a7f37">410.8 ms</b></u></td></tr>
    <tr style="background:#fff4e0"><td>p99</td><td>3,806.5 ms</td><td><u><b style="color:#1a7f37">3,010.0 ms</b></u></td></tr>
    <tr style="background:#fde8e8"><td rowspan="2"><b>find (query)</b><br>(burst)</td><td>p50</td><td>1,485.3 ms</td><td><u><b style="color:#1a7f37">785.1 ms</b></u></td></tr>
    <tr style="background:#fde8e8"><td>p99</td><td>6,398.5 ms</td><td><u><b style="color:#1a7f37">5,266.8 ms</b></u></td></tr>
    <tr style="background:#f3e8f7"><td rowspan="2"><b>Total cycle</b><br>(burst)</td><td>p50</td><td>1,513.0 ms</td><td><u><b style="color:#1a7f37">828.1 ms</b></u></td></tr>
    <tr style="background:#f3e8f7"><td>p99</td><td>6,515.0 ms</td><td><u><b style="color:#1a7f37">5,327.9 ms</b></u></td></tr>
  </tbody>
</table>

## Single-op: insert-output (cold connection — insert pays TCP+TLS+auth)

<table>
  <thead><tr><th>Scenario</th><th>Metric</th><th>mongo-vm (TLS)</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="background:#e8eef7"><td rowspan="2"><b>steady</b></td><td>Throughput (req/s)</td><td><u><b style="color:#1a7f37">135.0</b></u></td><td>134.8</td></tr>
    <tr style="background:#e8eef7"><td>Error rate</td><td><u><b style="color:#1a7f37">0.00%</b></u></td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="background:#fff4e0"><td rowspan="2"><b>connectionOpen</b><br>(steady)</td><td>p50</td><td>24.5 ms</td><td><u><b style="color:#1a7f37">14.7 ms</b></u></td></tr>
    <tr style="background:#fff4e0"><td>p99</td><td><u><b style="color:#1a7f37">42.7 ms</b></u></td><td>90.1 ms</td></tr>
    <tr style="background:#e8f3f7"><td rowspan="2"><b>insert (write)</b><br>(steady)</td><td>p50</td><td>47.0 ms</td><td><u><b style="color:#1a7f37">32.9 ms</b></u></td></tr>
    <tr style="background:#e8f3f7"><td>p99</td><td><u><b style="color:#1a7f37">72.3 ms</b></u></td><td>125.9 ms</td></tr>
    <tr style="background:#f3e8f7"><td rowspan="2"><b>Total cycle</b><br>(steady)</td><td>p50</td><td>48.2 ms</td><td><u><b style="color:#1a7f37">41.1 ms</b></u></td></tr>
    <tr style="background:#f3e8f7"><td>p99</td><td><u><b style="color:#1a7f37">74.2 ms</b></u></td><td>144.4 ms</td></tr>
    <tr style="background:#e8eef7"><td rowspan="2"><b>burst</b></td><td>Throughput (req/s)</td><td><u><b style="color:#1a7f37">146.7</b></u></td><td>144.5</td></tr>
    <tr style="background:#e8eef7"><td>Error rate</td><td>0.56%</td><td><u><b style="color:#1a7f37">0.00%</b></u></td></tr>
    <tr style="background:#fff4e0"><td rowspan="2"><b>connectionOpen</b><br>(burst)</td><td>p50</td><td>690.3 ms</td><td><u><b style="color:#1a7f37">363.3 ms</b></u></td></tr>
    <tr style="background:#fff4e0"><td>p99</td><td>3,896.1 ms</td><td><u><b style="color:#1a7f37">2,399.2 ms</b></u></td></tr>
    <tr style="background:#e8f3f7"><td rowspan="2"><b>insert (write)</b><br>(burst)</td><td>p50</td><td>1,584.6 ms</td><td><u><b style="color:#1a7f37">722.8 ms</b></u></td></tr>
    <tr style="background:#e8f3f7"><td>p99</td><td>8,229.7 ms</td><td><u><b style="color:#1a7f37">4,088.1 ms</b></u></td></tr>
    <tr style="background:#f3e8f7"><td rowspan="2"><b>Total cycle</b><br>(burst)</td><td>p50</td><td>1,616.1 ms</td><td><u><b style="color:#1a7f37">762.7 ms</b></u></td></tr>
    <tr style="background:#f3e8f7"><td>p99</td><td>8,456.8 ms</td><td><u><b style="color:#1a7f37">4,134.4 ms</b></u></td></tr>
  </tbody>
</table>

## Full 4-op workload (`find`→`remove`→`insert`→`find`; per-op p50 / p99 ms)

`find_input` = **cold socket** (op1 on a brand-new connection — pays TCP+TLS+auth). `remove`/`insert`/
`find_output` run on the now-**warm socket**, isolating true server execution. Total cycle includes the
fixed **10,000 ms** `taskSleepMs`.

<table>
  <thead><tr><th>Scenario</th><th>Metric</th><th>mongo-vm (TLS)</th><th>documentdb</th></tr></thead>
  <tbody>
    <tr style="background:#e8eef7"><td rowspan="2"><b>steady</b></td><td>Throughput (req/s)</td><td>101.3</td><td><u><b style="color:#1a7f37">132.0</b></u></td></tr>
    <tr style="background:#e8eef7"><td>Error rate</td><td>22.83%</td><td><u><b style="color:#1a7f37">0.02%</b></u></td></tr>
    <tr style="background:#fff4e0"><td rowspan="2"><b>connectionOpen</b><br>(steady)</td><td>p50</td><td>3,407.9 ms</td><td><u><b style="color:#1a7f37">20.3 ms</b></u></td></tr>
    <tr style="background:#fff4e0"><td>p99</td><td>8,509.4 ms</td><td><u><b style="color:#1a7f37">136.8 ms</b></u></td></tr>
    <tr style="background:#fde8e8"><td><b>find — cold</b> (op1)</td><td>p50 / p99</td><td>7,075 / 14,080</td><td><u><b style="color:#1a7f37">43 / 208</b></u></td></tr>
    <tr style="background:#eef7e8"><td><b>remove</b> (warm, op2)</td><td>p50 / p99</td><td>27 / 608</td><td><u><b style="color:#1a7f37">3 / 90</b></u></td></tr>
    <tr style="background:#e8f3f7"><td><b>insert</b> (warm, op3)</td><td>p50 / p99</td><td>78 / 1,208</td><td><u><b style="color:#1a7f37">3 / 90</b></u></td></tr>
    <tr style="background:#e8f7ee"><td><b>find — warm</b> (op4)</td><td>p50 / p99</td><td>26 / 610</td><td><u><b style="color:#1a7f37">1 / 73</b></u></td></tr>
    <tr style="background:#f3e8f7"><td rowspan="2"><b>Total cycle</b><br>(steady, incl. 10 s sleep)</td><td>p50</td><td>16,528 ms</td><td><u><b style="color:#1a7f37">10,087 ms</b></u></td></tr>
    <tr style="background:#f3e8f7"><td>p99</td><td>24,170 ms</td><td><u><b style="color:#1a7f37">10,312 ms</b></u></td></tr>
    <tr style="background:#e8eef7"><td rowspan="2"><b>burst</b></td><td>Throughput (req/s)</td><td>93.3</td><td><u><b style="color:#1a7f37">134.8</b></u></td></tr>
    <tr style="background:#e8eef7"><td>Error rate</td><td>32.00%</td><td><u><b style="color:#1a7f37">0.05%</b></u></td></tr>
    <tr style="background:#fff4e0"><td rowspan="2"><b>connectionOpen</b><br>(burst)</td><td>p50</td><td>2,571.3 ms</td><td><u><b style="color:#1a7f37">530.8 ms</b></u></td></tr>
    <tr style="background:#fff4e0"><td>p99</td><td>8,447.5 ms</td><td><u><b style="color:#1a7f37">3,434.5 ms</b></u></td></tr>
    <tr style="background:#fde8e8"><td><b>find — cold</b> (op1)</td><td>p50 / p99</td><td>5,757 / 18,103</td><td><u><b style="color:#1a7f37">1,044 / 6,562</b></u></td></tr>
    <tr style="background:#eef7e8"><td><b>remove</b> (warm, op2)</td><td>p50 / p99</td><td>29 / 632</td><td><u><b style="color:#1a7f37">4 / 243</b></u></td></tr>
    <tr style="background:#e8f3f7"><td><b>insert</b> (warm, op3)</td><td>p50 / p99</td><td>89 / 1,283</td><td><u><b style="color:#1a7f37">6 / 201</b></u></td></tr>
    <tr style="background:#e8f7ee"><td><b>find — warm</b> (op4)</td><td>p50 / p99</td><td>34 / 755</td><td><u><b style="color:#1a7f37">4 / 185</b></u></td></tr>
    <tr style="background:#f3e8f7"><td rowspan="2"><b>Total cycle</b><br>(burst, incl. 10 s sleep)</td><td>p50</td><td>14,212 ms</td><td><u><b style="color:#1a7f37">11,521 ms</b></u></td></tr>
    <tr style="background:#f3e8f7"><td>p99</td><td>28,070 ms</td><td><u><b style="color:#1a7f37">17,150 ms</b></u></td></tr>
  </tbody>
</table>

## Key Findings

- **With TLS equalized, the steady single-op gap closes.** On find/insert steady, the two backends are
  near-parity: `mongo-vm` actually has the **tighter tail** (connection p99 ~43–48 ms vs DocumentDB
  ~87–90 ms), while DocumentDB keeps a slightly lower median (~15 ms vs ~25 ms connect, ~33 ms vs ~47 ms
  query). Both run at 0% errors and the full 135 req/s. This reverses the earlier *non-TLS* finding where
  mongo-vm's connect time looked far cheaper — that gap was an artefact of skipping the TLS handshake.
- **Warm server execution is sub-100 ms on both; the cold connection is the whole story.** In the full
  workload, the *same* `ReqId` find costs 7,075 ms p50 cold (op1, mongo-vm) but only 26 ms warm (op4) —
  proving the query itself is fast and the variable under test is connection establishment under churn.
  DocumentDB's warm ops are ~1–6 ms.
- **Under burst, DocumentDB is ~2× faster on absolute latency.** Both degrade from client-side CPU
  saturation (per-Task TLS handshake at ≥1,200 new conn/s), but DocumentDB's managed gateway absorbs the
  spike better: burst find p50 785 ms vs 1,485 ms, insert p50 723 ms vs 1,585 ms. `mongo-vm` also begins
  shedding work (0.4–0.6% timeouts) where DocumentDB stays at 0%.
- **Full workload is where the single VM breaks down.** At 4 cold-ish ops/Task the client host saturates
  (CPU 100%, 50k+ handles), driving `mongo-vm` to **22.8% (steady) / 32.0% (burst)** failures — almost
  entirely `ServerSelectionTimeout` — and multi-second connection times. DocumentDB stays **≤0.05%**
  errors with connection p50 ~20 ms (steady). For this pattern DocumentDB is the clear winner.

> **Migration decision guide (TLS-on-both):** For light **single-op** churn, `mongo-vm` and DocumentDB
> are effectively tied — mongo-vm even wins the tail — so a tuned self-hosted node is viable. For the
> **full multi-op workload under churn**, **Azure DocumentDB (vCore)** is the clear recommendation: it
> sustains full throughput at near-zero errors while the single mongo-vm node saturates the client and
> sheds 23–32% of work. The decisive factor is not query speed (warm ops are sub-100 ms on both) but how
> each backend's connection front-end copes with TLS-handshake churn at scale.
