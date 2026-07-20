# Package B — VM infra + campaign run

**Scope:** Azure resource reset (delete 4 generator VMs, create 3 in AZ1), networking/DNS, host bootstrap
(including the monitor user), the sequential open-loop campaign, and server-side Azure metric capture.

**Requires:** Package A merged to `main`, `az login` with rights on the DB resources, and freed vCPU quota.
Runs from an operator box or an AZ1 VM with the Azure CLI.

> Read [README.md](README.md) first for shared context, conventions, and the reference topology.

---

## B0 — Safety net (before deleting anything)

1. Confirm Package A is merged to `main` and clone it fresh (e.g. `git clone … C:\bmt`).
2. Capture a **VM image or OS-disk snapshot of one existing generator VM** — insurance in case the AZ1
   create fails while out of quota.
3. Record the per-host connection strings / machine env-var names (secrets stay in machine env, not the repo).

---

## B1 — Resource reset (delete → create; being out of quota forces this order)

1. Delete the 4 generator VMs plus their OS disks/NICs: AZ3 `vm-dbtest-hpc-0` (+ `-gen2`), AZ2
   `vm-dbtest-hpc-0-az2` (+ `-gen2`). **Leave backends untouched.**
2. Verify freed **EASv6-family vCPU** quota (128 → 96; frees 32).
3. Create 3 generator VMs in AZ1: `vm-dbtest-hpc-0-az1`, `-az1-gen2`, `-az1-gen3` (Standard E32as v6,
   Windows Server 2025, Accelerated Networking).
4. Create `vm-dbtest-hpc-0-az1-vnet` + subnet + NSG (or reuse a shared VNet).

---

## B2 — Network / DNS (CRITICAL BLOCKER — nothing downstream works without this)

1. Peer the **AZ1 VNet ↔ mongo VNet (AZ3)** so generators reach mongo `10.3.0.x`.
2. Link **both** Private DNS zones to the AZ1 VNet so PE hostnames / SRV resolve to private IPs from AZ1:
   - `privatelink.mongocluster.cosmos.azure.com` (documentdb)
   - `privatelink.mongo.cosmos.azure.com` (cosmos-ru)
3. Update `infra/cosmos/variables.tf` → `dns_link_vnet_ids` (add the AZ1 VNet id).
4. Update `infra/documentdb-private-endpoint/setup-private-endpoint.ps1` config struct (RG/VNet/IP,
   ~lines 13–28).
5. **Gate:** from an AZ1 host, resolve the docdb SRV and the mongo host to **private** IPs and confirm a
   TCP connect. Do not proceed until this passes.

---

## B3 — Host bootstrap (each of the 3 AZ1 VMs) — uses Package A scripts

1. `scripts/setup/tune-vm1.ps1` — ephemeral ports 10000–65534, `TcpTimedWaitDelay=30s` (~1,850 conn/s/host).
2. `scripts/setup/Setup-Gen2Host.ps1` — idempotent: .NET 8, clone `C:\bmt`, build Release.
3. Set per-host machine env vars, including the `BMT_CONN_*` masked connection strings.
4. Run `scripts/setup/New-MongoMonitorUser.ps1` against the mongos routers + shard to create `bmt_monitor`
   (`clusterMonitor`); set `BMT_CONN_MONGO_MONITOR` on the generator VMs.
5. **TLS posture — FAIR + HONEST (Option A). Every target pays a *validated* TLS handshake so the
   connection-churn cost is comparable to DocumentDB/Cosmos (TLS-only).**
   - On each mongo VM / shard node, run `scripts/setup/enable-mongo-tls.ps1` with `BMT_MONGO_HOST` set to
     the DNS name the clients will use (must match the cert SAN and the connection string) and
     `BMT_MONGO_IP` to its private IP. It sets `mode: requireTLS` (no plaintext) and exports
     `E:\mongo\tls\mongod-ca.cer`.
   - Copy `mongod-ca.cer` to each generator host and import it into the trusted root store:
     `Import-Certificate -FilePath mongod-ca.cer -CertStoreLocation Cert:\LocalMachine\Root`.
   - Set every mongo `BMT_CONN_*` with `tls=true` and connect by the cert's DNS host name.
     **Do NOT use `tlsInsecure=true`** — validation must be ON (that was the old "encryption theater").

