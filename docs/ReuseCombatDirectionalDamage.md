# 재사용할 전투 구현과 정리 경계

## 이번 정리의 결정

개인 복사본 FateTempered에서 프로젝트 씬은 InGame, Map, CharacterTest만 유지한다. 외부 에셋의 데모 씬과 원본 모델·머티리얼·텍스처·애니메이션은 삭제하지 않는다. 멀티플레이 구현은 이번 범위가 아니다.

IntroUI·SelectCharacter·EndingScene, 전용 IntroUIController·ResultScreenUI·IntroCanvas를 제거한다. 선택 화면 코드와 전역 선택 상태도 제거하고 SpawnManager에서 플레이어 프리팹 하나를 직접 연결한다. GameEndHandler는 InGame에 남겨 결과를 먼저 저장하고 기존 GameManager.NotifyPlayerUnavailable로 세션을 종료한다. 엔딩 씬 이동, 자동 재시작, 새 결과 화면은 없다. 사망 후 현재 로드된 씬에 머문다. RunStats·GameResult·ResultData는 유지한다.

아이템 상자 생성·효과 적용 코드, 게임용 ItemBox·EquipApply·ConsumableApply 프리팹, Sword_Data·Potion_Data를 제거한다. 혼합 기여 파일 ItemBase를 수정하지 않으므로 그 타입 의존성인 ItemData·IInteractable도 보존한다. 이는 아이템 동작 제거이지 모든 아이템 소스 삭제가 아니다. 원본 상자 에셋과 ItemBox 애니메이터는 유지한다.

GameEndHandler와 삭제 씬에 의존하던 CharacterSpawnValidation 수정에 이어, 선택 코드 제거와 SpawnManager의 직접 프리팹 바인딩도 명시적으로 허용되었다. IntroUIController는 삭제 씬·프리팹 전용임을 확인한 삭제 예외다. 그 밖의 혼합 기여 소스와 몬스터 파일은 보존한다.

## 피해에서 성장까지

### 직접 시작과 테스트 씬

빌드 목록의 시작 씬은 InGame이다. `Assets/Prefabs/Manager/Managers.prefab`의 SpawnManager 컴포넌트에서 **Player Prefab** 슬롯에 원하는 플레이어 프리팹을 직접 연결한다. 기본 바인딩은 `Assets/Prefabs/Player/NinjaPlayer.prefab`이다. SwordPlayer·WizardPlayer로 교체하면 해당 프리팹으로 시작한다. 이전 선택 ID나 프리팹 목록은 사용하지 않는다. 프리팹에 유효한 PlayerCharacter가 없거나 슬롯이 비어 있으면 오류를 기록하고 생성하지 않으며 다른 캐릭터로 자동 대체하지 않는다.

InGame의 GameManager가 Map을 additive로 로드하고 활성 씬으로 설정한 다음 리소스 메타데이터와 세션을 초기화하고 SpawnManager.SpawnAll을 호출한다. PlayerSpawnPoint 태그 위치 중 하나에 연결한 프리팹을 생성하고 현재 플레이어 스냅샷·생성 이벤트·레벨 구독을 기존처럼 연결한다. 같은 플레이어가 살아 있는 동안 다시 스폰 요청해도 중복 생성하지 않는다. Map은 이미 게임에서 추가 로드하므로 InGame 시작 전에 별도로 중복 로드하지 않는다.

CharacterTest는 격리된 스킬 테스트 진입점이다. F1은 Wizard, F2는 Ninja, F3은 Knight 전환, F4는 궁극기 게이지 채우기, F5는 재생성·초기화다. WASD 이동, 마우스 조준, 좌클릭 기본 공격, Q/E/R 스킬, Tab 능력치 표시를 사용한다. 직접 InGame 실행 경로와 이 격리 테스트 경로를 혼동하지 않는다.

- PlayerDamageDealer.DealDamage가 살아 있는 EnemyHealth에 양수 피해만 전달한다. IDirectionalDamageable이 있으면 공격 출발 위치도 전달하고, 없으면 EnemyHealth.TakeDamage를 호출한다.
- 실제 피해는 호출 전후 CurrentHealth 차이의 양수 부분이다. 요청 피해가 아니라 이 값을 OnDamageDealt·OnTargetDamaged로 전달한다. 약점 배율·과잉 피해와 흡혈·궁극기 게이지 연결에서 중요한 계약이다.
- EnemyHealth는 체력을 0 이상으로 제한하고 OnHealthChanged, EnemyHitFeedback, 오디오와 애니메이션을 처리한다. deathHandled·expAwarded가 중복 사망 및 경험치 지급을 막는다.
- 현재 순서는 체력 0 → Die → RunStats.AddKill → AwardExpOnce → OnEnemyKilled다. 경험치는 사망 이벤트 구독자가 지급하는 것이 아니라 AwardExpOnce가 먼저 지급한다. 퀘스트는 처치 이벤트를 이용한다.
- AwardExpOnce는 Player 태그 오브젝트를 찾아 PlayerLevelSystem.AddExp를 호출한다. AddExp는 while로 여러 레벨 상승을 처리하고 각 레벨마다 UIManager.RequestAugmentChoice를 요청한다. 증강 선택과 적용, 세션 종료 시 해제는 기존 UIManager·AugmentManager·PlayerAugments 흐름을 보존한다.

