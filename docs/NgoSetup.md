# NGO 초기 설정

Netcode for GameObjects 기반 2인 LAN 협동의 에디터·런타임 초기값.

Unity `6000.3.10f1`, URP. 상세 동작은 코드 주석을 본다.

## 패키지

`Packages/manifest.json`

| 패키지 | 버전 | 역할 |
| --- | --- | --- |
| `com.unity.netcode.gameobjects` | 2.13.3 | Spawn, 소유권, RPC, NetworkVariable |
| `com.unity.transport` | NGO 의존성 | UDP |
| `com.unity.multiplayer.playmode` | 2.0.2 | 에디터 다중 플레이어 |

고정 버전은 `Packages/packages-lock.json`.

## NetworkManager

씬 `NetworkEntry`의 `NetworkSession` 오브젝트.

구성 도구: `Tools/Networking/Create LAN Lobby Scenes` (`NetworkLobbySetup`).

| 항목 | 값 |
| --- | --- |
| Transport | `UnityTransport` |
| Scene Management | 켜짐 |
| Connection Approval | 켜짐 |
| TickRate | 30 |
| LoadSceneTimeOut | 60초 |
| PlayerPrefab | 없음. 세션이 캐릭터별 스폰 |
| Prefab List | `Assets/Prefabs/Manager/NetworkTestPrefabs.asset` |
| 필수 컴포넌트 | `NetworkManager`, `UnityTransport`, `LanRoomDiscovery`, `NetworkGameSession` |

플레이어 프리팹 인덱스 = `PlayerCharacterId`: Sword, Ninja, Wizard.

## 포트·정원

| 용도 | 값 |
| --- | --- |
| 게임 UDP | 7777 |
| LAN 탐색 UDP | 47777 |
| 탐색 매직 | `LAND` (`0x4C414E44`) |
| 프로토콜 버전 | 1 |
| 방 정원 | 2 |
| 탐색 패킷 상한 | 512바이트 |

탐색: `LanDiscoveryProtocol`, `LanRoomDiscovery`.  
접속 봉투: `RoomConnectionPayload` (버전 + 방 GUID + 표시 이름). 챔피언·프리팹은 클라이언트가 지정하지 않는다.

제한 시간 (`NetworkGameSession`): 로비 동기화 30초, 씬 로드 60초, 정리 30초.

## 씬

빌드 설정 `ProjectSettings/EditorBuildSettings.asset`.

| 씬 | 역할 |
| --- | --- |
| `NetworkEntry` | 프로필, 방 목록, 생성·참가, DontDestroy 세션 |
| `TeamLobby` | 복제 로비 상태, 챔피언 선택·준비 |
| `InGame` | 로컬 전투 세션·HUD |
| `Map` | 서버 스폰·적 |
| `NetworkTest` | 독립 전투 테스트. LAN 로비와 분리 |
| `CharacterTest` | 오프라인 캐릭터 테스트 |

공유 로드 순서: `TeamLobby` → `InGame` → `Map`.  
언로드 역순: `Map` → `InGame` → `TeamLobby`. `NetworkEntry`는 유지.

## 프리팹 등록

목록: `Assets/Prefabs/Manager/NetworkTestPrefabs.asset`.

도구:

- `Tools/Networking/Configure Shared Combat Test`
- `Tools/Networking/Configure Network Enemies`

등록 대상:

- 플레이어: `SwordPlayer`, `NinjaPlayer`, `WizardPlayer`
- `BasePlayer`: `NetworkPlayerCombat`, `NetworkHealth`, `NetworkPlayerProgress`
- 피해 투사체: `NetworkObject` + 서버 `NetworkTransform` + `NetworkProjectile`
- 테스트 표적: `NetworkCombatTarget`
- 적: Skeleton, Giant, TreeBoss, Bat, Ghost, Rabbit, Slime
- 박쥐 음파: `BatSonicWave`

적 공통: 서버 `NetworkTransform`/`NetworkAnimator`, `NetworkHealth`, `EnemyNetworkRole`, `EnemyAggro`.

## 권한

`NetworkAuthority` 한 곳.

- `CanSimulate`: 오프라인 또는 서버만 상태 계산
- `IsCombatReady`: 오프라인이거나 네트워크 전투 진행 중

소유자는 입력만 제출. 서버가 이동·피해·스킬·체력·적 AI를 확정한다.

네트워크에서 막는 기능: 증강 선택, 퀘스트, 아이템 효과. 가드: `NetworkGameSession.IsNetworkSessionActive`.

## 로컬 확인

1. Play Mode 중지, 씬 저장.
2. `Tools/Networking/Configure Shared Combat Test`
3. `Tools/Networking/Configure Network Enemies`
4. `Tools/Networking/Create LAN Lobby Scenes` (씬이 없을 때만 생성)
5. `NetworkEntry`에서 호스트 방 생성, 다른 인스턴스에서 참가.
6. 같은 LAN, UDP 7777·47777 허용.

독립 전투만 볼 때는 `NetworkTest`. 호스트/클라이언트 GUI, 포트 7777.
