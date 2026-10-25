# MongoDB에서 Azure DocumentDB로: HPC 워크로드 이전의 비즈니스 가치

Caldova Securities(가명)는 안정성과 성능을 이유로 self-managed MongoDB에서 **Azure DocumentDB M80, shard 2개, HA 활성화** 구성으로 이전했습니다. 이 글은 이전으로 얻는 운영상의 가치와 이를 뒷받침하는 테스트 결과를 간단히 정리합니다. 기술 세부 내용과 재현 방법은 [영문 기술 문서](hpc-mongodb-to-documentdb-reference.md)에 있습니다. 최종 테스트는 2026-08-03에 수행했습니다.

## As-is에서 To-be로

| | As-is: self-managed MongoDB | To-be: Azure DocumentDB |
|---|---|---|
| 구성 | primary VM 2대, standby VM 2대 | M80, shard 2개, HA 활성화, global endpoint 1개 |
| 운영 범위 | primary, standby, 패치, 인증서, capacity planning을 직접 운영 | Managed service가 해당 계층을 운영 |
| 용량 조정 | VM 증설 및 구성 변경 | Tier 변경 또는 shard 추가 |

## 운영이 단순해지는 부분

- **MongoDB VM과 standby 관리 불필요.** 벤치마크의 MongoDB VM 2대는 각각 `Standard_FX24ms_v2`(24 vCPU, 약 500 GiB RAM)였으며, router와 shard를 함께 호스팅했습니다. 연결 유지 시험에서 VM1은 평균 65.2%, 최대 99.7%, VM2는 평균 44.5%, 최대 99.6% CPU에 도달해 4,714개 connection에서 한계에 부딪혔습니다. 전용 router VM 2대를 추가한 4-router 구성에서 이 병목이 해소됐고, DocumentDB는 같은 부하에서 server CPU가 약 0.8-1.5%에 머물렀습니다.
- **Tier 변경으로 용량 조정.** 벤치마크에서 M60, M80, M200을 비교했고, 필요에 따라 tier를 바꿔 compute를 조정할 수 있습니다.
- **접속 구성이 단순.** MongoDB 테스트에서는 router pinning과 인증서 검증 완화가 필요했지만, DocumentDB는 global endpoint를 그대로 사용했습니다.

## 테스트로 확인한 것

| 항목 | DocumentDB M80 (shard 2개) | MongoDB (router 4개) |
|---|---|---|
| 평상시 처리량 (약 135 tasks/s) | 134.9 tasks/s, error 0.000% | 134.9 tasks/s, error 0.001% |
| Read median latency | 27.5 ms | 45.6 ms |
| 12,000 connection 유지 시 connection 실패율 | 0.2% | 6.1% |
| Overload 시 connection-open p99 | 22.3 s | 159.7 s |

실제 production에서는 최대 약 200 IOPS, 매우 낮은 CPU 사용률을 보였습니다. 일상적인 워크로드에는 여유가 충분합니다.

## 고려할 점

- **Tail latency.** DocumentDB는 평상시 p99(107.8 vs 67.9 ms)와 connection 유지 시 keepalive p99(23.3 vs 14.8 s)가 MongoDB보다 높았습니다. Managed 서비스에서는 새 connection이 gateway와 인증을 거치기 때문으로 보이며, 인증 시간은 따로 측정하지 않았습니다. SLO에 맞는지 확인이 필요합니다.
- **Client 재사용.** 이 테스트는 task마다 새 client를 만드는 최악의 경우입니다. 실제 애플리케이션에서는 connection을 재사용하는 것이 좋습니다.
- **측정하지 않은 것.** 선택한 구성을 초당 약 1,210개 connection burst로 테스트하지 않았습니다. 비용 절감, availability 개선, failover 동작도 측정하지 않았습니다.

## 다음 단계

1. Production burst rate(약 1,210 connections/s)에서 M80 shard 2개 검증
2. Failover 테스트로 HA 동작 확인
3. Pooled client 기준 성능 비교
