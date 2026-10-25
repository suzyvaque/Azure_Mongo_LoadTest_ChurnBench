# Blog Review Plan

**Review target:** [Current English blog](hpc-mongodb-to-documentdb-reference.md). The points below summarize its six sections; they are not a new fact-check or a rewritten article. Review these claims first, then revise the blog. **The blog remains unchanged.**

**Publication:** Caldova Securities remains anonymous. English for LinkedIn first; Korean for the Korean Microsoft website afterward (keep setting/metric names in English; add a short glossary). Link only sanitized material: the setup doc contains real hostnames, private IPs, and resource names.

**Final test date:** 2026-08-03 (normal-load run `run-20260803-02`; date taken from the campaign folder and report update, as run files carry no timestamps). Earlier inputs: 2026-08-02 tier sweep, 2026-07-15 production-rate run.

## Key Takeaways (open the blog with these)

- Normal load (~135 tasks/s, four ops, new client per task): DocumentDB M80 two-shard and MongoDB four-router both sustained it; DocumentDB had the lower median, MongoDB the tighter tail.
- Connection pressure is a separate requirement from task rate; ~11k mostly idle connections is not 11k active workloads.
- Hold ceiling was reached (12,000 = the configured gate) but not reliably sustained on M80 (average 4,476 versus 10,570 on MongoDB four-router).
- The selected two-shard M80 was never tested at the production burst rate; overload runs are saturation evidence only.
- No measured cost, availability, or failover benefit.

## 1. Workload and Requirements

*Essential section; keep the lifecycle and the two-requirement explanation, keep the diagram small.*

- **As-is (per client; not documented in the repo):** Two MongoDB primaries, two standbys, one `mongos`; five components.
- **To-be:** DocumentDB M80, two shards, HA enabled; client chose it for stability and performance.
- **Task:** Fresh client -> input read -> calculation -> output delete/insert/read -> disconnect. Delete + insert is mandatory (no upsert).
- **Separate targets:** ~135 tasks/s; connection bursts ~1,210/s accumulating ~11,000 mostly idle connections (~1,210/s x ~9 s lifetime). Not 11,000 active workloads; ~9 connections per task in production came from driver monitoring.

## 2. DocumentDB Configuration

*Keep endpoint pattern and shard verification; reduce dataset and driver details to one line or a short table.*

- Global `mongodb+srv` endpoint, private endpoint/DNS, TLS, SCRAM-SHA-256. Use the service-supplied string with no endpoint pinning; do not hard-code IPs.
- Hashed `ReqId` shard key and indexed `ReqId`; verify data actually spans both shards (chunk metadata + explain). Earlier "two-shard" runs held data on one shard.
- Benchmark: .NET 8, driver 2.30, new client per task, pool 0/1, 5 s connect/selection timeouts, retryable writes on. Dataset: 100,000 docs, seed 42, 6/16/50/58 KiB. Effective code settings override the URI.

**Environment parity table (add; confirm values):**

| | DocumentDB | MongoDB four-router |
|---|---|---|
| Compute | M80 per shard (32 vCore / 128 GiB RAM per Microsoft Learn), two shards | Two shard VMs (24 vCore, co-hosting `mongos`) + two 8-vCore router VMs |
| HA/standby | HA on | Standbys not modelled in benchmark |
| Version/network | Managed; private endpoint, AZ2 | MongoDB 7.0; private; self-signed CA |
| Generators | 1 host (normal), 3 hosts (hold/overload) | Same |

Tiers tested per shard (Microsoft Learn): M60 = 16 vCore / 64 GiB, **M80 = 32 vCore / 128 GiB (adopted by the client)**, M200 = 64 vCore / 256 GiB. Report §5 vCore figures are corrected to match.

## 3. Client and Server Safeguards

*Condense to one short "generator pitfalls" call-out; detail lives in the repo. Not DocumentDB-specific.*

- **Port exhaustion (keep, short):** Windows ports 10000-65534, TIME_WAIT 30 s (~1,851 conn/s/host budget); overload offered ~1,300/s per host.
- **Client pressure (one line each):** ThreadPool floor 512/512 fixed an "idle DB, high client latency" pile-up; cluster cleanup is a benchmark control, not production guidance. Move internals to the repo.
- **Cut:** MongoDB `maxIncomingConnections` change (Mongo-only; its 20,000 value is unconfirmed).
- **TLS disclosure (keep):** certificate revocation checks disabled for DocumentDB (chain/hostname validation kept); chain validation relaxed (`MongoAllowInsecureTls`) for Mongo in the normal-load and hold runs. Setup doc claims validation stays on and gives two different reasons (schannel contention vs router cert name mismatch): reconcile and state which runs used it.
- **Overload (keep):** open-loop bypasses concurrency limits; give production backpressure advice separately.

## 4. Test Method

*Essential: the test-to-question table. Keep one command example; link the runbook.*

