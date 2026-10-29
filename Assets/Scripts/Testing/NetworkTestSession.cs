using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;

// 독립 네트워크 테스트용 접속·캐릭터 승인·전투 상태 화면
[RequireComponent(typeof(NetworkManager), typeof(UnityTransport))]
public sealed class NetworkTestSession : MonoBehaviour
{
    // 승인 가능한 캐릭터 프리팹과 서버 생성용 전투 표적
    [SerializeField] private GameObject[] playerPrefabs;
    [SerializeField] private GameObject combatTargetPrefab;
    // 로컬 테스트 화면의 캐릭터 선택과 이름 목록
    private int selectedCharacter = (int)PlayerCharacterId.Ninja;
    private static readonly string[] CharacterNames = { "Sword", "Ninja", "Wizard" };
    // 필수 네트워크 컴포넌트와 로컬 접속 입력·상태 문구
    private NetworkManager manager;
    private UnityTransport transport;
    private string address = "127.0.0.1";
    private string status = "Start Host in one instance, Client in another. UDP port 7777.";

    // 필수 컴포넌트 조회와 접속·승인·서버 시작 구독
    private void Awake()
    {
        manager = GetComponent<NetworkManager>();
        transport = GetComponent<UnityTransport>();
        manager.OnClientConnectedCallback += OnConnected;
        manager.OnClientDisconnectCallback += OnDisconnected;
        manager.OnTransportFailure += OnTransportFailure;
        manager.ConnectionApprovalCallback += ApproveConnection;
        manager.OnServerStarted += SpawnCombatTargets;
    }

    // 네트워크 이벤트 구독 해제
    private void OnDestroy()
    {
        if (manager == null) return;
        manager.OnClientConnectedCallback -= OnConnected;
        manager.OnClientDisconnectCallback -= OnDisconnected;
        manager.OnTransportFailure -= OnTransportFailure;
        manager.ConnectionApprovalCallback -= ApproveConnection;
        manager.OnServerStarted -= SpawnCombatTargets;
    }

    // 테스트 전용 1바이트 캐릭터 페이로드 검증과 서버의 플레이어 생성 승인
    private void ApproveConnection(NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        // 빈 페이로드의 기본 닌자 선택과 복수 바이트 요청 거부
        int character = request.Payload.Length == 0 ? (int)PlayerCharacterId.Ninja : request.Payload[0];
        if (request.Payload.Length > 1 || playerPrefabs == null || character >= playerPrefabs.Length ||
            playerPrefabs[character] == null ||
            !playerPrefabs[character].TryGetComponent(out NetworkObject prefabObject))
        {
            response.Approved = false;
            response.Reason = "Unknown character selection.";
            response.Pending = false;
            return;
        }
        response.Approved = true;
        response.CreatePlayerObject = true;
        response.PlayerPrefabHash = prefabObject.PrefabIdHash;
        response.Position = new Vector3((request.ClientNetworkId % 4) * 3f - 4.5f, 1f,
            (request.ClientNetworkId / 4 % 4) * 3f - 4.5f);
        response.Rotation = Quaternion.identity;
        response.Pending = false;
    }

    // 서버 시작 시 테스트 전투 표적의 네트워크 생성
    private void SpawnCombatTargets()
    {
        if (combatTargetPrefab == null) return;
        for (int i = 0; i < 3; i++)
        {
            var target = Instantiate(combatTargetPrefab, new Vector3(i * 3f - 4.5f, 1f, 2f),
                Quaternion.identity);
            target.GetComponent<NetworkObject>().Spawn();
        }
    }

    // 접속 중 취소 키의 테스트 세션 종료
    private void Update()
    {
        if (manager.IsListening && !manager.ShutdownInProgress &&
            Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            manager.Shutdown();
            status = "Session stopped.";
        }
    }

    // 접속 성공과 테스트 조작 안내 표시
    private void OnConnected(ulong clientId)
    {
        status = $"Client {clientId} connected. WASD: move, mouse: look, Space: jump, Esc: disconnect.";
    }

    // 연결 해제와 네트워크 종료 사유 표시
    private void OnDisconnected(ulong clientId)
    {
        status = $"Client {clientId} disconnected. {manager.DisconnectReason}";
    }

    // 전송 계층 실패 안내 표시
    private void OnTransportFailure()
    {
        status = "Transport failed. Check the address and whether port 7777 is already in use.";
    }

    // 테스트 전용 접속 조작과 로컬 스킬·서버 판정 체력 표시
    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(12, 12, 460, 370), GUI.skin.box);
        GUILayout.Label("NGO Network Test — UDP 7777");
        if (!manager.IsListening && !manager.ShutdownInProgress)
        {
            selectedCharacter = GUILayout.Toolbar(selectedCharacter, CharacterNames);
            GUILayout.Label("Host address (Client connection)");
            address = GUILayout.TextField(address);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Start Host"))
            {
                manager.NetworkConfig.ConnectionData = new[] { (byte)selectedCharacter };
                transport.SetConnectionData("127.0.0.1", 7777, "0.0.0.0");
                status = manager.StartHost() ? "Host started." : "Host failed to start.";
            }
            if (GUILayout.Button("Start Client"))
            {
                if (string.IsNullOrWhiteSpace(address))
                    status = "Enter the host IP address.";
                else
                {
                    manager.NetworkConfig.ConnectionData = new[] { (byte)selectedCharacter };
                    transport.SetConnectionData(address.Trim(), 7777);
                    status = manager.StartClient() ? "Connecting..." : "Client failed to start.";
                }
            }
            GUILayout.EndHorizontal();
        }
        else
        {
            GUILayout.Label(manager.IsHost ? $"Host — {manager.ConnectedClientsIds.Count} connected"
                : manager.IsConnectedClient ? $"Client — ID {manager.LocalClientId}" : "Connecting / shutting down...");
            GUI.enabled = !manager.ShutdownInProgress;
            if (GUILayout.Button("Disconnect"))
            {
                manager.Shutdown();
                status = "Session stopped.";
            }
            GUI.enabled = true;
        }
        GUILayout.Label(status);
        if (manager.IsConnectedClient)
        {
            GUILayout.Label("Basic attack: left click. Damage and target HP are decided by the server.");
            var player = manager.LocalClient.PlayerObject;
            if (player != null)
            {
                var skills = player.GetComponent<PlayerSkillController>();
                var gauge = player.GetComponent<PlayerUltimateGauge>();
                GUILayout.Label($"Q: {skills.QRemaining:F1}s | E: {skills.ECharges}/{skills.EMaxCharges} ({skills.ERechargeRemaining:F1}s)");
                GUILayout.Label($"R: {gauge.Normalized:P0} | Recast: {skills.UltimateRecastReady} | Channel: {skills.QChanneling || skills.EChanneling}");
                GUILayout.Label("Q/E/R skills: server validated. Wizard E: preview, then confirm.");
            }
            foreach (var target in FindObjectsByType<EnemyHealth>(FindObjectsSortMode.None))
                GUILayout.Label($"Target HP: {target.CurrentHealth} / {target.MaxHealth}");
        }
        GUILayout.EndArea();
    }
}
