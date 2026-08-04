# Campaign — run-20260624-shard (sharded mongo-shard vs DocumentDB)

One **benchmark campaign** = one higher folder under `results/`, holding every target run from the
**same code version**, plus the comparison report and summary. Runs were executed **sequentially**
on VM1; TIME_WAIT was drained to a clean baseline between runs.

This campaign measures the **connection-establishment tax of a self-managed *sharded* MongoDB** under
the same TLS-on-both methodology as `run-20260619-00`. The `mongo-shard` target is a **2-shard
MongoDB 7.0 cluster fronted by two `mongos` routers**, server-side TLS on (self-signed cert +
chain-of-trust `CAFile`, `mode: allowTLS`), so its `tls=true` handshake stays directly comparable to
the always-TLS managed `documentdb`. **DocumentDB was re-run in this same campaign** (same managed
instance, same code version, same workloads, same day) so the comparison is **same-campaign** rather
than against an older baseline — the two MongoDB shapes, single node (`mongo-vm`, prior campaign) and
sharded (`mongo-shard`, this campaign), can each be read against fresh managed numbers. Each workload
was run as two separate scenarios — **steady**
(135 Tasks/s) and **burst** (Poisson λ=0.57) — across three workloads: single-op **find-input**,
single-op **insert-output**, and the full 4-op **full-workload** (`find`→`remove`→`insert`→`find`).
Production sizing: **3 iterations × 600 s** per run.

> **Client-side round-robin direct-connect (read this).** A per-Task `MongoClient` pointed at the
> full 2-`mongos` topology spins up SDAM monitor threads **per mongos per client**; under no-reuse
> churn this melted the load generator (48,657 threads on the first timed attempt). The fix —
> documented in `INCIDENT-runaway-concurrency-meltdown.md` — **round-robin pins each per-Task client
> to ONE mongos as a direct single-server connection** (`directConnection=true`), preserving 2× router
> fan-out while eliminating per-client topology monitors. This is the same mitigation `mongo-vm` uses,
> keeping the two MongoDB targets methodologically comparable. See **Key Findings** for what this
> implies for production.

## Resource specs (as used for this campaign)

| Target | Resource spec (this campaign) |
|---|---|
| `mongo-shard` | MongoDB 7.0 **sharded cluster** on Azure VMs — **2 shards**, **2× `mongos` routers** (10.3.0.6:27017, 10.3.0.4:27016), 1-member config server; `bmt_db` sharded on **hashed `ReqId`**; **TLS enabled** (`allowTLS`, self-signed cert, `tlsInsecure=true` client-side). Client: **round-robin direct single-server** connection per Task across the two mongos (`directConnection=true`). |
| `documentdb` | Azure DocumentDB (vCore) — **HA enabled**, managed **TLS**. *(Re-run in this campaign on 2026-06-24.)* |

Common stack: **MongoDB Server 7.0 / wire 7.0**, **.NET 8 (LTS)**, **MongoDB C# Driver 2.30**.
No-reuse churn: new `MongoClient` per Task (`maxPoolSize=1`/`minPoolSize=0`).

## Run groups

| Run identifier | Target | Workload | Scenario | Finished (UTC) | Duration | Total | OK | Fail | Success |
|---|---|---|---|---|---|---|---|---|---|
| `mongo-shard-steady-find-input-20260624-041734` | mongo-shard | find-input | steady | 2026-06-24 04:47:34 | 1,800 s | 243,003 | 243,003 | 0 | 100.00% |
| `mongo-shard-burst-find-input-20260624-044801` | mongo-shard | find-input | burst | 2026-06-24 05:18:09 | 1,807 s | 265,701 | 265,701 | 0 | 100.00% |
| `mongo-shard-steady-insert-output-20260624-052209` | mongo-shard | insert-output | steady | 2026-06-24 05:52:09 | 1,800 s | 243,001 | 243,001 | 0 | 100.00% |
| `mongo-shard-burst-insert-output-20260624-055315` | mongo-shard | insert-output | burst | 2026-06-24 06:23:19 | 1,803 s | 265,522 | 265,522 | 0 | 100.00% |
| `mongo-shard-steady-full-workload-20260624-025641` | mongo-shard | full-workload | steady | 2026-06-24 03:27:11 | 1,830 s | 242,993 | 242,979 | 14 | 99.99% |
| `mongo-shard-burst-full-workload-20260624-032933` | mongo-shard | full-workload | burst | 2026-06-24 04:00:03 | 1,830 s | 235,228 | 234,841 | 387 | 99.84% |
| `documentdb-steady-find-input-20260624-035103` | documentdb | find-input | steady | 2026-06-24 04:21:03 | 1,800 s | 243,001 | 243,001 | 0 | 100.00% |
| `documentdb-burst-find-input-20260624-042119` | documentdb | find-input | burst | 2026-06-24 04:51:28 | 1,809 s | 264,824 | 264,824 | 0 | 100.00% |
| `documentdb-steady-insert-output-20260624-045150` | documentdb | insert-output | steady | 2026-06-24 05:21:50 | 1,800 s | 243,000 | 243,000 | 0 | 100.00% |
| `documentdb-burst-insert-output-20260624-052203` | documentdb | insert-output | burst | 2026-06-24 05:52:09 | 1,806 s | 265,730 | 265,730 | 0 | 100.00% |
| `documentdb-steady-full-workload-20260624-022440` | documentdb | full-workload | steady | 2026-06-24 02:55:11 | 1,830 s | 242,999 | 242,984 | 15 | 99.99% |
| `documentdb-burst-full-workload-20260624-025526` | documentdb | full-workload | burst | 2026-06-24 03:25:53 | 1,827 s | 247,351 | 247,269 | 82 | 99.97% |

