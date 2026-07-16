# MongoDB(IaaS) vs. Azure DocumentDB(PaaS) 결과 보고서 - Task 관점

**작성 목적:** 현재 IaaS(Azure VM 위의 자체 관리 MongoDB)로 제안된 HPC 백엔드를
Azure **DocumentDB(PaaS, Cosmos DB for MongoDB vCore)** 로 전환할 수 있는지 검증하기 위한 비교 테스트 결과 정리.

- **기준 결과:** `run-20260624-shard` (2026-06-24 실행)
- **기준 코드:** GitHub 커밋 [`ae56f50`](https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/tree/ae56f505c1230064864cb838056b725b9c27ed52) — *feat(mongo-shard): add sharded MongoDB target with round-robin direct-connect* (2026-06-24 17:05 KST)

> **중요.** 본 테스트는 프로덕션 트레이스에서 도출한
> **Task 수(볼륨)와 처리량(초당 속도)** 을 기준으로 설계·실행되었다.
> - **기준 부하 = 피크시간 지속 처리량:** 프로덕션 피크시간의 **약 484,755 Tasks/h ≈ 135 conn/s
>   (540 ops/s)** 를 그대로 재현한다. 테스트 steady 시나리오가 **135 Tasks/s**(실측 132.8~135.0)로
>   주입하므로, 총 Task 볼륨·처리량 축에서는 프로덕션 피크시간을 **이미 1:1로 대표한다.**
>   (프로덕션 데이터가 시간 단위로 집계되어 있어, 이 처리량을 지속 주입하면 피크시간 총량을 정확히
>   재현한다.)
> - **아직 완전히 재현되지 않은 축 = 순간 동시성:** 프로덕션에는 처리량과 **별개로** 순간 폭주
>   지표(동시 커넥션 피크 **≈11,012**, 순간 개설률 **≈1,210 conn/s**)가 존재한다. 이는 시간당
>   평균이 아니라 스케줄러가 한 Job의 Task(최대 500개)를 동시에 뿌려 생기는 **버스트 스파이크**로,
>   처리량이 같아도 자동으로 재현되지 않는다. 이번 테스트는 이 순간 동시성을 약 **3.1K~3.5K(피크의
>   ~32%)** 까지만 도달했다(상세·후속 계획은 **8.7**).
>
> **요약:** "피크시간 지속 부하(Task 수·처리량)를 버티는가"는 이미 검증되었고(양쪽 모두 0% 오류로
> 소화), 남은 검증 과제는 "**순간 커넥션 폭주(11K 동시 / 1,210 conn/s)** 에서 각 DB가 연결 한계·거절
> 없이 버티는가"이다.

---

## 1. 배경 및 테스트 목적

HPC 워크로드는 **매 작업(Task)마다 새로운 커넥션을 열고 작업 후 즉시 닫는**
"커넥션 처닝(connection churn)" 패턴이 특징이다. 즉 커넥션 풀 재사용이 없고, 모든 요청이
**TCP + TLS 핸드셰이크 + 인증(SCRAM)** 비용을 매번 지불한다.

본 벤치마크는 이 **최악의 커넥션 처리 시나리오**를 재현하여, 자체 관리
**MongoDB(IaaS)** 와 관리형 **DocumentDB(PaaS)** 가 커넥션 폭주 상황에서 각각 어떻게
동작하는지를 동일한 조건으로 비교한다.

> **핵심 관점:** 이 테스트는 일반적인 커넥션 풀 애플리케이션의 성능을 나타내지 않는다.
> 합격/불합격 임계치는 없으며, **평균이 아니라 p99 / p95 / p99.9 등 꼬리(tail) 지연시간**을
> 우선적으로 봐야 한다. 커넥션 처닝의 지연은 핸드셰이크·인증·서버 선택·스로틀링 같은
> 꼬리 이벤트가 지배하기 때문이다.

직전 단일 노드(`mongo-vm`) 테스트에서 발생했던
**풀 워크로드 붕괴(오류율 23~32%)** 는, MongoDB를 **샤딩 클러스터**로 구성함으로써
해소했다.

---

## 2. 테스트 방법

### 2.1 처닝 모델 (측정 대상 단위)

각 Task는 **완전히 새로운 커넥션 1개**를 열고, **정확히 아래 4개 연산**을 순서대로 수행한 뒤
커넥션을 닫는다. 모든 연산은 `_id` 포인트 조회가 아니라 **`ReqId` 필드** 기준이다.

1. `find` (입력) — `calc_input` 에서 `ReqId` 로 조회
2. *(계산 대체 sleep — Task 사이클에는 포함, 연산별 지연에는 제외)*
3. `remove` (출력) — `calc_output` 에서 `ReqId` 로 삭제
4. `insert` (출력) — `calc_output` 에 삽입
5. `find` (출력) — `calc_output` 에서 `ReqId` 로 조회

클라이언트/세션/커서/풀을 Task 간에 **재사용하지 않는다** (하드 제약, `maxPoolSize=1`,
`minPoolSize=0`). 커넥션은 매 Task 후 실제로 해제된다.

- **cold 연산:** 새 소켓에서 처음 실행되는 연산(op1 `find_input`) → 연결 접속 비용(TCP+TLS+auth)이 포함되기 때문에,
  결과 보고 시에는 `연산 − ConnectionOpenMs` 로 접속 비용을 분리한다.
- **warm 연산:** 이미 열린 소켓에서 실행(op2~4) → 순수 서버 실행 시간.
- **커넥션 생성:** `ConnectionOpenMs` 로 **별도 독립 측정**되어, 순수 핸드셰이크 비용을 나타낸다.

### 2.2 부하 시나리오

두 시나리오는 **각각 별도의 런으로** 실행했다(동시 실행 시 도착률이 중첩되므로 금지).

| 시나리오 | 부하 모델 | 파라미터 |
|---|---|---|
| **Steady** | 고정 도착률(open-loop) | **135 Tasks/s** |
| **Burst** | 포아송 도착 | **λ = 0.57 jobs/s**, job당 **14~500 Tasks**, 순간 최대 **≈1,200 conn/s** 스파이크 |

