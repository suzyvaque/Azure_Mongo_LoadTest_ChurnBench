# Azure Mongo Load-Test — Connection-Churn Benchmark

A MongoDB-wire-protocol **connection-churn** benchmark comparing self-managed **MongoDB (sharded)**
against **Azure DocumentDB (Cosmos vCore)** under an HPC-style workload where **every task opens a
brand-new connection and closes it** (no pooling/reuse). This isolates **per-connection establishment
cost** (TCP + TLS + SCRAM auth) — the dimension that dominates connection-churn workloads.

> **📊 [Final consolidated report → `results/REPORT-mongo-vs-documentdb-churn-benchmark.md`](results/REPORT-mongo-vs-documentdb-churn-benchmark.md)**
> Full methodology, evidence matrix, result tables, DocumentDB tier comparison, and the MongoDB-vs-DocumentDB
> conclusion — all backed by measured log data.

> **Worst-case by design.** New client per task (`maxPoolSize=1`, `minPoolSize=0`), so this does **NOT**
> represent typical connection-pool application performance. No pass/fail thresholds — prioritize the
> **p90 / p99** tail latencies over averages.

---

## Test structure

Two complementary tests share the same tool, dataset, and no-reuse model:

| Test | Question it answers | Traffic model | Per-task work |
|---|---|---|---|
| **Open-loop** (churn) | Can the system meet throughput/latency under continuous average load? | Open-loop Poisson arrivals (rate independent of response → exposes saturation) | Full **4-op cycle**: `find`→`remove`→`insert`→`find` |
| **Hold** (saturation) | How far can concurrency be sustained, and where does it bottleneck? | Closed-loop gate parking a fixed population (**12,000 combined**, 4,000/host) | Keepalive `find` holding one connection Ready for the window |

**Shared parameters:** 100,000-document dataset (~4.4 GiB, fixed seed 42 → byte-identical across targets),
3 synchronized generator hosts, 3 iterations, all 100k docs warmed before each run. Concurrency =
combined per-second SUM of each host's driver `ActiveReady` gauge.

---

## Targets

Final comparison spans **7 configurations** (all full 4-op workload, both tests):

| Backend | Configurations tested | Env var | Notes |
|---|---|---|---|
| **MongoDB sharded** (self-managed) | **2-router** (baseline), **4-router** (scaled-out) | `BMT_CONN_MONGO_SHARD` | 2-shard MongoDB 7.0; tasks pinned round-robin to `mongos` router(s), `directConnection=true`. TLS (self-signed CA) + SCRAM. |
| **Azure DocumentDB** (Cosmos vCore) | **1-shard** M80/M200, **2-shard** M60/M80/M200 | `BMT_CONN` | `mongodb+srv://` SRV gateway; TLS + SCRAM-SHA-256; `retrywrites` forced on. "2-shard" = data genuinely distributed across both physical shards (see report). |

> **Legacy targets** `mongo-vm` (single node, `BMT_CONN_MONGO`) and `cosmos-ru` (Cosmos RU, `BMT_CONN_COSMOS`)
> remain wired in the tool for earlier rounds but are **not part of the final comparison**.

**Secrets never live in the repo** — connection strings are read at runtime from the env vars above
(set at Machine/User scope on each host). Targets run **one at a time**, never in parallel, so each gets
the generators' full capacity and the comparison stays apples-to-apples.

---

## Results at a glance

Headline measured findings (full detail + caveats in the **[final report](results/REPORT-mongo-vs-documentdb-churn-benchmark.md)**):

| Config | Hold max concurrent | Cleared 10k? | Open-loop conn p99 |
|---|---|---|---|
| Mongo 2-router | 4,714 | ❌ (99.7% VM CPU ceiling) | 240.7 s |
| Mongo 4-router | **12,000** | ✅ | 159.7 s |
| DocDB 1-shard (M80 / M200) | ~11,000 | ✅ (~11k plateau) | 20.8 / 47.4 s |
| DocDB 2-shard (M60 / M80 / M200) | **12,000** | ✅ (all tiers) | 45.1 / 22.3 / 27.1 s |

