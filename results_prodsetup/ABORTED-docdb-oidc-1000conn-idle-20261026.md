# Aborted test — OIDC offered 1,000 connections/s mostly-idle workload

## Status

- Run tag: `docdb-oidc-1000conn-idle-20261026`
- Target: Azure DocumentDB M80, 2 shards, HA `ZoneRedundantPreferred`
- Authentication: Microsoft Entra managed identity (`MONGODB-OIDC`)
- Generators: three `Standard_D8ds_v5` VMs
- Intended input: 1,000 new task/client lifecycles per second combined
- Connection residence: 3.5 seconds with the full four-operation workload
- Requested iterations: 3 × 600 seconds
- Completed before cancellation: iterations 1 and 2
- Iteration 3 was launched, then the coordinator and any remaining generator processes were stopped at operator request.
- No benchmark process remained on any generator after cleanup.

## Reason for cancellation

The real production maximum was clarified as approximately 140 tasks/s. The already completed 250 tasks/s OIDC and SCRAM campaigns provide sufficient production-rate headroom. The offered 1,000 connections/s workload did not represent a production requirement and drove both the generators and the connection/admission path into a runaway saturation state.

The cancelled run must not be interpreted as a measured product hard cap. It is retained only as overload evidence and as input to the revised hard-cap test design.

## Observed overload evidence before cancellation

Representative host-3 observations:

| Metric | Iteration 1 | Iteration 2 |
|---|---:|---:|
| Configured per-host input | 333.3 tasks/s | 333.3 tasks/s |
| Realized scheduled/started | 147.8/s | 139.0/s |
| Scheduled tasks | 88,684 | 83,393 |
| Successful / failed tasks | 80,232 / 8,452 | 68,793 / 14,600 |
| Peak ActiveConnecting | 12,881 | 11,792 |
| Peak ActiveReady | 8,982 | 6,541 |
| Outstanding at arrival stop | 26,631 | 29,085 |
| Drain duration | 203.5 s | 181.3 s |
| Peak established target TCP | 22,270 | 24,139 |
| Peak ephemeral utilization | 40.1% | 43.5% |
| Peak handles | 338,579 | 348,530 |
| Peak working set | 6,002 MiB | 6,243 MiB |

The large scheduler queue, hundreds-of-seconds operation latency, high process handle count, and tens of thousands of client sockets show that the offered-rate test confounded generator pressure, long effective connection lifetime, and service admission behavior.

## Revised direction

Production suitability is already covered by the 250 tasks/s tests. The next test targets the concurrent-connection ceiling independently using a bounded, closed-loop hold population and negligible keepalive work. It does not attempt to sustain 1,000 full-workload connection cycles per second.
