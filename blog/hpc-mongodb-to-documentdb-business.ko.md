# MongoDB에서 Azure DocumentDB로: HPC 워크로드 이전의 비즈니스 가치

Caldova Securities(가명)는 안정성과 성능을 이유로 기존 상품평가 HPC 시스템의 자체 관리형(self-managed) MongoDB를 **Azure DocumentDB M80, 샤드 2개, HA 활성화** 구성으로 마이그레이션했습니다. 본 글은 마이그레이션으로 얻는 운영상의 가치와 이를 뒷받침하는 테스트 결과를 간단히 정리합니다. 기술 세부 내용과 재현 방법은 [영문 기술 문서](hpc-mongodb-to-documentdb-reference.md)에서 확인 가능합니다. 최종 테스트는 2026-08-03에 수행되었으며 최신 Azure 버전과 차이가 있을 수 있습니다.

## As-is에서 To-be로

| | As-is: 자체 관리형 MongoDB | To-be: Azure DocumentDB |
|---|---|---|
| 구성 | primary VM 2대, standby VM 2대 | M80, 샤드 2개, HA 활성화, global endpoint 1개 |
| 운영 범위 | primary, standby, 패치, 인증서, capacity planning을 직접 운영 | 완전 관리형(fully managed) 서비스가 해당 계층을 운영 |
| 용량 조정 | VM 증설 및 구성 변경 | 티어 변경 또는 샤드 추가 |

## Why Azure DocumentDB

### Azure DocumentDB란

