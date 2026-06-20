# Environment & Resource Setup — Reproducing the Benchmark

This document describes **how to recreate the full test environment** so the connection-churn
benchmark produces comparable results on a fresh set of Azure resources. It covers the load-generator
host(s), every required OS/TCP modification, the three backend topologies, and the network wiring.

> **Why a separate doc.** The top-level [`README.md`](../README.md) describes the *tool* (CLI, Task
> shape, metrics) and is intentionally **spec-agnostic** so the pinned numbers don't rot. This file is
> the *environment*: machine sizes, OS tuning, and Azure resource settings. Per-campaign backend tiers
> that change between test rounds are recorded in each campaign's `results/<campaign>/INDEX.md` — this
> doc is the **baseline blueprint**, and any campaign that deviates notes the difference in its INDEX.

> **Secrets never live in the repo.** Connection strings are read at runtime from env vars
> (`BMT_CONN_MONGO` / `BMT_CONN_COSMOS` / `BMT_CONN`). Nothing in this doc contains a credential — only
> hostnames, private IPs, ports, and sizing.

---

## 1. Topology overview

```
                         (private VNets, RFC1918 only — no public ingress)

  ┌──────────────────────────┐        ┌──────────────────────────────────────────┐
  │ Load generator host(s)   │        │ Backends under test (one at a time)        │
  │                          │        │                                            │
  │ VM1   (AZ3) ── mongo-vm ─┼───────▶│ MongoDB on VM — active AZ3 / standby AZ1   │
  │ VM1-az2 (AZ2) ─ docdb ───┼───────▶│ Azure DocumentDB (Cosmos vCore, M80, HA)   │
  │ VM1   (AZ3) ── cosmos-ru ┼───────▶│ Azure Cosmos DB for MongoDB (RU, fixed)    │
  └──────────────────────────┘        └──────────────────────────────────────────┘
```

- **One backend is exercised at a time** — never in parallel — so the generator's full capacity is
  available to each and the comparison stays apples-to-apples.
- **Co-locate the generator with the backend's AZ** where latency matters. DocumentDB lives in AZ2, so
  its load is driven from **VM1-az2 (AZ2)** rather than VM1 (AZ3) to avoid a cross-AZ network tax. Mongo
  and Cosmos are driven from **VM1 (AZ3)**.

---

## 2. Load-generator host (the most important tuning)

The benchmark opens a **brand-new TCP connection per Task and closes it** (no pooling). Every closed
socket holds an ephemeral port in `TIME_WAIT`, so the sustainable churn rate is:

```
churn capacity (conn/s) = ephemeral_port_count / TcpTimedWaitDelay
```

Windows defaults (16,384 ports / 120 s ≈ **137 conn/s**) are **far** below the Scenario B burst target of
**≥ 1,200 conn/s**, and preflight check 7 will WARN. **You must apply the TCP tuning below on every
load-generator host**, or the run fails with port exhaustion (WinSock error **10048**).

### 2.1 Host machine spec (as used)

| Item | Value |
|---|---|
| OS | Windows Server 2025 |
| vCPU / RAM | 32 vCore / 256 GB (generous; the generator is CPU- and handle-bound under burst, not RAM-bound) |
| OS disk | `Standard_LRS` (HDD) is fine — the generator does no hot disk I/O; keep both generator VMs symmetric |
| Runtime | **.NET 8 SDK** (LTS, 8.0.4xx) + **MongoDB C# Driver 2.30** (pinned, restored automatically) |
| Accelerated Networking | **Enabled at VM creation** (cannot be hot-added without deallocation) |

### 2.2 Apply the TCP tuning (required)

Use the committed script — it widens the ephemeral range to **10000–65534 (55,535 ports)** and sets
**`TcpTimedWaitDelay = 30 s`**, yielding ≈ **1,851 conn/s** of churn headroom:

```powershell
# Elevated PowerShell on each load-generator host:
powershell -ExecutionPolicy Bypass -File scripts\tune-vm1.ps1
# Reboot to guarantee TcpTimedWaitDelay is fully in effect, then verify:
netsh int ipv4 show dynamicport tcp          # Start=10000, Number=55535
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters").TcpTimedWaitDelay  # 30

# To restore Windows defaults after a campaign:
powershell -ExecutionPolicy Bypass -File scripts\tune-vm1.ps1 -Revert
```

What the script changes (registry: `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters`):

| Setting | Windows default | Benchmark value | Effect |
|---|---|---|---|
| Ephemeral port range (`netsh ... dynamicport tcp`) | 49152–65535 (16,384) | 10000–65534 (55,535) | More ports → more concurrent churn |
| `TcpTimedWaitDelay` (DWord, seconds) | 120 (key absent) | 30 | Ports return to the pool 4× faster |
| `MaxUserPort` (DWord) | — | 65534 | Upper bound for the widened range |

