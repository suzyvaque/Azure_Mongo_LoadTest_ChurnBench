# `infra/cosmos` — Re-provision the Cosmos DB for MongoDB (RU) backend

Terraform to **recreate the `cosmos-ru` benchmark backend** faithfully after you delete it for cost
reasons, plus the connect step that wires it back into the harness.

> **Why this exists.** Cosmos at 100,000 RU/s is expensive to leave running. When the budget is tight you
> can **delete the account** entirely (cost → $0), then re-apply this Terraform later to bring back a
> byte-for-byte-equivalent account, database, collections and private networking. Re-seed the 100k dataset
> with `prepare-data` afterwards.

---

## What it creates

| Resource | Notes |
|---|---|
| **Cosmos DB account** (`azapi`) | MongoDB API **server 7.0**, **Session** consistency (5 s / 100), single region **koreacentral** **zone-redundant**, **Continuous7Days** backup, **public network access disabled**, VNet filter on, TLS 1.2, automatic failover. Mirrors the captured live config. |
| **Mongo database** `bmt_db` | Holds the **shared manual throughput** (default **400 RU/s** — the cheap floor for a fresh account). |
| **Collections** `calc_input`, `calc_output` | Each with the default `_id` index **and** the mandatory **`ReqId`** index (non-unique — Cosmos platform constraint). |
| **Private DNS zone** `privatelink.mongo.cosmos.azure.com` + VNet link(s) | So the Mongo FQDN resolves to the private endpoint from the generator VNet(s). |
| **Private endpoint** `pe-cosmos-ru` | `MongoDB` sub-resource, in `vm-dbtest-hpc-0-vnet/default`, wired to the DNS zone group. |

`azapi` is used only for the **account** so the exact `apiProperties.serverVersion`, continuous-backup
tier, zone redundancy and network flags reproduce exactly; the database, collections, DNS and private
endpoint use the standard `azurerm` provider.

> **Throughput is intentionally 400 RU/s here**, not 100,000. Apply cheap, then raise to the test value
> just before a run with `scripts/cosmos-ru.ps1 -Set 100000 -Wait`, and drop back with `-Min` afterwards.
> Baking 100,000 into Terraform would bill it from the moment of `apply`.

---

## Prerequisites

- **Terraform ≥ 1.5**, **Azure CLI** logged in (`az login`) to the right subscription.
- The **VNet/subnet** referenced in `terraform.tfvars` must already exist (the generator VNet
  `vm-dbtest-hpc-0-vnet` / `default`). This config does **not** create the VNet.
- The resource group (`rg-db-test-hpc`) must exist.

---

## Usage

```powershell
cd infra/cosmos
Copy-Item terraform.tfvars.example terraform.tfvars   # adjust if anything changed

terraform init
terraform plan -out tfplan
terraform apply tfplan
```

Provisioning the account + private endpoint typically takes a few minutes.

### Connect the harness (set `BMT_CONN_COSMOS`)

The Cosmos primary Mongo connection string already includes `ssl=true`, `replicaSet=globaldb`,
`retrywrites=false`, `maxIdleTimeMS` and `appName` — exactly the options the harness expects — so it can be
used verbatim. Export it **without printing the secret**:

```powershell
# On the load-generator host (User scope persists across sessions):
terraform output -raw bmt_conn_cosmos |
  ForEach-Object { [Environment]::SetEnvironmentVariable('BMT_CONN_COSMOS', $_, 'User') }
```

> The connection string contains the account key, so the `bmt_conn_cosmos` output is marked `sensitive`
> and never lands in the repo. Treat `terraform.tfstate` as secret too (see below).

### Re-seed + run

```powershell
# 1) raise RU to the test value and wait until live:
pwsh -File ..\..\scripts\cosmos-ru.ps1 -Set 100000 -Wait
# 2) seed 100k + create ReqId indexes:
dotnet run --project ..\..\src\Bmt.Seeder -- prepare-data --config ..\..\config\production\full-workload.json --target cosmos-ru
# 3) preflight + run as usual, then drop RU back afterwards:
pwsh -File ..\..\scripts\cosmos-ru.ps1 -Min
```

---

## Deleting the account (the cost-saving action)

To stop **all** Cosmos cost, delete the account. Two ways:

```powershell
# A) If it is managed by this Terraform state:
terraform destroy

# B) If it still exists from the original (portal/CLI) provisioning, delete directly:
az cosmosdb delete --name cosmos-dbtest-hpc-0 --resource-group rg-db-test-hpc --yes
# (optionally also remove the private endpoint + DNS zone if you want them gone too)
```

After deletion, re-running `terraform apply` here brings the account back. **Data is not preserved** — you
must re-seed with `prepare-data`. The dataset is deterministic (fixed RNG **seed 42**), so the re-seeded
data is byte-identical to before.

> **Continuous backup note.** The account uses `Continuous7Days` backup, so a *deleted* account can be
> restored within the retention window via point-in-time restore **if** you delete-then-restore rather
> than recreate. For this benchmark, recreate + re-seed is simpler and deterministic.

---

## Faithfulness notes / caveats

- **Account name reuse.** Cosmos account names are globally unique. After a delete, the name
  `cosmos-dbtest-hpc-0` is typically reusable shortly after, but a soft-delete/restore window can briefly
  block reuse. If `apply` fails on a name conflict, wait or pick a new `account_name`.
- **RU floor after first 100k.** Once you provision 100,000 RU/s on the *new* account, its permanent floor
  becomes ~1,000 RU/s (Azure rule `highest/100`). A brand-new account before any scale-up floors at 400.
- **Private IPs are assigned by Azure**, not pinned here. The original was acct→`10.2.0.5`,
  region→`10.2.0.6`; a fresh PE may get different IPs. DNS resolution still works because the zone group is
  wired automatically — don't hard-code the IP anywhere.
- **Multiple generator VNets.** To let VM1-az2 (or any other generator) resolve the private FQDN, add its
  VNet resource ID to `dns_link_vnet_ids` in `terraform.tfvars`.
- **State contains secrets.** `terraform.tfstate` holds the connection string/keys. Keep it out of the repo
  (the repo `.gitignore` already ignores `*.tfstate*` and `infra/**/.terraform/`). For team use, move state
  to a secured remote backend (e.g. an Azure Storage container).

---

## Files

| File | Purpose |
|---|---|
| `versions.tf` | Provider/version pins (`azurerm` ~> 3.116, `azapi` ~> 1.15). |
| `variables.tf` | All inputs (names, region, throughput, networking) with the captured defaults. |
| `main.tf` | Account (azapi) + database + collections + DNS zone/link + private endpoint. |
| `outputs.tf` | Mongo host, document endpoint, current RU, and the sensitive `bmt_conn_cosmos`. |
| `terraform.tfvars.example` | Copy to `terraform.tfvars`; mirrors the original account. |