---

## B4 — AZ1 3-host config / script wiring

1. Update `Invoke-Campaign.ps1` pool mapping to the **AZ1 trio for both mongo\* and documentdb** (runs are
   sequential, so both share the same pool); keep the `-HostVms` override; `host-count = 3`.
2. Add `config/production/full-workload-open-loop-3host.json`, pinned like the 2-host variant:
   λ = 6/host × mean(150..500 = 325) × 3 ≈ **5,850 tasks/s → ≈11,700 concurrent, ≈5,850 conn/s** (exceeds
   the 11,012 / 1,210 envelope). Tune the `TaskSleepMs` hold to sustain concurrency at a safe per-host
   conn/s.
3. Rename/replace `vm1-az2-setup-and-run.ps1` with an AZ1 variant (VNet references).

---

## B5 — Server-side Azure metric capture (fulfills report §8.7 recommendation #3)

Wire into the `Invoke-LocalCampaign.ps1` metric-pull hook authored in Package A.

- **Collection:** post-run **pull over the exact run window** (`RunResult.StartedUtc` / `FinishedUtc`).
  No load-gen impact; timestamps align.
- **Mechanism:** PowerShell (`az` CLI). **No C# change.** Identity comes from `config/azure-resources.json`.
  Precondition: `az login` + subscription check.
- **Scope per target:**
  - **documentdb** — active connections, CPU, memory, IOPS, request throttling / 429 / RU consumed
    (Azure Monitor).
  - **mongo-shard / mongo-vm** — `serverStatus` / `connPoolStats` via `bmt_monitor` + a `mongod.log` slice
    + VM host CPU/mem/net via Azure Monitor. Fall back to the `mongod.log` slice if the monitor user fails.
  - *(cosmos-ru consumed RU/s is out of scope this round.)*
- **Output:** per-run `azure-metrics.json` + raw `az` output + `mongod.log` slice under
  `results/{campaignId}/…`, with the run window recorded and referenced from `INDEX.md`.
- Extend preflight to verify `bmt_monitor` can run `serverStatus` (the current check fails at
  `PreflightRunner.cs:409` because the app user lacks `clusterMonitor`).

---

## B6 — Open-loop campaign + validation (§8.7 follow-up, now valid)

1. Run per target **sequentially** (never parallel): `mongo-shard`, then `documentdb` (same 3 AZ1 hosts),
   via `Invoke-LocalCampaign.ps1` (or `Invoke-Campaign.ps1 -PushResults`, shared `--start-at`,
   `host-count 3`).
2. `report merge --tag <campaign>` to **prove** ≥1,200 conn/s and ≥11,000 concurrent (client + server side).
3. Server metrics (B5) confirm the injected load actually reached the backend.

---

## Package B — done criteria

1. An AZ1 host resolves the docdb SRV and mongo to private IPs; TCP connect OK (the B2 gate).
2. Preflight's 10 checks pass on each AZ1 host, including the `bmt_monitor` `serverStatus` check.
3. `report merge` shows a combined peak **≥11,000 concurrent AND ≥1,210 conn/s**.
4. Client hosts stay finite (no §8.1 meltdown): thread/handle/ephemeral-port peaks below ceilings.
5. Error rate ≈ 0; latency tail percentiles produced for both backends under symmetric cross-AZ.
6. `azure-metrics.json` present for both targets over the run window; `mongod.log` slice captured; the
   consolidated run log + `INDEX.md` written; the cross-target comparison `summary.md` generated.

---

## Open considerations

1. **3-host config:** reuse `multihost.json` (host-count 3) vs. pin `3host.json`. Recommend **pin
   `3host.json`** for byte-identical repeatability.
2. **Mongo reachability from AZ1:** VNet peering (keeps mongo in AZ3 for symmetry) vs. moving the endpoint.
   Recommend **peering**.
3. **Insurance image before delete:** yes — one generator VM — given the out-of-quota recreate risk.
