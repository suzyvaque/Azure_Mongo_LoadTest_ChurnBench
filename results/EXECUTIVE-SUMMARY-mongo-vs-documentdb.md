# Executive Summary — DocumentDB vs MongoDB (Connection-Churn Benchmark)

*Condensed from [`REPORT-mongo-vs-documentdb-churn-benchmark.md`](REPORT-mongo-vs-documentdb-churn-benchmark.md). All figures are measured unless labelled **assumption** or **recommendation**. Two independent production dimensions are evaluated separately and never combined into a single "11k active workload" figure: **(A) workload processing** ~135 tasks/s, and **(B) connection holding** ~11k mostly-idle live connections accumulating at ~1,210 conn/s.*

---

## 1. DocumentDB vs. MongoDB Comparison

### 1a. Average production workload — closed-loop, ~135 tasks/s (Dimension A, §4a)

Full 4-op cycle at a steady non-saturating rate — the normal production processing pattern.

| Metric | DocumentDB 2-shard M60 | DocumentDB 2-shard M80 | DocumentDB 2-shard M200 | MongoDB 4-router |
|---|---|---|---|---|
| Throughput (tasks/s) | 134.7 | 134.9 | 134.9 | 134.9 |
| Error % | 0.002 | 0.000 | 0.001 | 0.001 |
| find p50 / p90 / p99 (ms) | 26.9 / 59.6 / 112.5 | 27.5 / 59.3 / 107.8 | 27.6 / 64.4 / 123.7 | 45.6 / 51.7 / **67.9** |
| insert p50 / p99 (ms) | 3.3 / 39.5 | 3.5 / 41.1 | 4.0 / 47.9 | 4.7 / **7.4** |
| Connection-open p90 / p99 (ms) | 25.5 / 77.3 | 25.8 / 75.6 | 26.3 / 82.4 | 28.8 / **37.0** |
| Server CPU | idle (~1.5%) | idle (~1.5%) | idle (~1.5%) | ~20%/router |

**Read:** All configurations sustain ~135 tasks/s at ≤0.006% error. **DocumentDB has the lower median** (find p50 ~27 ms vs 46 ms); **MongoDB has the tighter tail** (find p99 68 ms vs 108–124 ms; write-path p99 single-digit-ms). DocumentDB tier does **not** change service latency at this non-saturating rate. Both backends comfortably meet the average workload.

### 1b. High-connection workload — churn / hold, ~1,210 conn/s → ~11k live-but-idle (Dimension B, §4b–§4c)

Fresh connection per task (no reuse); the binding cost is per-connection TLS + SCRAM establishment.

| Metric | DocumentDB (best config) | MongoDB (best config) |
|---|---|---|
| **Connection holding (§4c)** | | |
| Max held connections | **12,000** — 2-shard, any tier | **12,000** — 4-router |
| Cleared ≥10k (of 3 iters) | 3/3 (M200) | 3/3 (4-router) |
| Keepalive-find p99 (ms) | 23,271–34,212 (2-shard) | **14,803** (4-router) |
| Establish p90/p99 (ms) | 102,653 / 131,835 (1s-M200)¹ | **17,923 / 34,005** (4-router) |
| Server CPU at peak hold | **idle (~0.8–1.5%)** | ~17–21%/router (2-router: **99.7% saturated**) |
| Bottleneck layer | Managed gateway admission | mongo VM TLS/SCRAM CPU |
| **Churn storm, ≈3,900 conn/s (§4b-1)** | | |
| Best throughput (tasks/s) | **154.7** (1-shard M200) | 71.2 (4-router) |
| Connection p99 (ms) | **27,108** (2s-M200) | 159,686 (4-router) / 240,699 (2-router) |
| Conn-open failures (3 iters) | 268–819 (healthy tiers) | 610 (4-router) / 12,132 (2-router) |
| **Production-rate spike, ~1,210 conn/s (§4b-2)** | | |
| Max concurrent live/idle | 9,979 (single-shard M80) | 12,069 (2-shard, pre-TLS-fix²) |
| Error % | 38.8 (admission-front queueing) | 92.1² |

¹ Establish latency not captured in the 2-shard hold compacts; 1-shard M200 shown. &nbsp; ² MongoDB §4b-2 column predates the mongo TLS-chain-validation fix — **directional context only**, not comparable to current mongo results.

**Read:** Under connection pressure the constraint is **connection establishment/admission, never the database engine**. DocumentDB's server CPU stays idle (~1.5%) in every scenario — its ceiling is the managed gateway front-end. MongoDB's ceiling is TLS/SCRAM CPU on the VMs (2-router saturates at 99.7% / ~4.7k; 4-router spreads it and holds 12k). DocumentDB establishes/admits connections far better under churn (conn p99 27 s vs 160–241 s); MongoDB 4-router holds connections with the lowest hold latency (keepalive p99 15 s vs 23–34 s).

### 1c. Key Takeaways

| Dimension | Winner | Why (measured) |
|---|---|---|
| Average workload throughput (135 tasks/s) | **Tie** | Both ~135 tasks/s, ≤0.006% error. |
| Median operation latency | **DocumentDB** | find p50 ~27 ms vs ~46 ms. |
| Tail (p99) operation latency | **MongoDB** | find p99 68 ms vs 108–124 ms; write p99 single-digit-ms. |
| Connection establishment under churn | **DocumentDB** | conn p99 27 s vs 160–241 s; 268–819 fails vs 12,132 (2-router). |
| Connection-holding latency | **MongoDB 4-router** | keepalive p99 15 s; establish p99 34 s. |
| Connection-holding capacity (≥11k) | **Tie (best configs)** | DocumentDB 2-shard and mongo 4-router both hold 12,000. |
| Server-side resource cost / ops effort | **DocumentDB** | idle server CPU, fully managed; mongo owns TLS/SCRAM CPU + router fleet. |
| Failure modes | — | DocumentDB: gateway admission throttling (M200, recovers on cooldown). MongoDB: hard VM-CPU saturation (2-router fails at scale). |