- **오픈 루프(offered-load):** 두 백엔드에 **동일한 도착 스케줄**을 주입하므로, 처리량(tasks/s)은
  포돠되지 않는 한 거의 동일하게 수렴한다. 따라서 **처리량은 용량이 아니라 "밀어넣은 부하"의 확인값**이며,
  실제 차별화 지표는 **지연시간(커넥션 + 연산 백분위수)** 이다.

### 2.3 실행 조건

- **양쪽 백엔드 모두 TLS 활성화** → 커넥션 확립 비용 직접 비교 가능.
- **한 번에 한 타깃씩 순차 실행**(병렬 금지). 런 사이 `TIME_WAIT` 를 깨끗한
  베이스라인까지 배수. 각 타깃은 **해당 백엔드와 동일 AZ의 부하 발생기**에서 구동
  (mongo-shard → AZ3 `vm-dbtest-hpc-0`, documentdb → AZ2 `vm-dbtest-hpc-0-az2`)하여 교차 AZ 지연을 배제.
- 프로덕션 사이징: 각 런 **3 iterations × 600초**.
- 워크로드 3종: 단일 연산 **find-input**, 단일 연산 **insert-output**, 전체 4연산 **full-workload**.
- 실행 전 **preflight 10개 필수 점검**(인덱스 존재, 사설망 경로, 호스트 TCP 튜닝 여유 등) 통과.
- **인덱스 전제(중요):** 모든 타깃의 `calc_input` / `calc_output` 에 `ReqId` 인덱스 존재
  (`prepare-data` 생성, `preflight` 검증). 인덱스 없는 런은 유효 비교가 아니다.
- **호스트 TCP 튜닝(필수):** 처닝 워크로드는 소켓마다 임시 포트를 `TIME_WAIT` 로 잡으므로,
  Windows 기본값(16,384 포트 / 120초 ≈ 137 conn/s)으로는 Burst 목표(≥1,200 conn/s)를 못 채운다.
  임시 포트 범위를 10000–65534(55,535개)로 넓히고 `TcpTimedWaitDelay=30초` 로 설정(≈1,851 conn/s).

### 2.4 데이터셋

- **정확히 100,000 문서**, 고정 시드 42(타깃 간 바이트 동일).
- 4개 크기 버킷: 6 KB×10,000 / 16 KB×15,000 / 50 KB×35,000 / 58 KB×40,000.
- 평균 문서 ≈ 43.7 KB, 총 ≈ 4.37 GB.

---

## 3. 리소스 스펙

### 3.1 비교 대상 백엔드

| 타깃 | 스펙 |
|---|---|
| **mongo-shard** (IaaS) | Azure VM (**Standard E32as v6**: 32 vcpus, 256 GiB memory) 위 **MongoDB 7.0 샤딩 클러스터** — **2 shards**, **mongos 라우터 2개**(10.3.0.6:27017, 10.3.0.4:27016), config server 1 member. `bmt_db` 는 **hashed `ReqId`** 로 샤딩(입력/출력이 샤드 간 ~50/50 분산). **TLS 활성화**(`allowTLS`, 자가서명 인증서 + `CAFile` 신뢰 체인). 클라이언트는 Task마다 **두 mongos에 라운드로빈으로 direct single-server 접속**(`directConnection=true`). |
| **documentdb** (PaaS) | **Azure DocumentDB (Cosmos DB for MongoDB vCore)** — **HA 활성화**, 관리형 **TLS**(항상 TLS). 티어 **M80**: 32 vCores, 128 GiB RAM, 512 GiB Disk. |

> `cosmos-ru`(Cosmos DB for MongoDB RU) 타깃은 코드에 존재하나 **본 결과에서는 제외**.

### 3.2 mongo-shard 토폴로지

| 역할 | 엔드포인트 | 비고 |
|---|---|---|
| mongos #1 | 10.3.0.6:27017 | TLS allowTLS + CAFile |
| mongos #2 | 10.3.0.4:27016 | TLS allowTLS + CAFile |
| config server (csrs, 1 member) | 10.3.0.6:27019 | |
| shard2 mongod | 10.3.0.6:27018 | |
| shard1 mongod (rs0) | 10.3.0.4:27017 | |
| 부하 발생기 | vm-dbtest-hpc-0 (10.2.0.4) | MongoDB와 동일 AZ에 배치 |

### 3.3 documentDB 토폴로지

관리형 서비스이므로 클라이언트에는 **단일 클러스터 엔드포인트**로 노출되며(내부 라우팅/복제/HA는
플랫폼이 처리), 사설 엔드포인트(Private Endpoint)를 통해 사설 IP로 접속한다.

**부하 발생기는 mongo-shard와 다른 VM이다.** DocumentDB 인스턴스는 **AZ2** 에 위치하므로,
**교차 AZ 네트워크 지연(cross-AZ tax)을 피하기 위해** 부하를 **AZ2에 함께 배치된 별도 VM(`VM1-az2`)**
에서 구동했다. (mongo-shard는 AZ3의 `vm-dbtest-hpc-0`에서 구동 — 백엔드가 위치한 AZ에 부하 발생기를
동일 AZ로 맞추는 것이 본 벤치마크의 설계 원칙이다.) 두 부하 발생기 VM은 TCP 튜닝·.NET·드라이버
버전을 **동일하게** 맞춰 비교 공정성을 유지했다.

| 역할 | 엔드포인트 | 비고 |
|---|---|---|
| 클러스터 호스트 | `docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com` | `mongodb+srv://` SRV 해석, 항상 TLS, `authMechanism=SCRAM-SHA-256`, `retrywrites=false` |
| SRV 타깃 포트 | `fc-…-000.global.mongocluster.cosmos.azure.com:10260` | SRV가 실제 접속 대상으로 해석하는 포트 |
| 사설 엔드포인트 IP | 10.2.0.7 | Private Endpoint로 사설 IP 해석(공인 인입 없음) |
| Private DNS zone | `privatelink.mongocluster.cosmos.azure.com` | 호스트명·SRV 타깃 모두 사설 IP로 해석되도록 부하 발생기 VNet에 링크 |
| 부하 발생기 | vm-dbtest-hpc-0-az2 (10.4.0.4) | DocumentDB와 동일 AZ에 배치 |

