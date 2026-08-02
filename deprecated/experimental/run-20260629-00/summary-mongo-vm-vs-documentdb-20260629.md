# Mongo-vm vs DocumentDB — HPC Connection Churn Summary

**Campaign:** `results/run-20260629-00`  
**Workload:** Full workload (`find -> remove -> insert -> find`), no connection reuse (`created/task = 1.000`)  
**Envelope:** 3 iterations x 300s, `taskSleepMs = 2000`

This report compares `mongo-vm` and `documentdb` for HPC-style connection churn where connection-open latency is the primary decision metric.

## 1. Connection-open latency (most important)

Lower is better.

| Scenario | Target | Connection p50 (ms) | Connection p90 (ms) | Connection p99 (ms) |
|---|---|---:|---:|---:|
| Steady | mongo-vm | **6.2** | **11.0** | **33.0** |
| Steady | documentdb | 12.9 | 32.7 | 92.3 |
| Burst (open-loop) | mongo-vm | **16.7** | **55.9** | **163.1** |
| Burst (open-loop) | documentdb | 288.1 | 1320.0 | 2110.7 |
| Burst (closed-loop) | mongo-vm | **19.0** | **62.0** | **154.6** |
| Burst (closed-loop) | documentdb | 310.7 | 1320.9 | 2047.6 |

## 2. Connection created/opened counts (avg and max)

`opened` is from driver `Connections.Ready` and is equal to created in this no-reuse campaign.

| Scenario | Target | Created/Opened avg per iteration | Created/Opened max per iteration | Created avg per second | Created max per second |
|---|---|---:|---:|---:|---:|
| Steady | mongo-vm | 40,500.7 | 40,501 | 133.67 | 140 |
| Steady | documentdb | 40,500.3 | 40,501 | 133.66 | 155 |
| Burst (open-loop) | mongo-vm | 46,024.0 | 46,024 | 176.56 | **712** |
| Burst (open-loop) | documentdb | 44,003.7 | 44,307 | 158.10 | 566 |
| Burst (closed-loop) | mongo-vm | 46,024.0 | 46,024 | 178.16 | **681** |
| Burst (closed-loop) | documentdb | 43,935.3 | 44,564 | 155.98 | 616 |

## 3. End-to-end context

| Scenario | Target | Successful tasks/s | Error % | Cycle p99 (ms) | find_input p99 (ms) |
|---|---|---:|---:|---:|---:|
| Steady | mongo-vm | 134.10 | **0.001** | 2274.5 | **47.0** |
| Steady | documentdb | 134.07 | 0.004 | **2215.4** | 137.6 |
| Burst (open-loop) | mongo-vm | **151.68** | **0.007** | **3882.1** | **1485.9** |
| Burst (open-loop) | documentdb | 144.46 | 0.024 | 6536.2 | 3601.3 |
| Burst (closed-loop) | mongo-vm | **152.62** | **0.010** | **3761.4** | **1416.9** |
| Burst (closed-loop) | documentdb | 144.11 | 0.030 | 6403.8 | 3485.4 |

## Key takeaway for HPC churn

In this campaign, `mongo-vm` is clearly stronger on the primary HPC churn metric (connection-open latency), including burst tails (p99). It also sustains higher connection creation peaks (max 712/sec open-loop, 681/sec closed-loop) while keeping lower cycle and cold-op tails than `documentdb`.

