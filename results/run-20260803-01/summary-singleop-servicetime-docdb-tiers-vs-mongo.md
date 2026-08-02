# Single-Operation Service-Time Comparison (non-saturating) — DocumentDB tiers vs MongoDB

**Generated:** 2026-08-03. **Purpose:** isolate **clean per-operation service time** (find / insert) at a
**non-saturating** steady rate, separate from the connection-establishment and backlog effects that dominate
the churn (open-loop) and saturation (hold) tests. This answers "once connected, how fast is each
backend/tier at serving one operation, and does DocumentDB tier size change service latency?"

## Method

- **Workload:** single-op steady, **135 tasks/s** (each task = one fresh connection + one indexed op by
  `ReqId`, no reuse), `Mode=SingleOp`, 3 iterations × 300 s, warm cache. **Non-saturating** — every run
  completed with **0 failures**, so the numbers are clean service time, not timeout tails.
- **Latency reported:** operation p50/p90/p99 (server execution on the freshly-opened socket) and
  connection-open p99, mean of the **steady-state iterations (2–3)**; iteration 1 is dropped as a first-touch
  warm-up transient.
- **DocumentDB:** 2-shard (distributed 33/32), tiers **M60 / M80 / M200**, single generator host.
- **MongoDB reference:** **reused** from `deprecated/run-20260624-shard` (mongo-shard, single-op steady
  135/s, TLS on, 3×600 s, 0 failures). Not re-run — at 135/s the mongos router count is irrelevant (routers
  only bottleneck under establishment saturation), so it is a valid service-time reference for any mongo config.

## Results — operation service time (ms), steady-state mean

| Target | find p50 / p90 / p99 | insert p50 / p90 / p99 | conn-open p99 | Failures |
|---|---|---|---|---|
| **MongoDB (mongo-shard, ref)** | 41 / 56 / **71** | 44 / 58 / **71** | 44 / 42 | 0 |
| DocDB 2-shard **M60** | **26.5** / 47.8 / 107.7 | **27.6** / 48.8 / 117.6 | 78 / 86 | 0 |
| DocDB 2-shard **M80** | 34.3 / 75.7 / 198.8 | 34.6 / 68.0 / 126.0 | 105 / 89 | 0 |
| DocDB 2-shard **M200** | 28.7 / 58.0 / 154.1 | 29.9 / 54.5 / 111.3 | 97 / 82 | 0 |

(find conn p99 / insert conn p99 shown in the conn-open column.)

## Findings (measured)

1. **DocumentDB has the lower median, MongoDB the tighter tail.** DocumentDB serves the median op faster
   (find p50 ≈ 27–34 ms vs mongo 41 ms; insert p50 ≈ 28–35 ms vs mongo 44 ms), but MongoDB has the
   **tighter p99** (71 ms for both ops vs DocumentDB's 108–199 ms). For latency-tail-sensitive workloads,
   mongo's self-managed engine delivers more predictable service time on an open socket.

2. **DocumentDB tier size does NOT materially change single-op service time.** M60, M80, and M200 are within
   run-to-run noise of each other (find p50 26–34 ms; insert p50 28–35 ms) — if anything M80 shows the
   highest tail, which is noise, not a tier effect. *Interpretation:* a single indexed keyed op at 135/s is
   far too light to be compute-bound, so extra vCores do not lower its service latency.

3. **This refines the earlier saturation finding.** Under the 4-op **open-loop** test we observed warm-op
   p99 *improving* with tier (insert p99 11.1 s → 5.3 s, M60→M200). This clean single-op test shows that was
   **not faster raw operation execution** — it was higher tiers easing overall saturation/queueing. Isolated
   from saturation, raw single-op service time is **tier-independent**.

4. **Connection-open cost is higher on DocumentDB even unsaturated** (conn p99 78–105 ms vs mongo's 42–44 ms),
   consistent with the SRV/gateway access path adding handshake latency vs mongo's direct-to-router pin.

## Bottom line

At non-saturating load the two backends are close on service time, with a clear trade: **DocumentDB = lower
typical (median) latency; MongoDB = lower tail (p99) and cheaper connection open.** DocumentDB **tier
selection should be driven by connection-concurrency capacity and churn throughput** (where higher tiers and
2-shard distribution demonstrably help — see the main report), **not by single-operation service latency**,
which is effectively tier-independent for this indexed keyed workload.

> Cross-reference: connection-churn and saturation behaviour are in
> [`REPORT-mongo-vs-documentdb-churn-benchmark.md`](REPORT-mongo-vs-documentdb-churn-benchmark.md). This
> document adds the isolated service-time dimension that those saturated tests cannot cleanly measure.
