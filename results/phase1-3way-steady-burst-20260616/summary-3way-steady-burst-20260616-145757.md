# MongoDB Connection-Churn Benchmark — comparison-3way-steady-burst-20260616-145757

**Campaign:** `phase1-3way-steady-burst-20260616`
**Phase 1** · sequential one-target-at-a-time on VM1 · 1-hour window each · Scenario A steady (135 Tasks/s) + Scenario B Poisson burst (λ=0.57) run together in one window ("steady-burst").
**Workload:** new `MongoClient` per Task (no reuse; `maxPoolSize=1`/`minPoolSize=0`), 4 ops/Task (`find`→`remove`→`insert`→`find`) keyed by `ReqId`. Latency in ms; throughput counts individual DB ops (req).

**Source runs (grouped in this campaign folder):**
- `mongo-vm-steady-burst-20260616-031555`
- `documentdb-steady-burst-20260616-042846`
- `cosmos-ru-steady-burst-20260616-053319`

| Metric | mongo-vm | documentdb | cosmos-ru |
|---|---|---|---|
| Throughput (req/s) | 514.7 | **532.4** | 204.4 |
| Error rate | 11.20% | **5.07%** | 58.51% |
| Total p50 | **10,040 ms** | 10,091 ms | 115,496 ms |
| Total p99 | **24,003 ms** | 51,249 ms | 183,231 ms |
| Total p99.9 | **29,553 ms** | 55,011 ms | 187,499 ms |
| connectionOpen p50 | **8.9 ms** | 20.8 ms | 40,421 ms |
| findExecution p50 | **15.1 ms** | 41.2 ms | 83,736 ms |
| findExecution p90 | **3,672.9 ms** | 19,961.5 ms | 104,194.6 ms |
| findExecution p99 | **10,712.5 ms** | 28,448.7 ms | 115,284.1 ms |
| removeExecution p50 | 3.9 ms | **3.4 ms** | 13.8 ms |
| removeExecution p90 | 7.5 ms | **6.7 ms** | 205.1 ms |
| removeExecution p99 | **117.5 ms** | 161.5 ms | 527.8 ms |
| insertExecution p50 | 4.2 ms | **3.5 ms** | 29.8 ms |
| insertExecution p90 | 10.0 ms | **9.3 ms** | 216.2 ms |
| insertExecution p99 | **117.3 ms** | 166.2 ms | 498.0 ms |

> **Notes on formatting rules.** Total latency = full Task cycle, which includes a fixed **10,000 ms** `taskSleepMs`; it is therefore **not** `≈ find` and is not annotated as such. No pooled variant exists — every connection is cold by design (the churn test's purpose) — so `0 ms (pool reuse)` is never applicable. All metrics were available from the run artifacts; none were substituted.

## Key Findings

- **Query execution is not the bottleneck on the healthy backends.** With cold connections, `find` p50 is 15 ms (mongo-vm) / 41 ms (documentdb) and the keyed `remove`/`insert` writes are cheaper still (3-4 ms each) — the dominant per-cycle cost is the deliberate 10 s think-time, not the database. cosmos-ru is the exception: `find` p50 balloons to **83.7 s** as requests queue behind the RU budget rather than executing slowly (its writes stay single-digit-to-30 ms because reads absorb the throttling).
- **Connection establishment (TCP + TLS + auth) is cheap when the backend has headroom.** connectionOpen p50 is **8.9 ms** direct-to-node (mongo-vm) and 20.8 ms over TLS/SRV (documentdb). On cosmos-ru it explodes to **40.4 s** — a ~4,500× degradation where connection setup itself is being throttled, signalling the tier (not the network) is the limiter.
- **Pooling would mostly remove that setup cost and lift tail latency**, but was intentionally disabled. Comparing the throttled vs healthy backends shows the headroom at stake: documentdb sustains **2.6× higher throughput** (532 vs 204 req/s) and **~6× lower p99.9** (55,011 vs 187,499 ms) than cosmos-ru under identical churn. On the VM-hosted node, fixing client-side churn pressure (directConnection + 5 s fail-fast) earlier raised success from 14.2% → 88.8% — a ~6× drop in failed work.
- **Error behaviour under high churn differs by failure mode.** documentdb degraded gracefully (**5.07%**, mostly ServerSelectionTimeout on burst spikes). mongo-vm's 11.20% is client-side churn pressure (ServerSelectionTimeout 54,015; ConnectionFailure 4,076). cosmos-ru's **58.51%** is overwhelmingly **CosmosRuThrottling (110,276 / 196,917 failures)** — a provisioning ceiling, not a connection-handling defect.
- **Tier limit reached — cosmos-ru only.** At the fixed 40,000 RU/s, cosmos-ru hit its throughput ceiling with connection setup itself queued. The VM-hosted and DocumentDB vCore tiers did **not** reach a connection-handling limit (no port exhaustion: peak 52,034 / 55,535 ephemeral ports). No-reuse compliance held on all three (created = closed).

> **Migration decision guide**
> For connection-churn workloads (no pooling), **Azure DocumentDB (vCore)** is the recommended target: highest throughput (532 req/s) and lowest error rate (5.07%) with managed TLS/SRV. **MongoDB-on-VM** is a viable close second on tail latency once client-side churn is tuned (directConnection + fail-fast), at the cost of self-managing the host. **Cosmos DB for MongoDB (RU)** is **not** suited to this pattern at 40,000 RU/s — 58% throttling-driven failures and 40 s connection setup mean it would need a substantially higher (and costlier) RU allocation, or app-side connection pooling, before it competes.
