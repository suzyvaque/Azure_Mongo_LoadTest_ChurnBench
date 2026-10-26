# DocumentDB M80 2-shard HA 재시험 계획

## 1. 전제와 실행 분기

- 테스트 대상은 **M80, 2 shards, HA enabled**여야 한다.
- 현재 repository의 Azure resource catalog가 가리키는 `docdb-dbtest-hpc-1`은 2026-10-26 조회 시
  **M30, 2 shards, HA disabled**였다. 따라서 M80·HA 전환 또는 올바른 cluster resource ID 지정 전에는
  본 시험을 실행하지 않는다. HA config의 preflight 기대 tier는 M80으로 고정해 잘못된 대상 실행을 막는다.
- `BMT_CONN`은 서비스가 제공한 global `mongodb+srv` endpoint여야 한다. HA profile은 SRV target을
  benchmark 코드에서 pin하지 않고 driver에 그대로 전달한다.
- 약 **1,210 new connections/s**는 과거 로그 기반의 임시 입력값이며 현재 production SLO가 아니다.

실행 분기:

1. 고객이 현재 peak 부하를 제공하지 못하면, 1,210/s로 provisional connection test를 수행한다.
2. 고객이 실제 peak new connections/s, tasks/s, connection lifetime, peak concurrent connections를
   제공하면, 그 값에 고객과 합의한 headroom을 더해 connection test를 수행하고 HA와 PITR을 시험한다.
3. MongoDB 비교는 동일 generator, dataset, TLS 정책, test window를 사용할 수 있을 때만 수행한다.

## 2. Connection profile

공통 profile: `config/production/documentdb-ha-full-workload.json`

| 설정 | 값 | 목적 |
|---|---:|---|
| Global endpoint | `mongodb+srv` 원문 유지 | failover/topology rediscovery |
| `retryWrites` | `true` | retryable write 1회 재시도 |
| `retryReads` | `true` | retryable read 1회 재시도 |
| `serverSelectionTimeoutMS` | 30,000 | primary 전환 중 topology 재선택 대기 |
| `connectTimeoutMS` | 10,000 | TCP/TLS 연결 상한 |
| `socketTimeoutMS` | 60,000 | operation 소켓 대기 상한 |
| `heartbeatFrequencyMS` | 5,000 | topology 변경 감지 간격 |
| `maxIdleTimeMS` | 60,000 | 장기 probe의 유휴 연결 정리 |
| Read preference | `primaryPreferred` | primary 장애 중 읽기 경로 허용 |
| Read concern | `majority` | failover 전후 읽기 일관성 |
| Write concern | `majority` | failover 전후 쓰기 내구성 |
| Pool | min 0 / max 1 | 기존 task별 client/no-reuse 모델 유지 |
| Direct connection | false | node pinning 금지 |

`primaryPreferred`는 장애 중 secondary read를 허용하므로, 고객 애플리케이션이 primary-only read를
요구한다면 별도 profile로 시험한다. 정상 성능 비교에서는 두 backend에 같은 read/write concern을
적용하거나 차이를 명시한다.

## 3. 시험 config

| 시험 | Config | 기본 부하 | 반복 |
|---|---|---:|---:|
| 최종 비교 부하 | `documentdb-ha-250tasks-3host.json` | 3 hosts 합계 250 tasks/s = nominal 1,000 ops/s | 3 × 300 s |
| 연결 생성률 | `documentdb-ha-1000conn-3host.json` | 3 hosts 합계 1,000 connections/s, indexed find 1회/connection | 3 × 300 s |
| 최종 구성 baseline | `documentdb-ha-steady.json` | 135 tasks/s | 3 × 300 s |
| 임시 connection rate | `documentdb-ha-open-loop-1210-multihost.json` | 3 hosts 합계 약 1,212/s | 3 × 300 s |
| HA event probe | `documentdb-ha-probe.json` | 50 tasks/s | event 전후를 포함한 1회 장시간 실행 |
| PITR validation | HA probe + 복원 cluster smoke | 고객 RPO 시점 | restore 1회 이상 |

HA 시험은 event 전 안정 구간, event 구간, 정상 복귀 후 안정 구간을 모두 포함한다. Tier resize 기록을
HA failover 결과로 대체하지 않는다.

## 4. 필수 측정 지표

### 4.1 Client와 연결

| 지표 | Source | 판정 용도 |
|---|---|---|
| offered/s, started/s, completed/s | LoadGen per-second series | 부하가 실제 공급·처리됐는지 확인 |
| connections created/ready/failed/closed | driver events | connection admission 성공률 |
| combined created/s, ready/s | 3-host merge | 1,210/s 입력과 실제 ready rate 구분 |
| ActiveConnecting/ActiveReady/WaitingForServer | driver gauges | 연결 단계별 queue와 동시 연결 |
| demand-to-ready, connection-open p50/p95/p99/max | driver timing | 사용자 관점 연결 수립 지연 |
| hello/auth latency | command events | topology handshake와 SCRAM 분리 |
| operation p50/p95/p99/max | LoadGen | read/write tail latency |
| error type/code/message count | LoadGen classifier | selection, socket, auth, throttle 구분 |
| retryable command failure count | driver events | retry가 숨긴 transient failure 확인 |
| generator CPU/RAM/threads/handles/TCP states | client sampler | generator 병목 배제 |