**Bottom line.** For the **average workload**, both meet the requirement — DocumentDB wins the median, MongoDB the tail. For the **high-connection workload**, DocumentDB is better at *establishing/admitting* connections and needs no operational effort, while MongoDB 4-router is better at *holding* connections at low latency but requires you to operate the router fleet and provision TLS/SCRAM CPU. In every case the bottleneck is connection handshake cost, not the database engine.

---

## 2. Is a Second Shard Necessary for M200?

**Question:** does DocumentDB **M200** need two physical shards for the tested production workload (~135 tasks/s average; ~11k live-but-idle connection accumulation at ~1,210 conn/s)?

### 2a. Evidence supported by the benchmark

**The binding constraint is never shard compute.** Across every churn/hold scenario, DocumentDB server CPU stays **idle (0.8–1.5%)** while failures occur at connection establishment/admission — *upstream* of the shards (§4b-1, §4c, §6d). Sharding distributes data/ops; it does not target the observed limit.

**Single-shard M200 is the strongest churn performer measured.** In the churn storm (§4b-1), **1-shard M200 delivered the best throughput of the entire matrix — 154.7 tasks/s**, with only 386 connection-open failures and ~16,000 concurrent, *beating 2-shard M80* (72.9 tps) and out-performing 2-shard M200.

**A second shard did not help M200 under churn — it hurt.** 2-shard M200 exhibited intermittent **gateway request-admission throttling** (only 1 of 3 iters healthy, idle server CPU, recovered after ~8-min cooldown) — a failure mode the consolidated 1-shard M200 did **not** show (§4b-1 †, §5).

**Single-shard already meets the average workload.** DocumentDB tier and shard count do not change closed-loop service latency at 135 tasks/s; every 2-shard tier runs ~135 tasks/s at ~0% error, and the single-op baseline is tier-independent (§4a, §4d). Nothing indicates the average workload needs a second shard at M200.

**Single-shard holds close to the production spike.** In the hold test (§4c), **1-shard M200 reached ~11,365 held connections and cleared ≥10k on all 3 iterations** — at/above the ~11k production accumulation — with idle server CPU. The production-rate spike test (§4b-2) independently held ~10k live/idle on a **single shard** (M80).

### 2b. Evidence *against* single-shard (where 2-shard genuinely helped)

**Holding headroom above 11k.** Only the **2-shard** configs reached the full **12,000** gate on all iterations; 1-shard (M80 and M200) plateaus at ~11,000–11,365 — at the production spike with **little margin** (§4c, §6c).

**Hold latency.** 2-shard tiers post **4–6× better keepalive p99** (23–34 s) than 1-shard (114–139 s), because distributing held connections across two shard nodes halves each node's serving load (§5). *(Note: 1-shard M200 establish p99 ~132 s; this is the connection front-end, not shard compute.)*

### 2c. Assumptions / caveats (not directly proven by the benchmark)

- **No 1-shard M200 *closed-loop* full-workload run exists.** Dimension-A evidence for M200 is 2-shard (§4a) plus the tier-independent single-op baseline; 1-shard M200 average-workload throughput is *inferred*, not directly measured.
- **1-shard M200's hold ceiling was measured at ~11,365, not a probed maximum.** Whether it clears a full 12k gate is untested — its plateau is a single-front-end property that tier does not lift.
- **The 2s-M200 throttling** is characterised as intermittent/recoverable, not a hard limit; its exact trigger threshold was not isolated.
- This benchmark is **worst-case no-reuse churn**; with realistic connection pooling the handshake bottleneck — and thus the entire shard-vs-tier question for the connection dimension — largely disappears.

### 2d. Recommendation

> **For the tested production workload, a second shard is NOT required for DocumentDB M200.** A **single-shard M200** meets both dimensions on the measured evidence: it sustains the ~135 tasks/s average workload with tier-independent latency, it is the **best churn performer in the matrix** (154.7 tps, 386 conn fails), it holds ~11.3k connections (≥ the ~11k production spike) at idle server CPU, and — critically — adding a second shard at M200 **introduced admission throttling without improving the compute-idle bottleneck**.

**Rationale.** The production constraint is connection establishment/admission at the managed gateway, not shard compute (idle throughout). Tier (gateway/admission capacity), not shard count, is the effective lever for this workload; M200 already provides it on a single shard.

**Caveats / additional testing required before a production sizing decision:**
1. **Run 1-shard M200 closed-loop full-workload** (§4a matrix) to *directly* confirm Dimension-A throughput/latency rather than inferring it.
2. **Probe 1-shard M200's true hold ceiling** at the production ~1,210 conn/s rate to quantify headroom above 11k (the ~11,365 plateau leaves thin margin vs an 11k spike; if production may exceed ~11k, the 2-shard 12k headroom becomes the deciding factor).
3. **Reproduce the 2s-M200 throttling** to establish whether it is configuration-tunable (backoff/retry) or an inherent M200 admission limit — this determines whether 2-shard is a net risk or benefit at this tier.
4. **Confirm the production connection model** (reuse/pooling vs strict no-reuse). If production pools connections, single-shard M200 has ample margin and the shard question is moot for this dimension.

**Net:** single-shard M200 is the recommended starting point for the tested workload; keep a second shard in reserve only if (a) hold demand is confirmed to exceed ~11k with required headroom, or (b) hold-latency SLOs demand the 2-shard 4–6× keepalive-latency improvement — neither of which the current production evidence (~11k mostly-idle at ~5 ops/s) requires.