> **주의:** mongo-shard(AZ3, `vm-dbtest-hpc-0`)와 documentdb(AZ2, `vm-dbtest-hpc-0-az2`)는 서로 다른 VM에서
> 구동되었다. 각 타깃을 해당 백엔드와 같은 AZ에서 구동해 교차 AZ 네트워크 세금을 배제한 것이 의도된
> 설계이며, 양 VM의 OS/TCP 튜닝·런타임·드라이버는 동일하게 맞췄다. 실행 시 커넥션 문자열은
> 산출물에서 자격증명·호스트가 모두 마스킹된다
> (`mongodb+srv://****:****@****/?retryWrites=false&authSource=admin`).

### 3.4 부하 발생기(로드 제너레이터) 호스트

| 항목 | 값 |
|---|---|
| OS | Windows Server 2025 |
| vCPU / RAM | 32 vCore / 256 GB |
| 런타임 | **.NET 8 SDK** (LTS) + **MongoDB C# Driver 2.30** (고정) |
| Accelerated Networking | 활성화 |
| TCP 튜닝 | 임시 포트 10000–65534, `TcpTimedWaitDelay=30s` |

### 3.5 공통 스택

**MongoDB Server 7.0 / wire 7.0**, **.NET 8 (LTS)**, **MongoDB C# Driver 2.30**.

비재사용 처닝: Task마다 새 `MongoClient`(`maxPoolSize=1` / `minPoolSize=0`).

---

## 4. 기준 코드 버전 및 링크

본 결과는 아래 커밋 버전의 코드로 생성되었다. (이후 커밋에서 iteration을 5분×3, sleep 2초 등으로
변경했으므로, **당시 값인 600초×3, sleep 10초** 를 기준으로 해석해야 한다.)

- **저장소:** https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench
- **기준 커밋(코드):** [`ae56f50`](https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/commit/ae56f505c1230064864cb838056b725b9c27ed52) — *feat(mongo-shard): add sharded MongoDB target with round-robin direct-connect*
- **해당 커밋의 전체 소스 트리:** https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/tree/ae56f505c1230064864cb838056b725b9c27ed52

핵심 소스 파일(기준 커밋에 고정된 링크):

| 구성 요소 | 링크 |
|---|---|
| Task별 커넥션 팩토리 (라운드로빈 direct-connect) | [TaskConnectionFactory.cs](https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/blob/ae56f505c1230064864cb838056b725b9c27ed52/src/Bmt.Core/Connections/TaskConnectionFactory.cs) |
| 부하 발생기 (Steady/Burst 시나리오) | [src/Bmt.LoadGen](https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/tree/ae56f505c1230064864cb838056b725b9c27ed52/src/Bmt.LoadGen) |
| 시더 (100k 시드 + ReqId 인덱스) | [src/Bmt.Seeder](https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/tree/ae56f505c1230064864cb838056b725b9c27ed52/src/Bmt.Seeder) |
| Preflight 게이트 | [src/Bmt.Preflight](https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/tree/ae56f505c1230064864cb838056b725b9c27ed52/src/Bmt.Preflight) |
| 리포트 생성기 | [src/Bmt.Report](https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/tree/ae56f505c1230064864cb838056b725b9c27ed52/src/Bmt.Report) |
| 프로덕션 설정(full-workload) | [config/production/full-workload.json](https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/blob/ae56f505c1230064864cb838056b725b9c27ed52/config/production/full-workload.json) |
| 환경 재현 블루프린트 | [docs/ENVIRONMENT-SETUP.md](https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench/blob/ae56f505c1230064864cb838056b725b9c27ed52/docs/ENVIRONMENT-SETUP.md) |

로컬 결과 산출물:
[INDEX.md](INDEX.md) ·
[summary-mongo-shard-vs-documentdb-20260624.md](summary-mongo-shard-vs-documentdb-20260624.md) ·
[comparison-mongo-shard-vs-documentdb-20260624.html](comparison-mongo-shard-vs-documentdb-20260624.html) ·
[INCIDENT-runaway-concurrency-meltdown.md](INCIDENT-runaway-concurrency-meltdown.md)

---

## 5. 부하 수치 요약

각 런은 3 iterations × 600초 = 총 1,800초(약 30분) 수준으로 진행되었다.

| 런 | 타깃 | 워크로드 | 시나리오 | 총 Task | 성공 | 실패 | 성공률 |
|---|---|---|---|---|---|---|---|
| find-input | mongo-shard | find-input | steady | 243,003 | 243,003 | 0 | 100.00% |
| find-input | mongo-shard | find-input | burst | 265,701 | 265,701 | 0 | 100.00% |
| insert-output | mongo-shard | insert-output | steady | 243,001 | 243,001 | 0 | 100.00% |
| insert-output | mongo-shard | insert-output | burst | 265,522 | 265,522 | 0 | 100.00% |
| full-workload | mongo-shard | full-workload | steady | 242,993 | 242,979 | 14 | 99.99% |
| full-workload | mongo-shard | full-workload | burst | 235,228 | 234,841 | 387 | 99.84% |
| find-input | documentdb | find-input | steady | 243,001 | 243,001 | 0 | 100.00% |
| find-input | documentdb | find-input | burst | 264,824 | 264,824 | 0 | 100.00% |
| insert-output | documentdb | insert-output | steady | 243,000 | 243,000 | 0 | 100.00% |
| insert-output | documentdb | insert-output | burst | 265,730 | 265,730 | 0 | 100.00% |
| full-workload | documentdb | full-workload | steady | 242,999 | 242,984 | 15 | 99.99% |
| full-workload | documentdb | full-workload | burst | 247,351 | 247,269 | 82 | 99.97% |

- **도착률:** steady 135 Tasks/s, burst는 포아송 스케줄이 조금 더 촘촘히 몰려 실현 처리량 약 147 Tasks/s.
- **커넥션 재사용 검증:** 정상적인 비재사용 런에서 생성 커넥션 ≈ 종료 커넥션 ≈ Task 수 (Task당 ≈ 1.0).
- 두 백엔드 모두 동일한 도착 규칙(같은 시드·상한)을 적용받았고 거의 포화되지 않아(오류율 ≈ 0%)
  실현 처리량이 사실상 동일 → **비교의 핵심은 지연시간**(단, 아래 5.1처럼 closed loop 실행으로
  절대 Task 수에는 소폭 차이가 남).

### 5.1 왜 성공률 100%인데 타깃 간 Task 수가 다른가

