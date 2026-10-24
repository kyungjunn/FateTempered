using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(NetworkManager), typeof(UnityTransport))]
public sealed class NetworkTestSession : MonoBehaviour
{
    [SerializeField] private GameObject[] playerPrefabs;
    [SerializeField] private GameObject combatTargetPrefab;
    private int selectedCharacter = (int)PlayerCharacterId.Ninja;
    private static readonly string[] CharacterNames = { "Sword", "Ninja", "Wizard" };
    private NetworkManager manager;
    private UnityTransport transport;
    private string address = "127.0.0.1";
    private string status = "Start Host in one instance, Client in another. UDP port 7777.";

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

    private void OnDestroy()
    {
        if (manager == null) return;
        manager.OnClientConnectedCallback -= OnConnected;
        manager.OnClientDisconnectCallback -= OnDisconnected;
        manager.OnTransportFailure -= OnTransportFailure;
        manager.ConnectionApprovalCallback -= ApproveConnection;
        manager.OnServerStarted -= SpawnCombatTargets;
    }

    private void ApproveConnection(NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
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

    private void Update()
    {
        if (manager.IsListening && !manager.ShutdownInProgress &&
            Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            manager.Shutdown();
            status = "Session stopped.";
        }
    }

    private void OnConnected(ulong clientId)
    {
        status = $"Client {clientId} connected. WASD: move, mouse: look, Space: jump, Esc: disconnect.";
    }

    private void OnDisconnected(ulong clientId)
    {
        status = $"Client {clientId} disconnected. {manager.DisconnectReason}";
    }

    private void OnTransportFailure()
    {
        status = "Transport failed. Check the address and whether port 7777 is already in use.";
    }

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
