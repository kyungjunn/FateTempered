using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

// Owners submit intent only. NetworkTransform and NetworkAnimator must use server authority.
[RequireComponent(typeof(NetworkObject), typeof(Rigidbody), typeof(PlayerMovement))]
public sealed class NetworkPlayerMotor : NetworkBehaviour
{
    public const float InputTimeout = 0.25f;
    public Vector2 CurrentMoveInput { get; private set; }

    private PlayerMovement movement;
    private PlayerSkillController skills;
    private PlayerHealth health;
    private PlayerStateManager state;
    private Animator animator;
    private NetworkAnimator networkAnimator;
    private Rigidbody body;
    private PlayerStats stats;
    private float yaw;
    private float lastInputTime = float.NegativeInfinity;
    private bool jumpPending;

    private void Awake()
    {
        movement = GetComponent<PlayerMovement>();
        skills = GetComponent<PlayerSkillController>();
        health = GetComponent<PlayerHealth>();
        state = GetComponent<PlayerStateManager>();
        animator = GetComponentInChildren<Animator>();
        networkAnimator = GetComponentInChildren<NetworkAnimator>();
        body = GetComponent<Rigidbody>();
        stats = GetComponent<PlayerStats>();
    }

    public override void OnNetworkSpawn()
    {
        body.isKinematic = !IsServer;
        yaw = transform.eulerAngles.y;
        ClearInput();
    }

    public override void OnLostOwnership() { ClearInput(); }
    public override void OnNetworkDespawn() { ClearInput(); }

    private void ClearInput()
    {
        CurrentMoveInput = Vector2.zero;
        jumpPending = false;
        lastInputTime = float.NegativeInfinity;
    }

    // Offline objects and spawned server objects alone may write root movement/animation.
    public static bool CanSimulate(GameObject target)
    {
        var networkObject = target.GetComponent<NetworkObject>();
        return networkObject == null || !networkObject.IsSpawned ||
            (networkObject.NetworkManager != null && networkObject.NetworkManager.IsServer);
    }

    // Owner-facing API: finite direction is clamped to unit length, yaw normalized to degrees.
    // No position, velocity, speed, delta time, or grounded claim is accepted from the owner.
    public void SubmitInput(Vector2 move, float yawDegrees, bool jump)
    {
        if (!IsSpawned || !IsOwner) return;
        if (IsServer) AcceptInput(move, yawDegrees, jump);
        else SubmitInputRpc(move, yawDegrees, jump);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void SubmitInputRpc(Vector2 move, float yawDegrees, bool jump)
    {
        AcceptInput(move, yawDegrees, jump);
    }

    private void AcceptInput(Vector2 move, float yawDegrees, bool jump)
    {
        if (!IsSpawned || !IsServer || !IsFinite(move.x) || !IsFinite(move.y) ||
            !IsFinite(yawDegrees)) return;
        // Clamp components first so extreme finite input cannot overflow magnitude arithmetic.
        move.x = Mathf.Clamp(move.x, -1f, 1f);
        move.y = Mathf.Clamp(move.y, -1f, 1f);
        CurrentMoveInput = Vector2.ClampMagnitude(move, 1f);
        yaw = Mathf.Repeat(yawDegrees, 360f);
        jumpPending |= jump;
        lastInputTime = Time.unscaledTime;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private void FixedUpdate()
    {
        if (!IsSpawned || !IsServer) return;
        if (Time.unscaledTime - lastInputTime > InputTimeout) ClearInput();
        if ((health != null && health.IsDead) || (state != null && state.CurrentState == PlayerState.Dead))
        {
            ClearInput();
            return;
        }

        bool locked = skills != null && skills.IsMovementLocked;
        Vector2 move = locked ? Vector2.zero : CurrentMoveInput;
        if (!locked)
        {
            if (Time.unscaledTime - lastInputTime <= InputTimeout)
                body.rotation = Quaternion.Euler(0f, yaw, 0f);
            movement.MoveAuthoritative(move, yaw, Time.fixedDeltaTime);
            if (jumpPending && movement.Jump())
            {
                if (networkAnimator != null) networkAnimator.SetTrigger("Jump");
                else if (animator != null) animator.SetTrigger("Jump");
            }
        }
        jumpPending = false;
        bool grounded = movement.CheckGrounded();
        if (animator != null)
        {
            animator.SetFloat("AttackSpeed", stats != null && stats.AttackSpeed > 0f ? stats.AttackSpeed : 1f);
            animator.SetFloat("MoveSpeed", move.sqrMagnitude <= 0.01f ? 0f : move.y < -0.01f ? -1f : 1f);
            animator.SetFloat("LegSpeed", grounded ? 1f : 0f);
        }
        if (state != null && state.CurrentState != PlayerState.Attack)
            state.ChangeState(!grounded ? PlayerState.Jump : move.sqrMagnitude > 0.01f ? PlayerState.Move : PlayerState.Idle);
    }
}