> The ephemeral range change is live immediately; `TcpTimedWaitDelay` applies to new connections, but a
> **reboot** is the only way to be 100% certain it's in force. **Both generator VMs must use identical
> values** — `scripts\vm1-az2-setup-and-run.ps1` STEP 1 applies the same settings on VM1-az2.

### 2.3 Operational gotchas

- **Wait for `TIME_WAIT` to drain (< ~200) between runs.** Back-to-back invocations within ~60 s reuse
  ports still held by the previous run's warmup/preflight sockets → error **10048** at startup. Check
  with `(netstat -an | Select-String TIME_WAIT).Count`.
- **Run detached for long campaigns.** A 3×10-min campaign is ~30 min per scenario; launch it so it survives the
  shell/SSH session closing (e.g. `Start-Process pwsh -WindowStyle Hidden ...`), and tee output to a log.

---

## 3. Backend 1 — MongoDB on a VM (`mongo-vm`)

A self-managed MongoDB replica set on Azure VMs, **active in AZ3 + standby in AZ1** (the client's
requested two-AZ shape — **no third AZ, no arbiter**).

| Item | Value |
|---|---|
| OS | Windows Server 2025 |
| Size | 32 vCore, 256 GB RAM |
| Data disk | **512 GB Premium SSD v2**, mounted at `E:` — all MongoDB hot I/O (dbPath) lives here, not the OS disk |
| Server | MongoDB **Server 7.0 / wire 7.0** |
| Replica set | `rs0`; client connects with `directConnection=true`, `authSource=admin` |
| Active node | **AZ3**, `10.3.0.4` — `votes:1, priority:1` (sole voter ⇒ always primary) |
| Standby node | **AZ1**, `10.3.0.5` — `votes:0, priority:0, hidden:true` (replicates data; never auto-elected) |
| Failover | **Manual only** (forced `rs.reconfig`); there is no arbiter, so an automatic election is intentionally impossible |

### 3.1 Why this shape

The standby exists for **data durability across AZs**, not automatic HA. Giving it 0 votes / 0 priority
keeps the active node in AZ3 deterministically the primary, so benchmark latency is never perturbed by an
unexpected election. Promotion is a deliberate `rs.reconfig` when AZ3 is lost.

### 3.2 Recreate

1. Provision two VMs (active AZ3, standby AZ1) with a 512 GB Premium SSD v2 data disk on `E:`.
   Name the standby VM with a `-standby` suffix for clarity.
2. Install MongoDB 7.0 on both; set `dbPath` to the `E:` disk; enable auth (`authSource=admin`).
3. Initiate the set on the active node, then add the standby **hidden, non-voting**:
   ```javascript
   rs.initiate({ _id: "rs0", members: [ { _id: 0, host: "10.3.0.4:27017" } ] })
   rs.add({ host: "10.3.0.5:27017", priority: 0, votes: 0, hidden: true })
   rs.status()   // active = PRIMARY, standby = SECONDARY (hidden)
   ```
4. Create the benchmark app user, then run `prepare-data` (which adds the `ReqId` indexes).
5. Connection string env var (example form — real value lives only on the host):
   ```
   BMT_CONN_MONGO = mongodb://<user>:<pass>@10.3.0.4:27017/bmt_db?replicaSet=rs0&authSource=admin&directConnection=true
   ```

> **Health check before every run:** `rs.status()` must show active = `PRIMARY`, standby = `SECONDARY`.

---

## 4. Backend 2 — Azure DocumentDB / Cosmos vCore (`documentdb`)

| Item | Value |
|---|---|
| Service | Azure DocumentDB (Cosmos DB for MongoDB **vCore**) |
| Tier | **M80** — 32 vCore, 128 GB RAM, 512 GB SSD |
| HA | **Enabled** |
| AZ | **AZ2** (drive its load from VM1-az2, same AZ) |
| Connection | `mongodb+srv://` (SRV resolution), `retrywrites=false`, TLS, `authMechanism=SCRAM-SHA-256` |
| Cluster host | `docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com` |
| Private endpoint IP | `10.2.0.7` |
| SRV target | `fc-…-000.global.mongocluster.cosmos.azure.com` on port **10260** |
| Private DNS zone | `privatelink.mongocluster.cosmos.azure.com` |

### 4.1 Network requirement (the easy thing to miss)

The `mongodb+srv` hostname **and** its SRV target must resolve to **private** IPs from the generator. The
private DNS zone is linked to VM1's VNet by default; **to drive DocumentDB from VM1-az2 you must add a
second VNet link**:

