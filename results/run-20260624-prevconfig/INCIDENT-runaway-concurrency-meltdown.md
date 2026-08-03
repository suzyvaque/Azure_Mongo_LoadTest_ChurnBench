# Incident — Runaway Concurrency Meltdown (client/load-generator VM)

**Date:** 2026-06-24
**Target under test:** `mongo-shard` (2-shard MongoDB 7.0 cluster, 2× mongos routers)
**Scenario:** full-workload steady, 3×600s, 135 Tasks/s
**Result:** Run aborted. **No valid metrics produced** for this attempt.
**Status:** Root-caused and fixed (see *How it was fixed*). Awaiting re-run.

---

## TL;DR
The benchmark's intentional **no-reuse churn model** (one brand-new `MongoClient` per Task)
melted down the **load-generator VM**, *not* the database. Against a **sharded** cluster the
client must keep a full multi-router topology, so every per-Task client started its own
background **SDAM heartbeat monitor for each mongos**. At 135 new Tasks/s those monitor threads
and their connections accumulated faster than Tasks completed, exploding to **~48,657 threads /
8.6 GB / 32,245 stuck-open connections** before the host thrashed to a standstill. This is a
**client-side artifact of the topology**, not a measurement of cluster query/connection capacity.

---

## What happened (symptoms)
On the load generator (`vm-dbtest-hpc-0`, 10.2.0.4):

| Metric                         | Observed at meltdown |
|--------------------------------|----------------------|
| `loadgen` worker threads       | **48,657** (and climbing) |
| Working set (RAM)              | **~8.6 GB** |
| ESTABLISHED conns to mongos    | **32,245** (flat — not draining) |
| Process                        | `loadgen` PID 10584 (host `dotnet` PID 10880) |
| Iteration 1 (start 11:00:52)   | never reached its ~11:10:52 end; still saturated at 11:19 |

The connection count was **flat, not draining** — the host was thrashing (even `Get-Process` /
`Get-NetTCPConnection` sampling became slow), confirming the generator, not the cluster, was the
bottleneck.

## Where it happened
- **Tier:** Client / load-generator VM (`vm-dbtest-hpc-0`). The cluster VMs were *not* the limiter.
- **Code path:** `src/Bmt.Core/Connections/TaskConnectionFactory.cs` →
  `MongoClientSettings.FromConnectionString(...)` → `new MongoClient(settings)` per Task.
- **Trigger config:** connection string lists **both** mongos seeds
  (`10.3.0.4:27016,10.3.0.6:27017`) with **no** `directConnection`, so the driver maintained a
  **Sharded** topology per client and ran an SDAM monitor per mongos for every live client.

## Why it happened (root cause)
1. **No-reuse model (by design):** each Task creates a fresh `MongoClient` and disposes it
   (worst-case "1 Task = 1 connection lifecycle"). This is the workload under test.
2. **Sharded topology per client:** unlike `mongo-vm` (which uses `directConnection=true` →
   single-server, **no** topology monitor), `mongo-shard` kept the full 2-mongos topology. Each
   live client therefore spun up **~2 background monitor threads** (one per mongos) plus its
   operation connection. ~24k concurrent live clients × ~2 ≈ the ~48k threads observed.
3. **Pile-up dynamics:** with `SocketTimeoutMs=0` (driver default — no socket timeout, matching
   the `mongo-vm` methodology), any slow operation through a mongos kept its client alive. New
   Tasks arrived at 135/s faster than slow ones drained, so live-client count (and thus monitor
   threads/connections) grew without bound until the host exhausted memory/scheduler capacity.

**Key point for decision-making:** This meltdown measures a **client/topology interaction**, not
cluster capacity. Notably, the managed **DocumentDB** target sustained the *same* churn workload
without this pathology (its gateway endpoint behaves as a single-server connection to the client),
which is itself evidence in DocumentDB's favor for naive connection-churn workloads.

## How it was fixed
Applied a **round-robin direct-connection** strategy for `mongo-shard` in
`src/Bmt.Core/Connections/TaskConnectionFactory.cs`:

- Each per-Task client is now **pinned to ONE mongos** as a **direct single-server** connection
  (`DirectConnection = true`, single `Server`), chosen **round-robin** across the two mongos seeds.
- This **preserves the 2× router fan-out** (Tasks alternate between `10.3.0.4:27016` and
  `10.3.0.6:27017`) while **eliminating per-client topology (SDAM) monitors** — the exact same
  mitigation `mongo-vm` already uses, keeping the two MongoDB targets methodologically comparable.
- Gated behind the existing `DirectConnectionForSingleNode` tuning switch.
- `SocketTimeoutMs` left at `0` deliberately, to keep the client tuning identical to the
  `mongo-vm` baseline (fair apples-to-apples comparison).

**Recovery actions taken:** killed `loadgen` PID 10584 + host `dotnet` PID 10880; verified the VM
released all threads/memory and connections (0 conns to cluster, TIME_WAIT back to ~1). Cluster,
TLS, sharding, seeding and preflight (10/10) were all healthy throughout — only the timed run
melted the client.

## Validation / next step
Re-run the steady (then burst) campaign into `results/run-20260624-shard` and confirm the
generator stays bounded (threads/connections roughly track in-flight Tasks, no unbounded growth).

> NOTE: the per-Task "cycle latency" (~10 s) is **workload-defined think-time**
> (`TaskSleepMs=10000` in `full-workload.json`), NOT connection cost. The true
> connection-establishment metric is `ConnectionOpenMs` (steady p50 ~35 ms / p99 ~137 ms).

## Outcome (after fix)
Both campaigns completed cleanly into `results/run-20260624-shard` with bounded concurrency
(steady ~4,090 threads; burst ~73k handles peak) — **no meltdown**. Steady mean error 0.005%,
burst mean error 0.16%. See `INDEX.md` for the full result summary.
