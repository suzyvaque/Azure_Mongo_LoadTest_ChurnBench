# Azure Mongo Load-Test — Connection-Churn Benchmark

A MongoDB-wire-protocol **connection-churn** benchmark for comparing three Azure Mongo-compatible
backends under an HPC-style workload where **every Task opens a brand-new connection and closes it**
(no pooling/reuse between requests). It seeds a fixed 100,000-document dataset, runs a strict 4-operation
Task cycle under steady + bursty load, and produces a self-contained HTML comparison report.

> **This benchmark deliberately models the worst case for connection handling** (new client per request,
> `maxPoolSize=1`, `minPoolSize=0`). It does **NOT** represent typical long-lived connection-pool
> application performance. It is a comparison study with **no pass/fail thresholds** — prioritize the
> **p99 / p95 / p99.9** tail latencies over averages.

---

## Targets

| CLI `--target` | Backend | Env var (connection string) | Notes |
|---|---|---|---|
| `mongo-vm` | MongoDB on a VM (single-node `rs0`) | `BMT_CONN_MONGO` | `authSource=admin`; only `_id` index by default, so `prepare-data` adds the `ReqId` index. |
| `cosmos-ru` | Azure Cosmos DB for MongoDB (RU) | `BMT_CONN_COSMOS` | `RetryWrites=false`; **fixed provisioned RU/s** (held constant within a campaign, never auto-raised); 429/`RetryAfterMs` backoff; `ReqId` index is **non-unique** (platform constraint). |
| `documentdb` | Azure DocumentDB / Cosmos vCore | `BMT_CONN` | `mongodb+srv://` SRV resolution; `retrywrites=false` in URI. |

**Secrets never live in the repo.** Connection strings are read at runtime from the env vars above. On
VM1 set them at User scope, e.g.:

```powershell
[Environment]::SetEnvironmentVariable("BMT_CONN_MONGO", "mongodb://user:pass@10.3.0.4:27017/bmt_db?replicaSet=rs0&authSource=admin", "User")
[Environment]::SetEnvironmentVariable("BMT_CONN_COSMOS", "<cosmos-ru connection string>", "User")
[Environment]::SetEnvironmentVariable("BMT_CONN",        "<documentdb mongodb+srv connection string>", "User")
```

Targets are run **one at a time on VM1, never in parallel**, so the generator's full capacity is
available to each and the comparison stays apples-to-apples.

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
  Bmt.LoadGen/     # test         : the timed connection-churn run (Scenario A steady + B burst)
  Bmt.Report/      # report       : results JSON/CSV -> self-contained HTML
config/
  production/      # full 100k dataset, 3 iterations x 30 min, steady + burst:
    full-workload.json   #   4-op cycle: find-input -> remove -> insert -> find-output (canonical run)
    single-find.json     #   single-op: find(calc_input) only — isolates cold read latency
    single-insert.json   #   single-op: insert(calc_output) only — isolates cold write latency
  smoke/           # tiny/fast configs for validation (30 s or 40 docs), one per mode:
    connectivity.json    #   40-doc connectivity/sizing/index check
    full-workload.json   #   30 s 4-op cycle
    single-find.json     #   30 s single-op find
    single-insert.json   #   30 s single-op insert
