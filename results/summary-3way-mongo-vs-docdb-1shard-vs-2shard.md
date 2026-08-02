# Three-way comparison — Mongo (4-router) vs DocumentDB 1-shard vs DocumentDB 2-shard

Generated 2026-08-02. Full 4-op workload, 3 synchronized generator hosts, 3 iterations each.
**Open-loop** = churn (fresh connection per task, 4-op cycle). **Hold** = park ~12k connections, keepalive find.
DocumentDB both columns are **M200** (apples-to-apples tier); the only DocumentDB variable is physical data
distribution. Concurrency = combined per-second SUM of driver ActiveReady (max of the 3 iterations' peaks).

## Configurations

| Column | What it is | Source runs (OL / hold) |
|---|---|---|
| **Mongo 4-router** | 2-shard MongoDB + 4 dedicated mongos routers | run-20260731-01/mongo · run-20260727-04/mongo |
| **DocDB 1-shard** | Cosmos vCore M200, 2 physical shards but data on **ONE** (never distributed) | run-20260731-01/docdb · run-20260727-04/docdb |
| **DocDB 2-shard** | Cosmos vCore M200, data **genuinely distributed 33/32** across both shards | run-20260802-01/docdb-m200-ol · -h |

> **"1-shard (actually 2 but used 1)"**: the cluster always had 2 physical shards, but until the empty-first reshard + reseed, all 100k docs lived on the primary shard — so only one shard's connection front-end was engaged. The 2-shard column is the same cluster/tier after genuine distribution.

---

## Open-loop (full 4-op churn)

| Metric | Mongo 4-router | DocDB 1-shard (M200) | DocDB 2-shard (M200) |
|---|---|---|---|
| **Max concurrent (best)** | 2,351 | 16,035 | 17,530 |
| Successful tasks / 3 iters | 64,333 | **140,620** | 67,842 * |
| Task error rate (%) | 94.9 | **72.1** | 88.1 * |
| Connection p99 (TLS+auth, ms) | 159,686 | 47,412 | **27,108** |
| find cold p99 (ms) | 110,204 | 59,049 | **43,180** |
| remove warm p99 (ms) | 17,222 | 8,446 | **8,059** |
| insert warm p99 (ms) | 6,098 | 7,149 | **5,318** |
| find warm p99 (ms) | **1,152** | 7,239 | 11,255 |
| Total cycle p99 (ms) | 38,640 | 67,901 | 60,066 |
| Client CPU peak (%) | 66 | 90 | 83 |
| DocumentDB server CPU | n/a | ~1.5% | ~1.5% |

\* **DocDB 2-shard open-loop was throttled.** The M200 gateway rate-limited request admission under sustained same-day churn (idle server CPU); only 1 of 3 iters was healthy, dragging the success/error/throughput figures. **The 2-shard success count is NOT comparable to 1-shard here** — read the 2-shard OL row for *latency* (which improved) rather than *throughput* (throttle-suppressed). The 1-shard M200 OL ran clean earlier (~155 tasks/s).

**Open-loop read:** DocumentDB (either topology) crushes mongo 4-router on connection establishment under churn — connection p99 27–47s vs mongo's **160s**, and ~2× the concurrency. Between DocumentDB topologies, **2-shard nearly halves the connection p99 (47.4s → 27.1s)** and lowers cold-find and warm-insert p99 — the two-front-end benefit. Mongo's one advantage is warm find_output p99 (1.2s) — mongod serves an already-open socket faster than the DocumentDB gateway.

---

## Hold (park ~12k, keepalive find)

| Metric | Mongo 4-router | DocDB 1-shard (M200) | DocDB 2-shard (M200) |
|---|---|---|---|
| **Max concurrent (best)** | 12,000 | 11,365 | **12,000** |
| Cleared 10k? | ✅ | ✅ | ✅ (3/3 iters) |
| Establish p99 (Demand→Ready, ms) | **34,005** | 131,835 | n/a † |
| Keepalive find p99 (ms) | **14,803** | 138,544 | 28,273 |
| Client CPU peak (%) | 88 | 82 | 92 |
| Server CPU | ~17–21% / router | ~1.5% | ~1.5% |

† Establish (Demand→Ready) latency wasn't captured in the 2-shard hold compacts; keepalive-find p99 is the common metric.

**Hold read:** The topology effect is decisive here. **1-shard M200 plateaued at 11,365** (couldn't quite hold the full gate) with a brutal keepalive-find p99 of **139s**. **2-shard M200 held the full 12,000 gate on all 3 iters** and cut keepalive-find p99 to **28s — a ~4.9× improvement** — because splitting the ~12k held connections across two shard nodes halves each node's serving load. Mongo 4-router also holds 12,000 and posts the best hold latencies of all (establish 34s, keepalive 15s) thanks to 4 stateless routers absorbing the handshake load.

---

## Verdict

| Question | Answer |
|---|---|
| **Best raw concurrency (hold)** | Tie: Mongo 4-router = DocDB 2-shard = **12,000**. DocDB 1-shard = 11,365. |
| **Best establishment under churn** | **DocDB 2-shard** (connection p99 27s vs 1-shard 47s vs mongo 160s). |
| **Best hold latency** | **Mongo 4-router** (keepalive 15s), then DocDB 2-shard (28s), far ahead of 1-shard (139s). |
| **Effect of DocDB sharding** | Genuine 2-shard distribution **raised the hold ceiling to the full gate** and **cut hold latency ~5×** and connection-churn latency ~2× vs 1-shard — the second shard adds a real connection front-end. |
| **Operational cost** | DocDB: managed, ~1.5% server CPU, but M200 open-loop is **throttle-prone** under sustained same-day churn. Mongo 4-router: 4 router VMs at ~20% CPU, no throttling but self-managed. |

**Bottom line:** distributing DocumentDB across both physical shards closes most of the gap with the scaled-out (4-router) mongo — matching it on hold concurrency (12,000) and dramatically improving DocumentDB's own hold latency (~5×). Mongo 4-router still wins hold *latency*; DocumentDB 2-shard wins *connection-establishment* latency under churn. The persistent bottleneck for every configuration remains per-connection TLS+SCRAM establishment, not the database engine (DocumentDB server CPU idle at ~1.5% throughout).
