# Multi-host open-loop burst campaign (Track C)

Reaching the measured production peak — **≥1,200 conn/s instantaneous** and **≥11,000 concurrent
connections** (test_instruction.md §6.1/§6.2) — is not achievable from a single generator without
exhausting client ephemeral ports / TLS-handshake CPU. These scripts run the **open-loop burst**
across **multiple co-located generator VMs** (same AZ as the target, for §4 fairness) with a shared
wall-clock start, then merge each host's per-second series to prove the combined envelope was reached.

## Generator pools (deployed topology, koreacentral zone 1)

All targets run **sequentially** from the same AZ1 trio, so one pool serves every backend.

| Target | AZ | Generator VMs (host-id order) | VNet |
|--------|----|-------------------------------|------|
| `mongo-shard` / `mongo-vm` / `documentdb` | 1 | `vm-hpc-loadgen-az1-0`, `vm-hpc-loadgen-az1-1`, `vm-hpc-loadgen-az1-2` | `vm-hpc-loadgen-az1-0-vnet` (peered to mongo + docdb PE) |

Each host reads its own connection string from a **machine env var** (set once per host — see
`scripts/setup/vm1-az2-setup-and-run.ps1` STEP 4). Secrets are never passed through these scripts.

## Scripts

- **`Run-BurstHost.ps1`** — runs ON one generator VM. Builds, then runs the loadgen with
  `--host-id/--host-count/--run-tag/--start-at` and, in coordinator mode, `--iteration-number/--iteration-count`
  (so the invocation runs exactly one iteration). Optionally git-pushes results.
- **`Invoke-Campaign.ps1`** — coordinator (operator box with Azure CLI) that **owns the iteration loop**. For
  each of the `-Iterations` iterations it computes one fresh shared `--start-at`, fires `Run-BurstHost.ps1` on
  every pool VM concurrently via `az vm run-command invoke` (incrementing host-ids), waits for all hosts to
  finish including drain, validates every host reported, and only then advances — rerunning the whole
  three-host iteration on any failure (never continuing with a partial host set).
- **`Merge-Campaign.ps1`** — wrapper over `Bmt.Report merge`: groups each host's per-second series **per
  synchronized iteration**, requires the exact host set `{1,2,3}`, dedupes retries to the latest attempt per
  host, and reports start-time skew, combined conn/s + **ready/s** + **active-ready** (authoritative
  concurrency) vs ≥1,200 / ≥11,000, then a cross-iteration mean/min/max over the valid iterations.

## One-shot usage (from an operator box)

```powershell
# 1) Launch a 3-iteration DocumentDB burst (each iteration synchronizes all 3 AZ1 hosts on a fresh start):
az login
cd C:\bmt\scripts\run
.\Invoke-Campaign.ps1 -Target documentdb -RunTag docdb-m80-burst -Iterations 3 -PushResults

# 2) After all hosts finish and pushed, pull the repo so results/ has EVERY host, then merge:
cd C:\bmt
git pull --rebase origin main
.\scripts\run\Merge-Campaign.ps1 -RunTag docdb-m80-burst -InputDir results
```

For Mongo:

```powershell
.\Invoke-Campaign.ps1 -Target mongo-vm -RunTag mongo-burst -Iterations 3 -PushResults
.\scripts\run\Merge-Campaign.ps1 -RunTag mongo-burst -InputDir results
```

## Running a host manually (without the orchestrator)

Only for debugging a single host — the coordinator normally drives this. On each generator VM, with the SAME
`-StartAtUtc`, `-RunTag`, and `-IterationNumber` but a distinct `-HostId` (repeat per iteration with a fresh
shared start):

```powershell
# Iteration 1, host 1 (of 3):
.\Run-BurstHost.ps1 -Target documentdb -HostId 1 -HostCount 3 -RunTag docdb-m80-burst `
    -StartAtUtc 2026-07-16T06:00:00Z -IterationNumber 1 -IterationCount 3 -PushResults
# Iteration 1, host 2 (of 3):
.\Run-BurstHost.ps1 -Target documentdb -HostId 2 -HostCount 3 -RunTag docdb-m80-burst `
    -StartAtUtc 2026-07-16T06:00:00Z -IterationNumber 1 -IterationCount 3 -PushResults
```

## Sizing the load (config)

`config/production/full-workload-open-loop-3host.json` (the pinned default for the AZ1 trio) scales
per-host Poisson λ and Job size so that **3 hosts combined** reach the target while each host stays under
its ~1,850 conn/s ephemeral-port ceiling. Verify the ACTUAL combined peak with `Merge-Campaign.ps1`
and adjust λ / host-count if short — the merge output prints `REACHED` / `NOT reached` for the conn/s,
ready/s, and active-ready targets. The **authoritative** concurrency evidence is driver **ActiveReady** (peak
concurrent ready connections), not the client-side in-flight Task count (a generator diagnostic); corroborate
with the server side (mongod `serverStatus.connections`, or DocumentDB Azure Monitor metrics).

## New-host prerequisites (one-time, per newly deployed generator)

The new `*-gen2` VMs need the same setup as the originals before they can run:
1. TCP tuning (ephemeral 10000–65534, `TcpTimedWaitDelay=30`) + reboot — see
   `scripts/setup/vm1-az2-setup-and-run.ps1` STEP 1.
2. .NET 8 SDK, clone repo to `C:\bmt`, `dotnet build -c Release`.
3. Set the target's connection env var at **Machine** scope (so `az vm run-command`'s SYSTEM context
   sees it): e.g. `[Environment]::SetEnvironmentVariable("BMT_CONN", "<conn>", "Machine")`.
4. Confirm private reachability to the target (peering + private DNS).
