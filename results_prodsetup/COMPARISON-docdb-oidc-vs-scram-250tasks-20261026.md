# DocumentDB M80 2-shard HA — OIDC vs SCRAM at 250 tasks/s

## Test envelope

- Cluster: `docdb-dbtest-hpc-1`
- Configuration: M80, 2 shards, `ZoneRedundantPreferred`, private endpoint
- Dataset: 100,000 input documents; `ReqId` indexes verified
- Load: three synchronized generators × 83.333 tasks/s = 250 tasks/s combined
- Work per task: fresh client/connection, find input, remove output, insert output, find output
- Nominal operation rate: 1,000 operations/s
- Duration: 600 seconds × 3 iterations per authentication mode
- Synchronization: all six iterations had 0-second cross-host start skew
- Result root: `results_prodsetup`

The latency values below are arithmetic means of the nine per-host/per-iteration percentile values. They are not pooled percentiles.

## Result summary

| Metric | Entra OIDC | SCRAM-SHA-256 | Observation |
|---|---:|---:|---|
| Runs | 9 | 9 | 3 hosts × 3 iterations |
| Total tasks | 449,998 | 449,999 | Equivalent offered load |
| Failed tasks | 7 | 8 | All classified as `QueryFailure` |
| Task error rate | 0.001556% | 0.001778% | Operationally similar |
| Connection failures | 0 | 0 | No authentication/connection-open failures |
| Task cycle p50 | 38.130 ms | 38.755 ms | SCRAM +1.6% |
| Task cycle p95 | 69.470 ms | 69.774 ms | Similar |
| Task cycle p99 | 90.117 ms | 88.590 ms | SCRAM 1.5 ms lower |
| Demand-to-ready p50 | 20.753 ms | 21.816 ms | OIDC 1.1 ms lower |
| Demand-to-ready p95 | 43.739 ms | 45.061 ms | OIDC 1.3 ms lower |
| Demand-to-ready p99 | 60.458 ms | 62.354 ms | OIDC 1.9 ms lower |
| Driver-open p50 | 9.178 ms | 10.478 ms | OIDC 1.3 ms lower |
| Driver-open p95 | 25.739 ms | 27.310 ms | OIDC 1.6 ms lower |
| Driver-open p99 | 40.426 ms | 42.760 ms | OIDC 2.3 ms lower |
| Hello p50 / p95 / p99 | 0.703 / 1.810 / 5.570 ms | 0.620 / 1.687 / 5.335 ms | Similar |
| Auth p50 / p95 / p99 | 1.273 / 2.613 / 4.084 ms | 0.543 / 1.708 / 2.668 ms | SCRAM auth command timing lower |
| Find-input p50 | 23.185 ms | 24.017 ms | Similar |
| Find-input p95 | 48.207 ms | 48.896 ms | Similar |
| Find-input p99 | 67.739 ms | 67.639 ms | Effectively equal |
| Insert p99 | 13.257 ms | 10.427 ms | SCRAM lower in this campaign |
| Max generator CPU | 19.55% | 25.16% | Peak-of-nine; not an average |
| Max generator working set | 413.3 MiB | 414.2 MiB | Equal |
| Max threads | 5,078 | 5,068 | Equal |
| Max handles | 17,676 | 17,679 | Equal |
| Max ActiveConnecting per host | 18 | 19 | Similar |
| Max ActiveReady per host | 17 | 25 | Low in both tests |
| Max WaitingForServer per host | 19 | 19 | Equal |
| Retryable command failures | 0 | 0 | Driver event telemetry |

## Combined synchronization and rate

| Metric | OIDC iterations | SCRAM iterations |
|---|---|---|
| Offered/started rate | 250/250 tasks/s in all iterations | 250/250 tasks/s in all iterations |
| Peak created connections/s | 258, 257, 261 | 258, 256, 260 |
| Peak ready connections/s | 260, 261, 269 | 263, 258, 260 |
| Peak combined ActiveReady | 27, 25, 23 | 23, 35, 31 |
| Worst-host e2e p99 | 90.4, 91.4, 92.3 ms | 89.6, 89.8, 90.2 ms |
| Drain | 0.1, 0.0, 0.0 s | 0.0, 0.1, 0.1 s |
| Start-time skew | 0 s | 0 s |

## DocumentDB Azure Monitor

| Metric | Entra OIDC | SCRAM-SHA-256 |
|---|---:|---:|
| CPU average / maximum | 2.148% / 7.941% | 1.902% / 6.502% |
| Memory average / maximum | 28.835% / 29.260% | 28.756% / 29.008% |
| Committed memory maximum | 3.557% | 3.418% |
| Captured request count | 3,033,429 | 3,393,507 |
| Request-duration average / maximum | 1.443 / 368.517 ms | 3.484 / 1,442.938 ms |

Server request count and aggregate request-duration values are not directly comparable as authentication mechanisms generate different command mixes and Azure Monitor did not capture every expected connection command in either window. SCRAM emitted both `SaslStart` and `SaslContinue`, while OIDC primarily emitted `SaslStart`; this explains most of the higher SCRAM request count. Client-observed operation and connection timings are the primary comparison evidence.

## Interpretation

- Both authentication methods sustained the requested workload with no connection or authentication failures.
- OIDC was slightly faster for complete connection establishment despite having higher measured authentication-command latency. The result indicates that the complete handshake path, not one SASL command alone, determines connection cost.
- End-to-end task latency was effectively equivalent. SCRAM had a slightly lower p99; OIDC had a slightly lower p50 and connection-open percentiles.
- CPU and memory saturation were not observed with either method.
- The 7 versus 8 `QueryFailure` counts are too small to establish a meaningful reliability difference.
- The driver settings forced `RetryWrites=true` for DocumentDB in both runs, overriding `retrywrites=false` in the supplied URI. Retryable command failure telemetry remained zero.

## Artifact locations

- OIDC client runs: `results_prodsetup/docdb-oidc-250tasks`
- OIDC server metrics: `results_prodsetup/_campaign-docdb-oidc-250tasks-20261026`
- SCRAM client runs: `results_prodsetup/docdb-scram-250tasks`
- SCRAM server metrics: `results_prodsetup/_campaign-docdb-scram-250tasks-20261026`
- Combined OIDC merge: `results/merge-docdb-oidc-250tasks-20261026.json`
- Combined SCRAM merge: `results/merge-docdb-scram-250tasks-20261026.json`