> The `documentdb-*` rows were **re-run in this campaign on 2026-06-24** against the same managed
> instance, so the comparison is same-campaign.

## Topology (mongo-shard)

| Role | Endpoint | Notes |
|------|----------|-------|
| mongos #1 | 10.3.0.6:27017 | TLS allowTLS + CAFile |
| mongos #2 | 10.3.0.4:27016 | TLS allowTLS + CAFile |
| config server (csrs, 1 member) | 10.3.0.6:27019 | |
| shard2 mongod | 10.3.0.6:27018 | |
| shard1 mongod (rs0) | 10.3.0.4:27017 | |
| Load generator | vm-dbtest-hpc-0 (10.2.0.4) | runs the benchmark locally |

`bmt_db` sharded on **hashed `ReqId`**; `calc_input` / `calc_output` ~50/50 across shards.

## Folder layout

```
results/
  run-20260624-shard/                                        <- campaign (this folder)
    mongo-shard-<scenario>-<workload>-<stamp>/               <- per-target run (sharded, TLS)
      aggregate.json                                          <- mean-of-3 stats
      iter-01/ iter-02/ iter-03/                              <- per-iteration artifacts
        *.json  *-timeseries.csv  *-latency.csv
    documentdb-<scenario>-<workload>-<stamp>/                 <- per-target run (managed, TLS) — re-run this campaign
    comparison-mongo-shard-vs-documentdb-20260624.html        <- self-contained 2-way report
    summary-mongo-shard-vs-documentdb-20260624.md             <- concise metrics summary
    INCIDENT-runaway-concurrency-meltdown.md                  <- first-attempt client meltdown: cause + fix
    INDEX.md                                                  <- this manifest
```

Per-run folder contents:

- `aggregate.json` — mean-of-3-iteration `RunResult` stats (per-op + cycle + connection latency
  percentiles, throughput, error rate) plus each iteration's full result.
- `iter-NN/<run-id>-iter-NN-<stamp>.json` — full machine-readable per-iteration result.
- `iter-NN/<run-id>-iter-NN-<stamp>-timeseries.csv` — one row per second (connection open/close, per-op
  QPS, in-flight tasks, ephemeral ports, TIME_WAIT, handles, threads, CPU%, working set).
- `iter-NN/<run-id>-iter-NN-<stamp>-latency.csv` — per-op + cycle + connection latency percentiles.
- `*-console.log` — captured console log for that run (**git-ignored**).

## Reproduce / regenerate

```
# Server-side (one-time): 2-shard cluster + 2 mongos with TLS (allowTLS, self-signed cert + CAFile);
#   shard bmt_db on hashed ReqId. Secrets/topology held in mongo-shard.env (git-ignored).
# Client-side: BMT_CONN_MONGO_SHARD points at both mongos; the load-gen round-robin-pins each
#   per-Task client to ONE mongos as directConnection=true (see TaskConnectionFactory.cs).

# Load the connection string for the run shell only:
$cs = Get-Content mongo-shard.env | Where-Object { $_ -match '^\s*BMT_CONN_MONGO_SHARD\s*=' } |
  ForEach-Object { ($_ -replace '^\s*BMT_CONN_MONGO_SHARD\s*=','') -replace '"','' }
$env:BMT_CONN_MONGO_SHARD = $cs.Trim()

# find / insert single-op (clean-output before insert; insert-only accumulates calc_output):
loadgen.exe test --config config/production/single-find-steady.json --target mongo-shard \
  --scenario steady --results results/run-20260624-shard
# ...repeat for -burst, single-insert-{steady,burst}, full-workload-{steady,burst}.
```

## Publishing / confidentiality

- **Published** (no confidential data): `*.json`, `*.csv`, `*.html`, `*.md`, `INDEX.md`. Connection
  strings are masked for **credentials *and* host/IP/appName**.
- **Git-ignored**: `*-console.log` (captured console logs include preflight check-3 output that prints
  resolved **private IPs**; the same information, masked, is preserved in the published artifacts) and
  `mongo-shard.env` (holds the connection string + cluster secrets).