표를 보면 mongo-shard와 documentdb의 성공률이 모두 100%인데도 총 Task 수가 다르다(예: burst
find-input 265,701 vs 264,824). 이는 오류가 아니라 **부하 주입 방식의 설계**에서 비롯된 정상 현상이다.

**(1) 이 벤치마크는 "고정 Task 개수"가 아니라 "고정 시간(600초 × 3회)" 기준으로 주입한다.**
두 시나리오 모두 정해진 개수를 채우는 것이 아니라, 벽시계 데드라인에 도달할 때까지 계속 Task를
주입한다. 따라서 **주입(=시도)된 Task 수 자체가 런마다·타깃마다 달라질 수 있다.** 표의 "성공률"은
`성공 ÷ 시도` 이므로, 시도 개수가 다르면 둘 다 100%여도 절대 Task 수는 다르다.

- **steady(고정 135/s):** 실시간 페이싱 루프가 `1000/135 ms` 간격으로 주입하는데, OS 스케줄러
  오차 때문에 1,800초 누적에서 **몇 개 단위**로만 미세하게 어긋난다(243,001 vs 243,003, 약 0.001%).
- **burst(포아송 λ=0.57):** 아래 (2)의 feedback까지 겹쳐 차이가 조금 더 커진다(약 0.3%).

**(2) 이 캠페인은 "closed-loop" 주입 모드로 실행되었다.**
설정값 `Burst.OpenLoop = false`(closed loop)의 의미는 다음과 같다.

- **closed-loop = feedback이 있는 부하 주입.** 클라이언트가 다음 부하를 발사하기 전에, 이미 던진
  작업들이 어느 정도 완료되기를 기다린다. 구체적으로 동시 실행 Task에 상한
  (`MaxConcurrentTasks = 15,000`)을 두고, 상한에 도달하면 in-flight가 빌 때까지 새 주입이
  **블록(back-pressure)** 된다. 따라서 **in-flight를 더 빨리 비워주는(=빠른) 백엔드일수록 다음
  Job을 더 일찍 실행**하게 되어, 같은 600초 창에 더 많은 Task를 주입한다. 즉 백엔드의 처리 속도가
  주입 개수에 feedback된다.
- **open-loop와의 차이.** open-loop는 백엔드 완료 여부와 무관하게 미리 정해진 도착
  스케줄을 그대로 강제 주입한다. 이 경우 타깃 간 주입 수는 동일해지지만, 느린 백엔드에서는
  클라이언트 소켓이 무한정 쌓여 **부하 발생기 자체가 터질 수 있다**(본 캠페인 8.1의 MongoDB 멜트다운과
  동일한 위험).

**(3) 왜 이 시나리오에서 closed-loop가 valid한가.**

- **실제 시스템에 back-pressure가 존재하기 때문이다.** 프로덕션 HPC도 무한한 동시성을 갖지 않는다.
  백엔드가 느려지면 상류(클라이언트/워커)가 결국 밀리고 새 작업 투입이 줄어든다. closed-loop는
  바로 이 현실적 되먹임을 재현하므로, "백엔드가 빠르면 더 많은 일을 처리한다"는 결과는 **정확히
  우리가 측정하고 싶은 성질**이다. 주입 수 차이는 버그가 아니라 **백엔드 성능 신호의 일부**다.
- **부하 발생기 붕괴(측정 불가)를 막아 유효 측정을 보장한다.** 8.1에서 보듯 샤딩 클러스터를 향한
  no-reuse 처닝은 open-loop로 몰면 클라이언트가 스레드/소켓 폭주로 정지해 **아무 지표도 못 낸다.**
  closed-loop 상한은 클라이언트를 유한하게 유지해 두 타깃 모두에서 완주 가능한 유효 런을 만든다.
- **양 타깃에 동일한 규칙을 적용해 공정하다.** 두 타깃 모두 같은 `MaxConcurrentTasks`, 같은 시드,
  같은 closed loop 규칙을 적용받는다. 주입 수 차이는 "규칙이 달라서"가 아니라 "백엔드 속도가 달라서"
  생기며, 이것이 곧 비교하려는 대상이다.
- **그래서 처리량이 아니라 지연시간이 헤드라인이다.** 주입 수·처리량(tasks/s)은 closed loop 되먹임의
  산물이므로 **용량 지표로 직접 해석하면 안 되고**, 백엔드를 실제로 구분하는 값은
  **커넥션·연산 지연 백분위수**다. (오류율이 0에 근접해 어느 쪽도 포화되지 않았음을 함께 확인한다.)

> **요약:** 100% 성공률 + Task 수 차이 = "고정 시간 창 + closed loop feedback"의 정상 결과.
> 시도 개수가 애초에 동일하지 않으며, 그 차이 자체가 백엔드 속도 신호다. 절대 Task 수 혹은 초당 처리량(Throughput)이 아니라
> 개별 요청 하나에 걸린 **지연시간(Latency) 백분위수**로 두 백엔드를 비교해야 한다.

### 5.2 실제 테스트의 부하·리소스 지표 (측정값)

아래 값은 각 런의 **3회 iteration 실측치**에서 뽑은 것이다. 처리량·Task 수는 iteration당 평균이고,
피크(최대) 컬럼은 3회 iteration의 초당 시계열(timeseries) 중 **최댓값**이다.

- **평균 전체 Task:** 600초 iteration 1회에 완료된 평균 전체 Task 수.
- **평균 처리량:** 성공 Task ÷ 시간(tasks/s).
- **최대 동시 커넥션(≈in-flight Task):** 처닝 모델에서 Task 1개 = 커넥션 1개이므로, 동시 진행 Task
  수가 곧 **동시에 열려 있는 커넥션 수의 피크**다.
- **커넥션 확립률(conn/s):** 초당 새로 여는 커넥션(=TCP+TLS+auth 핸드셰이크) 발생 속도. 처닝 부하의
  실제 강도를 나타내는 핵심 지표.
- **클라이언트 리소스 피크:** 부하 발생기(클라이언트) 호스트의 임시포트/`TIME_WAIT`/스레드/핸들/CPU
  최댓값 — 호스트가 포화되지 않고 유효 측정이 성립했는지 확인용.

#### STEADY (135 Tasks/s)

