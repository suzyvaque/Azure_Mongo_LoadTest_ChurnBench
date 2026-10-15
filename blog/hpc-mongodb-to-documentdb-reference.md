# From MongoDB to Azure DocumentDB: An HPC Workload Reference from Caldova Securities

A database supporting high-performance computing (HPC) can face relatively modest query volume and still experience severe connection pressure. When short-lived tasks repeatedly establish clients, negotiate TLS, authenticate, and disconnect, the connection lifecycle becomes a capacity requirement of its own.

Caldova Securities selected **Azure DocumentDB M80 with two shards and high availability (HA) enabled**, citing stability and performance. According to the client, its previous MongoDB deployment used four VMs: two primaries and two standbys.

This article describes the workload, the validation method, the configuration details that affected the tests, and the lessons another team can apply. Caldova Securities is a pseudonym. The final test campaign ran on 2026-08-03 (earlier campaigns are identified in Section 5). The measurements describe the database access pattern, not the HPC calculation engine or a quantified post-migration availability improvement.

## Key Takeaways

- **Normal vs worst case:** Normal behavior is about 135 tasks/s (four operations per task); in production on DocumentDB the maximum observed was about 200 IOPS with very low CPU utilization. The worst case is a burst of about 1,210 new connections/s that accumulates about 11,000 mostly idle connections. The burst is a connection-lifecycle requirement, not 11,000 active queries.
- **Normal behavior:** With a new client per task at 135 tasks/s, DocumentDB M80 with two shards matched MongoDB with four routers on throughput (134.9 tasks/s) with near-zero errors, and had the lower median read latency (27.5 vs 45.6 ms).
- **Resource use:** DocumentDB server CPU stayed at about 0.8-1.5% in the retained telemetry. In the two-router MongoDB benchmark, VM1 averaged 65.2% and peaked at 99.7% CPU, while VM2 averaged 44.5% and peaked at 99.6%; the configuration capped at 4,714 held connections. Adding two dedicated router VMs removed that bottleneck.
- **Worst case:** At the 12,000-connection test ceiling, DocumentDB had far fewer connection failures (88 of 49,885 attempts, 0.2%, against 2,377 of 38,991, 6.1%) but a higher keepalive p99 (23.3 vs 14.8 s). That is consistent with a managed (PaaS) front end, where each new connection passes through the gateway and authentication. Under overload its connection-open tail was lower (22.3 vs 159.7 s).
- **Limit:** Both targets saturated under overload. DocumentDB's constraint was connection admission, not CPU, so reuse clients and bound load rather than rely on capacity.

## 1. Translate the Existing Workload into Requirements

The database supplied calculation inputs and stored task results. A task followed this lifecycle:

```text
Create fresh client -> find input -> calculate -> delete previous output
                    -> insert result -> find result -> release resources
```

The four database operations were mandatory: two reads, one delete, and one insert. Replacing delete-plus-insert with an upsert would change the workload rather than reproduce it. Operations used the logical request identifier, `ReqId`.

The architecture change can be summarized as follows:

```mermaid
flowchart LR
    subgraph Existing["As-is: self-managed MongoDB"]
        TasksA[HPC tasks] --> PrimaryA[Primary 1]
        TasksA --> PrimaryB[Primary 2]
        PrimaryA --> StandbyA[Standby 1]
        PrimaryB --> StandbyB[Standby 2]
    end
    subgraph Selected["To-be: Azure DocumentDB"]
        TasksB[HPC tasks] --> Endpoint[Global endpoint via private connectivity]
        Endpoint --> Cluster[M80 / two shards / HA enabled]
    end
```

This is a logical diagram, not a complete replica-set deployment specification. The original four-VM architecture must also be distinguished from the **two-router and four-router MongoDB configurations used in the benchmark**.

Production telemetry exposed two separate dimensions:

| Dimension | Behavior | Requirement Used for Testing | What It Means |
|---|---|---|---|
| Task processing | **Normal** | Approximately 135 tasks/s, four database operations per task | Sustained processing of the calculation input/output cycle |
| Connection pressure | **Worst case** | Bursts near 1,210 new connections/s; approximately 11,000 mostly idle live connections | Establishment and accumulation of connections, not 11,000 simultaneous active queries |

These peaks did not occur simultaneously. At the recorded connection peak, operation volume was only about five operations/s. Production also had multiple physical connections per logical task, including driver topology-monitoring connections. Task arrival rate and socket creation rate are therefore not interchangeable.

