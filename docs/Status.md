# 프로젝트 진행 상태

현재 단계는 **NetworkTest 씬에서 Ninja / Sword / Wizard의 서버 권위 이동·기본 공격·Q/E/R·체력/사망 동기화**다. 기존 싱글플레이 캐릭터 구조는 유지한다. InGame/Map 전체 전환, 적 AI, 성장 시스템, 로비, Relay는 아직이다.

## 최신: 서버 권위 이동 + Q/E/R

- `BasePlayer`에 `NetworkObject`, 서버 권위 `NetworkTransform`/`NetworkAnimator`, `PlayerNetworkOwnership`, `NetworkPlayerMotor`, `NetworkPlayerCombat`, `NetworkPlayerSkills`, `NetworkHealth`, `NetworkSkillPresentation`을 붙였다. Ninja/Sword/Wizard가 상속한다.
- 소유자는 WASD/시점/점프/공격/스킬 **입력만** 보낸다. 서버가 이동·돌진·피해·쿨다운·게이지를 계산한다.
- 모든 클라이언트의 플레이어 Rigidbody는 Kinematic이다. 서버 인스턴스만 물리를 시뮬레이션한다.
- 스킬 이펙트는 서버가 생성하고, 클라이언트는 카탈로그 프리팹/라인/트레일로 표시만 한다. 큰 `NetworkList` 고정 배열 스냅샷은 Mono 크래시와 `FixedList1024Bytes` CS0246을 유발해서 **서버 RPC 배열**로 바꿨다.
- Unity MCP로 이펙트 카탈로그 15개, 사운드 2개를 Inspector에 연결했다.

### 검증 (에디터 MCP, 2026-10-24)

- `NetworkSkillValidation.Run` 통과: Ninja, Sword, Wizard 각각 Q/E/R 서버 피해, 게이지 권위, 체력 복제, 사망 정리.
- `NetworkTestValidation.Run` 통과: 서버 모터 이동·애니메이션 복제, 소유권 분리, 접속 종료.
- `NetworkCombatValidation.RunBatch`는 에디터 MCP에서 배치 전용이라 이 세션에서 재실행하지 않았다. 이전 batchmode에서는 기본 공격 서버 권위 검증을 통과한 기록이 있다.

별도 프로세스 창에서 스킬 VFX가 얼마나 자연스러운지는 이번 자동 검증 범위가 아니다.

## NGO 패키지

| 패키지 | 버전 | 역할 |
| --- | --- | --- |
| Netcode for GameObjects | 2.13.3 | Spawn, 소유권, RPC, NetworkVariable |
| Unity Transport | NGO 의존성 | UDP 통신 |
| Multiplayer Play Mode | 2.0.2 | 빌드 없이 추가 에디터 프로세스 |

에디터는 Unity `6000.3.10f1`. 선언은 `Packages/manifest.json`, 고정 버전은 `Packages/packages-lock.json`.

## 권한 모델

```text
소유 클라이언트 입력
  → NetworkPlayerMotor / NetworkPlayerCombat / NetworkPlayerSkills RPC
  → 서버가 기존 PlayerMovement·IPlayerBasicAttack·PlayerSkillController 실행
  → 서버 스탯·쿨다운·물리·체력 변경
  → NetworkTransform / NetworkAnimator / NetworkHealth / 스킬 표현 RPC
  → 각 클라이언트가 표시만 갱신
```

- 클라이언트는 위치, 피해량, 명중 대상, 게이지 값을 서버에 적용시키지 않는다.
- 조준 피치와 Wizard E 지면 좌표는 입력으로만 보내고, 서버가 범위·지면·기울기를 검사한다.
- 캐릭터 이름 분기 대신 `IPlayerBasicAttack`과 기존 SkillSO `Cast`를 서버에서 호출한다.
- 승인된 공격/스킬만 애니메이션과 이펙트를 복제한다. 클라이언트에서 한 번 실행하고 서버에서 또 실행하지 않는다.

