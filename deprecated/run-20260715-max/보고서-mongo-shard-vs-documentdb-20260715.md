# MongoDB(IaaS) vs. Azure DocumentDB(PaaS) 결과 보고서 - Max connection 관점

**작성 목적:** 현재 IaaS(Azure VM 위의 자체 관리 MongoDB)로 제안된 HPC 백엔드를
Azure **DocumentDB(PaaS, Cosmos DB for MongoDB vCore)** 로 전환할 수 있는지 검증하기 위한 비교 테스트 결과 정리.

- **기준 결과:**  (2026-07-15 실행)
- **기준 코드:** GitHub 커밋

> **중요.** 본 테스트는 프로덕션 트레이스에서 도출한
> **동시 연결(Concurrency)** 을 기준으로 설계·실행되었다.