- Azure DocumentDB는 완전 관리형 오픈소스 [DocumentDB](https://github.com/documentdb/documentdb) 기반의 MongoDB 호환 문서 데이터베이스입니다. 
- Azure DocumentDB는 99.03%의 MongoDB 호환성을 보입니다. Wire protocol과 BSON을 지원합니다. Azure DocumentDB로 기존 MongoDB 도구와 driver를 그대로 사용하면서, 데이터베이스 운영은 Azure가 맡도록 애플리케이션 변경을 최소화한 마이그레이션이 가능합니다.
- 고가용성(HA)이 활성화된 클러스터는 샤드마다 standby를 두고 synchronous replication으로 데이터 무손실 failover를 보장합니다. Failover와 무관하게 global 연결 문자열이 유지되며, 샤드당 99.99% availability SLA가 적용됩니다.

참고: [Azure DocumentDB overview](https://learn.microsoft.com/en-us/azure/documentdb/overview)

### 티어별 스펙과 클러스터 구조

**티어별 스펙**

| 티어 | vCore | RAM | 비고 |
|---|---|---|---|
| M10 / M20 / M25 | 1 / 2 / 2 (burstable) | 2 / 4 / 8 GiB | 개발/테스트용 |
| M30 | 2 | 8 GiB | |
| M40 | 4 | 16 GiB | |
| M50 | 8 | 32 GiB | |
| M60 | 16 | 64 GiB | 테스트 대상 |
| **M80** | **32** | **128 GiB** | **테스트 대상, 프로덕션 선정** |
| M200 | 64 | 256 GiB | 테스트 대상 |

vCore와 RAM은 티어에 따라 한 묶음으로 정해지고, **storage는 티어와 별도로 선택**합니다. Storage는 늘릴 수만 있고 줄일 수는 없습니다. 출처: [Compute and storage](https://learn.microsoft.com/en-us/azure/documentdb/compute-storage)

**클러스터 구조**

- **기본 용량 단위는 물리 샤드이며, 샤드 1개가 노드 1개입니다.**
- **각 샤드에는 티어에 해당하는 vCore와 RAM이 한 묶음으로 할당되고, 독립적인 SSD storage 용량이 설정됩니다.**
- **한 클러스터의 모든 샤드는 동일한 compute/storage 사양을 가집니다.** 따라서 샤드를 추가하면 같은 사양의 노드가 하나 더 늘어납니다. 예를 들어 M80 샤드 2개는 총 64 vCore / 256 GiB RAM입니다. HA를 켜면 샤드마다 같은 사양의 standby 샤드가 추가로 만들어집니다.
- **Database는 클러스터 안에서 데이터를 논리적으로 구분하는 단위입니다.**
- **샤드는 그 데이터를 물리적으로 분산해 저장하고 처리하는 단위입니다.**

### 운영의 효율성

- **MongoDB VM과 standby 관리 불필요.** 벤치마크의 MongoDB VM 2대는 각각 `Standard_FX24ms_v2`(24 vCPU, 약 500 GiB RAM)였으며, `mongos`와 shard `mongod`를 함께 호스팅했습니다. 연결 유지 시험에서 VM1은 평균 65.2%, 최대 99.7%, VM2는 평균 44.5%, 최대 99.6% CPU에 도달해 4,714개 연결에서 한계에 부딪혔습니다. 전용 `mongos` VM 2대를 추가한 4-router 구성에서 이 병목이 해소됐고, DocumentDB는 같은 부하에서 server CPU가 약 0.8-1.5%에 머물렀습니다.
- **티어 변경으로 용량 조정.** 필요에 따라 DocumentDB의 티어를 바꿔 vCore 수와 메모리를 선택하고 zero-downtime으로 compute를 조정할 수 있습니다.
- **연결 구성이 단순.** MongoDB는 `mongos` pinning과 인증서 검증 과정이 필요하지만, 완전 관리형 DocumentDB는 global endpoint를 사용합니다.

### 관리 및 가용성

- **운영 영역을 서비스가 담당.** HA, standby 샤드, failover, 모니터링(Azure Monitor) 등을 직접 구성하지 않아도 됩니다.
- **자동 백업과 point-in-time restore(PITR).** 활성 클러스터의 백업은 자동으로 수행되며 35일간 보존됩니다. 다만 복원은 새 클러스터를 만드는 방식이고, 백업만으로는 대규모 장애에 대한 disaster recovery를 대체할 수 없습니다.
- **장애 시 동작.** 노드 장애가 나면 standby 샤드가 primary로 승격되고 connection string은 그대로 유지됩니다. Global connection string와 함께 retryWrites=true, retryReads=true 파라미터를 사용해 전환 시에도 zero downtime으로 읽기와 쓰기를 계속할 수 있습니다.

참고: [High availability](https://learn.microsoft.com/en-us/azure/documentdb/high-availability), [Backup and restore](https://learn.microsoft.com/en-us/azure/documentdb/how-to-restore-cluster)

## Azure DocumentDB 성능 테스트

### 개요

| 항목 | Azure DocumentDB M80 (샤드 2개) | MongoDB (`mongos` 4개) |
|---|---|---|
| 평균 워크로드 처리량 (약 135 tasks/s) | 134.9 tasks/s, 오류율 0.000% | 134.9 tasks/s, 오류율 0.001% |
| Read median latency | 27.5 ms | 45.6 ms |
| 12,000개 연결 유지 시 연결 실패율 | 0.2% | 6.1% |
| worst case 시 연결 수립 p99 | 22.3 s | 159.7 s |

실제 프로덕션에서 Azure DocumentDB는 최대 약 200 IOPS, 매우 낮은 CPU 사용률을 보였습니다. 테스트에서는 worst case 시나리오까지 검증했지만, 프로덕션 워크로드에서는 여유가 충분합니다.

### 테스트 정리

| 테스트 | 부하 | 확인한 것 | 결과 | 의미 |
|---|---|---|---|---|
| 평균 워크로드 | 약 135 tasks/s, 4 operations | 티어별 throughput, 오류율 | M60/M80/M200 모두 134.7-134.9 tasks/s, 오류율 0.002% 이하 | 평균 워크로드에서는 티어가 성능을 좌우하지 않음 |
| 연결 폭주 (worst case) | 약 3,900 new connections/s, host 3대 | server CPU, 오류율, 연결 수립 p99 | CPU 1.2-1.5%, 오류율 72-97% | 병목은 server compute가 아니라 연결 허용(admission) 단계. 샤드 1개에서 티어를 올리면 처리량 증가(22.0 → 154.7 tasks/s) |
| 연결 폭주 (운영 피크) | 약 1,210 new connections/s, host 2대 | 최대 동시 연결, throughput | 샤드 1개 M80: 최대 9,979개 연결, 191.6 tasks/s, 오류율 38.8% | 연결 약 10,000개 누적은 샤드 1개로도 도달하나 오류율은 여전히 존재 |
| 연결 유지(Connection hold) | 12,000개 연결, find only | 12,000개 도달, 10,000개 이상 달성 iteration | 샤드 2개: M60 2/3, M80 2/3, M200 3/3. 샤드 1개는 약 11,000개에서 정체 | 티어 상향은 연결 유지 한계를 못 높임. 데이터 분산(샤드 2개)이 효과적 |

### 요약

- 평균 워크로드(약 135 tasks/s)에서 두 DB 모두 오류율이 0.01% 이하(DocumentDB 0.000%, MongoDB 0.001%)였고, DocumentDB의 read median latency가 더 낮았습니다(DocumentDB 27.5 ms, MongoDB 45.6 ms).
- 연결 후 준비가 완료된 상태의 연산(warm operation) median은 비슷하거나 DocumentDB가 낮았습니다(DocumentDB insert 3.5 ms, MongoDB 4.7 ms). 상위 백분위는 MongoDB가 낮았습니다(DocumentDB remove/insert p99 약 39-41 ms, MongoDB 약 7 ms).
- 연결 수립 성능은 구간에 따라 달랐습니다. 평균 워크로드의 연결 수립 p99(DocumentDB 75.6 ms, MongoDB 37.0 ms)와 연결 유지 시험의 연결 확인(keepalive) p99(DocumentDB 23.3 s, MongoDB 14.8 s)는 MongoDB가 낮았고, worst case에서는 DocumentDB가 낮았습니다(DocumentDB 22.3 s, MongoDB 159.7 s).
- 병목은 DB compute가 아니라 연결 수립 단계입니다. MongoDB는 `mongos`를 2대에서 4대로 늘려 대응했고, DocumentDB는 샤드를 1개에서 2개로 늘리자 연결 유지 구간의 병목이 크게 줄었습니다(연결 실패율 약 37-38% → 0.1-1.0%, 연결 확인 p99 114-139 s → 23-34 s).
- 연결 폭주 구간의 처리량은 샤드 수보다 티어의 영향이 컸습니다(샤드 1개 M80 → M200: 22.0 → 154.7 tasks/s).

### Lessons Learned

아래는 DB 스펙(티어, 샤드 수, HA)을 정하기 위해 수행한 테스트에서 얻은 내용입니다. 다른 프로젝트에서도 같은 방식으로 재현해 볼 수 있으며, 재현 방법은 [영문 기술 문서](hpc-mongodb-to-documentdb-reference.md)에 있습니다.

**1. 자체 관리형 MongoDB와 확장 방식이 다르다.**

이 워크로드는 task마다 연결을 새로 맺기 때문에 TLS 핸드셰이크와 인증 부하가 큽니다. 벤치마크의 자체 관리형 MongoDB에서는 `mongos`와 shard `mongod`를 함께 호스팅한 `Standard_FX24ms_v2` VM 2대(각 24 vCPU, 약 500 GiB RAM)의 CPU가 각각 최대 99.7%와 99.6%에 도달해 4,714개 연결에서 한계에 부딪혔습니다. DocumentDB는 라우터 VM을 직접 운영하지 않고 티어와 샤드 구성으로 조정하며, 같은 시험에서 server CPU가 약 0.8-1.5%였습니다. 연결 폭주 대응이 "VM 추가와 운영"에서 "서비스 구성 선택"으로 바뀝니다.

**2. 티어와 샤드는 부하 유형에 따라 효과가 다르다. DocumentDB에서는 샤드를 늘리자 연결 유지 병목이 크게 해소되었다.**

- **평균 워크로드(약 135 tasks/s):** M60, M80, M200이 모두 134.7-134.9 tasks/s로 같았습니다. 티어는 평균 워크로드 성능에 영향을 주지 않습니다.
- **연결 유지(Connection hold):** 샤드 1개에서는 티어를 올려도 약 11,000개 연결에서 정체했고, 연결 실패율이 약 37-38%였습니다. 데이터를 샤드 2개에 분산하자 12,000개(테스트 상한)에 도달했고, 연결 실패율은 0.1-1.0%로, 연결 확인(keepalive) p99는 114-139초에서 23-34초로 낮아졌습니다. 두 번째 샤드가 연결을 함께 받아주는 효과로 보입니다.
- **연결 폭주(churn):** 처리량은 샤드 수가 아니라 티어가 좌우했습니다. 샤드 1개에서 M80을 M200으로 올리자 22.0에서 154.7 tasks/s로 늘었습니다. M200 샤드 2개에서는 간헐적인 연결 허용 제한(admission throttling)이 발생했고 약 8분 뒤 회복되었습니다.

일반적으로는 최소 샤드 구성으로 시작해 필요에 따라 확장하는 것이 권장됩니다. 그러나 이 HPC 워크로드는 task마다 새 연결을 만들기 때문에, 예기치 않은 연결 폭주에 대비한 안정성이 중요합니다. 프로덕션에서 단일 샤드로 운영하는 것은 위험하다고 판단해 M80 샤드 2개를 선택했습니다.

샤드는 만들기만 하면 분산되는 것이 아닙니다. 샤드를 2개 provisioning했는데도 데이터가 한 샤드에만 있던 사례가 있었습니다. 비어 있는 collection을 샤딩한 뒤 데이터를 넣고, chunk metadata와 query explain으로 분산을 확인해야 합니다.

**3. Client 쪽 병목을 먼저 배제한다.**
DB는 한가한데 latency가 오르면 DB 성능 문제로 오해하기 쉽습니다. 이번에는 두 가지가 client 쪽 원인이었습니다. 하나는 .NET ThreadPool starvation으로, 기본 최소 thread 수로는 순간적으로 몰리는 동시 작업을 처리하지 못했고 최소 thread 수를 올려 해결했습니다. 다른 하나는 Private endpoint 환경에서 인증서 revocation check가 막혀 동시 40개 handshake가 각각 약 29.5초 걸린 것입니다(순차 실행은 100 ms 미만). Global endpoint(SRV)를 쓸 때도 seed hostname과 SRV target hostname이 모두 private으로 resolve되는지 확인해야 합니다. DocumentDB 성능을 판단하기 전에 client의 thread, CPU, 인증서 경로, DNS부터 점검해야 합니다.

**4. CPU만으로 capacity를 판단하지 않는다.**
worst case(약 3,900 new connections/s)에서 오류율이 72-97%였는데도 server CPU는 1.2-1.5%였습니다. 한계는 compute가 아니라 새 연결을 받아들이는 연결 허용(admission) 단계에 있었습니다. CPU와 함께 연결 수, 연결 오류, throttling 지표를 모니터링해야 합니다. 이는 관찰된 지표에 근거한 해석이며, 내부 gateway 용량을 직접 측정한 것은 아닙니다.

### MongoDB 대비 Azure DocumentDB 도입 시 고려 사항

- **Tail latency.** DocumentDB는 평균 워크로드 p99(DocumentDB 107.8 ms, MongoDB 67.9 ms)와 연결 유지 시 연결 확인(keepalive) p99(DocumentDB 23.3 s, MongoDB 14.8 s)가 MongoDB보다 높았습니다. 완전 관리형 서비스에서는 새 연결이 gateway와 인증을 거치기 때문으로 추정됩니다.
- **Client 재사용.** 이 테스트는 task마다 새 client를 만드는 HPC 시나리오입니다. 일반적인 MongoDB 대체 애플리케이션에서는 연결을 재사용하는 것이 좋습니다.