- **The bottleneck is per-connection TLS+SCRAM establishment, not the database engine** — DocumentDB server
  CPU stayed idle (~1.5%) in every run; mongo's fix was router CPU headroom (2→4 routers), not engine tuning.
- **Genuine 2-shard distribution** lets DocumentDB clear the full 12,000 gate at every tier (vs ~11k single-shard)
  and cuts hold keepalive p99 ~4–5×, by engaging a second connection front-end.
- **Tier size drives churn throughput / warm-op latency, not hold concurrency** (which is data-distribution-bound).

---

## Requirements

- **.NET 8 SDK** (LTS).
- **MongoDB C# Driver 2.30** (pinned; restored automatically).
- Network reachability from VM1 to all three backends (private endpoints resolve to RFC1918 from VM1).
- **Host TCP tuning on every load-generator VM** (ephemeral ports + `TcpTimedWaitDelay`) — without it the
  burst scenario fails with port exhaustion. See below and **[`docs/ENVIRONMENT-SETUP.md`](docs/ENVIRONMENT-SETUP.md)**.

> **Recreating the whole environment?** [`docs/ENVIRONMENT-SETUP.md`](docs/ENVIRONMENT-SETUP.md) is the
> blueprint for the load-generator hosts, the required OS/TCP modifications, the MongoDB active/standby
> (AZ3/AZ1) replica-set topology, the DocumentDB / Cosmos-RU settings, and the network/DNS wiring needed
> for a faithful re-run. This README covers the *tool*; that doc covers the *environment*.

---

## Project layout

```
Bmt.sln
src/
  Bmt.Core/        # shared types: config, target->env mapping, flat Calc{Input,Output}Doc models,
                   #   per-Task no-reuse connection factory, ReqId index spec, error taxonomy,
                   #   Cosmos 429 backoff, metrics models (RunResult, LatencyDigest)
  Bmt.Seeder/      # prepare-data : seed 100k + create ReqId indexes (idempotent/resumable)
                   # clean-output : empty only calc_output after a campaign (batched, Cosmos-429-aware)
  Bmt.Preflight/   # preflight    : the 10 mandatory pre-run checks (gate)
  Bmt.LoadGen/     # test         : the timed connection-churn run (open-loop churn / saturation hold)
  Bmt.Report/      # report / merge: results JSON/CSV -> self-contained HTML; multi-host per-iteration merge
test/
  Bmt.Tests/       # xUnit suite: arrival/scheduler/drain decomposition, connection-lifecycle gauges,
                   #   target-endpoint resolution, and the per-iteration multi-host merge contract
config/
  production/      # full 100k dataset, 3 iterations x 300 s arrival window (+ drain):
    full-workload.json   #   4-op cycle: find-input -> remove -> insert -> find-output (canonical; burst-only via run.json)
    single-find.json     #   single-op: find(calc_input) only — isolates cold read latency (burst-only)
    single-insert.json   #   single-op: insert(calc_output) only — isolates cold write latency (burst-only)
    # Per-scenario variants (pin exactly ONE scenario so an individual run never stacks arrival rates):
    full-workload-steady.json / full-workload-burst.json
    single-find-steady.json   / single-find-burst.json
    single-insert-steady.json / single-insert-burst.json
    base.json            #   plumbing envelope (dataset/seeder/preflight/client) — rarely edited
    run.json             #   operator hot knobs (iterations/duration/rates/open-loop; burst on, steady off)
  smoke/           # tiny/fast configs for validation (30 s or 40 docs), one per mode:
    connectivity.json    #   40-doc connectivity/sizing/index check
    full-workload.json   #   30 s 4-op cycle
    single-find.json     #   30 s single-op find
    single-insert.json   #   30 s single-op insert
scripts/
  setup/           # one-time host & backend setup: TCP tuning, gen-host bootstrap, TLS, monitor user
    tune-vm1.ps1                 # §7.3 host TCP tuning (ephemeral ports + TcpTimedWaitDelay); -Revert to undo
    vm1-az2-setup-and-run.ps1    # end-to-end host runbook: tune -> prepare-data -> preflight -> test ->
                                 #   clean-output -> commit results (adapt per target)
    Setup-Gen2Host.ps1 / Raise-MongoMaxConn.ps1 / Reset-MongoPassword.ps1 / enable-mongo-tls.ps1
  run/             # campaign execution: preflight, per-host burst, multi-host orchestration, merge
    Invoke-Campaign.ps1 / Run-BurstHost.ps1 / Run-Campaign2Host.ps1 / Merge-Campaign.ps1
    Invoke-Preflight.ps1 / Invoke-Preflight-Portable.ps1
  ops/             # operational helpers between/around runs
    cosmos-ru.ps1                # show/raise/min the shared Cosmos RU/s for cost control (-Set/-Min/-Show)
    diag-mongo-start.ps1 / read-mongo-log.ps1 / Reseed-MongoShard.ps1
infra/             # provision/destroy the Azure backends + private networking (each subfolder is self-contained)
  cosmos/          # Terraform to recreate the cosmos-ru account + bmt_db + collections + PE/DNS
  documentdb-private-endpoint/  # VNet peering + private DNS so VM1 reaches DocumentDB privately
    README.md                   #   manual procedure + validation checklist
    setup-private-endpoint.ps1  #   automation for the same (-Cleanup to tear down)
docs/
  ENVIRONMENT-SETUP.md  # reference blueprint to recreate the full environment: load-gen hosts,
                        #   OS/TCP tuning, MongoDB active/standby topology, backend settings, network wiring
results/           # benchmark campaigns: results/<campaign>/<target-run>/ + comparison HTML + summary
                   #   published, EXCEPT *.log (raw console logs may echo private IPs) which are ignored
artifacts/         # preflight JSON artifacts (git-ignored)
```

