# `scripts/` — host & campaign automation

PowerShell helpers for the benchmark, grouped by lifecycle stage. Infra provisioning (Terraform, private
endpoints) lives under `infra/`; these scripts run **host-side** or from an operator box with Azure CLI.

| Folder | Purpose | Scripts |
|--------|---------|---------|
| [`setup/`](setup/) | One-time host & backend setup | `tune-vm1.ps1`, `vm1-az2-setup-and-run.ps1`, `Setup-Gen2Host.ps1`, `Raise-MongoMaxConn.ps1`, `Reset-MongoPassword.ps1`, `enable-mongo-tls.ps1`, `New-MongoMonitorUser.ps1` |
| [`run/`](run/) | Campaign execution & aggregation | `Invoke-Campaign.ps1`, `Run-BurstHost.ps1`, `Run-Campaign2Host.ps1`, `Merge-Campaign.ps1`, `Invoke-Preflight.ps1`, `Invoke-Preflight-Portable.ps1`, `Invoke-LocalCampaign.ps1` |
| [`ops/`](ops/) | Operational helpers around runs | `cosmos-ru.ps1`, `diag-mongo-start.ps1`, `read-mongo-log.ps1`, `Reseed-MongoShard.ps1` |

See [`run/README.md`](run/README.md) for the multi-host open-loop burst campaign walkthrough.

## Typical flow

```
setup/   →  tune host TCP, bootstrap generators, create DB users (incl. bmt_monitor)
run/     →  Invoke-LocalCampaign.ps1 (single VM set, sequential targets)  OR
            Invoke-Campaign.ps1 (multi-host coordinated burst) → Merge-Campaign.ps1
ops/     →  cosmos-ru scale up/down, read mongod.log, reseed shard as needed
```

Secrets are never passed through these scripts — each host reads its connection string from a machine env
var (`BMT_CONN*`; see `setup/vm1-az2-setup-and-run.ps1` STEP 4).