- Five tests: normal processing, production-rate connection pressure, deliberate overload, 12,000-connection hold, single-operation baseline (reference only; keep out of the headline).
- Normal test: one host, 3 x 300 s, no calculation sleep (connection lifetime ms, not production's ~9 s). Overload: three hosts, 2,900 ms sleep. Hold: three hosts x 4,000 slots. Data caches warmed; task connections never reused.
- Targets run sequentially; multi-host iterations synchronized; prepare -> preflight/warm-up -> test -> drain/cleanup -> merge.
- **Metric definitions (add, 3 lines):** concurrency = per-second sum of host `ActiveReady`; offered vs started vs completed; averaged p99 is not a pooled p99. No pass/fail thresholds exist in the repo: propose example acceptance criteria.
- **Fidelity limits (state both):** (1) Hold keepalives every 10 s offer ~1,200 reads/s at 12,000 connections versus production's ~5 ops/s. (2) Open-loop runs a full four-op cycle on every connection, while production's extra connections were mostly monitoring with almost no operations, so DB work is overstated.

## 5. Results and Business Value

Comparison below is **DocumentDB M80 two-shard versus MongoDB four-router**, not the original one-router deployment.

*Essential. Lead with the normal-load and hold tables; keep other numbers to one line each.*

- **Normal load:** Both 134.9 tasks/s; error 0.000% vs 0.001%. Input-read p50/p99: DocumentDB 27.5/107.8 ms; MongoDB 45.6/67.9 ms. Insert p99 41.1 vs 7.4 ms. Lower DocumentDB median; tighter MongoDB tail.
- **Hold:** Best peak 12,000 for both, but that equals the gate (3 x 4,000), so true capacity is unknown. **Average Ready: 4,476 vs 10,570; >10,000 in 2/3 vs 3/3 iterations; keepalive p99 23.3 s vs 14.8 s; attempts/fails 49,885/88 vs 38,991/2,377.** MongoDB two-router capped near 4,714 at ~99.7% VM CPU; four routers fixed it.
- **Overload:** 72.9 vs 71.2 tasks/s; errors 89.1% vs 94.9%; connection-open p99 22 s vs 160 s. Saturation evidence only; DocumentDB server CPU stayed idle, so the constraint is interpreted as admission, not shard compute.
- **Production-rate run (2026-07-15):** M80 with data on one shard reached 9,979 connections with 38.8% errors; pre-TLS-fix Mongo comparison is not like-for-like. The selected two-shard M80 was not tested at this rate.
- **Business value:** stability/performance rationale plus expected operational simplification. No measured cost, availability, or zero-downtime result.
- **Fix before citing:** report §1a and §6g still say closed-loop results are "pending" although §4a has data.

**Provenance table (add):**

| Result | Campaign | Date | Notes |
|---|---|---|---|
| Normal load | `run-20260803-02` | 2026-08-03 | 1 host, 3 x 300 s, Mongo relaxed TLS |
| DocumentDB M80 hold/overload | `run-20260802-01` | 2026-08-02 | 3 hosts |
| Mongo four-router hold/overload | `run-20260727-*`/`run-20260731-01` (confirm) | late July | 3 hosts, relaxed TLS |
| Production-rate | `run-20260715-2host-openloop` | 2026-07-15 | One-shard M80, older code |

## 6. Lessons Learned

*Essential. Promote client reuse first: it is the most actionable lesson for HPC readers.*

- **Reuse clients/pools where semantics allow;** this benchmark's no-reuse model is a worst case, not a design.
- **Shard** for data or sustained operation load, and verify placement. In this data, 2-shard raised the hold ceiling (one shard plateaued near 11k; keepalive p99 114-139 s vs 23-34 s) but did not fix churn throughput; tier mattered more there (state both).
- **HA** is for availability, not throughput; zero-downtime needs failover tests.
- **Endpoint:** keep service discovery; scale the observed bottleneck (generator, router, admission), not automatically shard compute.
- **Bound overload:** deadlines, queues, backpressure, retry budgets; test recovery, not only peak.

## 7. Production Readiness and Open Gaps (add, short)

- **Checklist:** pooled clients, retry budgets/backoff, failover test, overload-recovery test, monitoring (client `ActiveReady`/TIME_WAIT, DocumentDB CPU and throttling).
- **Limits:** M200 intermittent admission throttling (~8 min recovery, observation not a guarantee); link official DocumentDB tier/connection limits; confirm `DocumentDbCompatibility` errors were zero.
- **Recommended next tests:** two-shard M80 at 1,210 conn/s; a pooled-client baseline; a failover run.

## Points to Confirm Before Rewriting

- Is the exact production task/connection lifecycle represented correctly, including which settings are benchmark-only?
- Does the original five-component topology and its standby behavior match the client architecture?
- What specifically motivated **M80 two-shard** selection, beyond the general stability/performance rationale?
- Are HA and security/network settings confirmed for each cited campaign, and is there additional production/failover evidence?
- Which TLS mode did each campaign use?
- Is 2026-08-03 the actual last run date (confirm from host logs)?

**Evidence references:** [Results](../results/REPORT-mongo-vs-documentdb-churn-benchmark.md), [setup](../docs/ENVIRONMENT-SETUP.md), and [effective client settings](../src/Bmt.Core/Connections/TaskConnectionFactory.cs).