Each tool is a separate executable. You can run them with `dotnet run --project <proj>` or directly
from the built DLL (`dotnet <assembly>.dll`).

---

## CLI usage

### 1. `prepare-data` — seed + index (Bmt.Seeder)

```powershell
dotnet run --project src/Bmt.Seeder -- prepare-data --config config/production/full-workload-open-loop-3host.json --target mongo-shard
dotnet run --project src/Bmt.Seeder -- prepare-data --config config/production/full-workload-open-loop-3host.json --target documentdb
```

Loads **exactly 100,000** documents into `calc_input` (four whole-document size buckets, fixed RNG
seed 42 -> byte-identical across targets) and creates the `ReqId` index on **both** `calc_input`
and `calc_output`. Idempotent and resumable (`--force` empties both collections first via small batched deletes).

#### `clean-output` — empty `calc_output` after a campaign (Bmt.Seeder)

```powershell
dotnet run --project src/Bmt.Seeder -- clean-output --config config/production/full-workload-open-loop-3host.json --target documentdb
```

Empties **only** `calc_output` via small batched deletes, leaving `calc_input` and the `ReqId` index
intact — much lighter than `prepare-data --force`. **Run this after every campaign.**

### 2. `preflight` — the mandatory gate (Bmt.Preflight)

```powershell
dotnet run --project src/Bmt.Preflight -- preflight --config config/production/full-workload-open-loop-3host.json --target documentdb --warmup
```

Runs the pre-run checks and writes a JSON artifact to `artifacts/`. Exit `0` = may proceed (pass/warn),
`3` = abort (a check failed). `--warmup` performs the untimed 100k-doc data-cache pre-read.

> **Host TCP tuning — required for the churn workload.** Each closed socket holds an ephemeral port in
> `TIME_WAIT`, so sustainable churn ≈ `ephemeral_port_count / TcpTimedWaitDelay`. Windows defaults
> (16,384 ports / 120 s ≈ **137 conn/s**) are far below the target and preflight will WARN. Run
> `scripts\setup\tune-vm1.ps1` (elevated) on each generator — it widens the ephemeral range to 10000–65534
> and sets `TcpTimedWaitDelay=30 s` (≈ 1,851 conn/s); `-Revert` restores defaults.

### 3. `test` — the timed churn run (Bmt.LoadGen)

```powershell
# open-loop churn (single-host invocation; the final runs use the 3-host coordinator below)
dotnet run --project src/Bmt.LoadGen -- test --config config/production/full-workload-open-loop-3host.json --target documentdb --scenario burst
```

