# DocumentDB SCRAM connection hard-cap comparison status

- Cluster: `docdb-dbtest-hpc-1`
- Configuration: M80, 2 shards, HA `ZoneRedundantPreferred`
- Authentication: native `SCRAM-SHA-256`
- Client pool: min 0 / max 1, one client per held connection
- Keepalive: indexed find every 60 seconds
- Generators: 3 × `Standard_D8ds_v5`
- Authoritative connection count: combined driver `ActiveReady`
- Comparison scope: same stages A–C used by the OIDC campaign; no stages above 12,000

| Stage | Combined gate | Per-host gate | Iteration | Status | Safety verdict |
|---|---:|---:|---:|---|---|
| A | 6,000 | 2,000 | 1 × 8 min | Complete | PASS — no client saturation |
| B | 9,000 | 3,000 | 1 × 8 min | Complete | PASS — no client saturation |
| C | 12,000 | 4,000 | 1 × 8 min | Starting | Pending |

The same client-safety criteria as the OIDC campaign apply. The comparison ends after Stage C even if
SCRAM remains healthy, because OIDC reached the generator thread boundary at that stage.

## Stage A — 6,000 combined Ready

- Combined/per-host peak `ActiveReady`: 6,000 / 2,000
- Start skew: 0 seconds; task/connection/keepalive failures: 0
- Drain: 0.30–0.38 seconds; lifecycle reconciled
- Working set: 827–833 MiB; handles: 42,849–43,735; threads: 6,561–6,568
- CPU peak: 42.9–50.7%; ephemeral utilization: 7.2%; dropped samples: 1/481
- DocumentDB CPU average/maximum: 0.649% / 3.513%
- DocumentDB memory average/maximum: 29.191% / 30.596%
- DocumentDB committed-memory maximum: 5.481%

## Stage B — 9,000 combined Ready

- Combined/per-host peak `ActiveReady`: 9,000 / 3,000
- Start skew: 0 seconds; task/connection/keepalive failures: 0
- Drain: 0.54–1.10 seconds; lifecycle reconciled
- Working set: 1,172–1,220 MiB; handles: 65,237–68,829; threads: 9,578–9,596
- CPU peak: 46.9–49.0%; ephemeral utilization: 10.8%; dropped samples: 4–6/481
- DocumentDB CPU average/maximum: 0.929% / 3.191%
- DocumentDB memory average/maximum: 29.237% / 30.760%
- DocumentDB committed-memory maximum: 5.623%
