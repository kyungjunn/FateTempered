using Unity.Netcode;
using UnityEngine;

// Attach once to shared BasePlayer with NetworkObject, PlayerHealth, and one IPlayerBasicAttack.
// This synchronizes basic attacks only; character-specific skills remain on their existing paths.
[RequireComponent(typeof(NetworkObject))]
public sealed class NetworkPlayerCombat : NetworkBehaviour
{
    private IPlayerBasicAttack basicAttack;
    private PlayerHealth playerHealth;
    private PlayerStateManager stateManager;
    private bool executingAuthoritativeAttack;

    private void Awake()
    {
        basicAttack = GetComponent<IPlayerBasicAttack>();
        playerHealth = GetComponent<PlayerHealth>();
        stateManager = GetComponent<PlayerStateManager>();
    }

    // Returns true for any spawned network player so its attack implementation cannot run locally.
    // The owner sends intent; the server owner executes directly through the same guarded path.
    public static bool TryRouteAttack(MonoBehaviour attackSource)
    {
        if (attackSource == null)
        {
            return false;
        }

        NetworkPlayerCombat combat = attackSource.GetComponent<NetworkPlayerCombat>();
        if (combat == null || !combat.IsSpawned || combat.executingAuthoritativeAttack)
        {
            return false;
        }

        if (!combat.IsProductionCombatReady())
            return true;

        if (combat.IsServer)
        {
            if (combat.IsOwner)
            {
                combat.ExecuteAuthoritativeAttack(combat.ReadAimPitch());
            }
        }
        else if (combat.IsOwner)
        {
            combat.RequestBasicAttackRpc(combat.ReadAimPitch());
        }

        return true;
    }

    private float ReadAimPitch()
    {
        var look = GetComponent<PlayerLook>();
        return look != null && look.cameraPivot != null
            ? Mathf.DeltaAngle(0f, look.cameraPivot.localEulerAngles.x) : 0f;
    }

    private void ExecuteAuthoritativeAttack(float aimPitch)
    {
        if (playerHealth == null) playerHealth = GetComponent<PlayerHealth>();
        var skills = GetComponent<NetworkPlayerSkills>();
        var skillController = GetComponent<PlayerSkillController>();
        if (!IsProductionCombatReady() || (skills != null && skills.BlocksBasicAttack) ||
            (skillController != null && skillController.IsSessionEnded)) return;
        if (float.IsNaN(aimPitch) || float.IsInfinity(aimPitch) ||
            !IsSpawned || !IsServer || !NetworkHealth.CanWrite(gameObject) ||
            playerHealth == null || playerHealth.IsDead || playerHealth.CurrentHealth <= 0 ||
            (stateManager != null && stateManager.CurrentState == PlayerState.Dead) ||
            basicAttack == null || executingAuthoritativeAttack)
        {
            return;
        }

        bool accepted;
        var look = GetComponent<PlayerLook>();
        // Camera pitch is an input intent, not a client hit/damage claim. Origin and damage stay server-owned.
        Quaternion previousAim = look != null && look.cameraPivot != null
            ? look.cameraPivot.localRotation : Quaternion.identity;
        if (look != null && look.cameraPivot != null)
            look.cameraPivot.localRotation = Quaternion.Euler(
                Mathf.Clamp(aimPitch, look.downClamp, look.upClamp), 0f, 0f);
        executingAuthoritativeAttack = true;
        try
        {
            // Re-enter the existing implementation once; TryRouteAttack sees the guard and lets it execute.
            accepted = basicAttack.TryAttack();
        }
        finally
        {
            executingAuthoritativeAttack = false;
            if (look != null && look.cameraPivot != null) look.cameraPivot.localRotation = previousAim;
        }

        if (accepted)
        {
            AcceptedBasicAttackRpc();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void RequestBasicAttackRpc(float aimPitch)
    {
        ExecuteAuthoritativeAttack(aimPitch);
    }

    private bool IsProductionCombatReady()
    {
        if (!NetworkGameSession.IsNetworkSessionActive)
            return true;

        GameManager gameManager = GameManager.Instance;
        return NetworkGameSession.IsCombatRunning && gameManager != null &&
            gameManager.IsActiveSession && gameManager.IsNetworkCombatActive;
    }

    public void PresentMeleeHit(Vector3 contactPoint)
    {
        PlayerSkillController skillController = GetComponent<PlayerSkillController>();
        if (IsServer && IsSpawned && IsProductionCombatReady() &&
            (skillController == null || !skillController.IsSessionEnded))
            MeleeHitRpc(contactPoint);
    }

    [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
    private void MeleeHitRpc(Vector3 contactPoint)
    {
        // Never slow the server or client simulation for a cosmetic impact.
        PlayerSkillController skillController = GetComponent<PlayerSkillController>();
        if (IsProductionCombatReady() && (skillController == null || !skillController.IsSessionEnded))
            GetComponent<SwordCombatFeedback>()?.PlayMeleeImpact(contactPoint, false);
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Server)]
    private void AcceptedBasicAttackRpc()
    {
        PlayerSkillController skillController = GetComponent<PlayerSkillController>();
        if (!IsProductionCombatReady() || (skillController != null && skillController.IsSessionEnded))
            return;

        PlayerController controller = GetComponent<PlayerController>();
        if (controller != null)
        {
            controller.PresentAcceptedBasicAttack();
        }

        if (!IsServer && TryGetComponent(out MeleePlayerAttack meleeAttack))
        {
            meleeAttack.PlayAcceptedAttackVisual();
        }
    }
}
