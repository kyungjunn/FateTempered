using UnityEngine;
using Unity.Netcode.Components;

public class PlayerController : MonoBehaviour
{
    private static readonly int AttackSpeedParameter = Animator.StringToHash("AttackSpeed");

    private PlayerInputHandler input;
    private PlayerMovement movement;
    private IPlayerBasicAttack playerAttack;
    private Animator anim;
    private PlayerStateManager stateManager;
    private PlayerSkillController skillController;
    private WizardSkillTargeting skillTargeting;
    private PlayerStats playerStats;
    private bool basicAttackActive;
    private bool attackSpeedSubscribed;
    private float appliedAttackSpeed = float.NaN;
    private NetworkPlayerMotor motor;
    private PlayerLook look;

    private void Awake()
    {
        EnsureCombatComponents();
        InitializeReferences();
        SubscribeToStats();
    }

    private void OnEnable()
    {
        SubscribeToStats();
    }

    private void OnDisable()
    {
        UnsubscribeFromStats();
    }

    private void Start()
    {
        InitializeReferences();
        UpdateAttackAnimationSpeed();
    }

    private void Update()
    {
        if (motor != null && motor.IsSpawned)
        {
            UpdateNetworkInput();
            return;
        }

        if (GameManager.Instance != null && GameManager.Instance.IsPaused)
        {
            if (input != null)
            {
                input.AttackTriggered = false;
                input.JumpTriggered = false;
            }

            return;
        }

        if (stateManager.CurrentState == PlayerState.Dead)
        {
            input.AttackTriggered = false;
            return;
        }

        UpdateAttackAnimationSpeed();

        if (skillController == null || !skillController.IsMovementLocked)
            movement.Move(input.MoveInput);

        bool isGrounded = movement.CheckGrounded();

        UpdateMovementState(isGrounded);

        if (anim != null)
        {
            bool isMoving = input.MoveInput.sqrMagnitude > 0.01f;
            float moveSpeedParameter = !isMoving ? 0f :
                input.MoveInput.y < -0.01f ? -1f : 1f;

            anim.SetFloat("MoveSpeed", moveSpeedParameter);
            anim.SetFloat("LegSpeed", isGrounded ? 1f : 0f);
        }

        if (skillTargeting != null && skillTargeting.ShouldSuppressBasicAttack)
            input.AttackTriggered = false;

        if (input.AttackTriggered)
        {
            // 공격 중 입력은 소비만 한다. Trigger를 다시 쌓으면 공격 종료 직후
            // 애니메이션만 재시작되고 발사체와 공격 판정이 어긋날 수 있다.
            if (stateManager.CurrentState != PlayerState.Attack &&
                (skillController == null || !skillController.IsUltimateDashing) &&
                playerAttack != null && playerAttack.TryAttack())
            {
                PresentAcceptedBasicAttack();
            }

            input.AttackTriggered = false;
        }

        if (input.JumpTriggered)
        {
            if ((skillController == null || !skillController.IsUltimateDashing) &&
                movement.Jump() && anim != null)
            {
                anim.SetTrigger("Jump");
            }

            input.JumpTriggered = false;
        }
    }

    private void UpdateNetworkInput()
    {
        if (!motor.IsOwner || input == null) return;
        bool blocked = (GameManager.Instance != null && GameManager.Instance.IsPaused) ||
            (stateManager != null && stateManager.CurrentState == PlayerState.Dead);
        motor.SubmitInput(blocked ? Vector2.zero : input.MoveInput,
            look != null ? look.InputYaw : transform.eulerAngles.y, !blocked && input.JumpTriggered);
        if (!blocked && input.AttackTriggered &&
            (skillTargeting == null || !skillTargeting.ShouldSuppressBasicAttack))
        {
            // NetworkPlayerCombat owns acceptance and presentation, including on the host.
            playerAttack?.TryAttack();
        }
        input.JumpTriggered = false;
        input.AttackTriggered = false;
    }

    private void EnsureCombatComponents()
    {
        if (!TryGetComponent(out PlayerHealth _))
        {
            gameObject.AddComponent<PlayerHealth>();
        }

        if (!TryGetComponent(out PlayerDamageDealer _))
        {
            gameObject.AddComponent<PlayerDamageDealer>();
        }

    }