scripts/
  tune-vm1.ps1     # §7.3 host TCP tuning (ephemeral ports + TcpTimedWaitDelay); -Revert to undo
  cosmos-ru.ps1    # show/raise/min the shared Cosmos RU/s for cost control between rounds (-Set/-Min/-Show)
  vm1-az2-setup-and-run.ps1  # end-to-end VM1 runbook: tune -> prepare-data -> preflight -> test ->
                   #   clean-output -> commit results (DocumentDB AZ2 host; adapt per target)
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
dotnet run --project src/Bmt.Seeder -- prepare-data --config config/production/full-workload.json --target mongo-vm
dotnet run --project src/Bmt.Seeder -- prepare-data --config config/production/full-workload.json --target documentdb
dotnet run --project src/Bmt.Seeder -- prepare-data --config config/production/full-workload.json --target cosmos-ru
```

Loads **exactly 100,000** documents into `calc_input` (four whole-document size buckets, fixed RNG
seed 42 -> byte-identical across targets) and creates the `ReqId` index on **both** `calc_input`
(unique, except non-unique on `cosmos-ru`) and `calc_output`. Idempotent and resumable
(`--force` empties both collections first via small batched deletes).

#### `clean-output` — empty `calc_output` after a campaign (Bmt.Seeder)

```powershell
dotnet run --project src/Bmt.Seeder -- clean-output --config config/production/full-workload.json --target mongo-vm
```

Empties **only** `calc_output` via small batched (Cosmos-429-aware) deletes, leaving `calc_input`
and the `ReqId` index intact — much lighter than `prepare-data --force`, which re-seeds the full
100k input. **Run this after every campaign**, and it is **required after an insert-only run**:
single-op insert never removes, so `calc_output` grows without bound (see below). Does not change
provisioned Cosmos RU/s.

### 2. `preflight` — the mandatory gate (Bmt.Preflight)

```powershell
dotnet run --project src/Bmt.Preflight -- preflight --config config/production/full-workload.json --target mongo-vm --warmup
```

Runs the 10 §6.3 checks and writes a JSON artifact to `artifacts/`. Exit `0` = may proceed (pass/warn),
`3` = abort (a check failed). `--warmup` performs the untimed data-cache pre-read; `--verify-distinct`
runs a full distinct-ReqId aggregation (RU-heavy on `cosmos-ru`).

> **Host tuning (§7.3) — required for the burst scenario.** The churn workload opens a fresh connection
> per Task; each closed socket holds an ephemeral port in `TIME_WAIT`, so sustainable churn ≈
> `ephemeral_port_count / TcpTimedWaitDelay`. Windows defaults (16,384 ports / 120 s ≈ **137 conn/s**) are
> far below the Scenario B target of **≥ 1,200 conn/s** and preflight check 7 will WARN. Run
> `scripts\tune-vm1.ps1` (elevated) on VM1 before a real run — it widens the ephemeral range to
> 10000–65534 and sets `TcpTimedWaitDelay=30 s` (≈ 1,851 conn/s); `-Revert` restores defaults.

### 3. `test` — the timed churn run (Bmt.LoadGen)

```powershell
dotnet run --project src/Bmt.LoadGen -- test --config config/production/full-workload.json --target mongo-vm --scenario both
```

Warms the cache -> runs the preflight gate (aborts on FAIL unless `--no-preflight`) -> executes the
selected scenario(s) -> writes a JSON run artifact + per-second/latency CSVs to `results/`.

Options: `--scenario steady|burst|both` (default `both`), `--duration-sec N` (override each scenario's
duration for short smoke runs), `--results <dir>`, `--no-preflight` (NOT recommended).

**Workload mode is chosen by which config you pass** (see the table below) — e.g.
`config/production/single-find.json` for find-only or `config/production/single-insert.json` for
insert-only. All three production configs run 3 iterations × 30 min, steady + burst.

### 4. `report` — self-contained HTML (Bmt.Report)

```powershell
dotnet run --project src/Bmt.Report -- report --input results/<campaign>/ --output results/<campaign>/comparison-3way-steady-burst-<ts>.html
```

Consumes one or more target result sets from the campaign folder (plus any preflight JSON) and produces a
single self-contained HTML report. To compare all three candidates, run `test` once per `--target` (with
`--results results/<campaign>`), then run `report` over that campaign directory.

---

## Configuration (`config/`)

Configs are split into **`config/production/`** (full 100k dataset, 3 × 30 min, steady + burst) and
**`config/smoke/`** (tiny/fast validation). **The workload mode is selected by which config you pass** —
there is no CLI flag for it:

| Workload | Production config | Smoke config | `Workload` block |
|---|---|---|---|
| Full 4-op cycle (canonical) | `config/production/full-workload.json` | `config/smoke/full-workload.json` | `Mode=FullWorkload` |
| Single-op **find** (cold read) | `config/production/single-find.json` | `config/smoke/single-find.json` | `Mode=SingleOp`, `SingleOpType=FindInput` |
| Single-op **insert** (cold write) | `config/production/single-insert.json` | `config/smoke/single-insert.json` | `Mode=SingleOp`, `SingleOpType=InsertOutput` |
| Connectivity / sizing check | — | `config/smoke/connectivity.json` | `Mode=FullWorkload` (40 docs) |

> **Single-op insert accumulates** docs in `calc_output` (no remove), so the collection grows for the whole
> campaign. Run `clean-output` before **and** after an insert campaign (and record the starting count); it
> empties only `calc_output` without re-seeding the 100k input. See the header comment in
> `config/production/single-insert.json`.

Config keys (all configs share this shape):

- `TaskSleepMs` — calc-time substitute sleep between the input-find and output-remove (default 10,000 ms;
  **0** and skipped entirely in single-op modes).
- `Dataset` — `DocumentCount` (100,000), `Seed` (42), and the four whole-document size `Buckets`
  (6 KB×10,000 / 16 KB×15,000 / 50 KB×35,000 / 58 KB×40,000; mean ≈ 43.7 KB, total ≈ 4.37 GB). Sizes are
  **whole-BSON-document** targets — the `Input` field is padded so the entire doc hits the bucket size.
- `Seeder` — insert/delete batch sizes (`cosmos-ru` uses smaller batches to ease RU throttling).
- `Preflight` — expected server values (RU/s, tier, max connections) and host-headroom thresholds.
- `Scenario` — `Iterations` (production 3), `IterationDurationSeconds` (production 1800 — overrides each
  scenario's `DurationSeconds`), `MaxConcurrentTasks`, resource sample interval, and the two scenarios:
  - **Steady (A)**: `TasksPerSecond` 135.
  - **Burst (B)**: Poisson `JobsPerSecondLambda` 0.57, `MinTasksPerJob`..`MaxTasksPerJob` 14..500.
- `Workload` — `Mode` (`FullWorkload` | `SingleOp`) and `SingleOpType` (`FindInput` | `InsertOutput`).

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
(e.g. `results/phase1-3way-steady-burst-20260616/`) holding one **per-target run subfolder** plus the
comparison report and summary. Each `test` run writes its own `results/<run-id>/` subfolder, where the
run id is `<target>-<scenario>-<yyyyMMdd-HHmmss>` — the scenario token `steady-burst` means Scenario A
steady + Scenario B burst in one window. Point `--results` at the campaign folder so a campaign's runs
group together. See each campaign's `INDEX.md` for a manifest.

- `results/<campaign>/<run-id>/<run-id>.json` — the full machine-readable `RunResult` (totals, per-op +
  cycle + connection-open + client-create latency percentiles, connection counters, reuse verification,
  error taxonomy, per-second throughput, client-host resource samples).
- `results/<campaign>/<run-id>/<run-id>-timeseries.csv` — one row per second (connection open/close rates,
  per-op QPS, in-flight Tasks, ephemeral ports, TIME_WAIT, handles, CPU%, working set).
- `results/<campaign>/<run-id>/<run-id>-latency.csv` — per-op + cycle + connection latency percentiles.
- `results/<campaign>/<run-id>/<run-id>.log` — captured console log (**git-ignored**; see below).
- `results/<campaign>/comparison-3way-<scenario>-<ts>.html` — the self-contained comparison report.
  Its title carries the identifier from `--output`, so name it `comparison-3way-<scenario>-<ts>.html`.
- `results/<campaign>/summary-3way-<scenario>-<ts>.md` — a concise metrics summary.

**Summary template (keep for every campaign).** The `summary-3way-*.md` follows a fixed layout so
comparisons stay consistent across runs:
- A grouped, colour-banded **HTML** comparison table (`<tr style="background:...">`), one colour band per
  metric family (Headline, Total cycle, connectionOpen, find-cold-socket, find-warm-socket, remove, insert)
  using a merged `Group` column via `rowspan`.
- `find` is always split into **cold socket** (op1 `find_input`, new connection — pays TCP+TLS+auth) and
  **warm socket** (op4 `find_output`, same query on the open connection) so the connection tax is isolated.
- In every row, the **best-performing value is bold + underlined + green** (`<u><b style="color:#1a7f37">…</b></u>`); the underline is the fallback for viewers that ignore the colour.
- A **cost-component table** stating what the op timer does/does not include (data-cache miss removed via
  §6.5 warm-up; cold connection setup kept as the variable under test; `taskSleepMs` excluded from op
  timers; pure query cost visible on the warm socket).
- A `## Key Findings` section (3-5 bullets) and a `> Migration decision guide` callout.
  Note: inline `<tr>` colours render in VS Code/most viewers but GitHub.com strips them (table still renders).