| 워크로드 | 타깃 | 평균 전체 Task | 평균 tasks/s | 최대 동시 커넥션 | 최대 conn/s | 평균 conn/s | 최대 TIME_WAIT | 최대 임시포트 | 최대 스레드 | 최대 핸들 | 최대 CPU% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| find-input | mongo-shard | 81,001 | 135.0 | 80 | 169 | 134.8 | 8,088 | 8,271 | 251 | 2,259 | 35.9 |
| find-input | documentdb | 81,000 | 135.0 | 166 | 215 | 134.8 | 8,302 | 8,322 | 8,342 | 29,619 | 83.0 |
| insert-output | mongo-shard | 81,000 | 135.0 | 207 | 189 | 134.8 | 8,431 | 8,610 | 628 | 5,465 | 40.1 |
| insert-output | documentdb | 81,000 | 135.0 | 41 | 147 | 134.8 | 8,120 | 8,141 | 8,207 | 42,462 | 44.6 |
| full-workload | mongo-shard | 80,998 | 132.8 | 1,502 | 169 | 132.6 | 8,260 | 12,477 | 4,441 | 43,167 | 75.7 |
| full-workload | documentdb | 81,000 | 132.8 | 1,417 | 169 | 132.6 | 8,149 | 10,866 | 10,990 | 58,803 | 95.0 |

#### BURST (포아송 λ=0.57)

| 워크로드 | 타깃 | 평균 전체 Task | 평균 tasks/s | 최대 동시 커넥션 | 최대 conn/s | 평균 conn/s | 최대 TIME_WAIT | 최대 임시포트 | 최대 스레드 | 최대 핸들 | 최대 CPU% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| find-input | mongo-shard | 88,567 | 147.0 | 1,349 | 923 | 191.6 | 13,944 | 14,772 | 3,949 | 31,280 | 66.6 |
| find-input | documentdb | 88,275 | 146.4 | 1,524 | 565 | 188.1 | 13,290 | 13,570 | 14,581 | 57,805 | 50.2 |
| insert-output | mongo-shard | 88,507 | 147.3 | 1,386 | 859 | 189.5 | 14,051 | 14,605 | 3,469 | 31,346 | 72.0 |
| insert-output | documentdb | 88,577 | 147.2 | 1,518 | 576 | 189.7 | 13,848 | 13,858 | 14,111 | 60,781 | 52.3 |
| full-workload | mongo-shard | 78,409 | 128.4 | 3,104 | 600 | 131.7 | 10,372 | 16,595 | 8,444 | 75,007 | 72.0 |
| full-workload | documentdb | 82,450 | 135.3 | 3,522 | 508 | 141.3 | 11,835 | 15,372 | 17,173 | 95,796 | 48.3 |

**관찰 포인트**

- **최대 동시 커넥션**은 steady에서 수십~1,500 수준, burst에서 최대 3,000~3,500(full-workload)까지
  치솟아 순간 스파이크를 재현한다. 설정 상한 `MaxConcurrentTasks=15,000` 에는 어느 런도 도달하지
  않았다 → **양 타깃 모두 클라이언트 상한에 막히지 않고 완주**(유효 측정).
- **커넥션 확립률(conn/s)** 은 burst full-workload에서 mongo-shard 600 / documentdb 508 conn/s,
  단일 연산 burst에서는 mongo-shard가 최대 **859~923 conn/s** 까지 도달. 이는 호스트 TCP 튜닝
  후 목표(≥1,200 conn/s 여유) 범위 안이며, `TIME_WAIT` 최대 ~14,000 / 임시포트 최대 ~16,600으로
  **포트 고갈(WinSock 10048) 없이** 견뎠다.
- **클라이언트 스레드/핸들:** documentdb 부하 발생기는 SRV 토폴로지 모니터링 때문에 스레드가
  더 많고(steady에도 ~8,000~11,000, burst full-workload ~17,000), mongo-shard는 라운드로빈
  direct-connect 덕에 스레드가 훨씬 적다(steady 수백, burst ~3,500~8,400). **8.1 멜트다운(48,657
  스레드)과 달리 모두 유한하게 유지**되었다.
- 이 표의 처리량·Task 수·conn/s는 **5.1의 closed-loop 되먹임 산물**이므로 용량 지표로 직접 해석하지
  말고, 백엔드 비교는 6장의 **지연시간 백분위수**로 판단한다.

---

## 6. 결과 수치 요약

지연시간 단위 ms, 값은 **3회 iteration 백분위수의 평균**. 각 행에서 더 우수한 값을 **굵게** 표시.
`find (cold)` 는 접속 비용(TCP+TLS+auth) 제외 후의 연산 시간. Total cycle 은 고정 sleep(10,000 ms) 제외.

### 6.1 단일 연산 — find-input

**STEADY (135 Tasks/s)**

| 지표 | 백분위 | mongo-shard | documentdb |
|---|---|---|---|
| Connection (TCP+TLS+auth) | p90 | 31.9 | **23.8** |
| Connection (TCP+TLS+auth) | p99 | **43.7** | 75.6 |
| find (cold) | p90 | 23.9 | **19.2** |
| find (cold) | p99 | **26.4** | 31.3 |
| Total cycle | p90 | **56.9** | 60.1 |
| Total cycle | p99 | **71.7** | 121.9 |

**BURST (λ=0.57)**

| 지표 | 백분위 | mongo-shard | documentdb |
|---|---|---|---|
| Connection (TCP+TLS+auth) | p90 | **1,185.2** | 1,256.1 |
| Connection (TCP+TLS+auth) | p99 | **1,901.5** | 2,408.1 |
| find (cold) | p90 | 1,355.5 | **970.6** |
| find (cold) | p99 | 1,886.2 | **1,884.1** |
| Total cycle | p90 | 2,569.4 | **2,255.9** |
| Total cycle | p99 | **3,813.1** | 4,328.5 |

### 6.2 단일 연산 — insert-output

**STEADY (135 Tasks/s)**

| 지표 | 백분위 | mongo-shard | documentdb |
|---|---|---|---|
| Connection (TCP+TLS+auth) | p90 | 32.0 | **22.9** |
| Connection (TCP+TLS+auth) | p99 | **47.5** | 71.6 |
| insert (cold) | p90 | 26.6 | **18.6** |
| insert (cold) | p99 | 37.4 | **26.7** |
| Total cycle | p90 | 59.9 | **58.2** |
| Total cycle | p99 | **92.4** | 111.8 |