## 타격감

EnemyHitFeedback은 MaterialPropertyBlock의 _BaseColor·_Color로 짧은 피격 플래시를 만든다. 재피격 시 기존 코루틴을 교체하고 비활성화 시 정리한다. 공유 머티리얼 자체를 수정하지 않는다. 현재 정리는 PropertyBlock 전체를 비우므로 다른 시각 효과와 함께 재사용할 때 소유권을 확인해야 한다.

SwordCombatFeedback.PlayMeleeImpact는 실제 접촉 위치에 VFX를 배치하고 Cinemachine 임펄스와 짧은 히트스톱을 적용한다. unscaledTime 기반 간격 제한으로 다중 적중의 연출 과잉을 줄인다. EnemyHealth가 피격음을 처리하므로 여기서 중복 재생하지 않는다. 히트스톱은 전역 Time.timeScale을 사용하므로 멀티플레이에서는 그대로 공유 시뮬레이션 정지로 옮기지 말고 로컬 연출과 서버 판정을 분리해야 한다.

## 방향 피해

### 애니메이션과 판정 시점

적 공격 클립의 실제 접촉 프레임에 OnAttackHit 이벤트를 배치하고, Animator가 자식에 있으면 EnemyAnimationEventRelay가 EnemyAttack으로 전달하는 연결을 확인한다. EnemyAttack은 애니메이션 이벤트와 fallbackHitDelay 경로가 같은 대기 피해 처리로 모이므로 이벤트와 지연 처리가 겹칠 때에도 중복 피해가 없어야 한다. 타격 시점·대상 이탈·사망·비활성화와 쿨다운을 함께 확인하고 이벤트 이름만 바꾸지 않는다.

전사 기본 공격은 같은 이벤트 구조가 아니라 MeleePlayerAttack.AttackSequence의 hitDelay 타이머를 사용한다. 기본값은 hitDelay 0.20초, 트레일 시작 0.08초·종료 0.27초이며 공격 속도로 조정한다. Animator가 트리거를 받을 최소 한 프레임을 기다리고 실제 판정 시 접촉 위치 연출을 요청한다. 클립 교체 시 시각적 접촉 프레임과 hitDelay를 맞추고 별도 애니메이션 피해 이벤트를 추가해 두 번 때리게 하지 않는다.

IDirectionalDamageable.TakeDamage(int, Vector3)는 공격 출발 위치 계약이다. BossWeakPoint는 보스에서 공격자까지의 수평 방향과 보스 뒤쪽 방향의 내적을 반각 코사인과 비교한다. 경계값도 후방 판정에 포함된다. 거의 같은 위치이거나 전방 벡터가 무효하면 후방 보너스를 주지 않는다. BossEnemyStats가 있으면 해당 약점 설정을 사용하고, 아니면 직렬화 값을 사용한다. 배율 적용 후 반올림한 피해를 EnemyHealth에 전달한다.

## 세션과 멀티플레이 재사용 시 경계

GameManager는 세션 ID로 오래된 비동기 요청을 무효화한다. 종료 시 UI·증강·퀘스트·리소스를 정리하고 시간 배율과 커서를 복구한다. 플레이어 바인딩의 이벤트와 현재 스냅샷을 함께 쓰는 패턴, 중복 관리자 방지, 재바인딩 해제는 재사용할 가치가 있다.

현재 Player 태그 검색, 싱글턴, 단일 생성 플레이어 스냅샷, 전역 시간 배율은 단일 플레이어 전제다. 향후 멀티에서는 피해·사망·경험치·증강 권한과 플레이어 식별을 명확히 설계해야 한다. 직접 프리팹 바인딩은 로컬 전투 테스트용이며 네트워크 동기화가 구현됐다는 의미가 아니다.

## ADR: 기능 제거와 기여 코드 보존

- 결정: 연결부터 정리하고 승인된 자산만 .meta와 함께 삭제한다. 나머지 GUID는 유지한다.
- 동인: 혼합 기여 보호, 전투 성장 흐름 유지, 비 Git 복사본에서의 복구 가능성.
- 대안: 아이템·결과 하위 시스템 전체 삭제는 보호 파일 수정과 넓은 재설계를 요구한다. 모든 연결을 보존하면 요청한 씬·아이템 정리를 달성하지 못한다.
- 선택 이유: 좁은 예외 수정과 ItemBase/ItemData 잔존이 현재 요구를 가장 적게 침범한다.
- 결과: 비활성 아이템 기반 타입과 결과 데이터가 의도적으로 남는다. 몬스터 삭제는 별도 목록 검토 후 결정한다.
- 후속: 멀티플레이 설계와 몬스터 단순화는 별도 작업이다. 자동으로 삭제 범위를 확대하지 않는다.

