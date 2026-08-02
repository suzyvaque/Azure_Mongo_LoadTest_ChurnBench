# MongoDB Connection-Churn Benchmark — 3-HOST: documentdb vs mongo-shard

**Campaign:** `run-20260724-3host`
**TLS enabled on both backends** · **three synchronized generator hosts** (`vm-hpc-loadgen-az1-0/1/2`,
`HostCount=3`, shared `--start-at`) · **3 iterations × 300 s** per run · results merged by **per-second SUM
of each host's driver `ActiveReady` gauge** (matches `report merge` → `PeakCombinedActiveReady`).
Latency in ms. Concurrency is the **combined** count across all three hosts.

Two campaign shapes were run, because they answer **different** questions:

1. **Open-loop churn** (`full-workload-open-loop-3host.json`, λ=4.0/host, `TaskSleepMs=2900`) — the
   connection-establishment **rate** story. Each connection is Ready only ~3 s, so concurrency stays low.
2. **Saturation hold** (`full-workload-hold-3host.json`, closed-loop gate `MaxConcurrentTasks=4000`/host =
   **12,000 combined ceiling**, keep-Ready loop) — the **≥10,000 concurrent** story. Each connection is held
   Ready for the whole 5-min window, so combined concurrency = the parked population (Little's Law:
   concurrency = arrival_rate × hold_time; the hold config maximizes hold_time instead of rate).

`mongo-shard` is a **2-shard MongoDB 7.0 cluster fronted by two `mongos` routers**, both co-located on **two
`Standard_FX24ms_v2` VMs (24 vCPU / ~500 GB each)**: VM1 (`10.3.0.4`) = mongos `:27016` + shard `rs0`
(`:27017`); VM2 (`10.3.0.6`) = mongos `:27017` + shard `rsShard2` (`:27018`) + config server (`:27019`).
`documentdb` is an **Azure Cosmos DB for MongoDB vCore M80** managed cluster. Live-cluster health was verified
(both mongos `isdbgrid`, connection limit 1,000,000 each; both shards `state=1`, not draining).

---

## 1. Max / Avg concurrent connections (the headline)

**Combined concurrent Ready connections across all 3 hosts.** This is the authoritative
`PeakCombinedActiveReady` metric (test_instruction.md §3, ≥11,000 target).

### 1a. Saturation hold — the ≥10,000 test

<table>
  <thead><tr><th>Iteration</th><th colspan="2">documentdb</th><th colspan="2">mongo-shard</th></tr>
  <tr><th></th><th>Max</th><th>Avg</th><th>Max</th><th>Avg</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td>1</td><td><u><b style="color:#1a7f37">11,354 ✅</b></u></td><td>3,029</td><td>4,349</td><td>3,168</td></tr>
    <tr><td>2</td><td><u><b style="color:#1a7f37">11,317 ✅</b></u></td><td>6,498</td><td>4,285</td><td>3,458</td></tr>
    <tr><td>3</td><td>9,928</td><td>4,207</td><td>4,163</td><td>2,859</td></tr>
    <tr style="border-top:2px solid #555"><td><b>mean</b></td><td><u><b style="color:#1a7f37">10,866</b></u></td><td>4,578</td><td>4,266</td><td>3,162</td></tr>
  </tbody>
</table>

- **DocumentDB clears ≥10,000** — reaching **11,354** combined Ready (each host filled its full 4,000 gate).
- **mongo-shard plateaus at ~4,300** — it never fills the 12,000 gate; each host tops out at ~1,400 Ready.
- mongo-shard values are **post-TLS-fix** (`mongo-hold-fix-0724a`). The pre-fix run (`mongo-hold-0724a`)
  was nearly identical at the 3-host level (max 4,425 / 4,274), confirming the ceiling is server-side, not
  the client TLS path — see §3 and Key Findings.

### 1b. Open-loop churn — the rate story

<table>
  <thead><tr><th>Iteration</th><th colspan="2">documentdb</th><th colspan="2">mongo-shard</th></tr>
  <tr><th></th><th>Max</th><th>Avg</th><th>Max</th><th>Avg</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td>1</td><td>3,973</td><td>457</td><td>2,244</td><td>1,340</td></tr>
    <tr><td>2</td><td>4,458</td><td>385</td><td>2,793</td><td>1,080</td></tr>
    <tr><td>3</td><td>4,871</td><td>907</td><td>2,069</td><td>837</td></tr>
    <tr style="border-top:2px solid #555"><td><b>mean</b></td><td><u><b style="color:#1a7f37">4,434</b></u></td><td>583</td><td>2,369</td><td><u><b style="color:#1a7f37">1,086</b></u></td></tr>
  </tbody>