```
Azure portal → Private DNS zones → privatelink.mongocluster.cosmos.azure.com
  → Virtual network links → + Add → link VM1-az2's VNet
```

> **Automated procedure.** The full VNet-peering + private-DNS-link + validation steps (manual checklist
> *and* a `setup-private-endpoint.ps1` script) live in
> [`infra/documentdb-private-endpoint/`](../infra/documentdb-private-endpoint/README.md).

Verify both DNS resolution and TCP reachability **before** a timed run — see
`scripts\vm1-az2-setup-and-run.ps1` STEP 5 (it tests the private IP and the hostname independently).

> Full step-by-step for the AZ2 generator (TCP tuning, .NET install, clone, env var, reachability,
> run, push) is in **[`scripts/vm1-az2-setup-and-run.ps1`](../scripts/vm1-az2-setup-and-run.ps1)**.

---

## 5. Backend 3 — Azure Cosmos DB for MongoDB (RU) (`cosmos-ru`)

| Item | Value |
|---|---|
| Service | Azure Cosmos DB for MongoDB (request-units model) |
| Account / RG | `cosmos-dbtest-hpc-0` / `rg-db-test-hpc` |
| Throughput | **Provisioned (manual), SHARED at the database level on `bmt_db`** — both `calc_input` and `calc_output` draw from one RU/s budget; neither collection has dedicated throughput. Test value **100,000 RU/s**; **never auto-raised**, even for seeding. |
| Throughput floor | **1,000 RU/s** (Azure rule `MAX(400, highest-ever-provisioned/100, storage-floor)`; since 100,000 was provisioned, the floor is permanently ~1,000 — you cannot reach 0 without deleting the database) |
| Connection | port **10255**, `ssl=true`, `replicaSet=globaldb`, **`retrywrites=false`**, `maxIdleTimeMS=120000` |
| Private endpoint | resolves to a private IP (e.g. `10.2.0.6`) from the generator |
| `ReqId` index | **non-unique** (platform constraint) — distinct ReqId still guaranteed because `ReqId == _id` and `_id` is system-unique |

### 5.1 RU model rules (honored by the tool — do not override)

- The provisioned RU/s is **held constant within a campaign** and **never auto-raised**. Seeding uses
  **smaller delete/insert batches** to ease throttling instead of bumping RU.
- During a timed run, **429 / `RetryAfterMs`** responses are **classified as `CosmosRuThrottling`** (a
  separate error bucket) — not silently retried — so RU pressure shows up in the report as
  failures/latency instead of being hidden.
- A high `CosmosRuThrottling` count means the workload is **RU-bound, not latency-bound**. Interpret
  Cosmos results with that in mind.

> The RU/s in force for a given run is recorded in that campaign's `INDEX.md`. The first Phase-1 campaign
> ran at **40,000 RU/s** (RU-bound); from 2026-06-18 onward Cosmos runs at **100,000 RU/s**.

### 5.2 Cost control — scale down between rounds (`scripts/cosmos-ru.ps1`)

100,000 RU/s is expensive to leave idle. Because the throughput is **manual and shared at the database
level**, you can scale the whole `bmt_db` budget up and down with one operation. Use the committed helper
(it logs every change to `scripts/cosmos-ru.log`, which is git-ignored):

```powershell
# Inspect only (no change):
pwsh -File scripts\cosmos-ru.ps1 -Show

# BEFORE a Cosmos run — raise to the test value and block until it is actually live:
pwsh -File scripts\cosmos-ru.ps1 -Set 100000 -Wait

# AFTER the Cosmos run (+ your buffer) — drop to the real Azure minimum to save cost:
pwsh -File scripts\cosmos-ru.ps1 -Min
```

**Recommended cost-saving cycle (one Cosmos round):**

1. **~15–30 min before** the Cosmos run: `-Set 100000 -Wait`. Scale-*up* is fast (minutes) because the
   partitions were already split at 100k previously; the full Mongo window is *not* needed as warm-up, so
   raising 15–30 min ahead avoids ~90 min of needless 100k billing. `-Wait` gates on completion so you
   never start a timed run mid-scale (which would throw 429s).
2. Set `CosmosExpectedRuPerSec` in the config to the live value — **preflight check 6** then verifies the
   account is actually at the expected RU/s before the run starts.
3. Run the Cosmos campaign.
4. **Keep a ~1 h trailing buffer** in case the run failed or you want a back-to-back rerun — do **not**
   scale down immediately.
5. After the buffer: `-Min` (drops to ~1,000 RU/s). The seeded dataset + `ReqId` indexes are preserved, so
   the next round only needs a scale-up, not a re-seed.

