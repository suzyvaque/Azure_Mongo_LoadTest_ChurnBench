# DocumentDB OIDC connection hard-cap campaign status

- Cluster: `docdb-dbtest-hpc-1`
- Configuration: M80, 2 shards, HA `ZoneRedundantPreferred`
- Authentication: Entra managed identity (`MONGODB-OIDC`)
- Client pool: min 0 / max 1, one client per held connection
- Keepalive: indexed find every 60 seconds
- Generators: 3 × `Standard_D8ds_v5`
- Authoritative connection count: combined driver `ActiveReady`

| Stage | Combined gate | Per-host gate | Iteration | Status | Safety verdict |
|---|---:|---:|---:|---|---|
| A | 6,000 | 2,000 | 1 × 8 min | Complete | PASS — no client saturation |
| B | 9,000 | 3,000 | 1 × 8 min | Starting | Pending |
| C | 12,000 | 4,000 | 1 × 8 min | Pending | Pending |
| D | 15,000 | 5,000 | 1 × 8 min | Pending | Pending |
| E | 18,000 | 6,000 | 1 × 8 min | Pending | Pending |

Client safety is evaluated after every stage using process RAM, handles, threads, ephemeral ports,
sample drops, errors, ActiveReady stability, and drain duration. A failed safety gate stops progression.

## Stage A — 6,000 combined driver-ready connections

- Cross-host start skew: 0 seconds
- Combined peak `ActiveReady`: 6,000 / 6,000
- Per-host peak `ActiveReady`: 2,000 on all three generators
- Connection and keepalive failures: 0
- Drain: 0.3–0.4 seconds; lifecycle fully reconciled
- Per-host peak working set: 837–873 MiB
- Per-host peak handles: 43,185–44,584
- Per-host peak threads: 6,564–6,571
- Per-host peak ephemeral utilization: 7.2%
- Per-host peak CPU: 40.1–43.5%
- TCP/resource samples: 481 per host; one dropped sample per host (0.21%)
- Last-five-minute target TCP average: 3,988–4,000 established sockets per host
- DocumentDB CPU average/maximum: 0.733% / 4.570%
- DocumentDB memory average/maximum: 29.273% / 30.763%
- DocumentDB committed-memory maximum: 5.580%

OIDC creates approximately two target TCP sockets for every held driver-ready client in this setup
(workload plus topology/monitoring path). Therefore Stage A's 6,000 authoritative `ActiveReady` clients
corresponded to approximately 12,000 established target TCP sockets across the three generators. Both
values are retained; the staged gate and pass/fail definition use driver `ActiveReady`.