## 검증과 복구

변경 전 Assets·ProjectSettings·Packages·docs를 외부 FateTempered-cleanup-backup-20261021에 복사하고 3,161개 파일의 SHA-256 일치를 확인했다. 이는 디스크 백업이며 실행 중 Unity의 미저장 상태까지 저장한 것은 아니다.

정리 검증은 삭제 GUID의 잔존 참조, 보존 파일 해시·GUID, 씬의 컴포넌트 연결, Unity 컴파일, 기존 CharacterSpawnValidation을 구분하여 확인한다. 특히 직접 InGame 시작·Map 추가 로드, 중복 피해/사망, 경험치·레벨업·증강, 사망 결과 선저장·세션 종료, 2초 이상 경과 후에도 씬 이동 없음이 핵심이다. 컴파일·플레이 검증 결과는 실제 실행 로그를 근거로 보고하며, 이 문서 자체를 통과 증거로 사용하지 않는다.

### 이번 실행에서 확인한 결과와 한계

- 원본 Editor를 닫거나 플레이시키지 않고 별도 임시 복사본에서 Unity 6000.3.10f1 batchmode/nographics로 컴파일했다. C# 오류는 없었다. 변경하지 않은 PlayerController의 CS0414와 WizardAugmentValidation의 CS0618 경고는 남아 있다.
- CharacterSpawnValidation.RunBatch가 종료 코드 0과 CHARACTER_SPAWN_VALIDATION_PASS를 출력했다. Ninja·Sword·Wizard 생성, 세션 취소·제거·Map 언로드, 결과 저장과 현재 씬 유지 사망 회귀가 통과했다.
- LevelOneEnemyValidation.RunBatch의 에셋 검사는 정리본·백업본 모두 191개 통과했다. 런타임은 두 경우 모두 Bat: NavMesh chase에서 실패했다. 기존 실패의 재현이며 전체 몬스터 런타임 통과로 보고하지 않는다. 이를 수정하기 위해 보호된 몬스터 코드를 바꾸지 않았다.
- 최초 씬·아이템 정리 당시 정적 비교에서 삭제 자산 GUID의 잔존 참조와 새로 생긴 씬·프리팹 미해결 GUID는 0개였다. 당시 승인한 기존 파일 5개와 삭제 파일 30개 외 백업 대상 원본 파일은 바이트 단위로 동일했다. 이후 선택 코드 제거·직접 프리팹 바인딩 변경은 별도 검증 대상이다. Status.md는 0바이트로 유지한다.
- 증거 위치: 임시 디렉터리 `/var/folders/vw/m68wfn110vz7tpfm2fvv4n980000gn/T/FateTempered-validation-aa2kzc33`의 `cleanup-static-report.json`, `cleanup-validation.log`, `enemy-validation.log`, `enemy-baseline.log`. 임시 파일은 OS에 의해 정리될 수 있다.
- 이후 직접 프리팹 바인딩 변경도 같은 격리 복사본에서 컴파일하고 CharacterSpawnValidation.RunBatch를 재실행했다. `direct-prefab-validation.log`에서 종료 코드 0, Ninja·Sword·Wizard 직접 바인딩 생성과 기존 세션·사망 회귀, `DIRECT_PREFAB_INVALID_CONFIGURATION_PASS`, `CHARACTER_SPAWN_VALIDATION_PASS`를 확인했다. 프리팹 미지정·PlayerCharacter 없음·잘못된 ID·스폰 지점 없음의 네 실패 경로는 각각 예상 오류 한 건과 생성·이벤트 없음까지 검증하며 로그를 숨기지 않는다. CS0414·CS0618 경고는 그대로다. UnityEditor.Search.SearchDatabase의 인덱싱 예외도 로그에 남아 있으며 게임 스폰 회귀 통과와 별개로 전체 Editor 무오류를 주장하지 않는다.
- standalone 빌드, 실제 창에서의 수동 플레이·시각 품질 확인, 전체 전투·스킬 테스트 묶음은 실행하지 않았다. headless 회귀 통과는 이 검증들을 대체하지 않는다. Editor의 미저장 씬 상태는 디스크 검증 대상이 아니다.
- 개인 복사본이 Git 저장소가 아니므로 Ultragoal의 권위 있는 변경 집합 source-hash 생성은 실패했다. 제품 검증 결과와 별개인 완료 증빙 제약이며, 임의 Git 초기화나 가짜 해시로 우회하지 않는다.