Connection lifetime explains why a low-operation workload can accumulate many connections. As an approximation, 1,210 new connections/s held for about nine seconds gives approximately 10,890 live connections. A throughput-only benchmark would miss this requirement.

## 2. Configure DocumentDB and the Access Path

The selected architecture was M80, two shards, and HA enabled. Record the storage allocation, region, network path, and HA state of your own campaign.

| | Azure DocumentDB | MongoDB, Four Routers |
|---|---|---|
| Compute | M80 per shard: 32 vCore / 128 GiB RAM, two shards | Two shard VMs (24 vCore each, also hosting `mongos`) plus two 8-vCore router VMs |
| Version and network | Managed service, private endpoint | MongoDB 7.0, private network, self-signed CA |
| Standby and failover | HA enabled; failover not tested | Standby behavior not tested |
| Load generators | One host (normal load); three hosts (hold and overload) | Same |

The other DocumentDB tiers tested were M60 (16 vCore / 64 GiB per shard) and M200 (64 vCore / 256 GiB per shard); see [Microsoft Learn](https://learn.microsoft.com/en-us/azure/documentdb/compute-storage) for tier sizes.

### Endpoint, Networking, and Authentication

Use the service-provided global endpoint with SRV discovery, without pinning to a specific backend host. A sanitized URI shape is:

```text
mongodb+srv://<user>:<encoded-password>@<cluster>.global.mongocluster.cosmos.azure.com/<database>?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=true
```

Start from the connection string supplied by the service and retain its required options.

The reference environment used private connectivity. Each generator VNet needed connectivity to the private endpoint and the appropriate link to `privatelink.mongocluster.cosmos.azure.com`. Verify both the seed hostname and the SRV target hostnames resolve privately, then verify TCP reachability. Do not hard-code a resolved backend IP into the application.

The `.global` hostname is an access-path detail, not evidence that cross-region disaster recovery has been configured or tested.

### Data Distribution and Driver Settings

| Setting | Reference Behavior |
|---|---|
| Dataset | 100,000 input documents generated with seed 42 |
| Document sizes | 6 KiB: 10,000; 16 KiB: 15,000; 50 KiB: 35,000; 58 KiB: 40,000 |
| Operation key | Indexed `ReqId` in input and output collections |
| Distributed shard key | Hashed `ReqId` in the two-shard test |
| Software | .NET 8 and MongoDB C# driver 2.30 |
| Per-task client | New client and settings; `maxPoolSize=1`, `minPoolSize=0`; no cross-task reuse |
| Timeouts | Server selection and connect: 5,000 ms each; no socket timeout |
| Retryable writes | Forced on for DocumentDB by the benchmark factory |

Provisioned shards were not sufficient evidence of distribution. Earlier runs described as single-shard had data concentrated on one shard, even where two physical shards were provisioned. For the distributed tests, the collection was sharded while empty and then reseeded; distribution was checked using chunk metadata and query explain output.

Configuration files inherit through `Extends`, and code can override URI settings; the final DocumentDB client forces retryable writes on regardless of the URI. Reproduction requires the **effective configuration**, not just the connection string.

## 3. Keep the Generator from Becoming the Bottleneck

Connection churn exercises the client host as well as the database. These generator details are not DocumentDB-specific, so they are summarized here; the repository holds the full detail.

### TCP Capacity and Arrival Budgets

Windows generators were tuned to a dynamic TCP port range of **10000-65534** with **`TcpTimedWaitDelay=30 s`**, applied on every generator, followed by reboot and verification.

Dividing 55,535 ports by 30 seconds gives a TIME_WAIT-only budget of about **1,851 connections/s per host**. This is a sizing estimate, not guaranteed throughput; CPU and handles also matter.

The three-host overload configuration used four Poisson job arrivals/s per host and 150-500 tasks/job, an expected offered rate of about 1,300 tasks/s per host (3,900 combined), below that budget. The inherited multi-host base (six jobs/s, about 1,950 tasks/s per host) would have exceeded it.

Configured offered rate is not measured connection rate; inspect arrival counters, connection attempts, Ready events, and completions separately. Apply the [TCP tuning script](../scripts/setup/tune-vm1.ps1) only to dedicated test hosts.

### Client Runtime and Lifecycle Controls

| Failure Risk | What the Benchmark Did | Production Implication |
|---|---|---|
| Slow failures accumulating | Applied 5-second connect and server-selection timeouts | Add whole-task deadlines and bounded retry budgets; connection timeouts do not bound every operation |
| ThreadPool starvation | Set `ThreadPool.SetMinThreads(512, 512)` after a sustained-load incident (idle database, rising client latency) | Measure queueing, threads, CPU, and handles first; 512 is not a universal recommendation |
| Topology-monitor growth | Round-robin pinned self-managed tasks to one router with `directConnection=true` | A benchmark access-path optimization for MongoDB, not a general-purpose HA discovery pattern; DocumentDB used its global endpoint unpinned |
| Cluster resources outliving tasks | Explicitly unregistered and disposed each cluster | Uses a driver-internal API; pin the driver and retest cleanup on upgrades |

The driver still created an internal pool object; the constraint was **no reuse between tasks**.

### TLS Settings Must Be Disclosed

The implementation records concurrent certificate-revocation checks causing handshakes around 29.5 seconds at 40-way concurrency, compared with under 100 ms sequentially. The factory disabled revocation checks through `CheckCertificateRevocation=false`.

Separately, the self-managed MongoDB normal-load and hold configurations enabled `MongoAllowInsecureTls`, relaxing certificate validation for private-CA routers. These are different security changes: disabling revocation checks is not the same as bypassing chain/hostname validation.

Both affect comparison conditions and should not be copied into production. Use trusted certificates, plan certificate and revocation-path connectivity, and obtain security review. For DocumentDB, chain and hostname validation were retained.

### Server Limits, Backpressure, and Recovery

Open-loop tests intentionally bypassed `MaxConcurrentTasks`. They exposed accumulation when the backend slowed, but were not overload-safe application designs. For production, use bounded queues/concurrency, rate limits, backpressure, and retries that cannot amplify a failing dependency. Enable only one arrival scenario unless combined traffic is explicitly intended.

Between tests, verify that Ready connections and outstanding work drain, CPU recovers, and TIME_WAIT falls (the benchmark used a count below roughly 200).

## 4. Run Separate Tests for Separate Questions

The benchmark used the same deterministic dataset and operation semantics across targets, with untimed data-cache warm-up. Connections remained fresh during measurement: **warm data did not mean warm reusable connections**.

| Scenario | Model | Question |
|---|---|---|
| Normal processing | Closed-loop full four-operation workload, approximately 135 tasks/s | Can the platform process the task workload at stable latency? |
| Production-rate connection pressure | Two-host open-loop campaign near 1,210 connections/s | How do connections accumulate at the production-shaped rate? |
| Deliberate overload | Three-host open-loop configuration, expected offered rate near 3,900 tasks/s | Where do backlog, latency, and failures emerge? |
| Connection holding | Three hosts, 4,000 gated tasks/host, 12,000 combined ceiling | How many established connections can be held? |
| Operation baseline | One indexed find or insert per fresh client | What is the isolated operation latency at non-saturating load? |

Normal processing represents everyday behavior; the production-rate, overload, and hold scenarios represent the worst case.

The normal-load final comparison used a single generator, three 300-second iterations, and no calculation sleep, so each connection lived for milliseconds rather than production's roughly nine seconds. The overload configuration used a 2,900 ms task wait. The three-host hold test used a 300-second window and a keepalive input read every 10 seconds per held connection. The production-rate campaign was earlier, with different timing and code history. Record these differences rather than describing all tests as one identical experiment.

Two fidelity limits apply:

- **Hold:** at a full 12,000 connections, the keepalive schedule can offer about 1,200 reads/s, against production's roughly five operations/s at its connection peak. It approximates connection holding, not production load.
- **Open-loop:** every connection ran the full four-operation cycle, whereas production's additional connections were mostly driver monitoring with almost no operations. Database work at the connection peak is overstated.

The benchmark defined no pass/fail thresholds. Before sizing production, agree acceptance criteria: an error-rate limit, a p99 within the task deadline, a connection population sustained for the full window, and recovery to baseline after overload.

### A Minimal Reproduction Workflow

1. Provision private connectivity and record target/generator topology, VM sizes, software revision, and effective settings.
2. Set connection secrets outside the repository, tune each generator, and verify DNS/TCP connectivity.
3. Seed data, create indexes, and verify physical distribution.
4. Run preflight and untimed warm-up before each measured iteration.
5. Execute one scenario against one target at a time; synchronize multi-host iterations on a shared start time, and accept a combined result only when every host reported.
6. Drain and clean output, merge matching host artifacts, and retain raw metrics plus resource telemetry.

For example, these repository commands prepare and run the single-host normal-load scenario against DocumentDB:

```powershell
dotnet build Bmt.sln -c Release
dotnet run --project src/Bmt.Seeder -- prepare-data --config config/production/full-workload-closed-loop-steady.json --target documentdb
dotnet run --project src/Bmt.Preflight -- preflight --config config/production/full-workload-closed-loop-steady.json --target documentdb --warmup
dotnet run --project src/Bmt.LoadGen -- test --config config/production/full-workload-closed-loop-steady.json --target documentdb --scenario steady
dotnet run --project src/Bmt.Seeder -- clean-output --config config/production/full-workload-closed-loop-steady.json --target documentdb
```

Use the [multi-host runbook](../scripts/run/README.md) for synchronization and merge requirements, adapting infrastructure identifiers to your own environment. Do not launch an ungated overload scenario merely to verify connectivity; start with smoke configurations.

## 5. Read the Results Without Combining Different Claims

The following values are taken from the [consolidated report](../results/REPORT-mongo-vs-documentdb-churn-benchmark.md). Reported percentile summaries follow the campaign's host/iteration aggregation; an average of p99 values is not a pooled global p99. Combined connection concurrency is the peak of the synchronized per-second sum of driver `ActiveReady`, not the sum of unrelated host peaks.

| Result | Campaign | Date | Notes |
|---|---|---|---|
| Normal load (both targets) | `run-20260803-02` | 2026-08-03 | One host, 3 x 300 s, MongoDB with relaxed TLS |
| DocumentDB M80 hold and overload | `run-20260802-01` | 2026-08-02 | Three hosts |
| MongoDB four-router hold and overload | `run-20260727-*` / `run-20260731-01` | Late July 2026 | Three hosts, relaxed TLS |
| Production-rate run | `run-20260715-2host-openloop` | 2026-07-15 | One-shard M80, older code |

### Normal Task Processing

The final-code, single-host comparison used three 300-second iterations:

| Metric | DocumentDB M80, Two Shards | MongoDB, Four Routers |
|---|---|---|
| Task throughput | 134.9 tasks/s | 134.9 tasks/s |
| Reported error rate | 0.000% | 0.001% |
| Input find p50 / p90 / p99 | 27.5 / 59.3 / 107.8 ms | 45.6 / 51.7 / 67.9 ms |
| Insert p99 | 41.1 ms | 7.4 ms |
| Connection-open p99 | 75.6 ms | 37.0 ms |

Both sustained the processing target. DocumentDB had the lower median input-read latency; MongoDB had the tighter tail. The MongoDB runs used the disclosed TLS relaxation, so this is not a security-identical comparison.

### Connection Holding

| Metric | DocumentDB M80, Two Shards | MongoDB, Four Routers |
|---|---|---|
| Best-iteration peak Ready connections | 12,000 | 12,000 |
| Reported average Ready connections | 4,476 | 10,570 |
| Iterations exceeding 10,000 | 2 of 3 | 3 of 3 |
| Keepalive find p99 | 23.271 s | 14.803 s |
| Connection attempts / failures | 49,885 / 88 | 38,991 / 2,377 |

12,000 equals the configured gate (3 x 4,000), so it shows the ceiling was reached, not where capacity ends. With M80 above 10,000 in 2 of 3 iterations, headroom beyond the ceiling is unproven. Establishment latency was not captured in the two-shard hold results, so establishment cannot be compared across targets here.

### Resource Utilization and Tail Latency

| Configuration | Server-side CPU in retained telemetry | Outcome |
|---|---|---|
| DocumentDB (all tiers, churn and hold) | About 0.8-1.5% | Constrained by connection admission |
| MongoDB, two co-hosted router/shard VMs | VM1: 65.2% average / 99.7% peak; VM2: 44.5% average / 99.6% peak | Capped at 4,714 held connections |
| MongoDB, four routers | Two dedicated router VMs added | Held 12,000 |

These compare managed cluster CPU with self-managed VM or router CPU, so they are indicative rather than identical measurements.

| Metric | DocumentDB M80, Two Shards | MongoDB, Four Routers |
|---|---|---|
| Input find p99 (normal load) | 107.8 ms | 67.9 ms |
| Connection-open p99 (normal load) | 75.6 ms | 37.0 ms |
| Keepalive find p99 (hold) | 23.3 s | 14.8 s |
| Connection failure rate (hold) | 0.18% | 6.1% |
| Connection-open p99 (overload) | 22.3 s | 159.7 s |

DocumentDB's tail was higher at normal load and in hold, and lower under overload. The higher tails are consistent with a managed service in which each new connection goes through the gateway and authentication; authentication time was not measured separately in this report. The MongoDB runs used direct router pinning and relaxed certificate validation, both of which reduce handshake cost, so the tail comparison is not like-for-like. Validate tail latency against your own SLO.

### Deliberate Overload and Production-Rate Pressure

Under the three-host overload scenario, M80 two-shard DocumentDB reported 72.9 tasks/s, connection-open p99 of 22.340 seconds, and an 89.1% error rate. MongoDB four-router reported 71.2 tasks/s, connection-open p99 of 159.686 seconds, and a 94.9% error rate. These are **saturation results**, not acceptable production error rates.

DocumentDB server CPU remained near idle in the retained churn/hold telemetry. Establishment failures and queueing were consistent with an admission-layer constraint rather than shard compute saturation. This is an interpretation, not a direct measurement of gateway capacity; exclude client ThreadPool, DNS, TLS, and cleanup issues first.

The earlier production-rate run (2026-07-15) used M80 with data on one shard and reached 9,979 connections with a 38.8% error rate. It predates the TLS and lifecycle changes, so its MongoDB counterpart is not a current comparison, and the selected two-shard M80 was not tested at that rate.

### Production Observation

In production on the selected configuration, the maximum observed was about 200 IOPS with very low CPU utilization, consistent with the benchmark's idle server CPU. Low CPU does not by itself show connection headroom; connection counts and errors during bursts would. The benchmark did not measure cost, availability, or failover.

## 6. Lessons to Apply to Another Workload

**Reuse clients where semantics allow.** Connection pools avoid repeated TCP, TLS, and authentication work. This benchmark deliberately prohibited cross-task reuse to reproduce a constrained lifecycle, so its results are a worst case, not an application design to copy.

**Shard for distribution, not simply for more connections.** Add shards when data/storage growth or sustained operation load requires distribution, using a shard key that matches the workload, and verify actual placement. In these tests, data on two shards raised the hold ceiling (one shard plateaued near 11,000 held connections, with keepalive p99 of 114-139 s versus 23-34 s on two shards), but sharding did not fix churn throughput, where compute tier mattered more.

**Use HA for availability, not throughput.** The standby is not an additional readable scale-out shard. A zero-downtime objective requires failover tests covering driver reconnection, retryable operations, and interrupted work; these churn tests did not validate failover.

**Use the service endpoint and validate discovery.** Preserve the service-provided global endpoint and private DNS reachability, without pinning to a backend host, and validate failover behavior rather than assuming it.

**Scale the layer that is under pressure.** More shard compute will not automatically fix a generator thread queue, a router performing TLS/authentication, or an admission bottleneck. At normal load, M60, M80, and M200 were within noise of each other; under churn higher tiers helped, and M200 showed intermittent admission throttling.

**Bound overload and test recovery.** Apply deadlines, bounded work queues, backpressure, and retry budgets, and measure recovery after overload, not only the highest connection peak.

## 7. Monitoring and Open Gaps

Monitor client `ActiveReady` and TIME_WAIT, plus DocumentDB CPU and throttling signals. Review the error taxonomy for compatibility errors before drawing performance conclusions, and check the [official DocumentDB limits](https://learn.microsoft.com/en-us/azure/documentdb/compute-storage) for your tier. M200 recovered from throttling after about eight minutes; that is an observation, not a guarantee.

Gaps this benchmark leaves open:

- Two-shard M80 at the production burst rate of about 1,210 connections/s.
- A pooled-client baseline for the same workload.
- A failover run covering reconnection and interrupted work.

## Conclusion

The transferable reference is the method: derive processing and connection requirements separately, reproduce the relevant lifecycle, validate generator headroom, and interpret latency, failures, and sustained capacity together.

## Reference Material

- [Private endpoint procedure](../infra/documentdb-private-endpoint/README.md)
- [Benchmark CLI and project structure](../README.md), [production configuration guide](../config/production/README.md), and [multi-host execution runbook](../scripts/run/README.md)
- [TCP tuning](../scripts/setup/tune-vm1.ps1) and [base client configuration](../config/production/base.json)
- [Effective client settings](../src/Bmt.Core/Connections/TaskConnectionFactory.cs), [cluster teardown](../src/Bmt.Core/Connections/MongoClientReleaser.cs), and [ThreadPool configuration](../src/Bmt.LoadGen/Program.cs)
- [Consolidated results and campaign provenance](../results/REPORT-mongo-vs-documentdb-churn-benchmark.md) and [metric definitions](../docs/metrics-reference.md)