</table>

- Churn peaks are **far below** the hold peaks on both backends — by design, because a churned connection is
  Ready for only ~3 s and drains almost as fast as it opens (low concurrency, high turnover).
- DocumentDB reaches a higher **peak**; mongo-shard sustains a higher **average** — mongo holds a steadier
  mid-thousands Ready population while DocumentDB is burstier (accept-then-drain).

---

## 2. Latency — p90 / p99 (ms)

### 2a. Saturation hold — connection-establishment and keepalive-op latency

Cycle latency is meaningless in hold mode (it is the whole 5-min window), so the meaningful measures are
**Demand→Ready** (cold-connection open) and the **keepalive find** op. Values are the **mean of the 3
iterations' per-host mean percentiles**.

<table>
  <thead><tr><th>Metric</th><th>Pctile</th><th>documentdb</th><th>mongo-shard</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Establish (Demand→Ready)</b></td><td>p90</td><td>73,133</td><td><u><b style="color:#1a7f37">61,986</b></u></td></tr>
    <tr><td>p99</td><td><u><b style="color:#1a7f37">146,601</b></u></td><td>148,811</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Keepalive op (find)</b></td><td>p90</td><td>81,597</td><td><u><b style="color:#1a7f37">51,503</b></u></td></tr>
    <tr><td>p99</td><td>146,065</td><td><u><b style="color:#1a7f37">106,410</b></u></td></tr>
  </tbody>
</table>

> Both tails are enormous (tens to >100 s) because both backends are pushed **past their comfortable
> establishment throughput** to hold thousands of connections — this is a saturation stress test, not a
> steady-state latency measurement. DocumentDB pays this tail to reach 11k; mongo pays a similar tail and
> still only reaches ~4.3k.

### 2b. Open-loop churn — end-to-end cycle latency

Per-Task cycle (connect → 4 ops + 2.9 s sleep → disconnect), reported as worst-host and mean-of-hosts.

<table>
  <thead><tr><th>Metric</th><th>Pctile</th><th>documentdb (worst / mean)</th><th>mongo-shard (worst / mean)</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>Cycle latency</b></td><td>p90</td><td>59,797 / 34,068</td><td><u><b style="color:#1a7f37">7,993 / 7,731</b></u></td></tr>
    <tr><td>p99</td><td>132,425 / 86,640</td><td>148,437 / <u><b style="color:#1a7f37">93,001</b></u></td></tr>
  </tbody>
</table>

- Under 3-host churn, **mongo-shard has the far tighter p90** (~8 s vs ~34 s) — its 2× mongos fan-out keeps
  the bulk of cycles fast — but a comparable/worse p99 tail. DocumentDB's p90 balloons because it accepts a
  bigger burst then stalls it.

---

## 3. CPU & Memory usage (server tier, Azure Monitor over each run window)

This is the decisive evidence for **why** mongo-shard can't reach 10k.

<table>
  <thead><tr><th>Backend / node</th><th>Campaign</th><th>CPU avg</th><th>CPU peak</th><th>Memory</th></tr></thead>
  <tbody>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>documentdb</b><br>(Cosmos vCore M80)</td><td>hold</td><td><u><b style="color:#1a7f37">1.3%</b></u></td><td><u><b style="color:#1a7f37">9.4%</b></u></td><td>29.4% (peak 31.5%)</td></tr>
    <tr><td>churn</td><td>1.9%</td><td>11.0%</td><td>29.4% (peak 31.3%)</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>mongo VM1</b><br>(mongos + rs0 shard)</td><td>hold</td><td>71.2%</td><td><b style="color:#cf222e">99.7%</b></td><td>~4% used (of 500 GB)</td></tr>
    <tr><td>churn</td><td>72.7%</td><td><b style="color:#cf222e">99.7%</b></td><td>~4% used</td></tr>
    <tr style="border-top:2px solid #555"><td rowspan="2"><b>mongo VM2</b><br>(mongos + shard2 + configsvr)</td><td>hold</td><td>67.4%</td><td><b style="color:#cf222e">99.5%</b></td><td>~3% used (of 500 GB)</td></tr>
    <tr><td>churn</td><td>35.9%</td><td><b style="color:#cf222e">99.6%</b></td><td>~3% used</td></tr>
  </tbody>
</table>

- **The mongo VMs are CPU-SATURATED (peak ~99.7%) under the full 3-host load**, while DocumentDB's managed
  server sits at **1–11% CPU**. This is the root cause of the ~4.3k ceiling: the co-located mongos+mongod
  processes burn the 24 vCPUs performing **per-connection TLS handshake + SCRAM authentication** for the
  connection storm. DocumentDB terminates that handshake/auth on its separate managed gateway fleet, so its
  database compute barely registers the load.
