using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Shared by BasePlayer variants. Offline instances keep their existing behavior.
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(NetworkObject))]
public sealed class PlayerNetworkOwnership : NetworkBehaviour
{
    private Behaviour[] localBehaviours;
    private bool[] initialEnabled;
    private CinemachineCamera[] playerCameras;
    private bool[] initialCameraEnabled;
    private Rigidbody body;
    private bool hadLocalControl;
    private bool localSimulationEnabled;
    private bool controlStateInitialized;
    private bool registeredLocalPlayer;
    private bool localRegistrationAttempted;
    private bool networkDespawned;

    private void Awake()
    {
        localBehaviours = new Behaviour[]
        {
            GetComponent<PlayerInput>(), GetComponent<PlayerInputHandler>(),
            GetComponent<PlayerController>(), GetComponent<PlayerMovement>(),
            GetComponent<PlayerLook>(), GetComponent<PlayerSkillController>(),
            GetComponent<PlayerSkillInput>(), GetComponent<WizardSkillTargeting>()
        };
        initialEnabled = new bool[localBehaviours.Length];
        for (int i = 0; i < localBehaviours.Length; i++)
            initialEnabled[i] = localBehaviours[i] != null && localBehaviours[i].enabled;
        playerCameras = GetComponentsInChildren<CinemachineCamera>(true);
        initialCameraEnabled = new bool[playerCameras.Length];
        for (int i = 0; i < playerCameras.Length; i++)
            initialCameraEnabled[i] = playerCameras[i].enabled;
        body = GetComponent<Rigidbody>();

        // Prevent remote PlayerInput instances pairing devices before OnNetworkSpawn.
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            SetLocalControl(false);
    }

    public override void OnNetworkSpawn()
    {
        networkDespawned = false;
        controlStateInitialized = false;
        SetLocalControl(IsOwner);
    }

    public override void OnGainedOwnership()
    {
        if (IsSpawned)
        {
            localRegistrationAttempted = false;
            SetLocalControl(true);
        }
    }

    public override void OnLostOwnership()
    {
        if (IsSpawned)
        {
            localRegistrationAttempted = false;
            SetLocalControl(false);
        }
    }

    public override void OnNetworkDespawn()
    {
        networkDespawned = true;
        if (TryGetComponent(out PlayerSkillController skills))
            skills.EndSession();
        UnregisterLocalPlayer();
        localRegistrationAttempted = false;
        if (body != null && !body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        SetLocalControl(false);
    }

    private void Update()
    {
        if (IsSpawned && NetworkGameSession.IsNetworkSessionActive)
            SetLocalControl(IsOwner);
    }

    private void SetLocalControl(bool local)
    {
        bool networkSession = NetworkGameSession.IsNetworkSessionActive;
        GameManager gameManager = GameManager.Instance;
        bool combatReady = !networkSession || (NetworkGameSession.IsCombatRunning &&
            gameManager != null && gameManager.IsActiveSession && gameManager.IsNetworkCombatActive);
        bool localControl = local && combatReady && !networkDespawned;
        bool serverSimulation = IsSpawned && IsServer && combatReady && !networkDespawned;

        if (networkSession && local && combatReady && !registeredLocalPlayer && !localRegistrationAttempted)
        {
            localRegistrationAttempted = true;
            if (gameManager == null || !gameManager.RegisterLocalPlayer(gameObject))
                localControl = false;
            else
                registeredLocalPlayer = true;
        }
        if (!local || !combatReady)
            localRegistrationAttempted = false;
        if (!localControl)
            UnregisterLocalPlayer();

        if (controlStateInitialized && hadLocalControl == localControl &&
            localSimulationEnabled == serverSimulation)
            return;

        for (int i = 0; i < localBehaviours.Length; i++)
            if (localBehaviours[i] != null)
            {
                bool simulation = localBehaviours[i] is PlayerMovement ||
                    localBehaviours[i] is PlayerSkillController;
                localBehaviours[i].enabled = (localControl || (serverSimulation && simulation)) && initialEnabled[i];
            }
        for (int i = 0; i < playerCameras.Length; i++)
            playerCameras[i].enabled = localControl && initialCameraEnabled[i];
        // Server movement begins only after production combat is running; clients follow NetworkTransform.
        if (body != null) body.isKinematic = !serverSimulation;
        if (localControl)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else if (hadLocalControl)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        hadLocalControl = localControl;
        localSimulationEnabled = serverSimulation;
        controlStateInitialized = true;
    }

    private void UnregisterLocalPlayer()
    {
        if (!registeredLocalPlayer) return;
        registeredLocalPlayer = false;
        GameManager.Instance?.UnregisterLocalPlayer(gameObject);
    }
}