**BURST (λ=0.57)**

| 지표 | 백분위 | mongo-shard | documentdb |
|---|---|---|---|
| Connection (TCP+TLS+auth) | p90 | 1,316.4 | **1,146.9** |
| Connection (TCP+TLS+auth) | p99 | **2,064.2** | 2,208.7 |
| insert (cold) | p90 | 1,520.7 | **883.6** |
| insert (cold) | p99 | 2,357.6 | **1,670.4** |
| Total cycle | p90 | 2,864.4 | **2,078.4** |
| Total cycle | p99 | 4,451.3 | **3,926.0** |

### 6.3 전체 4연산 — full-workload (`find`→`remove`→`insert`→`find`)

**STEADY (135 Tasks/s)** — 처리량 132.8 tasks/s (양측 동일), 오류율 mongo-shard 0.005% / documentdb 0.006%

| 지표 | 백분위 | mongo-shard | documentdb |
|---|---|---|---|
| Connection (TCP+TLS+auth) | p90 | 89.7 | **30.9** |
| Connection (TCP+TLS+auth) | p99 | 137.1 | **113.0** |
| find (cold) | p90 | **51.3** | 62.3 |
| find (cold) | p99 | 74.6 | **46.0** |
| remove (warm) | p90 | 6.5 | **5.0** |
| remove (warm) | p99 | 80.2 | **77.1** |
| insert (warm) | p90 | 7.3 | **5.1** |
| insert (warm) | p99 | 83.2 | **79.1** |
| find (warm) | p90 | 4.2 | **2.9** |
| find (warm) | p99 | **16.0** | 53.6 |
| Total cycle | p90 | 185.0 | **138.8** |
| Total cycle | p99 | 265.1 | **245.1** |

**BURST (λ=0.57)** — 처리량 mongo-shard 128.4 / documentdb 135.3 tasks/s, 오류율 mongo-shard 0.16% / documentdb 0.033%

| 지표 | 백분위 | mongo-shard | documentdb |
|---|---|---|---|
| Connection (TCP+TLS+auth) | p90 | **1,059.1** | 1,605.5 |
| Connection (TCP+TLS+auth) | p99 | **2,022.1** | 3,235.9 |
| find (cold) | p90 | 2,396.0 | **1,433.5** |
| find (cold) | p99 | 3,300.8 | **2,589.7** |
| remove (warm) | p90 | 136.6 | **22.3** |
| remove (warm) | p99 | 268.2 | **153.5** |
| insert (warm) | p90 | 149.7 | **26.1** |
| insert (warm) | p99 | 309.2 | **139.3** |
| find (warm) | p90 | 191.9 | **21.9** |
| find (warm) | p99 | 381.7 | **144.8** |
| Total cycle | p90 | 4,998.9 | **3,650.7** |
| Total cycle | p99 | 7,108.8 | **6,551.7** |

> 참고(steady full-workload, mongo-shard 원자료): `ConnectionOpenMs` p50 ≈ 35 ms / p99 ≈ 134 ms,
> warm 연산 p50 은 대부분 1~5 ms 수준. 즉 "느린 쿼리"처럼 보이던 부분은 서버 실행이 아니라
> **매 연산의 cold 커넥션 핸드셰이크 비용**이었다.

---

## 7. 결과 해석

- **샤딩이 풀 워크로드 붕괴를 해결했다.** 직전 단일 노드 `mongo-vm` 캠페인에서는
  전체 4연산 워크로드가 클라이언트 포화로 **steady 22.8% / burst 32.0%** 를 탈락시켰다.
  이번 **2-shard / 2-mongos `mongo-shard`** 클러스터는 **132.8 / 128.4 tasks/s** 로 전체 부하를
  유지하며 오류율 **0.005% / 0.16%** — steady 처리량·오류율에서 DocumentDB와 대등하다.

- **접속 비용을 제거하면 연산 자체는 매우 빠르다.** steady 단일 연산의 순수 서버 실행은
  mongo-shard ≈ 19~22 ms p50, DocumentDB ≈ 12~13 ms p50 수준. 체감 지연의 대부분은
  **cold 커넥션 핸드셰이크**다.

- **중앙값은 DocumentDB, 버스트 꼬리는 mongo-shard.** 모든 steady 행에서 DocumentDB가 더 낮은
  p50/p90(핸드셰이크 약 2배 빠름)을 보인다. 반면 **burst** 에서는 mongo-shard의 2× mongos
  팬아웃이 커넥션 p90/p99를 더 조이며(full-workload 커넥션 p90 1,059 vs 1,606 ms,
  p99 2,022 vs 3,236 ms) 스파이크를 더 잘 흡수한다.

- **warm 연산은 DocumentDB 우세.** 소켓이 열린 뒤의 `remove`/`insert`/`find` 는 DocumentDB가
  2~10배 빠르고(대부분 sub-6 ms p50), burst에서도 warm 꼬리가 더 작다.

- **종합:** steady 상태에서는 **DocumentDB가 종단 간(end-to-end) 더 빠르다**(사이클 p50/p90/p99가
  낮음). mongo-shard는 경쟁력이 있고 일부 **burst 커넥션 꼬리 백분위수**에서 앞선다.
  양측 모두 오류율이 0에 근접 — 단일 노드에서 보였던 붕괴는 사라졌다.

### 마이그레이션 판단 가이드

지배적 운영 모드인 **steady 상태에서 DocumentDB가 종단 간 더 빠르며**(커넥션·사이클 p50/p90 약 2배 낮음),
warm 연산 지연도 전반적으로 더 매끄럽다. 직전 단일 노드 대비 결정적 결과는 **샤딩이 풀 워크로드
붕괴를 제거**했다는 점이다. 따라서 선택은 성능 문제가 아니라 **운영상의 트레이드오프**로 좁혀진다.

- **DocumentDB(PaaS):** 관리형 단순성 + 낮은 중앙값 지연. 단일 게이트웨이 엔드포인트라
  처닝에 대한 클라이언트 측 병리(아래 8.1)가 없음.