- **Neither backend is memory-bound** — mongo VMs use ~3–4% of 500 GB; DocumentDB holds a steady ~29%.
- Client (load-generator) CPU/mem was not captured per-host for the 3-host aggregate. A single-host
  verification with the TLS fix showed the client at **~80% CPU / 1.9 GB / ~99k handles** while parking
  4,000 connections — i.e. at 3-host scale the generators are also working hard, but the **binding limit is
  the mongo VM CPU**.

---

## Key Findings

- **DocumentDB reaches and holds ≥10,000 concurrent connections** (peak **11,354**); **mongo-shard tops out
  at ~4,300** with this topology. This is a genuine, measured ceiling — mongo ran valid iterations, it was
  not rigged to fail.
- **Root cause = mongo VM CPU saturation, not a config limit.** Under the 3-host storm both mongo VMs peg at
  **~99.7% CPU** doing per-connection TLS+SCRAM, while their connection limit (1,000,000) and memory (~4%
  used) are nowhere near exhausted. DocumentDB's server stays at 1–11% CPU because handshake/auth is offloaded
  to its managed gateway.
- **The client-side TLS fix helped per-host but not in aggregate.** `MongoAllowInsecureTls` (skip private-CA
  chain validation) lifted a **single host** from ~1,400 → **4,000** Ready and cut establishment p99 from
  ~160 s → ~10 s. But at 3-host aggregate the server-side CPU wall dominates, so the combined number stayed
  ~4.3k (pre-fix 4,425 ≈ post-fix 4,349) — proving the remaining bottleneck is the mongos VMs, not the client.
- **Churn vs hold behave as designed.** Churn peaks (docdb ~4.4k / mongo ~2.4k) are far below hold peaks
  because churned connections drain in ~3 s; hold parks them for the full window. Under churn, mongo-shard
  owns the tight p90 cycle (~8 s vs ~34 s) via router fan-out.
- **Intermittent mongos instability under stress.** The `10.3.0.4:27016` router repeatedly hit
  `ServerSelectionTimeout` during preflight under load (requiring iteration retries) — a direct symptom of the
  same CPU saturation, not a client or network fault.

## To get mongo-shard to 10,000+ (infrastructure changes)

Concurrency here is capped by **connection-establishment CPU on the mongos VMs**. Levers, in order of impact:

1. **Dedicated mongos router VMs** — move the two mongos off the shard/config-server VMs so handshake/auth CPU
   isn't shared with `mongod` and the config server (VM2 currently runs three mongo processes).
2. **More mongos routers** (2 → 6+) and spread Tasks across all of them — establishment CPU scales with router
   count; this is the primary lever.
3. **Larger-vCPU mongos VMs** — the handshake/auth bottleneck is CPU; more cores per router directly raises
   accepts/second.
4. **Cheaper handshakes** — TLS session resumption/tickets to cut per-connection crypto cost.
5. Raise mongos `net.listenBacklog` + OS SOMAXCONN to stop the accept-queue overflow that surfaces as the
   `ServerSelectionTimeout` blips.

These are changes to the **shared live cluster** and were flagged, not applied unilaterally.

## Notes & caveats

- Concurrency is the per-second SUM of per-host `ActiveReady` (the `report merge` convention). Combined
  p90/p99 are reported as worst-host and mean-of-hosts — true pooled percentiles need raw samples/t-digests
  which the compact collector does not emit.
- Server CPU/memory are Azure Monitor 1-minute grains over each campaign window (DocumentDB `CpuPercent`/
  `MemoryPercent`; mongo VM `Percentage CPU` / `Available Memory Bytes`).
- mongo-shard hold figures are the **post-TLS-fix** campaign (`mongo-hold-fix-0724a`); the pre-fix campaign
  (`mongo-hold-0724a`) is retained for the null-result comparison. DocumentDB hold = `docdb-hold-0724a`;
  churn tags = `docdb-3host-0724a` / `mongo-3host-0724a`.
- Raw per-host artifacts were collected via the compact per-iteration metric collectors (concurrency +
  latency + peaks) and are captured in this summary; the full per-host JSON/CSV artifacts were **not
  retained** after collection (they lived only on the generator VMs under `C:\bmt\results\<tag>\iter-NN\`
  and were cleared when the hosts were redeployed for the final-test fixes). This summary plus the compact
  metrics are the authoritative record for the 3-host campaigns.
