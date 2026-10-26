# DocumentDB OIDC vs SCRAM connection-capacity comparison

## Scope

- Cluster: `docdb-dbtest-hpc-1`, M80, 2 shards, HA `ZoneRedundantPreferred`
- Endpoint and workload: identical global SRV, closed-loop hold, indexed find keepalive every 60 seconds
- Generators: 3 × `Standard_D8ds_v5`
- Client model: one fresh MongoDB client per held connection, pool min 0 / max 1
- Authentication variable: Entra managed identity (`MONGODB-OIDC`) versus native `SCRAM-SHA-256`
- Duration: one synchronized eight-minute iteration at each 6,000, 9,000, and 12,000 combined Ready gate
- Authoritative concurrency: combined driver `ActiveReady`; physical TCP is reported separately

## Verdict

Both authentication methods produced the same capacity conclusion:

- 6,000 and 9,000 combined Ready connections were clean and client-safe.
- 12,000 combined Ready connections were reached and sustained for both methods.
- Neither method produced task or keepalive failures at 12,000.
- Both crossed the predefined 12,000-thread generator safety threshold at the 12,000-Ready stage.
- Sampling degradation also reached or crossed the 5% stop boundary on one generator.
- DocumentDB CPU and memory remained low, so the DocumentDB connection hard cap was not reached.

Therefore, the highest clean client-safe result is **9,000 Ready connections**, and the highest observed
and sustained result is **12,000 Ready connections** for both OIDC and SCRAM. No material authentication-
method capacity difference was established within the measurable range.

## Stage results

| Stage | OIDC result | SCRAM result | Comparative conclusion |
|---|---|---|---|
| 6,000 Ready | PASS; 0 failures | PASS; 0 failures | Both clean and client-safe |
| 9,000 Ready | PASS; 0 failures | PASS; 0 failures | Both clean and client-safe |
| 12,000 Ready | Conditional PASS; sustained, generator boundary | Conditional PASS; sustained, generator boundary | Equivalent observed capacity; product cap not reached |

## Stage C — 12,000 Ready comparison

| Metric | OIDC | SCRAM | Interpretation |
|---|---:|---:|---|
| Combined peak `ActiveReady` | 12,000 | 12,000 | Both reached the authoritative concurrency gate |
| Per-host peak `ActiveReady` | 4,000 | 4,000 | Even distribution across all three generators |
| Combined peak created rate | 1,206/s | 1,076/s | Ramp diagnostic only; not a hold-capacity requirement |
| Combined peak Ready rate | 1,230/s | 1,088/s | Ramp diagnostic only; both completed the gate |
| Task failures | 0 | 0 | No application-visible failure |
| Keepalive failures | 0 | 0 | Held clients remained usable |
| Driver connection-failure events | 1 transient | 0 | OIDC event did not become a task failure |
| Drain | 0.92–0.96 s | 0.82–0.90 s | Both fully and quickly reconciled |
| Working set per host | 1,629–1,649 MiB | 1,595–1,629 MiB | Similar; SCRAM slightly lower in this run |
| Handles per host | 88,989–92,360 | 87,939–91,123 | Similar |
| Threads per host | 12,577–12,606 | 12,595–12,600 | Both exceeded the 12,000 safety threshold |
| Generator CPU peak | 42.1–51.2% | 50.4–53.9% | Neither CPU-saturated; SCRAM peaks were somewhat higher |
| Ephemeral utilization | 14.4% | 14.4% | Ports were not limiting |
| Dropped samples | 17–25 / 481–482 | 20–32 / 481 | Monitoring overhead reached the stop boundary |
| Last-5-minute target TCP per host | 7,970–7,977 | 7,994–8,000 | About two physical sockets per Ready client in both runs |
| DocumentDB CPU avg / max | 1.095% / 8.252% | 0.983% / 6.389% | No server CPU saturation |
| DocumentDB memory avg / max | 29.330% / 31.063% | 29.257% / 30.908% | Essentially equal; no memory saturation |
| DocumentDB committed memory max | 5.654% | 5.468% | No committed-memory pressure |

## Resource progression

### OIDC

| Ready | Working set/host | Handles/host | Threads/host | Dropped samples/host |
|---:|---:|---:|---:|---:|
| 6,000 | 837–873 MiB | 43,185–44,584 | 6,564–6,571 | 1 / 481 |
| 9,000 | 1,184–1,230 MiB | 67,067–69,901 | 9,585–9,606 | 6–7 / 481 |
| 12,000 | 1,629–1,649 MiB | 88,989–92,360 | 12,577–12,606 | 17–25 / 481–482 |

### SCRAM

| Ready | Working set/host | Handles/host | Threads/host | Dropped samples/host |
|---:|---:|---:|---:|---:|
| 6,000 | 827–833 MiB | 42,849–43,735 | 6,561–6,568 | 1 / 481 |
| 9,000 | 1,172–1,220 MiB | 65,237–68,829 | 9,578–9,596 | 4–6 / 481 |
| 12,000 | 1,595–1,629 MiB | 87,939–91,123 | 12,595–12,600 | 20–32 / 481 |

The resource curves are nearly identical. They primarily reflect the one-client-per-connection .NET load-
generator design rather than an authentication-specific server limit.

## Interpretation boundaries

1. `ActiveReady` is the authoritative logical held-connection count. DocumentDB does not expose a direct
   active-connection Azure Monitor metric.
2. Target TCP was approximately twice `ActiveReady`; it must not be substituted for logical client count.
3. The 12,000 result demonstrates a lower bound: this configuration sustained at least 12,000 Ready clients
   under either authentication method.
4. It does not establish a DocumentDB hard cap because generator thread and sampling limits appeared first.
5. A higher-cap experiment requires a generator design with lower per-client thread/monitor overhead or more
   distributed generator capacity, followed by identical OIDC and SCRAM reruns above 12,000.