**Confidentiality / publishing.** Results are committed to the repo **except `*.log`**. Connection
strings in the published JSON/CSV/HTML are masked for credentials **and** host/IP/`appName` (internal
Azure hostnames and private IPs are redacted to `****`). Raw `.log` files are git-ignored because
preflight check-3 prints the resolved **private IPs** verbatim (to prove the path is private); the same
information, masked, survives in the published artifacts.

The `report` loader scans the campaign directory recursively, so the per-run grouping does not change how
reports are generated.

---

## Interpretation guide (§9)

**Why prioritize p95 / p99 / p99.9 over the average.** Connection-churn latency is dominated by tail
events — TCP/TLS handshakes, auth, server-selection, and (on managed services) throttling. Averages hide
these. A backend with a great mean but a terrible p99.9 will stall the real burst that this workload
recreates, so the **tail percentiles are the headline numbers**; the average is informational only.

**How to read the connection created/closed ratio.** In a correct no-reuse run, **connections created ≈
connections closed ≈ number of Tasks** (one fresh connection opened and released per Task), so
`created/Task ≈ 1.0`. A ratio well below 1.0 means connections were reused (constraint violated); a large
gap between created and closed means connections leaked or were not released. The report's
**connection-reuse verification** box surfaces this directly. (An internal driver pool object may exist,
but no pooling/reuse occurs between requests; within a single Task the four ops legitimately share that
Task's one connection.)

**How Cosmos RU throttling affects results.** `cosmos-ru` runs at a **fixed provisioned RU/s** budget
(held constant within a campaign, never auto-raised — even for seeding). When the workload exceeds it, the
server returns **429 / `RetryAfterMs`**. During the **timed run** these are **classified and recorded as
`CosmosRuThrottling`** (a separate error bucket) rather than silently retried, so throttling shows up as
failures/latency in the report instead of being hidden. High `CosmosRuThrottling` counts mean the workload
is RU-bound, not latency-bound — compare Cosmos against the others with that in mind. The exact RU/s in
force for any given run is recorded in that campaign's `results/<campaign>/INDEX.md`, not here.

**How to read DocumentDB Mongo-compatibility limits.** `documentdb` (Cosmos vCore) is Mongo-compatible
but not identical. Unsupported commands/features surface as the **`DocumentDbCompatibility`** error
bucket. A non-zero count there indicates the workload hit a compatibility gap rather than a performance
limit — investigate the specific command before drawing performance conclusions.

**Cautions comparing Mongo-on-VM vs. managed services.** `mongo-vm` is a single self-managed node; the
managed services include their own routing/replication/throttling layers. Two effects are **not**
modeled and can make real `mongo-vm` behavior worse than the steady-warm numbers here: (a) **post-load
cold start** — the first wave of Tasks right after the daily bulk load hits a partially cold
`calc_input`; and (b) **mid-day input updates** (Model B append-only / Model C mutable-in-place). The
benchmark runs warm-cache Model A only, so it is fair for the majority of the day but does not capture
Mongo's daily cold edge. The report includes a Mongo-VM caveat box restating this.

**Index assumption (critical).** Results assume a `ReqId` index is present on **both** `calc_input` and
`calc_output` on every target (created by `prepare-data`, verified by `preflight`). An unindexed run
forces full collection scans and is **not** a valid comparison — the Cosmos RU figures in particular
become meaningless (every keyed op would burn RU scanning). On `cosmos-ru` the `calc_input` `ReqId`
index is **non-unique** by platform constraint; distinct-ReqId is still guaranteed because `ReqId == _id`
by construction and `_id` is system-unique on all backends. The report shows this divergence explicitly.

**Starting-state assumptions and their limits.** Results reflect a **warm data cache + cold connection
state** under **Model A (immutable input)**. Managed services are effectively always warm, so the
warm-cache comparison is fair for most of the day; it does not model Mongo's daily cold start or mid-day
updates (see above).

**This benchmark does NOT represent typical connection-pool app performance.** Production apps reuse
long-lived pooled connections; this tool intentionally measures the opposite (churn) to compare how each
backend tolerates connection storms. Do not extrapolate these numbers to a pooled workload.

---

## Typical Phase-1 workflow

```powershell
# Per target (one at a time on VM1); --results points every run at the same campaign folder.
# Swap the production config to choose the workload (full-workload / single-find / single-insert):
dotnet run --project src/Bmt.Seeder    -- prepare-data --config config/production/full-workload.json --target <key>
dotnet run --project src/Bmt.Preflight -- preflight    --config config/production/full-workload.json --target <key> --warmup
dotnet run --project src/Bmt.LoadGen   -- test         --config config/production/full-workload.json --target <key> --scenario both --results results/<campaign>

# After every campaign: empty calc_output (REQUIRED after a single-insert run, which accumulates docs).
dotnet run --project src/Bmt.Seeder    -- clean-output --config config/production/full-workload.json --target <key>

# After all three targets have run:
dotnet run --project src/Bmt.Report    -- report --input results/<campaign>/ --output results/<campaign>/comparison-3way-steady-burst-<ts>.html
```
