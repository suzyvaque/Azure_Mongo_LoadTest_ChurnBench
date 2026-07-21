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
  `--host-id/--host-count/--run-tag/--start-at`. Optionally git-pushes results.
- **`Invoke-Campaign.ps1`** — orchestrator (operator box with Azure CLI). Computes one shared
  `--start-at`, then fires `Run-BurstHost.ps1` on every pool VM concurrently via
  `az vm run-command invoke`, with incrementing host-ids.
- **`Merge-Campaign.ps1`** — wrapper over `Bmt.Report merge`: unions all hosts' per-second series on
  the absolute wall-clock second and reports combined conn/s + concurrency vs ≥1,200 / ≥11,000.

## One-shot usage (from an operator box)

```powershell
# 1) Launch a 2-host DocumentDB burst (starts 2 min from now, hosts push results to the shared repo):
az login
cd C:\bmt\scripts\run
.\Invoke-Campaign.ps1 -Target documentdb -RunTag docdb-m80-burst -PushResults

# 2) After all hosts finish and pushed, pull the repo so results/ has EVERY host, then merge:
cd C:\bmt
git pull --rebase origin main
.\scripts\run\Merge-Campaign.ps1 -RunTag docdb-m80-burst -InputDir results
```

For Mongo:

```powershell
.\Invoke-Campaign.ps1 -Target mongo-vm -RunTag mongo-burst -PushResults
.\scripts\run\Merge-Campaign.ps1 -RunTag mongo-burst -InputDir results
```

## Running a host manually (without the orchestrator)

On each generator VM, with the SAME `-StartAtUtc` and `-RunTag` but a distinct `-HostId`:

```powershell
# Host 1 (on vm-dbtest-hpc-0-az2):
.\Run-BurstHost.ps1 -Target documentdb -HostId 1 -HostCount 2 -RunTag docdb-m80-burst `
    -StartAtUtc 2026-07-16T06:00:00Z -PushResults
# Host 2 (on vm-dbtest-hpc-0-az2-gen2):
.\Run-BurstHost.ps1 -Target documentdb -HostId 2 -HostCount 2 -RunTag docdb-m80-burst `
    -StartAtUtc 2026-07-16T06:00:00Z -PushResults
```

## Sizing the load (config)

`config/production/full-workload-open-loop-3host.json` (the pinned default for the AZ1 trio) scales
per-host Poisson λ and Job size so that **3 hosts combined** reach the target while each host stays under
its ~1,850 conn/s ephemeral-port ceiling. Verify the ACTUAL combined peak with `Merge-Campaign.ps1`
and adjust λ / host-count if short — the merge output prints `REACHED` / `NOT reached` for both the
conn/s and concurrent targets. Corroborate the concurrency peak with the server side (mongod
`serverStatus.connections`, or DocumentDB metrics) since the client-side merged in-flight is an
upper-bound estimate.

## New-host prerequisites (one-time, per newly deployed generator)

The new `*-gen2` VMs need the same setup as the originals before they can run:
1. TCP tuning (ephemeral 10000–65534, `TcpTimedWaitDelay=30`) + reboot — see
   `scripts/setup/vm1-az2-setup-and-run.ps1` STEP 1.
2. .NET 8 SDK, clone repo to `C:\bmt`, `dotnet build -c Release`.
3. Set the target's connection env var at **Machine** scope (so `az vm run-command`'s SYSTEM context
   sees it): e.g. `[Environment]::SetEnvironmentVariable("BMT_CONN", "<conn>", "Machine")`.
4. Confirm private reachability to the target (peering + private DNS).
