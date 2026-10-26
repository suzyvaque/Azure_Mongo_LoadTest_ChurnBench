# DocumentDB M80 2-shard HA — OIDC connection hard-cap test plan

## Objective

Measure the maximum concurrent **driver-ready** connections that the current Azure DocumentDB M80, 2-shard, HA cluster can establish and hold using Entra managed-identity OIDC, without conflating the result with a high full-workload arrival rate or unbounded generator queues.

This is a capacity characterization test, not a production requirement. Production task rate is approximately 140 tasks/s, and the separate 250 tasks/s tests already demonstrate processing headroom.

## Fixed environment

- Cluster: `docdb-dbtest-hpc-1`
- Tier: M80
- Shards: 2, with verified data distribution
- HA: `ZoneRedundantPreferred`
- Endpoint: global `mongodb+srv`
- Authentication: `MONGODB-OIDC`, one system-assigned managed identity per generator
- Network: private endpoint/private DNS
- Generators: three `Standard_D8ds_v5` VMs in Korea Central AZ1
- Dataset: existing 100,000 input documents and verified `ReqId` index

## Workload isolation

Use `Workload.Mode=Hold`:

1. Create one fresh client per task with pool min 0/max 1.
2. Establish and authenticate the connection.
3. Hold the connection in driver `Ready` state.
4. Run a low-frequency indexed keepalive find.
5. Use a closed-loop, per-host concurrency gate. Never use unbounded open-loop arrival.

The keepalive period should be 60 seconds for this capacity test. At 12,000 ready connections this generates about 200 reads/s, rather than the previous 10-second period's roughly 1,200 reads/s. The test therefore emphasizes connection holding rather than database throughput.

## Safety gates

Stop the current stage and do not advance if any generator reaches one of these limits:

- Process working set ≥ 80% of VM RAM
- Handle count ≥ 250,000
- Thread count ≥ 12,000
- Ephemeral port utilization ≥ 70%
- Resource/TCP sampling drops > 5%
- ActiveReady declines for two consecutive minutes while the gate remains fixed
- ServerSelectionTimeout or ConnectionFailure rate ≥ 1% during the stable hold window
- Drain exceeds 180 seconds

A stage that trips a generator gate is client-limited and cannot establish a DocumentDB hard cap.

## Staged search

Run one 8-minute iteration per stage first. The final confirmed stage is repeated three times for 10 minutes.

| Stage | Combined gate | Per-host gate | Purpose |
|---|---:|---:|---|
| A | 6,000 | 2,000 | Verify OIDC hold stability and metric quality |
| B | 9,000 | 3,000 | Approach the previously observed one-shard region |
| C | 12,000 | 4,000 | Reproduce the previous two-shard test ceiling |
| D | 15,000 | 5,000 | Probe above the old configured ceiling |
| E | 18,000 | 6,000 | Continue only if Stage D remains healthy |

After the first failed or unstable stage, do not jump higher. Bracket the ceiling between the last stable and first unstable values, then use binary-search increments of 500 connections combined until the bracket is ≤500.

Examples:

- If 12,000 passes and 15,000 fails, test 13,500, then 12,750 or 14,250 as indicated.
- The measured cap is the highest gate satisfying the acceptance definition below, not a momentary peak.

## Stable-stage acceptance definition

A stage passes only when all conditions hold:

- At least 95% of the configured combined gate reaches `ActiveReady`
- At least 95% of the gate remains ready for five continuous minutes
- Connection establishment failures < 1%
- Keepalive operation failures < 0.1%
- Keepalive p99 remains within the customer-agreed timeout
- No generator safety gate triggers
- Lifecycle reconciles after drain: created equals closed and residual connecting/ready/closing are zero or negligible

## Required metrics

### Client and connection

- Offered, started, and completed tasks/s during ramp
- Connections created, ready, failed, and closed
- Combined ready/s and created/s
- `ActiveConnecting`, `ActiveReady`, `WaitingForServer`
- Time to 90%, 95%, and 100% of the gate
- Five-minute stable-window minimum/mean/maximum ActiveReady
- Demand-to-ready and driver-open p50/p95/p99/max
- Hello and authentication p50/p95/p99/max
- Keepalive find p50/p95/p99/max and error count
- Error taxonomy and command error codes
- Retryable command failures
- Drain/recovery duration and lifecycle reconciliation

### Generator safety

- CPU, working set, thread count, and handle count
- Target established/SYN_SENT/TIME_WAIT/CLOSE_WAIT
- Host ephemeral port utilization
- Sampling drops

### DocumentDB

- `CpuPercent`, `MemoryPercent`, and `CommittedMemoryPercent` by `ServerName`
- IOPS and network ingress/egress
- `MongoRequestDurationMs` by operation and status class
- 4xx/5xx counts

DocumentDB does not expose a server-side active-connection counter. Combined client driver `ActiveReady` is therefore the authoritative connection count.

## Artifacts

Use a separate root and run tag:

- Client stages: `results_prodsetup/docdb-oidc-hardcap-<gate>`
- Server metrics: `results_prodsetup/_campaign-docdb-oidc-hardcap-<gate>-<date>`
- Final report: `results_prodsetup/REPORT-docdb-oidc-hardcap-<date>.md`

Each stage must record cluster tier, shard count, HA mode, auth mode, dataset count, index verification, generator SKU, exact gate, keepalive period, start/end timestamps, and stop reason.

## Execution order

1. Confirm no process/socket residue from the aborted offered-rate campaign.
2. Run preflight once with OIDC and verify 100,000 documents/index/private path.
3. Stage A → B → C sequentially, evaluating safety gates after each.
4. Continue to D/E only while the preceding stage passes.
5. Binary-search the pass/fail bracket.
6. Repeat the highest passing gate three times for 10 minutes.
7. Pull Azure Monitor metrics after ingestion and generate the hard-cap report.

No SCRAM hard-cap test is planned until the OIDC cap and limiting layer are established. SCRAM can then be tested at the final OIDC passing gate if an authentication-mode capacity comparison is still required.
