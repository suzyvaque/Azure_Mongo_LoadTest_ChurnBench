# MongoDB Connection-Churn Benchmark — comparison-3way-steady-burst-20260616-145757

**Campaign:** `phase1-3way-steady-burst-20260616`
**Phase 1** · sequential one-target-at-a-time on VM1 · 1-hour window each · Scenario A steady (135 Tasks/s) + Scenario B Poisson burst (λ=0.57) run together in one window ("steady-burst").
**Workload:** new `MongoClient` per Task (no reuse; `maxPoolSize=1`/`minPoolSize=0`), 4 ops/Task (`find`→`remove`→`insert`→`find`) keyed by `ReqId`. Latency in ms; throughput counts individual DB ops (req).

**Source runs (grouped in this campaign folder):**
- `mongo-vm-steady-burst-20260616-031555`
- `documentdb-steady-burst-20260616-042846`
- `cosmos-ru-steady-burst-20260616-053319`

Rows are grouped and colour-banded by metric family; each find is split into **cold socket** (op1 `find_input`,
on a brand-new connection — pays TCP+TLS+auth) and **warm socket** (op4 `find_output`, same query on the
now-open connection). The best value per row is **bold + underlined**.

<table>
  <thead>
    <tr><th>Group</th><th>Metric</th><th>mongo-vm</th><th>documentdb</th><th>cosmos-ru</th></tr>
  </thead>
  <tbody>
    <tr style="background:#e8eef7"><td rowspan="2"><b>Headline</b></td><td>Throughput (req/s)</td><td>514.7</td><td><u><b>532.4</b></u></td><td>204.4</td></tr>
    <tr style="background:#e8eef7"><td>Error rate</td><td>11.20%</td><td><u><b>5.07%</b></u></td><td>58.51%</td></tr>
    <tr style="background:#f3e8f7"><td rowspan="3"><b>Total cycle</b><br>(incl. 10 s sleep)</td><td>p50</td><td><u><b>10,040 ms</b></u></td><td>10,091 ms</td><td>115,496 ms</td></tr>
    <tr style="background:#f3e8f7"><td>p99</td><td><u><b>24,003 ms</b></u></td><td>51,249 ms</td><td>183,231 ms</td></tr>
    <tr style="background:#f3e8f7"><td>p99.9</td><td><u><b>29,553 ms</b></u></td><td>55,011 ms</td><td>187,499 ms</td></tr>
    <tr style="background:#fff4e0"><td rowspan="3"><b>connectionOpen</b><br>(TCP+TLS+auth)</td><td>p50</td><td><u><b>8.9 ms</b></u></td><td>20.8 ms</td><td>40,421 ms</td></tr>
    <tr style="background:#fff4e0"><td>p90</td><td><u><b>25.9 ms</b></u></td><td>8,952.97 ms</td><td>54,910.6 ms</td></tr>
    <tr style="background:#fff4e0"><td>p99</td><td><u><b>164.3 ms</b></u></td><td>14,598.6 ms</td><td>65,131.2 ms</td></tr>
    <tr style="background:#fde8e8"><td rowspan="3"><b>find — cold socket</b><br>(op1, new connection)</td><td>p50</td><td><u><b>15.1 ms</b></u></td><td>41.2 ms</td><td>83,735.9 ms</td></tr>
    <tr style="background:#fde8e8"><td>p90</td><td><u><b>3,672.9 ms</b></u></td><td>19,961.5 ms</td><td>104,194.6 ms</td></tr>
    <tr style="background:#fde8e8"><td>p99</td><td><u><b>10,712.5 ms</b></u></td><td>28,448.7 ms</td><td>115,284.1 ms</td></tr>
    <tr style="background:#e8f7ee"><td rowspan="3"><b>find — warm socket</b><br>(op4, same query)</td><td>p50</td><td><u><b>0.6 ms</b></u></td><td>1.4 ms</td><td>14.6 ms</td></tr>
    <tr style="background:#e8f7ee"><td>p90</td><td>8.7 ms</td><td><u><b>6.9 ms</b></u></td><td>174.8 ms</td></tr>
    <tr style="background:#e8f7ee"><td>p99</td><td><u><b>79.9 ms</b></u></td><td>155.9 ms</td><td>516.9 ms</td></tr>
    <tr style="background:#eef7e8"><td rowspan="3"><b>remove</b><br>(write, op2)</td><td>p50</td><td>3.9 ms</td><td><u><b>3.4 ms</b></u></td><td>13.8 ms</td></tr>
    <tr style="background:#eef7e8"><td>p90</td><td>7.5 ms</td><td><u><b>6.7 ms</b></u></td><td>205.1 ms</td></tr>
    <tr style="background:#eef7e8"><td>p99</td><td><u><b>117.5 ms</b></u></td><td>161.5 ms</td><td>527.8 ms</td></tr>
    <tr style="background:#e8f3f7"><td rowspan="3"><b>insert</b><br>(write, op3)</td><td>p50</td><td>4.2 ms</td><td><u><b>3.5 ms</b></u></td><td>29.8 ms</td></tr>
    <tr style="background:#e8f3f7"><td>p90</td><td>10.0 ms</td><td><u><b>9.3 ms</b></u></td><td>216.2 ms</td></tr>
    <tr style="background:#e8f3f7"><td>p99</td><td><u><b>117.3 ms</b></u></td><td>166.2 ms</td><td>498.0 ms</td></tr>
  </tbody>