    private void InitializeReferences()
    {
        if (input == null) input = GetComponent<PlayerInputHandler>();
        if (movement == null) movement = GetComponent<PlayerMovement>();
        if (playerAttack == null) playerAttack = GetComponent<IPlayerBasicAttack>();
        if (anim == null) anim = GetComponentInChildren<Animator>();
        if (stateManager == null) stateManager = GetComponent<PlayerStateManager>();
        if (skillController == null) skillController = GetComponent<PlayerSkillController>();
        if (skillTargeting == null) skillTargeting = GetComponent<WizardSkillTargeting>();
        if (playerStats == null) playerStats = GetComponent<PlayerStats>();
        if (motor == null) motor = GetComponent<NetworkPlayerMotor>();
        if (look == null) look = GetComponent<PlayerLook>();
    }

    // Called once after authoritative acceptance; also initializes a disabled replica before Start.
    public void PresentAcceptedBasicAttack()
    {
        InitializeReferences();
        if (stateManager != null && stateManager.CurrentState == PlayerState.Dead)
        {
            return;
        }

        if (NetworkPlayerMotor.CanSimulate(gameObject))
        {
            UpdateAttackAnimationSpeed();
            stateManager?.ChangeState(PlayerState.Attack);
            basicAttackActive = true;
            var networkAnimator = GetComponentInChildren<NetworkAnimator>();
            if (motor != null && motor.IsSpawned && networkAnimator != null)
                networkAnimator.SetTrigger("Attack");
            else if (anim != null)
                anim.SetTrigger("Attack");
        }

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayPlayerAttack();
        }
    }

    private void SubscribeToStats()
    {
        if (playerStats == null || attackSpeedSubscribed)
        {
            return;
        }

        playerStats.onStatsChanged += UpdateAttackAnimationSpeed;
        attackSpeedSubscribed = true;
    }

    private void UnsubscribeFromStats()
    {
        if (playerStats == null || !attackSpeedSubscribed)
        {
            return;
        }

        playerStats.onStatsChanged -= UpdateAttackAnimationSpeed;
        attackSpeedSubscribed = false;
    }

    private void UpdateAttackAnimationSpeed()
    {
        if (!NetworkPlayerMotor.CanSimulate(gameObject)) return;
        if (anim == null)
        {
            return;
        }

        float attackSpeed = playerStats != null && playerStats.AttackSpeed > 0f
            ? playerStats.AttackSpeed
            : 1f;

        if (Mathf.Approximately(appliedAttackSpeed, attackSpeed))
        {
            return;
        }

        anim.SetFloat(AttackSpeedParameter, attackSpeed);
        appliedAttackSpeed = attackSpeed;
    }

    private void UpdateMovementState(bool isGrounded)
    {
        if (stateManager.CurrentState == PlayerState.Dead)
        {
            return;
        }

        if (stateManager.CurrentState == PlayerState.Attack)
        {
            return;
        }

        if (!isGrounded)
        {
            stateManager.ChangeState(PlayerState.Jump);
            return;
        }

        if (input.MoveInput.sqrMagnitude > 0.01f)
        {
            stateManager.ChangeState(PlayerState.Move);
        }
        else
        {
            stateManager.ChangeState(PlayerState.Idle);
        }
    }

    public void EndAttackState()
    {
        if (!NetworkPlayerMotor.CanSimulate(gameObject)) return;
        if (skillController != null && skillController.IsUltimateDashing)
            return;
        basicAttackActive = false;

        if (stateManager == null || stateManager.CurrentState == PlayerState.Dead)
        {
            return;
        }

        bool isGrounded = movement != null && movement.CheckGrounded();
        bool moving = motor != null && motor.IsSpawned
            ? motor.CurrentMoveInput.sqrMagnitude > 0.01f
            : input != null && input.MoveInput.sqrMagnitude > 0.01f;

        if (!isGrounded)
        {
            stateManager.ChangeState(PlayerState.Jump);
        }
        else if (moving)
        {
            stateManager.ChangeState(PlayerState.Move);
        }
        else
        {
            stateManager.ChangeState(PlayerState.Idle);
        }
    }
}