Warms the cache -> runs the preflight gate (aborts on FAIL unless `--no-preflight`) -> executes the run ->
writes a JSON run artifact + per-second/latency CSVs to `results/`.

Options: `--scenario steady|burst|both` (default `both`), `--duration-sec N` (smoke override),
`--results <dir>`, `--no-preflight` (NOT recommended). **Workload/test mode is chosen by which config you
pass** — `full-workload-open-loop-3host.json` (open-loop) or `full-workload-hold-3host.json` (hold).

### 4. `report` — self-contained HTML (Bmt.Report)

```powershell
dotnet run --project src/Bmt.Report -- report --input results/<campaign>/ --output results/<campaign>/comparison-<ts>.html
```

Consumes one or more target result sets from the campaign folder (plus any preflight JSON) and produces a
single self-contained HTML report. Run `test` once per `--target` (with
`--results results/<campaign>`), then run `report` over that campaign directory.

---

## Configuration (`config/`)

Configs are split into **`config/production/`** (full 100k dataset) and **`config/smoke/`** (tiny/fast
validation). **The workload mode is selected by which config you pass** — there is no CLI flag for it.

**Final-run configs** (the 3-host open-loop + hold tests behind the report):

| Test | Config | Key knobs |
|---|---|---|
| **Open-loop churn** | `config/production/full-workload-open-loop-3host.json` | Open-loop Poisson `JobsPerSecondLambda=4.0`/host, `MinTasksPerJob..MaxTasksPerJob=150..500`, `TaskSleepMs=2900` |
| **Saturation hold** | `config/production/full-workload-hold-3host.json` | Closed-loop gate `MaxConcurrentTasks=4000`/host (12,000 combined), `Workload.Mode=Hold` (keepalive `find`), `TaskSleepMs=10000` |

Both warm all 100,000 docs (`WarmupSampleSize=100000`) before the timed phase.

**Other production/smoke configs** (single-op isolation + smoke checks) remain available:

| Workload | Production config | Smoke config | `Workload` block |
|---|---|---|---|
| Full 4-op cycle (canonical) | `config/production/full-workload.json` | `config/smoke/full-workload.json` | `Mode=FullWorkload` |
| Single-op **find** (cold read) | `config/production/single-find.json` | `config/smoke/single-find.json` | `Mode=SingleOp`, `SingleOpType=FindInput` |
| Single-op **insert** (cold write) | `config/production/single-insert.json` | `config/smoke/single-insert.json` | `Mode=SingleOp`, `SingleOpType=InsertOutput` |
| Connectivity / sizing check | — | `config/smoke/connectivity.json` | `Mode=FullWorkload` (40 docs) |

> **Single-op insert accumulates** docs in `calc_output` (no remove), so the collection grows for the whole
> campaign. Run `clean-output` before **and** after an insert campaign (and record the starting count); it
> empties only `calc_output` without re-seeding the 100k input.

Config keys (all configs share this shape):

- `TaskSleepMs` — calc-time substitute sleep between the input-find and output-remove (open-loop 2,900 ms;
  hold 10,000 ms keepalive interval; **0** and skipped in single-op modes).
- `Dataset` — `DocumentCount` (100,000), `Seed` (42), and the four whole-document size `Buckets`
  (6 KB×10,000 / 16 KB×15,000 / 50 KB×35,000 / 58 KB×40,000; mean ≈ 43.7 KB, total ≈ 4.37 GB).
- `Seeder` — insert/delete batch sizes.
- `Preflight` — expected server values (tier, max connections) and host-headroom thresholds; `WarmupSampleSize`.
- `Scenario` — `Iterations` (3), `MaxConcurrentTasks`, resource sample interval, and the arrival model:
  - **Open-loop**: Poisson `JobsPerSecondLambda`, `MinTasksPerJob..MaxTasksPerJob` (`Burst.OpenLoop=true`).
  - **Hold**: closed-loop gate (`Burst.OpenLoop=false`) parking `MaxConcurrentTasks` connections/host.
- `Workload` — `Mode` (`FullWorkload` | `Hold` | `SingleOp`) and `SingleOpType` (`FindInput` | `InsertOutput`).

---