## NetworkTest 구성

`Assets/Scenes/NetworkTest.unity`

| 항목 | 설정 |
| --- | --- |
| Player Prefab | 기본 NinjaPlayer. 승인 시 Sword/Ninja/Wizard 프리팹 |
| 프리팹 목록 | `Assets/Prefabs/Manager/NetworkTestPrefabs.asset` |
| Transport | UnityTransport, UDP `7777` |
| Host listen | `0.0.0.0` |
| Client 주소 | `127.0.0.1` |
| Tick Rate | 30 |
| Scene Management | 활성화 |
| Connection Approval | 활성화, 캐릭터 번호 검사 |
| Run In Background | 활성화 |

`NetworkTestSession`은 Start Host / Start Client / Esc 종료, 캐릭터 선택, 표적 HP, Q/E/R 쿨다운·게이지 HUD를 표시한다. 인증·인원 제한·로비는 없다. 스폰 위치는 ClientId 격자 배치다.

서버 시작 시 `NetworkCombatTarget` 캡슐 3개를 Spawn한다. 적 AI가 있는 몬스터는 아니다.

에디터 재구성: `Tools → Networking → Configure Shared Combat Test`. 테스트 프리팹 목록을 덮어쓰므로 수동 수정 후 주의한다.

## 플레이어 컴포넌트

오프라인 인스턴스는 기존 동작을 유지한다. 네트워크 Spawn 후에만 아래가 적용된다.

| 컴포넌트 | 역할 |
| --- | --- |
| NetworkObject | 식별·Spawn·소유권 |
| NetworkTransform | 서버 위치·회전 |
| NetworkAnimator | 서버 Animator |
| PlayerNetworkOwnership | 소유자 입력/카메라, 서버 물리/스킬 시뮬레이션 |
| NetworkPlayerMotor | 소유자 이동 입력 RPC, 서버 `PlayerMovement` 실행 |
| NetworkPlayerCombat | 기본 공격 의도 RPC, 서버 `TryAttack` |
| NetworkPlayerSkills | Q/E/R 의도 RPC, 쿨다운·게이지·잠금 스냅샷 |
| NetworkHealth | 서버 현재/최대 체력 스냅샷. 사망은 체력 0 |
| NetworkProjectile | 서버 투사체 Spawn/Despawn, 클라이언트 표시 |
| NetworkSkillPresentation | 서버 스킬 이펙트 관찰, 클라이언트 카탈로그 복제 |

소유자: PlayerInput, PlayerInputHandler, PlayerController, PlayerLook, PlayerSkillController 활성. Rigidbody는 Kinematic.

서버 복제본: PlayerSkillController·PlayerMovement 활성, 입력/카메라 비활성, Rigidbody 동적.

다른 클라이언트의 원격 플레이어: 입력/카메라/물리 비활성, Transform/Animator/체력/이펙트만 표시.

## 기본 공격

- Ninja/Wizard: 서버 `ProjectilePlayerAttack` → 네트워크 투사체. 충돌·관통·폭발 피해는 서버.
- Sword: 서버 `MeleePlayerAttack` 범위 판정. 클라이언트는 검 궤적만. 네트워크에서는 Hit Stop으로 `Time.timeScale`을 바꾸지 않는다.
- 클라이언트 `TakeDamage`/`Heal`/스탯 변조는 거부한다.
- 적 처치 보상·퀘스트는 서버에서만, 마지막 `PlayerDamageDealer` 기준. 경험치 자체의 클라이언트 동기화는 아직이다.

## 스킬

