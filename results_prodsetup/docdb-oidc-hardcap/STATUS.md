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
| A | 6,000 | 2,000 | 1 × 8 min | Starting | Pending |
| B | 9,000 | 3,000 | 1 × 8 min | Pending | Pending |
| C | 12,000 | 4,000 | 1 × 8 min | Pending | Pending |
| D | 15,000 | 5,000 | 1 × 8 min | Pending | Pending |
| E | 18,000 | 6,000 | 1 × 8 min | Pending | Pending |

Client safety is evaluated after every stage using process RAM, handles, threads, ephemeral ports,
sample drops, errors, ActiveReady stability, and drain duration. A failed safety gate stops progression.