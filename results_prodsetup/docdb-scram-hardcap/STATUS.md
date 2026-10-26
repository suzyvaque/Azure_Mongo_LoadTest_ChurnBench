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
| A | 6,000 | 2,000 | 1 × 8 min | Starting | Pending |
| B | 9,000 | 3,000 | 1 × 8 min | Pending | Pending |
| C | 12,000 | 4,000 | 1 × 8 min | Pending | Pending |

The same client-safety criteria as the OIDC campaign apply. The comparison ends after Stage C even if
SCRAM remains healthy, because OIDC reached the generator thread boundary at that stage.