</table>

### What the find latency is actually measuring

The cold-vs-warm split above is the headline of a **connection-churn** test: op1 (`find_input`) and op4
(`find_output`) are the *identical* `ReqId` query, yet op1's p90 is 3.7 s (mongo-vm) / 20.0 s (documentdb) /
104.2 s (cosmos-ru) while op4's p90 is single-digit ms on the healthy backends. The query is not slow — op1
is simply the **first** use of a brand-new `MongoClient` (no-reuse: `maxPoolSize=1`/`minPoolSize=0`), so it
absorbs TCP + TLS + SCRAM auth + server-selection queuing under ≥1,200 new conn/s. The 10 s `taskSleepMs` is
**not** in any op timer (it lands only in the Total cycle). What each find timer does and does not include:

| Cost component | In the find timer? | Status in these results |
|---|---|---|
| Disk/cache miss for the data | No | **Removed** — §6.5 warm-up pass makes the working set hot before timing |
| TCP + TLS + auth + server selection | **Yes (cold socket only)** | **Kept** — this is the variable under test (no-reuse churn) |
| 10 s think-time sleep (`taskSleepMs`) | No | Excluded from per-op latency; only in Total cycle |
| Pure query execution | Yes (~0.6-1.4 ms p50) | Seen cleanly on the **warm socket** (op4) once the connection tax is paid |

So cold connections are **not** filtered out — they are the measurement. The §6.5 warm-up removes only the
*data-cache* variable (and is forbidden from pre-opening/retaining connections); connections stay cold by
design. The warm-socket find row is included as the contrast that isolates the connection-establishment tax.

> **Notes on formatting rules.** Total latency = full Task cycle, which includes a fixed **10,000 ms** `taskSleepMs`; it is therefore **not** `≈ find` and is not annotated as such. No pooled variant exists — every connection is cold by design (the churn test's purpose) — so `0 ms (pool reuse)` is never applicable. All metrics were available from the run artifacts; none were substituted.

## Key Findings

- **Query execution is not the bottleneck on the healthy backends.** The cold/warm find split proves it: op4 (`find_output`, warm socket) runs the *same* `ReqId` query in 0.6 ms (mongo-vm) / 1.4 ms (documentdb) p50, while op1 (`find_input`, cold socket) p90 balloons to 3.7 s / 20.0 s purely from connection establishment, not slow reads. The keyed `remove`/`insert` writes are also cheap (3-4 ms p50). The dominant per-cycle cost is the deliberate 10 s think-time. cosmos-ru is the exception: even the warm find is RU-pressured and the cold find p50 queues to **83.7 s** behind the 40,000 RU/s budget.
- **Connection establishment (TCP + TLS + auth) is cheap when the backend has headroom.** connectionOpen p50 is **8.9 ms** direct-to-node (mongo-vm) and 20.8 ms over TLS/SRV (documentdb). On cosmos-ru it explodes to **40.4 s** — a ~4,500× degradation where connection setup itself is being throttled, signalling the tier (not the network) is the limiter.
- **Pooling would mostly remove that setup cost and lift tail latency**, but was intentionally disabled. Comparing the throttled vs healthy backends shows the headroom at stake: documentdb sustains **2.6× higher throughput** (532 vs 204 req/s) and **~6× lower p99.9** (55,011 vs 187,499 ms) than cosmos-ru under identical churn. On the VM-hosted node, fixing client-side churn pressure (directConnection + 5 s fail-fast) earlier raised success from 14.2% → 88.8% — a ~6× drop in failed work.
- **Error behaviour under high churn differs by failure mode.** documentdb degraded gracefully (**5.07%**, mostly ServerSelectionTimeout on burst spikes). mongo-vm's 11.20% is client-side churn pressure (ServerSelectionTimeout 54,015; ConnectionFailure 4,076). cosmos-ru's **58.51%** is overwhelmingly **CosmosRuThrottling (110,276 / 196,917 failures)** — a provisioning ceiling, not a connection-handling defect.
- **Tier limit reached — cosmos-ru only.** At the fixed 40,000 RU/s, cosmos-ru hit its throughput ceiling with connection setup itself queued. The VM-hosted and DocumentDB vCore tiers did **not** reach a connection-handling limit (no port exhaustion: peak 52,034 / 55,535 ephemeral ports). No-reuse compliance held on all three (created = closed).

> **Migration decision guide**
> For connection-churn workloads (no pooling), **Azure DocumentDB (vCore)** is the recommended target: highest throughput (532 req/s) and lowest error rate (5.07%) with managed TLS/SRV. **MongoDB-on-VM** is a viable close second on tail latency once client-side churn is tuned (directConnection + fail-fast), at the cost of self-managing the host. **Cosmos DB for MongoDB (RU)** is **not** suited to this pattern at 40,000 RU/s — 58% throttling-driven failures and 40 s connection setup mean it would need a substantially higher (and costlier) RU allocation, or app-side connection pooling, before it competes.
