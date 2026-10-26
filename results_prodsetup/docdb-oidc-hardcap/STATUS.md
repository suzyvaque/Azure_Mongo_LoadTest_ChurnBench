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
| B | 9,000 | 3,000 | 1 × 8 min | Complete | PASS — no client saturation |
| C | 12,000 | 4,000 | 1 × 8 min | Complete | CONDITIONAL PASS — target reached; client thread safety threshold exceeded |
| D | 15,000 | 5,000 | 1 × 8 min | Not run | Blocked by client-safety gate at Stage C |
| E | 18,000 | 6,000 | 1 × 8 min | Not run | Blocked by client-safety gate at Stage C |

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

## Stage B — 9,000 combined driver-ready connections

- Cross-host start skew: 0 seconds
- Combined peak `ActiveReady`: 9,000 / 9,000
- Per-host peak `ActiveReady`: 3,000 on all three generators
- Connection, task, and keepalive failures: 0
- Drain: 0.57–0.61 seconds; lifecycle fully reconciled
- Per-host peak working set: 1,184–1,230 MiB
- Per-host peak handles: 67,067–69,901
- Per-host peak threads: 9,585–9,606
- Per-host peak ephemeral utilization: 10.8%
- Per-host peak CPU: 43.6–46.5%
- TCP/resource dropped samples: 6–7 of 481 per host (1.25–1.46%)
- Last-five-minute target TCP average: 5,982–6,000 established sockets per host
- DocumentDB CPU average/maximum: 1.004% / 5.998%
- DocumentDB memory average/maximum: 29.339% / 31.081%
- DocumentDB committed-memory maximum: 5.757%

The 9,000-ready stage remains below the client stop thresholds. Thread count is approximately 80% of
the 12,000 safety threshold, so Stage C is allowed but must be treated as the likely client-safety boundary;
no higher stage may proceed if Stage C crosses that threshold or materially drops samples.

## Stage C — 12,000 combined driver-ready connections

- Cross-host start skew: 0 seconds
- Combined peak `ActiveReady`: 12,000 / 12,000
- Per-host peak `ActiveReady`: 4,000 on all three generators
- Combined peak created/ready rate: 1,206 / 1,230 connections/s
- Task and keepalive failures: 0
- Driver connection-failure events: one on host 1, with no final task failure
- Drain: 0.92–0.96 seconds; lifecycle fully reconciled
- Per-host peak working set: 1,629–1,649 MiB
- Per-host peak handles: 88,989–92,360
- Per-host peak threads: 12,577–12,606
- Per-host peak ephemeral utilization: 14.4%
- Per-host peak CPU: 42.1–51.2%
- TCP/resource dropped samples: 17–25 of 481–482 per host (3.5–5.2%)
- Last-five-minute target TCP average: 7,970–7,977 established sockets per host
- DocumentDB CPU average/maximum: 1.095% / 8.252%
- DocumentDB memory average/maximum: 29.330% / 31.063%
- DocumentDB committed-memory maximum: 5.654%

The service accepted and held all 12,000 driver-ready connections with no task/keepalive failures and
idle server resources. However, every generator exceeded the predefined 12,000-thread safety threshold,
and one host marginally exceeded the 5% sampling-drop threshold. Stages D and E are therefore blocked.
The result proves **at least 12,000 OIDC driver-ready connections** for this configuration, but does not
measure the DocumentDB hard cap. The upper bound is generator-limited under this one-client-per-connection
.NET design.

## Campaign conclusion

- Highest clean client-safe stage: 9,000 Ready connections
- Highest observed and sustained stage: 12,000 Ready connections
- DocumentDB hard cap: not reached
- Limiting layer for further probing: load-generator thread/sampling overhead
- Required next step for a higher cap: redesign the generator to reduce per-client thread/monitor overhead
	or increase/distribute generator capacity, then repeat above 12,000.