- 소유자 `RequestSkill(Q/E/Ultimate)`. Wizard E는 미리보기 후 검증된 지면 좌표.
- 서버가 기존 SkillSO를 실행한다. 같은 프레임 중복 시전을 막는다.
- HUD의 쿨다운·E 충전·채널·게이지는 서버 스냅샷을 읽는다.
- Ninja 화둔/돌진/궁극기, Sword 베기/돌진/궁극기, Wizard Q/E/R을 이 경로로 넣었다.
- 두 Wizard가 같은 적을 동시에 끌어당기면 공유 refcount로 AI 복원을 맞춘다.

## 빌드 없이 두 명 테스트

시나리오: `Assets/Scenes/NinjaMultiplayerScenario.asset` (이름은 Ninja지만 세 캐릭터 선택 가능)

Unity 6.3: `Window → Play Mode → Scenarios`, Play 옆 드롭다운.

1. NetworkTest 씬.
2. `NinjaMultiplayerScenario` 선택 후 Play.
3. 각 창에서 Sword/Ninja/Wizard 선택.
4. 메인: Start Host. Player 2: `127.0.0.1` → Start Client.
5. Game 뷰를 클릭한 뒤 WASD / 마우스 / Space / 좌클릭 / Q,E,R.
6. Esc로 종료.

포커스 없는 창은 입력이 나가지 않는다. LAN은 Host 내부 IP와 UDP 7777. Relay는 없다.

## 아직 아닌 것

- InGame/Map을 NGO 스폰·씬 전환에 연결
- SpawnManager와 네트워크 플레이어 생성 통합
- 이동 예측/보정, 서버 위치 치트 검증
- 적 AI·적 스폰 네트워크화
- 경험치·증강·아이템·퀘스트 동기화
- 로비, 인증, 매치메이킹, Relay, 재접속, Host migration
- 실제 빌드·WAN·기기 간 테스트
- 별도 프로세스에서 스킬 VFX 체감 확인

## 주요 파일

| 파일 | 내용 |
| --- | --- |
| `Packages/manifest.json` | NGO, Multiplayer Play Mode |
| `Assets/Prefabs/Player/BasePlayer.prefab` | 공통 네트워크 컴포넌트 |
| `Assets/Prefabs/Player/NinjaPlayer.prefab` | Ninja Variant |
| `Assets/Prefabs/Player/SwordPlayer.prefab` | Sword Variant |
| `Assets/Prefabs/Player/WizardPlayer.prefab` | Wizard Variant |
| `Assets/Scripts/Networking/NetworkPlayerMotor.cs` | 서버 이동 |
| `Assets/Scripts/Networking/NetworkPlayerCombat.cs` | 서버 기본 공격 |
| `Assets/Scripts/Networking/NetworkPlayerSkills.cs` | 서버 Q/E/R |
| `Assets/Scripts/Networking/NetworkHealth.cs` | 서버 체력 |
| `Assets/Scripts/Networking/NetworkProjectile.cs` | 서버 투사체 |
| `Assets/Scripts/Networking/NetworkSkillPresentation.cs` | 스킬 이펙트 복제 |
| `Assets/Scripts/Player/PlayerNetworkOwnership.cs` | 입력/카메라/물리 분리 |
| `Assets/Scripts/Testing/NetworkTestSession.cs` | 연결 UI, 캐릭터 승인 |
| `Assets/Scripts/Editor/NetworkTestValidation.cs` | 이동 스모크 |
| `Assets/Scripts/Editor/NetworkCombatValidation.cs` | 기본 공격 통합 검증 (batch) |
| `Assets/Scripts/Editor/NetworkSkillValidation.cs` | Q/E/R 통합 검증 |
| `Assets/Scenes/NetworkTest.unity` | 테스트 씬 |
| `Assets/Scenes/NinjaMultiplayerScenario.asset` | MPPM 시나리오 |
| `Assets/Prefabs/Enemy/NetworkCombatTarget.prefab` | 테스트 표적 |
| `docs/Status.md` | 이 문서 |

임시 캡슐 NetworkTestPlayer는 실제 캐릭터로 바꾼 뒤 삭제했다.