`retryWrites=true` 또는 `retryReads=true`라는 설정만으로 실제 재시도 횟수가 완전히 관측되는 것은
아니다. 현재 driver event는 retryable command failure의 근사 증거다. HA 결과에는 최종 성공/실패와
업무 ID 중복·누락 검증을 함께 사용한다.

### 4.2 HA/RTO/RPO

| 지표 | 정의 |
|---|---|
| Full outage | 연속된 구간에서 성공 operation이 0이고 요청 실패가 발생한 시간 |
| Degraded interval | 정상 baseline 대비 throughput 또는 latency SLO를 벗어난 시간 |
| Recovery time | HA event 시작부터 baseline throughput/latency가 연속 안정화될 때까지 |
| Read/write error rate | event window의 operation별 실패/시도 |
| Retry recovery | transient failure 후 최종 성공한 요청 수 |
| RPO check | event 직전 acknowledged write의 누락 수와 중복 수 |
| Topology recovery | global endpoint로 재접속한 최초 시각과 정상 Ready rate 복귀 시각 |

현재 LoadGen per-second series로 outage를 1초 해상도로 계산할 수 있다. sub-second 단절 시간이
필요하면 operation별 timestamp event를 추가해야 한다.

### 4.3 PITR

- 선택 restore point와 실제 복원 데이터 시점 차이
- restore 요청부터 cluster ready까지의 시간
- Private Endpoint, DNS, HA, firewall 및 애플리케이션 연결 재구성 시간
- 원본/복원 document count, index와 shard metadata
- 표본 또는 전체 checksum, acknowledged write 누락·중복
- 복원 cluster에서 preflight와 정상 부하 smoke 성공 여부
- 전체 RTO는 cluster 생성 시간이 아니라 애플리케이션 정상화 완료까지로 계산

## 5. DocumentDB 대 MongoDB CPU/RAM 비교 가능성

### 현재 가능한 측정

| 대상 | CPU | RAM | 해상도/출처 |
|---|---|---|---|
| DocumentDB | `CpuPercent` | `MemoryPercent`, `CommittedMemoryPercent` | Azure Monitor, PT1M, `ServerName`별 |
| MongoDB VM | `Percentage CPU` | `Available Memory Bytes` | Azure Monitor, PT1M, VM별 |
| Load generator | process CPU | process working set | LoadGen, 1초 |

`Get-AzureMetrics.ps1`은 DocumentDB CPU/RAM을 `ServerName`별로 보존하고, Mongo VM의 SKU/vCPU/RAM
capacity와 RAM used percentage를 계산해 `azure-metrics.json`에 기록한다. 현재 Mongo shard VM 두 대는
각각 `Standard_FX24ms_v2`로 조회되며 VM당 24 vCPU, 504 GiB RAM이다. M80은 shard당 32 vCore,
128 GiB이며 HA standby는 처리 용량으로 합산하지 않는다.

### 비교 규칙

1. idle baseline과 load window를 분리한다.
2. 평균, 최대, p95에 해당하는 time-series 값과 시간축을 함께 비교한다. 단일 max만 비교하지 않는다.
3. DocumentDB는 `ServerName`별 primary/standby와 shard를 구분하고, 역할을 식별할 수 없으면 합산하지 않는다.
4. Mongo RAM used %는 $100 \times (1 - availableBytes / totalBytes)$로 정규화한다.
5. CPU % 외에 approximate used-vCPU를 $vCPU \times CPU\% / 100$으로 병기할 수 있으나,
   PaaS vCore와 VM vCPU의 동일 성능을 가정하지 않는다.
6. DocumentDB의 managed gateway 자원은 CPU/RAM metric에 포함되지 않을 수 있으므로, 낮은 shard CPU를
   connection admission 여유로 해석하지 않는다.
7. DocumentDB HA standby 자원과 Mongo standby/router topology 차이를 비용·처리량 비교와 분리한다.

### 제한

- DocumentDB OS RSS/available bytes와 gateway CPU/RAM은 제공되지 않는다.
- DocumentDB active connection counter와 `serverStatus`는 제공되지 않는다.
- 따라서 RAM 절대 사용량의 완전한 apples-to-apples 비교는 불가능하고, **각 platform이 노출하는
  utilization/headroom 비교**가 정확한 표현이다.

## 6. 고객에게 재확인할 부하 데이터

- 측정 기간과 timezone
- 평균/최대 tasks/s
- 1초 또는 1분 bucket의 new physical connections/s
- peak concurrent connections와 connection lifetime p50/p95/p99
- task당 생성되는 physical connection 수
- client/pool 재사용 여부와 pool 설정
- read/write 비율, 문서 크기, operation 순서
- timeout, retry, read preference, read/write concern
- 허용 error rate, p99 latency, HA RTO/RPO
- 원본 application/driver 또는 Azure Monitor log

고객 peak가 제공되면 테스트 입력은 `confirmed peak × agreed headroom`으로 정하고, 1,210/s 결과는
과거 가정에 대한 참고값으로만 남긴다.