- **자체 관리 샤딩 MongoDB(IaaS):** 통제권/비용 유연성. 단, **반드시 다중 mongos로 샤딩**하고
  단명 클라이언트가 **여러 라우터로 처닝을 분산**해야 함(단일 mongos 핀 또는 L4 로드밸런서).
  전체 토폴로지를 향해 Task마다 클라이언트를 붙이면 안 됨.

프로덕션 HPC 워크로드가 **단명 커넥션 처닝** 그 자체이므로, 결정 요인은 서버 연산 속도가 아니라
각 백엔드의 **처닝 상황에서의 커넥션 프론트엔드(핸드셰이크 처리 능력)** 이다.

---

## 8. 추가 우려 및 고려 사항

### 8.1 [IaaS 리스크] 샤딩 클러스터에서의 클라이언트 동시성 폭주

이번 캠페인의 첫 시도는 **부하 발생기 VM이 멜트다운**되어 유효 지표를 못 냈다
([INCIDENT-runaway-concurrency-meltdown.md](INCIDENT-runaway-concurrency-meltdown.md) 참조).

- **원인:** 샤딩 클러스터를 향한 `MongoClient`는 **mongos마다** SDAM 하트비트 모니터 스레드를 띄운다.
  비재사용 처닝에서 Task마다 새 클라이언트가 생기면, 135 Tasks/s 속도에서 모니터 스레드/커넥션이
  완료보다 빠르게 누적되어 **약 48,657 스레드 / 8.6 GB / 32,245개의 stuck-open 커넥션**까지 폭증 후
  호스트가 스래싱으로 정지.
- **중요:** 이는 **클러스터 용량이 아니라 클라이언트-토폴로지 상호작용의 인공물**이다. 동일 처닝을
  관리형 **DocumentDB는 병리 없이 견뎌냈다**(게이트웨이가 단일 서버 접속처럼 동작) — 나이브한
  처닝 워크로드에서 DocumentDB에 유리한 근거.
- **해결:** Task별 클라이언트를 **두 mongos에 라운드로빈으로 direct single-server(`directConnection=true`)** 로
  고정 → 2× 라우터 팬아웃 유지하며 per-client SDAM 모니터 제거. (mongo-vm과 동일한 완화책, 비교 공정성 유지)
- **프로덕션 함의:** 이는 일회성 트릭이 아니라 **이관 요건**이다. 프로덕션도 단명 클라이언트를 쓰므로
  동일한 SDAM 폭주에 노출된다. **각 단명 클라이언트를 단일 mongos로 향하게 하거나, mongos 앞에
  L4 로드밸런서**를 두어 단일 엔드포인트로 처닝을 팬아웃해야 한다. IaaS를 택할 경우 이 아키텍처
  제약을 반드시 설계에 반영해야 한다.

### 8.2 [IaaS 리스크] mongo-vm 일일 콜드 스타트 / 데이 중 갱신 미모델링

본 벤치마크는 **warm 데이터 캐시 + Model A(불변 입력)** 상태만 측정한다. 자체 관리 MongoDB에는
아래 두 상황이 **모델링되지 않았고**, 실제로는 결과보다 나빠질 수 있다.

- **로드 직후 콜드 스타트:** 일일 벌크 로드 직후 첫 Task 파도는 부분적으로 콜드인 `calc_input` 을 침.
- **데이 중 입력 갱신:** Model B(append-only) / Model C(mutable-in-place).

관리형 서비스는 사실상 항상 warm이므로 warm-cache 비교는 하루 대부분에 대해 공정하나,
MongoDB의 일일 콜드 엣지는 포착하지 못한다.

### 8.3 [비교 공정성] 단일 노드 vs 관리형 서비스

`mongo-vm`(단일 노드)은 자체 관리 단일 노드이고, 관리형 서비스는 자체 라우팅/복제/스로틀링
계층을 포함한다. 본 캠페인의 `mongo-shard` 는 이를 보완하기 위해 샤딩으로 재구성한 것이다.

### 8.4 [측정 한계] 프로덕션 규모와의 차이 (패턴이 아닌 스케일)

본 단일 VM 테스트가 생략한 요소들:

- **다수 클라이언트 호스트**(호스트당 스레드/핸들 압력이 분산됨)
- **AZ 간 네트워크 홉**
- **더 많은 shard / mongos 수**, 실제 **config server 부하**

이들은 절대 지연을 위아래로 이동시키지만, **처닝 기반 커넥션 비용이 지배적**이라는 점은 변하지 않으며,
**mongos/shard 를 늘리는 것**이 이를 확장하는 지렛대다.

### 8.5 [해석 원칙] 평균이 아니라 꼬리 백분위수

커넥션 처닝 지연은 핸드셰이크/인증/서버 선택/스로틀링 같은 꼬리 이벤트가 지배한다. 평균이 좋아도
p99.9가 나쁜 백엔드는 실제 버스트에서 멈춘다. **p95/p99/p99.9 를 헤드라인 숫자로** 볼 것.

### 8.6 [범위] 본 벤치마크가 나타내지 않는 것

- **일반 커넥션 풀 애플리케이션 성능** — 커넥션을 재사용하는 풀링 앱에는 이 수치를 외삽하지 말 것.
- **DocumentDB 호환성 한계** — Mongo 호환이나 동일하지 않음. 미지원 명령은 `DocumentDbCompatibility`
  오류 버킷으로 표면화됨. 이번 캠페인에서는 문제되지 않았으나, 실제 이관 전 사용 명령/드라이버 기능의
  호환성 검증 필요.
- **cosmos-ru(RU 모드) 미포함** — 향후 라운드에서 100k RU/s 고정 예산 하 비교 예정.

### 8.7 [중대 한계] 프로덕션 피크 부하가 재현되지 않음 — 검증 미완결

**본 캠페인은 프로덕션 실제 피크 부하에 도달하지 못했다.** 이는 결과 해석 범위를 결정하는 가장
중요한 한계이므로, 프로덕션 `mongod.log`(2026-03-26 피크 순간) 실측치와 이번 테스트 재현치를
직접 대조한다.

