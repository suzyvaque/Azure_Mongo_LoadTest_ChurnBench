# Handoff — BMT AZ1 open-loop reset

This folder holds two **self-contained** execution packages. Each can be handed to a separate agent or
operator and executed without opening the other. Read this README first, then run the packages **in order**.

| Order | Package | File | Needs Azure / VMs / quota? |
|-------|---------|------|-----------------------------|
| 1 | **A — Repo cleanup** | [01-repo-cleanup.md](01-repo-cleanup.md) | No — runs on the current dev machine |
| 2 | **B — VM infra + campaign run** | [02-vm-infra-and-run.md](02-vm-infra-and-run.md) | Yes — `az login`, new AZ1 VMs, quota |

```
Package A (repo clean) ──► commit + push clean main + tag ──► Package B (infra + run)
```

Do **Package A first**. It has no cloud dependency, gives immediate value (centralized config + automated
summaries), and leaves a clean, validated baseline that Package B clones and consumes. Do **not** let the
Package B agent perform the repo refactor — that would re-fragment the codebase.

---

## Shared context (read before either package)

- **Goal.** Close the report's §8.7 open-loop validity gap: the production peak (**≈11,012 concurrent /
  ≈1,210 conn/s**) was never reproduced. Move from scattered single-host generators to a **3-host AZ1 set**
  running an **open-loop multi-host burst**, which fixes the §8.1 single-host meltdown by distributing churn.

- **Do not rewrite `src/`.** `Bmt.Core` + the 4 CLIs (`Bmt.LoadGen`, `Bmt.Preflight`, `Bmt.Report`,
  `Bmt.Seeder`) are clean. The past open-loop failure was a **client meltdown**, not a code defect. The fix
  is multi-host + tooling/config hygiene, not a rewrite.

- **Azure Load Testing (ALT) was evaluated and rejected** as a replacement: it runs only JMeter/Locust
  (no .NET → breaks driver parity, an explicit fairness variable), gives no OS TCP tuning (the central
  churn constraint), is closed-loop by design (doesn't solve §8.7), and drops the `ConnectionOpenMs` /
  host-resource metrics that are the point of the benchmark. The VM harness stays.

- **Topology = symmetric-fair.** Keep **mongo active in AZ3** and **DocumentDB in AZ2**; both are exactly
  **one cross-AZ hop** from the AZ1 generators. **Do not** fail mongo over to its AZ1 standby (that would
  bias toward mongo). This is a **new baseline** — not directly comparable to `run-20260624-shard`, which
  used same-AZ generators per target. Document it as fresh methodology.

- **Connection-string env-var convention** (secrets never committed):
  - `BMT_CONN` → documentdb
  - `BMT_CONN_MONGO` → mongo-vm
  - `BMT_CONN_MONGO_SHARD` → mongo-shard
  - `BMT_CONN_COSMOS` → cosmos-ru
  - `BMT_CONN_MONGO_MONITOR` → **new** monitor user (`bmt_monitor`, `clusterMonitor` role)

---

## Reference topology

- **Generator VMs (current, to be deleted in Package B):** AZ3 `vm-dbtest-hpc-0` (+ `-gen2`); AZ2
  `vm-dbtest-hpc-0-az2` (+ `-gen2`). All Standard **E32as v6** (32 vCPU / 256 GB), Windows Server 2025,
  Accelerated Networking.
- **Backends:** mongo-vm active AZ3 `10.3.0.4` / standby AZ1 `10.3.0.5`; mongo-shard (2 shards + 2 mongos);
  documentdb **M80** AZ2 (private endpoint `10.2.0.7`); cosmos-ru koreacentral (regional).
- **Private DNS zones:** `privatelink.mongocluster.cosmos.azure.com` (docdb),
  `privatelink.mongo.cosmos.azure.com` (cosmos-ru) — currently linked to the AZ3 VNet.
