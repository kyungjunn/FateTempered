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
        SetLocalControl(IsOwner);
    }

    public override void OnGainedOwnership()
    {
        if (IsSpawned) SetLocalControl(true);
    }

    public override void OnLostOwnership()
    {
        if (IsSpawned) SetLocalControl(false);
    }

    public override void OnNetworkDespawn()
    {
        SetLocalControl(false);
    }

    private void SetLocalControl(bool local)
    {
        for (int i = 0; i < localBehaviours.Length; i++)
            if (localBehaviours[i] != null)
            {
                bool simulation = localBehaviours[i] is PlayerMovement ||
                    localBehaviours[i] is PlayerSkillController;
                localBehaviours[i].enabled = (local || (IsSpawned && IsServer && simulation)) && initialEnabled[i];
            }
        for (int i = 0; i < playerCameras.Length; i++)
            playerCameras[i].enabled = local && initialCameraEnabled[i];
        // The server simulates every root; all clients follow the server NetworkTransform.
        if (body != null) body.isKinematic = !IsSpawned || !IsServer;
        if (local)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else if (hadLocalControl)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        hadLocalControl = local;
    }
}