## The Task (unit under test)

Each Task = a brand-new connection and **exactly four DB operations in this order**, all keyed on the
**`ReqId` field** (never the `_id` point-read):

1. `find` input — `calc_input` by `ReqId`
2. *(sleep `taskSleepMs`)* — excluded from per-op latency, included in the full cycle latency
3. `remove` output — `calc_output` by `ReqId` (**mandatory, never upsert**)
4. `insert` output — `calc_output`
5. `find` output — `calc_output` by `ReqId`

No client / session / cursor / pool is reused across Tasks (HARD constraint). The connection is actually
released after each Task.

---

## Output artifacts

Run artifacts are organised as **campaigns**: one folder per benchmark campaign under `results/`
(e.g. `results/run-20260802-01/`) holding one **per-target run subfolder** plus the comparison report and
summary. Each grouped run folders results as `<target>-<tier>-<test>/iter-NN/`. Point `--results` at the
campaign folder so a campaign's runs group together.

- `results/<campaign>/<run-id>/<run-id>.json` — the full machine-readable `RunResult`: totals + **open-loop
  generator fidelity** (`TasksScheduled`/`TasksStarted`, scheduled/started per-sec on the 300 s arrival-window
  denominator, scheduler-queue / execution / authoritative offered-to-finished latency), the explicit
  **`Arrival`** window/drain model (arrival & drain bounds, outstanding-at-stop, max drain backlog), the
  driver-event **`Lifecycle`** model (created/ready/failed/closed, peak active-connecting / active-ready /
  waiting-for-server, demand-to-ready + driver-open latency, reconciliation), per-op + cycle + connection
  latency percentiles, error taxonomy, per-second throughput, client-host resource samples, and the
  **`TargetTcp`** target-filtered TCP telemetry (resolved endpoint set, per-state peaks, dropped samples).
- `results/<campaign>/<run-id>/<run-id>-timeseries.csv` — one row per second (scheduled/started tasks,
  connection created/ready/failed/closed rates, active-connecting / active-ready / waiting-for-server gauges,
  per-op QPS, in-flight Tasks, ephemeral ports, TIME_WAIT, handles, CPU%, working set).
- `results/<campaign>/<run-id>/<run-id>-latency.csv` — per-op + cycle + scheduler-queue + execution +
  offered-to-finished + connection + demand-to-ready + driver-open latency percentiles.
- `results/<campaign>/<run-id>/<run-id>-target-tcp.csv` — per-second (sub-second-peak) target-specific TCP
  states (SYN_SENT / ESTABLISHED / TIME_WAIT / CLOSE_WAIT / FIN_WAIT_1/2, socket + distinct-local-port
  totals) plus host-wide totals and ephemeral-port utilization.
- `results/<campaign>/<run-id>/<run-id>.log` — captured console log (**git-ignored**; see below).
- `results/<campaign>/comparison-<ts>.html` — the self-contained comparison report.
- `results/<campaign>/summary-<...>.md` — a concise metrics summary. The consolidated cross-campaign report
  lives at [`results/REPORT-mongo-vs-documentdb-churn-benchmark.md`](results/REPORT-mongo-vs-documentdb-churn-benchmark.md).

**Confidentiality / publishing.** Results are committed to the repo **except `*.log`**. Connection
strings in the published JSON/CSV/HTML are masked for credentials **and** host/IP/`appName` (internal
Azure hostnames and private IPs are redacted to `****`). Raw `.log` files are git-ignored because
preflight prints resolved **private IPs** verbatim (to prove the path is private); the same information,
masked, survives in the published artifacts.

The `report` loader scans the campaign directory recursively, so the per-run grouping does not change how
reports are generated.

---

## Interpretation guide

**Prioritize p90 / p99 tails over the average.** Connection-churn latency is dominated by tail events —
TCP/TLS handshakes, auth, server-selection, and (on managed services) gateway throttling. Averages hide
these, so the **tail percentiles are the headline numbers**.

**Connection created/closed ratio.** In a correct no-reuse run, **connections created ≈ closed ≈ number of
tasks** (`created/task ≈ 1.0`). A ratio well below 1.0 means connections were reused (constraint violated);
a large created-vs-closed gap means connections leaked. The report surfaces this directly.

