# No God! Please No! - Selected Source Code

> 팀 프로젝트 **No God! Please No!** 에서 제가 직접 작성하거나 주도적으로 구현·리팩토링한 주요 C# 소스만 선별한 채용 포트폴리오용 저장소입니다.  
> 원본 팀 프로젝트 전체와 팀원 단독 담당 소스, 에셋, 씬 데이터는 포함하지 않습니다. 공동 수정 파일은 본인 담당 영역을 확인하는 데 필요한 일부 파일만 포함하며, 아래 **Source Guide**에 작업 범위를 구분해 명시했습니다.

## Project Overview

| 항목 | 내용 |
| --- | --- |
| 프로젝트 | No God! Please No! |
| 장르 / 플랫폼 | 3D 멀티플레이 타워 디펜스 / Steam |
| 개발 기간 | 2025.09 ~ 2026.03 |
| 개발 형태 | 기획 1명 / 프로그래밍 4명 공동 개발 |
| 네트워크 | 최대 3인 Listen Server |
| 주요 기술 | Unity, C#, FishNet, Steamworks, Vivox |
| 프로그래밍 기여도 | 약 35% - Git 이력과 실제 담당 기능 기준 |
| 출시 | Steam 정식 출시 |

- Gameplay Video : https://www.youtube.com/watch?v=b4sUalKoP0s
- Steam : https://store.steampowered.com/app/4179710/No_God_Please_No/

## Main Contributions

- **몬스터 AI / 도발 타겟팅** : NavMesh 도달 가능성 판정, `PrimaryTarget` / `ActionTarget` 분리, 도달 불가 상황의 대체 행동, 도발 세션 기반 리타겟팅
- **멀티플레이 세션 예외 처리** : 클라이언트 비정상 이탈 시 투표 모수 재계산, Steam P2P / Vivox / 플레이어 상태 정리
- **서버 중심 공유 상태 관리** : 공용 재화, 마을 피해 상태, 제단 업그레이드 요청 검증 및 적용
- **캐릭터 공통 구조** : ScriptableObject 기반 스탯 확장, 공통 스탯 인터페이스, 서버 중심 궁극기 충전/사용 구조
- **런타임 안정화** : CPU/GPU Frame Time을 기준으로 한 단계형 Auto Frame Cap 및 Listen Server 별도 기준 적용

## Source Guide

### 01. AI Targeting & Taunt

`01_AI_Targeting_Taunt/`

| 파일 | 주요 검토 포인트 | 작업 범위 |
| --- | --- | --- |
| `MonsterDetector.cs` | NavMesh 도달 가능성, Primary/Action Target 분리 | 탐지·타겟 선정 구조 주도 구현 및 리팩토링 |
| `MonsterMovement.cs` | 서버 기준 추적, 도달 불가 대상 fallback | 탐지·추적 구조 주도 구현 및 리팩토링 |
| `TankerUltimateShout.cs` | 도발 소유자/세션 관리, 강제 리타겟 | 탱커 도발 로직 직접 구현 |
| `IForceRetargetHandler.cs` | 공격 중 강제 행동 전환 계약 | 인터페이스 직접 구현 |

추천 메서드: `UpdatePathAndSubTarget_Server()`, `TryGetBestTauntStructure_Server()`, `TryGetTauntBuildingTarget_Server()`, `ApplyTaunt_Server()`

### 02. Server State & Altar

`02_Server_State_Altar/`

| 파일 | 주요 검토 포인트 | 작업 범위 |
| --- | --- | --- |
| `SharedEconomy.cs` | 공용 재화의 서버 중심 변경/동기화 | 공용 재화 관리 직접 구축 후 확장 |
| `TownDamageManager.cs` | 마을 파괴율 계산 및 복구 | 마을 피해·복구 로직 직접 구축 후 확장 |
| `AltarUpgradeServerProxy.cs` | ServerRpc 요청 검증, 비용 차감, 실패 시 반환 | 업그레이드 서버 처리 흐름 직접 구현 |

추천 메서드: `TrySpend_Server()`, `RequestUpgrade()`, `ComputeDamageRatio_Server()`

### 03. Character System

`03_Character_System/`

| 파일 | 주요 검토 포인트 | 작업 범위 |
| --- | --- | --- |
| `JobStatsDefinition.cs` | 레벨별 스탯/비용 데이터 구조 | 기존 SO 기반 구조 확장 및 리팩토링 |
| `IStatProvider.cs` | 런타임 스탯 조회 계약 | 공통 스탯 인터페이스 직접 구현 |
| `UltimateCharge.cs` | 서버 중심 궁극기 충전·사용 검증 | 공통 궁극기 흐름 직접 구현 |
| `IUltimateAbility.cs` | 캐릭터별 궁극기 실행 분리 | 공통 궁극기 인터페이스 직접 구현 |

추천 메서드: `TryUseUltimate_ServerRpc()`, `TryUseUltimate_Server()`, `AddDamage_Server()`, `BeginUltimateUse_Server()`

### 04. Session Lifecycle

`04_Session_Lifecycle/`

| 파일 | 주요 검토 포인트 | 작업 범위 |
| --- | --- | --- |
| `SceneTransitionManager.cs` | 이탈 시 투표 모수 재계산 및 씬 전환 복구 | 초기 구조 구현 후 공동 수정, 비정상 이탈 대응 영역 직접 수정 |
| `PlayerRegistry.cs` | Steam P2P / Vivox / 닉네임 / 직업 / 레디 상태 정리 | 공동 파일 중 이탈 정리 영역 담당 |

추천 메서드: `OnRemoteConnectionState_Server()`, `ReturnToLobbyVoteProgress_ObserversRpc()`, `OnRemoteConnState_Server()`, `OnPlayerLeft_ClearVoice_ObserversRpc()`

### 05. Performance

`05_Performance/`

| 파일 | 주요 검토 포인트 | 작업 범위 |
| --- | --- | --- |
| `AutoFrameCapController.cs` | CPU/GPU Frame Time 측정, 단계형 프레임 상한, Listen Server 튜닝 | 기능 직접 구현 |

추천 메서드: `Configure()`, `LateUpdate()`, `StepDown()`, `GetNextHigherCapWithinMax()`, `ApplyHostTuning()`

## Repository Scope

- 전체 게임 프로젝트가 아닌 **본인 담당 코드 중심의 선별본**입니다.
- UI, 연출, 에셋, 씬, 대규모 캐릭터 전투 클래스 및 팀원 단독 담당 파일은 제외했습니다.
- 공동 수정 파일은 위 표에 **본인 작업 범위와 검토 메서드**를 구분해 명시했습니다.
- 원본 팀 저장소의 커밋 이력과 전체 프로젝트는 팀 프로젝트 자산 보호를 위해 공개하지 않습니다.
- 별도의 오픈소스 라이선스를 부여하지 않으며, 원 프로젝트 관련 권리는 각 기여자에게 있습니다.