**Caveats:**
- **You cannot reach 0 RU/s.** `-Min` lands on the ~1,000 floor (still bills, but ~99% cheaper than 100k).
  True $0 requires deleting the database/collections → then you must re-seed 100k + reindex (and re-warm)
  before the next run, which also reintroduces seed/RU variance. Prefer `-Min`.
- **Do not switch to autoscale** to save money: autoscale's idle floor is 10% of max (= **10,000 RU/s**,
  *higher* than the 1,000 manual floor) **and** it violates the "fixed RU, never auto-raised" methodology.
  Stay on manual provisioned.
- **Re-warm after a long idle** with `preflight --warmup` (untimed pre-read) so a cold cache doesn't skew
  the first iteration.

### 5.3 Record the RU/s per campaign (required)

Because the RU/s now varies between rounds, **every campaign must state the RU/s in force** in its
`results/<campaign>/INDEX.md` resource-spec table (alongside the Mongo VM size and DocumentDB tier). A run
whose RU/s isn't recorded is not interpretable — `CosmosRuThrottling` counts only make sense against a
known budget. The embedded preflight gate in each run's JSON also captures the expected RU/s.

---

## 6. Common software stack (all backends)

| Component | Pinned value |
|---|---|
| MongoDB Server / wire | **7.0 / 7.0** |
| Client runtime | **.NET 8 (LTS)** |
| Driver | **MongoDB C# Driver 2.30** |
| Database | `bmt_db` |
| Collections | `calc_input`, `calc_output` |
| Mandatory index | `ReqId` on **both** collections (unique on `mongo-vm`/`documentdb`, non-unique on `cosmos-ru`) — created by `prepare-data`, verified by `preflight` |
| Dataset | exactly **100,000** docs, fixed RNG **seed 42** → byte-identical across all three targets; size buckets 6 KB×10k / 16 KB×15k / 50 KB×35k / 58 KB×40k (mean ≈ 43.7 KB, ≈ 4.37 GB) |

---

## 7. End-to-end recreate checklist

1. **Provision** the three backends (§3–§5) and the load-generator VM(s) (§2.1) in the right AZs, with
   private endpoints + DNS zone links so every host resolves the backends to **private** IPs.
   Provisioning automation lives under [`infra/`](../infra/): [`infra/cosmos/`](../infra/cosmos/README.md)
   (Terraform for the Cosmos RU account) and
   [`infra/documentdb-private-endpoint/`](../infra/documentdb-private-endpoint/README.md) (DocumentDB
   private connection).
2. **Tune** every generator host: `scripts\tune-vm1.ps1` (elevated) + reboot (§2.2). Verify the ephemeral
   range and `TcpTimedWaitDelay`.
3. **Install** .NET 8 SDK; clone the repo; `dotnet build -c Release`.
4. **Set** the connection-string env var for the target on its generator host (never commit it).
5. **Seed + index** once per backend: `prepare-data` (100k docs, seed 42, `ReqId` indexes).
6. **Preflight** the target (the 10-check gate; `--warmup`). Resolve any FAIL before timing.
7. **Confirm** `TIME_WAIT < ~200` and (for `mongo-vm`) `rs.status()` healthy.
8. **Run** the timed campaign (3×10-min iterations; full-workload or single-op per the chosen config),
   one target at a time, into a shared `results/<campaign>` folder.
9. **Clean** `calc_output` after each campaign with `clean-output` (empties only `calc_output`, keeps
   `calc_input` + the `ReqId` index). **Required after a single-insert run** (it accumulates docs without
   bound); harmless after full-workload/find-only. Run it before an insert campaign too, for a clean baseline.
10. **Report**: build the self-contained HTML from the campaign folder.
11. **Record** the exact backend tiers/RU for that round in `results/<campaign>/INDEX.md`, and commit
    results (`*.log` stays git-ignored — it echoes private IPs).

See the [`README.md`](../README.md) for the per-command CLI details and the metrics/interpretation guide.

---

## 8. What changes between campaigns vs. what is fixed

| Fixed (this blueprint) | Per-campaign (record in `INDEX.md`) |
|---|---|
| TCP tuning values (10000–65534, TIME_WAIT 30 s) | Backend tier (e.g. DocumentDB M-tier, Mongo VM size) |
| No-reuse Task model, dataset (100k, seed 42) | Cosmos RU/s (40,000 → 100,000 → …) |
| `ReqId` index on both collections | Run shape (1×60 min vs 3×10 min; full-workload vs single-op) |
| Software stack pins (.NET 8, driver 2.30, Server 7.0) | Which AZ each VM/backend sits in |