**Bottleneck location is the headline for the hold test.** The measured ceiling in every configuration is
**per-connection TLS+SCRAM establishment**, not the database engine — DocumentDB server CPU stays idle
(~1.5%) while mongo's 2-router config saturates its VM CPU at 99.7%. Read the hold results for *where and
how* each system fails, not just the max connection count.

**Index assumption (critical).** Results assume a `ReqId` index on **both** `calc_input` and `calc_output`
on every target (created by `prepare-data`, verified by `preflight`). An unindexed run forces collection
scans and is **not** a valid comparison.

**DocumentDB Mongo-compatibility.** Cosmos vCore is Mongo-compatible but not identical; unsupported
commands surface as a `DocumentDbCompatibility` error bucket — investigate those before drawing performance
conclusions.

**This benchmark does NOT represent pooled-connection app performance.** It intentionally measures the
worst case (churn, no reuse) to compare how each backend tolerates connection storms. Do not extrapolate to
a pooled workload.

---

## Typical campaign workflow

```powershell
# Per target (one at a time; --results points every run at the same campaign folder).
# Swap the config to choose the test: full-workload-open-loop-3host.json (churn) or full-workload-hold-3host.json (hold):
dotnet run --project src/Bmt.Seeder    -- prepare-data --config config/production/full-workload-open-loop-3host.json --target <key>
dotnet run --project src/Bmt.Preflight -- preflight    --config config/production/full-workload-open-loop-3host.json --target <key> --warmup
dotnet run --project src/Bmt.LoadGen   -- test         --config config/production/full-workload-open-loop-3host.json --target <key> --scenario burst --results results/<campaign>

# After every campaign: empty calc_output.
dotnet run --project src/Bmt.Seeder    -- clean-output --config config/production/full-workload-open-loop-3host.json --target <key>

# After all targets have run:
dotnet run --project src/Bmt.Report    -- report --input results/<campaign>/ --output results/<campaign>/comparison-<ts>.html
```

> `<key>` = `mongo-shard` or `documentdb`. The final multi-host runs use the coordinator below rather than
> single-host `test` invocations.

---

## Multi-host coordinated campaign (3 hosts → ≥12,000 concurrent)

A single generator host cannot reach the burst envelope without exhausting its own ephemeral ports / TLS
CPU, so the peak is produced by **three co-located AZ1 generators** driven by a central coordinator. The
coordinator (`scripts/run/Invoke-Campaign.ps1`) **owns the iteration loop**: for each of the 3 iterations it
computes one shared UTC start instant, launches **exactly one iteration** on hosts 1/2/3 with that start,
waits for all three to finish (including drain), validates every host reported, and only then advances —
rerunning the whole three-host iteration on any failure (never continuing with a partial host set).

```powershell
# From the operator box (per target, sequential — never two targets at once):
.\scripts\run\Invoke-Campaign.ps1 -Target documentdb -Iterations 3 -PushResults

# Once every host has pushed its results, merge per synchronized iteration and prove the envelope:
.\scripts\run\Merge-Campaign.ps1 -RunTag <campaign> -InputDir results
```

`report merge` groups **per (target, scenario, iteration)**, requires the exact host set `{1,2,3}`, dedupes
retries to the latest attempt per host, and reports start-time skew, combined offered/started rates, peak
combined **connections-created/s** and **connections-ready/s** (≥ 1,200), peak combined **ActiveReady** (the
authoritative concurrency verdict, ≥ 11,000 — in-flight Task count is a generator diagnostic only), failure
rate, true offered-to-finished p99, and drain duration, then a cross-iteration mean/min/max over the valid
iterations. See [`docs/metrics-reference.md`](docs/metrics-reference.md) §8 for the full metric semantics.

---

## Tests

A .NET xUnit suite under `test/Bmt.Tests` locks down the benchmark-correctness invariants (open-loop
arrival/drain decomposition, connection-lifecycle gauges, target-endpoint resolution, and the per-iteration
multi-host merge contract). Run it before a campaign:

```powershell
dotnet test test/Bmt.Tests/Bmt.Tests.csproj -c Release
```
