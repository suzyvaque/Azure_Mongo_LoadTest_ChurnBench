# Package A — Repo cleanup

**Scope:** Git branches, config restructure, script regroup, and `Bmt.Report` code. **No Azure resources,
no VMs, no quota.** Runs on the current dev machine.

**End state:** clean `main`, a tagged pre-cleanup baseline, a buildable solution, and the new tooling
(centralized config, automated markdown + comparison summaries, sequential runner, monitor-user script)
committed and pushed.

> Read [README.md](README.md) first for shared context and conventions.

---

## A0 — Precondition

1. Commit or stash current work-in-progress.
2. Tag a pre-cleanup baseline: `git tag pre-cleanup-20260720`.
3. Confirm a known-good build: `dotnet build Bmt.sln`.

---

## A1 — Git branch cleanup (DESTRUCTIVE — requires explicit go-ahead + merge verification)

Current branch state (from `.git/packed-refs`):

- **Local:** `main` (keep), `feat/multihost-burst`, `agents/open-loop-burst-multi-client-test`
- **Remote `origin`:** `main`, `feat/multihost-burst`, plus **5 stale `agents/*`**:
  `azure-documentdb-vm-connection`, `full-workload-test-steady-burst`, `mongo-open-loop-test-results`,
  `mongo-vs-documentdb-connection-report`, `open-loop-test-mongodb-results-update`
- `refs/sessions/*` — Copilot internal checkpoints → **do not touch**

Steps:

1. Verify each candidate is merged: `git branch --merged main`, `git log main..<branch> --oneline`.
2. Delete the **6 `agents/*`** branches and **`feat/multihost-burst`** — local and origin — **only where
   confirmed merged**. Keep only `main`. Push deletions to origin. Reflog is retained for recovery.
3. If any branch is **not** merged, **stop and ask** before deleting it.

---

## A2 — Config restructure (new Extends layer) + Azure identity

Loader: `src/Bmt.Core/Configuration/BmtConfig.cs` → `LoadMergedObject()` (follows `Extends`, deep-merges,
max depth 10; child wins, nested objects merge key-by-key, scalars/arrays replace).

1. Split `config/production/common.json` into:
   - `base.json` — plumbing only (`Dataset`, `Seeder`, `Client`, `Preflight`). Rarely edited.
   - `run.json` — `Extends: "base.json"`; holds the **operator hot knobs** under an `==== EDIT THESE ====`
     banner: `Iterations`, `IterationDurationSeconds` (**single canonical duration** — remove
     `Steady.DurationSeconds` and `Burst.DurationSeconds`, derive from this), `MaxConcurrentTasks`,
     `TaskSleepMs`, `Steady { Enabled, TasksPerSecond }`,
     `Burst { Enabled, JobsPerSecondLambda, MinTasksPerJob, MaxTasksPerJob, OpenLoop }`.
2. Repoint every child config's `Extends`:
   - Workload configs (`full-workload.json`, `single-find.json`, `single-insert.json`) → `Extends: "run.json"`,
     overriding only `Workload` (and `TaskSleepMs: 0` for single-op).
   - Scenario variants (`-steady`, `-burst`, `-open-loop`, `-multihost`, `-2host`) stay thin overrides
     (flip `Enabled` / `OpenLoop` / `lambda`).
3. **Do not touch** `config/smoke/*`.
4. Add `config/production/README.md` documenting the Extends graph and where each knob lives.
5. Add `config/azure-resources.json` — a versioned, single edit point for `az monitor` lookups in Package B:
   `subscription`, `resourceGroup`, DocumentDB cluster name, Cosmos account, mongo VM names, and the
   `mongod.log` path per target. (No secrets — identifiers only.)

**Verify:** after the split, `BmtConfig.Load()` resolves every production config with no missing keys and
identical effective values to before (spot-check `Iterations`, durations, `OpenLoop`).

---

## A3 — Script folder regroup (leave `src/` alone)

Move `scripts/*` into lifecycle subfolders:

- `scripts/setup/` — `tune-vm1`, `vm1-az2-setup-and-run` (renamed to an AZ1 variant in Package B),
  `enable-mongo-tls`, `Setup-Gen2Host`, `Raise-MongoMaxConn`, `Reset-MongoPassword`,
  **+ new `New-MongoMonitorUser.ps1`** (A4)
- `scripts/run/` — `Invoke-Campaign`, `Run-BurstHost`, `Run-Campaign2Host`, `Merge-Campaign`,
  `Invoke-Preflight`(`.Portable`), **+ new `Invoke-LocalCampaign.ps1`** (A6)
- `scripts/ops/` — `diag-mongo-start`, `read-mongo-log`, `cosmos-ru`, `Reseed-MongoShard`

**Critical:** grep **all** scripts for `scripts\multihost` and `scripts\` path references before moving and
fix them. Known reference: `Invoke-Campaign.ps1` builds `"$RepoDir\scripts\multihost\Run-BurstHost.ps1"` —
update to the new `scripts/run/` path.

---

## A4 — `scripts/setup/New-MongoMonitorUser.ps1`

Follow the existing `mongosh`-via-`az vm run-command` pattern used by `Reset-MongoPassword.ps1` and
`Raise-MongoMaxConn.ps1` (mongod on `E:\mongo`; `bmt_bench`/`bmt_admin` users already exist; `mongosh` is
located on the VM).

- Create `bmt_monitor` with role **`clusterMonitor`** on the `admin` DB (`createUser`, or `updateUser` if
  it already exists). **Idempotent.**
- Run against both mongos routers (cluster-wide `serverStatus` / connection view) and the shard mongod
  (per-shard).
- Its credential goes into machine env var `BMT_CONN_MONGO_MONITOR` on the generator VM — **never
  committed**.
- The script is **authored** here; it is **invoked against live VMs in Package B** (B3).

---

## A5 — `Bmt.Report`: markdown summary + cross-target comparison (code)

Current `src/Bmt.Report`: `HtmlReportBuilder.cs` (HTML only), `Merger.cs`, `ReportLoader.cs`, `Program.cs`.
There is **no** markdown/text summary today — that is why results were summarized manually each time.

1. Add a `MarkdownSummaryBuilder` that emits `summary.md` beside the HTML: per-target latency percentiles
   and the churn-resilience verdict (was peak ≥11k concurrent / ≥1.2k conn/s reached?).
2. When **more than one target** is present in `results/`, emit a **cross-target comparison** section:
   a percentile table in the style of `results/run-20260624-shard/보고서-*.md` — per-metric rows,
   percentile columns per target, the **better value bolded**, split by steady/burst. Reuse the existing
   3-way comparison data path in `HtmlReportBuilder.cs`.
3. Wire into the `report` command so it always emits **both** markdown and HTML.

---

## A6 — `scripts/run/Invoke-LocalCampaign.ps1` (single VM set, sequential)

- Loop targets (`mongo-shard`, `documentdb`) **sequentially**. Per target: `preflight → test → TIME_WAIT
  drain wait`.
- After all targets: run `report` to auto-generate `summary.md` + the comparison + HTML.
- Write a **consolidated run-log file** (today logging is console-only) and a campaign `INDEX.md`.
- Include the Package B Azure metric-pull hook (see B5), but **guard it** so it no-ops when `az` is not
  logged in or `config/azure-resources.json` is unfilled — so this runner is testable in Package A without
  any Azure dependency.
- No multi-host coordination logic (single VM set). This replaces the manual "ask an agent to summarize"
  loop.

---

## Package A — done criteria

1. `dotnet build Bmt.sln` succeeds.
2. `report` on an existing multi-target `results/*` directory emits `summary.md` **with the comparison
   table** plus HTML; numbers match the source JSON.
3. A short dry `test` using `run.json` (1 iteration, small duration) honors the centralized knobs;
   `OpenLoop` toggles correctly.
4. Every production config resolves via `Extends` (config-load smoke check, no missing keys).
5. Moved scripts run from their new paths; a grep shows **zero** stale `scripts\multihost\` / `scripts\`
   references.
6. `git branch -a` shows only the intended branches; `main` builds; changes are pushed.
