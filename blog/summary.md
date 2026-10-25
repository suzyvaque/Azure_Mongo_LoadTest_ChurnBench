**Moving an HPC workload from self-managed MongoDB to Azure DocumentDB: what we tested and what we learned**

HPC databases can see modest query volume and still face heavy connection pressure. When every short-lived task opens a new client, negotiates TLS, authenticates, and disconnects, the connection lifecycle becomes a capacity requirement of its own.

**The scenario**
Caldova Securities (a pseudonym) chose Azure DocumentDB M80 with two shards and HA, citing stability and performance. Its previous MongoDB setup had five components: two primaries, two standbys, and one `mongos`. Production needs two separate things: about 135 tasks/s (four operations each), and connection bursts near 1,210 new connections/s that accumulate about 11,000 mostly idle connections. That is not 11,000 active queries.

**How we tested**
A .NET 8 load generator with a new client per task (no reuse), a 100,000-document dataset, and separate tests for normal load, overload, and connection holding. Final campaign: 2026-08-03.

**Results (DocumentDB M80, two shards, versus MongoDB, four routers)**
- Normal load: both sustained 134.9 tasks/s. DocumentDB had the lower median read latency (27.5 vs 45.6 ms); MongoDB had the tighter tail (p99 67.9 vs 107.8 ms).
- Connection holding: both reached the 12,000 ceiling. DocumentDB averaged 4,476 Ready connections versus 10,570, and exceeded 10,000 in 2 of 3 iterations versus 3 of 3.
- Overload: both saturated (89.1% vs 94.9% errors). DocumentDB server CPU stayed near idle, pointing to connection admission rather than compute.

**DocumentDB strong points**
- Managed operations: no router fleet, patching, or certificate rotation to run.
- One global private endpoint held 12,000 connections on two shards with near-idle server CPU.
- Lower median latency at the production task rate.
- MongoDB-compatible drivers and access patterns.

**When to consider adopting it**
- You want to shed the operational load of a self-managed sharded MongoDB fleet.
- Your workload is moderate in throughput but bursty in connections, and clients can be pooled.
- You can validate failover and overload recovery before go-live.

**Caveats**
- The selected two-shard M80 was not tested at the 1,210 connections/s production burst rate.
- MongoDB had the tighter p99 tail; if tail latency is critical, test it against your own SLO.
- No cost, availability, or failover benefit was measured. HA enabled is not proof of zero downtime.
- The no-reuse model is a worst case. Reuse clients and pools wherever your application allows.

The transferable part is the method: derive processing and connection requirements separately, reproduce the connection lifecycle, check generator headroom, and read latency, failures, and sustained capacity together.

Full write-up and reproducible repo: [link]

#AzureDocumentDB #MongoDB #HPC #CloudMigration
