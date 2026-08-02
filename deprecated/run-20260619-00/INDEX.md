# Campaign — run-20260619-00 (mongo-vm TLS vs DocumentDB)

One **benchmark campaign** = one higher folder under `results/`, holding every target run from the
**same code version**, plus the comparison report and summary. Runs were executed **sequentially**
on VM1; TIME_WAIT was drained to a clean baseline between runs.

This campaign isolates the **connection-establishment tax with TLS enabled on both backends**. The
self-hosted `mongo-vm` had server-side TLS turned on (self-signed cert + chain-of-trust `CAFile`,
`mode: allowTLS`) so its `tls=true` handshake is now directly comparable to the always-TLS managed
`documentdb`. Each workload was run as two separate scenarios — **steady** (135 Tasks/s) and **burst**
(Poisson λ=0.57) — across three workloads: single-op **find-input**, single-op **insert-output**, and
the full 4-op **full-workload** (`find`→`remove`→`insert`→`find`). Production sizing: **3 iterations ×
600 s** per run.

> The earlier **non-TLS** mongo-vm single-find runs are preserved under `mongo_tls_false/` for
> reference; they are **not** part of this TLS-vs-TLS comparison (the connection-time numbers there are
> not apples-to-apples against DocumentDB's TLS handshake).

## Resource specs (as used for this campaign)

| Target | Resource spec (this campaign) |
|---|---|
| `mongo-vm` | MongoDB 7.0 on Azure VM — Windows Server, **single node**, `directConnection=true`, replica set `rs0`; **TLS enabled** (`allowTLS`, self-signed cert, `tlsInsecure=true` client-side) |
| `documentdb` | Azure DocumentDB (vCore) — **HA enabled**, managed **TLS** |

Common stack: **MongoDB Server 7.0 / wire 7.0**, **.NET 8 (LTS)**, **MongoDB C# Driver 2.30**.
No-reuse churn: new `MongoClient` per Task (`maxPoolSize=1`/`minPoolSize=0`).

## Run groups

| Run identifier | Target | Workload | Scenario | Finished (UTC) | Duration | Total | OK | Fail | Success |
|---|---|---|---|---|---|---|---|---|---|
| `mongo-vm-steady-find-input-20260619-173023` | mongo-vm | find-input | steady | 2026-06-19 18:00:23 | 1,800 s | 243,002 | 243,002 | 0 | 100.00% |
| `mongo-vm-burst-find-input-20260619-180045` | mongo-vm | find-input | burst | 2026-06-19 18:30:51 | 1,806 s | 264,278 | 263,307 | 971 | 99.63% |
| `mongo-vm-steady-insert-output-20260619-183213` | mongo-vm | insert-output | steady | 2026-06-19 19:02:13 | 1,800 s | 243,001 | 243,001 | 0 | 100.00% |
| `mongo-vm-burst-insert-output-20260619-190303` | mongo-vm | insert-output | burst | 2026-06-19 19:33:06 | 1,803 s | 266,017 | 264,521 | 1,496 | 99.44% |
| `mongo-vm-steady-full-workload-20260619-193407` | mongo-vm | full-workload | steady | 2026-06-19 20:04:57 | 1,850 s | 242,955 | 187,479 | 55,476 | 77.17% |
| `mongo-vm-burst-full-workload-20260619-200536` | mongo-vm | full-workload | burst | 2026-06-19 20:36:25 | 1,849 s | 253,626 | 172,434 | 81,192 | 67.99% |
| `documentdb-steady-find-input-20260619-154428` | documentdb | find-input | steady | 2026-06-19 16:14:28 | 1,800 s | 243,001 | 243,001 | 0 | 100.00% |
| `documentdb-burst-find-input-20260619-161443` | documentdb | find-input | burst | 2026-06-19 16:44:47 | 1,803 s | 258,984 | 258,984 | 0 | 100.00% |
| `documentdb-steady-insert-output-20260619-173525` | documentdb | insert-output | steady | 2026-06-19 18:05:27 | 1,802 s | 243,002 | 243,002 | 0 | 100.00% |
| `documentdb-burst-insert-output-20260619-180607` | documentdb | insert-output | burst | 2026-06-19 18:36:17 | 1,809 s | 261,454 | 261,454 | 0 | 100.00% |
| `documentdb-steady-full-workload-20260619-183706` | documentdb | full-workload | steady | 2026-06-19 19:07:48 | 1,841 s | 242,997 | 242,956 | 41 | 99.98% |
| `documentdb-burst-full-workload-20260619-190819` | documentdb | full-workload | burst | 2026-06-19 19:38:53 | 1,834 s | 247,334 | 247,220 | 114 | 99.95% |

## Folder layout

```
results/
  run-20260619-00/                                          <- campaign (this folder)
    mongo-vm-<scenario>-<workload>-<stamp>/                 <- per-target run (TLS)
      aggregate.json                                         <- mean-of-3 stats
      iter-01/ iter-02/ iter-03/                             <- per-iteration artifacts
        *.json  *-timeseries.csv  *-latency.csv
    documentdb-<scenario>-<workload>-<stamp>/
    mongo_tls_false/                                         <- earlier non-TLS mongo single-find runs (reference only)
    comparison-mongo-tls-vs-documentdb-20260620.html         <- self-contained 2-way report
    summary-mongo-tls-vs-documentdb-20260620.md              <- concise metrics summary
    INDEX.md                                                 <- this manifest
```

Per-run folder contents:

- `aggregate.json` — mean-of-3-iteration `RunResult` stats (per-op + cycle + connection latency
  percentiles, throughput, error rate) plus each iteration's full result.
- `iter-NN/<run-id>-iter-NN-<stamp>.json` — full machine-readable per-iteration result.
- `iter-NN/<run-id>-iter-NN-<stamp>-timeseries.csv` — one row per second (connection open/close, per-op
  QPS, in-flight tasks, ephemeral ports, TIME_WAIT, handles, threads, CPU%, working set).
- `iter-NN/<run-id>-iter-NN-<stamp>-latency.csv` — per-op + cycle + connection latency percentiles.
- `_*-console.log` — captured console log for that run (**git-ignored**).

## Reproduce / regenerate

```
# Server-side: enable TLS on mongo-vm once (self-signed cert + CAFile, mode allowTLS):
#   scripts/enable-mongo-tls.ps1   (run on the VM)
# Client-side: append &tls=true&tlsInsecure=true to BMT_CONN_MONGO for the run shell only.

# find / insert single-op (clean-output before insert; insert-only accumulates calc_output):
dotnet run --project src/Bmt.LoadGen -c Release -- test \
  --config config/production/single-find-steady.json --target mongo-vm --scenario steady \
  --results results/run-20260619-00
# ...repeat for -burst, single-insert-{steady,burst}, full-workload-{steady,burst}.
```

## Publishing / confidentiality

- **Published** (no confidential data): `*.json`, `*.csv`, `*.html`, `*.md`, `INDEX.md`. Connection
  strings are masked for **credentials *and* host/IP/appName**.
- **Git-ignored**: `_*-console.log` only — captured console logs include preflight check-3 output that
  prints resolved **private IPs**; the same information, masked, is preserved in the published artifacts.