| 지표 | 프로덕션 실측 | 이번 burst(closed-loop) | 재현율 |
|---|---|---|---|
| 동시 커넥션 피크 | **11,012** (평균 10,263) | 3,104(mongo) / 3,522(docdb) | **약 32%** |
| 커넥션 개설 속도 | **≈1,210 conn/s** (1초 창 994건 관측) | 500~600(full), 최대 923(single-op) | **약 40~60%** |
| 클라이언트 호스트 수 | **3대 이상**(10.1.0.23/25/26 분산) | **1대**(단일 부하 발생기) | 미재현 |
| 지속 시간 | 일 11시간 피크 곡선 | 3×600초(≈30분) | Phase 1 수준 |

**왜 미달했는가 (구조적 원인).**

- **closed-loop는 가장 세게 눌러야 할 지점에서 스스로 부하를 줄인다.** 5.1에서 설명한 되먹임
  (feedback) 구조상, in-flight가 빠질 때까지 다음 주입이 막힌다. 그 결과 동시 커넥션이 약 3,000~3,500
  에서 자연 수렴해 **프로덕션 피크 11,012의 약 1/3**에 그쳤다. Burst 시나리오의 원래 목적(11K 순간
  스파이크 재현)과 반대로, 느린 백엔드일수록 부하가 *덜* 주입된다.
- **이 목적을 위해 설계된 open-loop 모드를 이번엔 쓰지 않았다.** 코드에는 `Burst.OpenLoop=true`
  (게이트 우회 → 고정 스케줄 강제 주입)가 존재하지만, 이번 캠페인은 `false`(closed-loop)로 실행됐다.
  즉 "프로덕션 피크 엔벨로프 재현" 항목은 도구에 있으나 실행되지 않았다.
- **단일 클라이언트 호스트라 애초에 피크를 낼 수 없었다.** 프로덕션은 처닝을 3대+ 클라이언트에
  분산했다. 단일 호스트로 1,200 conn/s·11K 동시를 몰면 8.1의 멜트다운처럼 **DB가 아니라 부하
  발생기가 먼저 포화**된다. closed-loop 게이팅은 그 붕괴를 피하려는 조치였고, 그 대가로 부하가
  3K에 묶였다.

**아직 실행되지 않은 테스트 계획(원 설계 대비).**

- **Stress 시나리오(×2 헤드룸: ≥2,000 conn/s / ≥20,000 동시)** — 미실행.
- **Phase 2 soak(≥4시간 연속 또는 11시간 일 곡선 replay)** — 미실행. 따라서 **누적·서서히 나타나는
  결함**(파일 디스크립터/핸들 누수, 메모리 드리프트, TIME_WAIT 누적, 관리형 서비스의 RU/유지보수
  주기)은 관측되지 않았다.

**따라서 현재 결과로 말할 수 있는 것 / 없는 것.**

- **말할 수 있음:** 중간 수준 동시성(~3K)에서의 **상대적 지연시간(꼬리 백분위) 비교**, 샤딩이 단일
  노드 붕괴를 해소했다는 점, 클라이언트가 유한하게 유지되는 유효 런이라는 점.
- **말할 수 없음:** 프로덕션 실제 피크(11K 동시 / 1,200 conn/s)에서 각 DB가 커넥션 거절·타임아웃
  없이 버티는지, 장시간 soak에서 드리프트·누수가 없는지. **이번 데이터로는 단정 불가.**

**완결적 검증을 위한 후속 라운드(권장).**

1. **다중 클라이언트 호스트 + open-loop burst.** 백엔드(타깃)당 부하 발생기 **2-3대**로 확장하고
   `Burst.OpenLoop=true`, λ를 크랭크업(호스트당 ≈1.6 jobs/s)하여 aggregate **≥1,200 conn/s →
   ≈12K 동시**를 실제로 주입. 호스트는 각 AZ(백엔드와 동일 AZ)에 배치하고 동일 TCP 튜닝·런타임을
   맞춘다. 다중 호스트 **동기 시작**(NTP + 예약 실행)과 **호스트별 seed**, **결과 병합**이 필요.
2. **Stress ×2 시나리오**로 설계 한계(≥20,000 동시 / ≥2,000 conn/s) 확인.
3. **서버측 계측 동시 캡처**(Mongo `mongod.log` accepted rate·connectionCount, DocumentDB Azure
   Monitor active connections·throttling) → 클라이언트 주입 conn/s가 서버에 실제 도달했는지 대조.

> **참고 — 클라이언트 환경 차이.** 프로덕션 클라이언트는 `mongo-csharp-driver 2.10.4` / .NET
> Framework 4.8 / **x86 32비트**였고, 본 테스트는 Driver 2.30 / .NET 8 / 64비트다. 커넥션 확립
> 비용·클라이언트 측 한계가 다를 수 있으므로, 최종 검증 시 프로덕션 클라이언트 조건 정합도 고려.

---

## 9. 결론

> **먼저 읽을 것 — 검증 범위.** 아래 결론은 **중간 동시성(~3K) · closed-loop · 단일 호스트 · 약
> 30분** 조건에서의 상대 비교다. **프로덕션 실제 피크(11K 동시 / 1,200 conn/s)와 장시간 soak는
> 아직 검증되지 않았다**(8.7). 최종 이관 결정 전 8.7의 후속 라운드가 필요하다.

- 커넥션 처닝이라는 프로덕션 실제 패턴에서, **DocumentDB(PaaS)는 지배적인 steady 운영 모드에서
  종단 간 더 빠르고**, 관리형 단일 엔드포인트 덕에 IaaS 샤딩이 겪은 **클라이언트 동시성 폭주 병리가 없다.**
- **자체 관리 MongoDB(IaaS)도, 반드시 샤딩 + 다중 mongos + 처닝의 라우터 분산 설계를 갖추면**
  전체 부하를 오류율 0 근처로 견디며 일부 버스트 커넥션 꼬리에서 앞선다. 그러나 이는 단일 노드 제안
  대비 **추가 아키텍처 요건과 운영 부담**을 의미한다.
- 따라서 IaaS → PaaS 전환은 성능 관점에서 (현 검증 범위 내에서) **타당**하며, 선택의 축은 **운영
  단순성/중앙값 지연(DocumentDB)** 대 **통제권/비용 유연성 + 추가 설계 부담(샤딩 MongoDB)** 의
  트레이드오프로 정리된다. **단, 프로덕션 피크·soak 검증(8.7)을 완료한 뒤 최종 확정할 것.**
