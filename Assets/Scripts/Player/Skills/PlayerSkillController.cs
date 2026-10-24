// Q/E/R 입력, 시전 조건, 쿨타임과 닌자 궁극기 연결.
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

[RequireComponent(typeof(PlayerUltimateGauge))]
public sealed class PlayerSkillController : MonoBehaviour
{
    private const float DashMoveSpeedDuration = 2f;

    [Header("Skills")]
    // Q 스킬
    [SerializeField] private PlayerSkillSO skillQ;
    // E 스킬
    [SerializeField] private PlayerSkillSO skillE;
    // R 궁극기
    [SerializeField] private PlayerSkillSO ultimate;
    [SerializeField] private Sprite basicAttackIcon;
    [SerializeField] private Sprite specialUltimateIcon;

    public Sprite BasicAttackIcon => basicAttackIcon;
    // 일반 궁극기는 스킬 아이콘, 닌자 전용 궁극기는 별도 아이콘 사용.
    public Sprite UltimateIcon => ultimate != null ? ultimate.Icon : specialUltimateIcon;
    public PlayerSkillSO SkillQ => skillQ;
    public PlayerSkillSO SkillE => skillE;
    // 재사용 가능 시각과 현재 시각의 차이: HUD용 남은 쿨타임.
    public float QRemaining => Replica != null ? Replica.QRemaining : Mathf.Max(0f, qReadyTime - Time.time);
    public float ERemaining => ECharges > 0 ? 0f : ERechargeRemaining;
    public int ECharges
    {
        get
        {
            if (Replica != null) return Replica.ECharges;
            RefreshEChargeState();
            return eCharges;
        }
    }
    public int EMaxCharges
    {
        get
        {
            if (Replica != null) return Replica.EMaxCharges;
            RefreshEChargeState();
            return eChargeCapacity;
        }
    }
    public float ERechargeRemaining
    {
        get
        {
            if (Replica != null) return Replica.ERechargeRemaining;
            RefreshEChargeState();
            return ePendingRechargeCharges > 0
                ? Mathf.Max(0f, eNextRechargeTime - Time.time)
                : 0f;
        }
    }
    // 지연 쿨타임 스킬의 시전 중 상태와 궁극기 재시전 상태.
    public bool QChanneling => Replica != null ? Replica.QChanneling : skillQ != null && deferredCooldownSkill == skillQ;
    public bool EChanneling => Replica != null ? Replica.EChanneling : skillE != null && deferredCooldownSkill == skillE;
    public bool UltimateRecastReady => Replica != null ? Replica.UltimateRecastReady : ninjaUltimate != null && ninjaUltimate.IsReady;

    [Header("References")]
    // 시전 위치
    [SerializeField] private Transform skillOrigin;
    // 스킬 애니메이터
    [SerializeField] private Animator animator;
    [SerializeField] private GameObject skillEWeapon;

    private PlayerStats stats;
    private PlayerDamageDealer damageDealer;
    private PlayerUltimateGauge ultimateGauge;
    private PlayerStateManager stateManager;
    private PlayerHealth playerHealth;
    private PlayerAugments playerAugments;
    private PlayerInputHandler playerInput;
    private WizardCombat wizardCombat;
    // Q 재사용 가능 시각
    private float qReadyTime;
    // E 충전과 순차 재충전 상태.
    private int eChargeCapacity;
    private int eCharges;
    private int ePendingRechargeCharges;
    private float eNextRechargeTime;
    private float eRechargeInterval;
    private bool eChargeStateInitialized;
    private bool eDeferredChargePending;
    // 돌진 완료 이동속도 보너스.
    private bool dashMoveSpeedBonusActive;
    private float dashMoveSpeedBonus;
    private float dashMoveSpeedBonusEndTime;
    // 이동 잠금, 종료 대기 스킬, E 무기 이펙트, 닌자 궁극기 상태.
    private bool movementLocked;
    private PlayerSkillSO deferredCooldownSkill;
    private Coroutine skillEWeaponRoutine;
    private NinjaUltimate ninjaUltimate;
    private int resonancePreviewOpenedFrame = -1;
    private int suppressBasicAttackThroughFrame = -1;
    private NetworkPlayerSkills networkSkills;
    private NetworkPlayerSkills NetworkSkills
    {
        get
        {
            if (networkSkills == null) networkSkills = GetComponent<NetworkPlayerSkills>();
            return networkSkills != null && networkSkills.IsSpawned ? networkSkills : null;
        }
    }
    private NetworkPlayerSkills Replica => NetworkSkills != null && !NetworkSkills.IsServer ? NetworkSkills : null;
    private bool CanReadInput => NetworkSkills == null || NetworkSkills.IsOwner;
    internal float QReadyTime => qReadyTime;
    internal float EReadyTime => ePendingRechargeCharges > 0 ? eNextRechargeTime : 0f;
    internal bool BlocksNetworkBasicAttack => IsSkillCasting();

    public Transform SkillOrigin => skillOrigin;
    public PlayerStats Stats => stats;
    public PlayerDamageDealer DamageDealer => damageDealer;
    public bool IsMovementLocked => Replica != null ? Replica.IsMovementLocked : movementLocked;
    public bool IsUltimateDashing => Replica != null ? Replica.IsUltimateDashing : ninjaUltimate != null && ninjaUltimate.IsDashing;
    public bool IsWizardResonancePreviewActive
    {
        get
        {
            WizardCombat combat = GetWizardCombat();
            return combat != null && combat.IsResonancePreviewActive;
        }
    }
    public bool ShouldSuppressBasicAttack
    {
        get
        {
            if (IsWizardResonancePreviewActive)
                return true;

            if (playerInput == null)
                playerInput = GetComponent<PlayerInputHandler>();
            return Time.frameCount <= suppressBasicAttackThroughFrame &&
                   playerInput != null && playerInput.AttackTriggered;
        }
    }
    public bool IsPlayerDead =>
        (stateManager != null && stateManager.CurrentState == PlayerState.Dead) ||
        ((playerHealth != null || (playerHealth = GetComponent<PlayerHealth>()) != null) && playerHealth.IsDead);

    public bool HasSkill(PlayerSkillSO skill)
    {
        return skill != null && (skillQ == skill || skillE == skill || ultimate == skill);
    }

    public void SetMovementLocked(bool locked)
    {
        movementLocked = locked;
    }

    private void Awake()
    {
        // 시전과 피해 계산에 필요한 플레이어 컴포넌트 확보.
        stats = GetComponent<PlayerStats>();
        damageDealer = GetComponent<PlayerDamageDealer>();
        ultimateGauge = GetComponent<PlayerUltimateGauge>();
        stateManager = GetComponent<PlayerStateManager>();
        playerHealth = GetComponent<PlayerHealth>();
        playerAugments = GetComponent<PlayerAugments>();
        playerInput = GetComponent<PlayerInputHandler>();
        wizardCombat = GetComponent<WizardCombat>();
        ninjaUltimate = GetComponent<NinjaUltimate>();
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
        if (skillEWeapon != null)
            skillEWeapon.SetActive(false);

        RefreshEChargeState();
    }

    private void OnEnable()
    {
        if (stateManager != null)
            stateManager.OnDead += HandlePlayerDeath;
        if (playerAugments != null)
            playerAugments.onSkillsChanged += HandlePlayerSkillsChanged;

        RefreshEChargeState();
        HandlePlayerSkillsChanged();
    }

    private void Update()
    {
        if (Replica != null)
        {
            if (CanReadInput)
            {
                if (IsPlayerDead || Time.timeScale <= 0f ||
                    (GameManager.Instance != null && GameManager.Instance.IsPaused))
                    CancelWizardResonancePreview();
                else
                    UpdateWizardResonancePreviewInput();
            }
            return;
        }
        // 채널링 중 사망하면 대기 중인 쿨타임을 시작.
        if (deferredCooldownSkill != null &&
            IsPlayerDead)
            CompleteDeferredSkill(deferredCooldownSkill);

        RefreshEChargeState();
        if (IsPlayerDead)
        {
            CancelWizardResonancePreview();
            ClearDashMoveSpeedBonus();
            return;
        }

        if (Time.timeScale <= 0f || (GameManager.Instance != null && GameManager.Instance.IsPaused))
        {
            CancelWizardResonancePreview();
            return;
        }

        if (CanReadInput) UpdateWizardResonancePreviewInput();

        if (dashMoveSpeedBonusActive && Time.time >= dashMoveSpeedBonusEndTime)
            ClearDashMoveSpeedBonus();
    }

    private void OnDisable()
    {
        // 비활성화 시 궁극기 취소, E 무기 숨김, 지연 쿨타임 정리.
        CancelWizardResonancePreview();
        if (stateManager != null)
            stateManager.OnDead -= HandlePlayerDeath;
        if (playerAugments != null)
            playerAugments.onSkillsChanged -= HandlePlayerSkillsChanged;

        ninjaUltimate?.Cancel();
        if (skillEWeaponRoutine != null)
        {
            StopCoroutine(skillEWeaponRoutine);
            skillEWeaponRoutine = null;
        }
        if (skillEWeapon != null)
            skillEWeapon.SetActive(false);
        if (deferredCooldownSkill != null)
            CompleteDeferredSkill(deferredCooldownSkill);

        ClearDashMoveSpeedBonus();
        movementLocked = false;
    }

    // Q 입력
    public void OnSkillQ(InputValue value)
    {
        if (!CanReadInput) return;
        if (value.isPressed)
        {
            CancelWizardResonancePreview();
            if (NetworkSkills != null) NetworkSkills.RequestSkill(NetworkSkillSlot.Q);
            else TryCast(skillQ, false);
        }
    }

    // E 입력
    public void OnSkillE(InputValue value)
    {
        if (!CanReadInput) return;
        if (value.isPressed)
        {
            if (IsWizardResonancePreviewActive && Mouse.current != null &&
                Mouse.current.rightButton.wasPressedThisFrame)
            {
                CancelWizardResonancePreview();
                return;
            }

            if (IsWizardResonancePreviewActive && !TryGetWizardResonanceSkill(out _))
                CancelWizardResonancePreview();

            if (TryGetWizardResonanceSkill(out _))
            {
                if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                {
                    CancelWizardResonancePreview();
                    return;
                }

                if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
                {
                    CancelWizardResonancePreview();
                    return;
                }

                if (IsPointerOverUI())
                    return;

                if (IsWizardResonancePreviewActive)
                    TryConfirmWizardResonancePreview();
                else
                    TryBeginWizardResonancePreview();
                return;
            }

            if (NetworkSkills != null) NetworkSkills.RequestSkill(NetworkSkillSlot.E);
            else TryCast(skillE, true);
        }
    }

    // R 입력. 게이지 풀일 때만 시전.
    public void OnUltimate(InputValue value)
    {
        if (!CanReadInput) return;
        if (value.isPressed)
            TryUseUltimate();
    }

    public bool TryUseUltimate()
    {
        if (!CanReadInput) return false;
        CancelWizardResonancePreview();
        if (NetworkSkills != null) return NetworkSkills.RequestSkill(NetworkSkillSlot.Ultimate);
        return TryUseUltimateAuthoritative();
    }

    private bool TryUseUltimateAuthoritative()
    {
        if (!NetworkHealth.CanWrite(gameObject)) return false;
        // 일시정지·사망·다른 스킬 시전 중이면 사용 차단.
        if (!CanCast() || IsSkillCasting())
            return false;

        // 닌자 궁극기: 준비 중이면 재시전, 아니면 게이지를 써서 준비 시작.
        if (ninjaUltimate != null)
        {
            if (ninjaUltimate.IsReady)
                return ninjaUltimate.TryRecast();
            return ultimateGauge.IsFull && ninjaUltimate.TryActivate() && ultimateGauge.TryConsume();
        }

        // 일반 궁극기: 게이지가 가득 찬 경우에만 시전 후 소모.
        if (ultimate == null || !ultimateGauge.IsFull)
            return false;

        if (ultimate.Cast(this, PlayerDamageType.Skill) && ultimateGauge.TryConsume())
        {
            PlayAnimation(ultimate);
            return true;
        }
        return false;
    }

    public void NotifyChannelFinished(PlayerSkillSO skill)
    {
        // 지속형 스킬 종료 알림을 받아 지연 쿨타임 완료.
        CompleteDeferredSkill(skill);
    }

    public bool TryBeginWizardResonancePreview()
    {
        if (!CanReadInput) return false;
        if (IsPointerOverUI())
            return false;

        if (!TryGetWizardResonanceSkill(out WizardSkillSO skill) ||
            !CanCast() || IsSkillCasting() || ECharges <= 0 || IsWizardResonancePreviewActive)
        {
            return false;
        }

        WizardCombat combat = GetWizardCombat();
        if (combat == null || !combat.TryBeginResonancePreview(skill))
            return false;

        resonancePreviewOpenedFrame = Time.frameCount;
        SuppressBasicAttackForCurrentFrame();
        return true;
    }

    public bool TryConfirmWizardResonancePreview()
    {
        if (!CanReadInput) return false;
        if (!IsWizardResonancePreviewActive)
            return false;

        SuppressBasicAttackForCurrentFrame();
        if (Time.frameCount <= resonancePreviewOpenedFrame || IsPointerOverUI())
            return false;

        if (!TryGetWizardResonanceSkill(out WizardSkillSO skill) ||
            !CanCast() || IsSkillCasting() || ECharges <= 0)
        {
            return false;
        }

        WizardCombat combat = GetWizardCombat();
        if (NetworkSkills != null)
        {
            if (combat == null || !combat.TryGetNetworkResonanceTarget(out Vector3 target))
                return false;
            bool submitted = NetworkSkills.RequestSkill(NetworkSkillSlot.E, target, true);
            if (submitted) CancelWizardResonancePreview();
            return submitted;
        }
        if (combat == null || !combat.TryPrepareResonancePreviewCast(skill))
            return false;

        bool cast = TryCast(skillE, true);
        if (!cast)
            combat.CancelPreparedResonancePreviewCast();
        else
            resonancePreviewOpenedFrame = -1;

        return cast;
    }

    public void CancelWizardResonancePreview()
    {
        resonancePreviewOpenedFrame = -1;
        GetWizardCombat()?.CancelResonancePreview();
    }

    public void SuppressBasicAttackForCurrentFrame()
    {
        int suppressThroughFrame = Time.frameCount;
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            suppressThroughFrame++;
        suppressBasicAttackThroughFrame = Mathf.Max(suppressBasicAttackThroughFrame, suppressThroughFrame);
        if (playerInput == null)
            playerInput = GetComponent<PlayerInputHandler>();
        if (playerInput != null)
            playerInput.AttackTriggered = false;
    }

    // 쿨다운 확인 후 시전
    private bool TryCast(PlayerSkillSO skill, bool isE)
    {
        if (!NetworkHealth.CanWrite(gameObject)) return false;
        // 시전 가능 상태, Q 쿨타임 또는 E 충전 수, 스킬 실행 결과 검사.
        if (!CanCast() || IsSkillCasting() || skill == null ||
            (isE ? ECharges <= 0 : Time.time < qReadyTime) ||
            !skill.Cast(this, PlayerDamageType.Skill))
            return false;

        PlayAnimation(skill);
        // E 무기는 애니메이션 종료 시까지 표시.
        if (skill == skillE && skillEWeapon != null && animator != null)
        {
            skillEWeapon.SetActive(true);
            skillEWeaponRoutine = StartCoroutine(HideSkillEWeaponAfterAnimation());
        }
        // 지속형은 종료 알림을 기다리고, 일반 스킬은 즉시 쿨타임 시작.
        if (skill.DefersCooldown)
        {
            deferredCooldownSkill = skill;
            if (isE)
                ConsumeECharge(true, skill.Cooldown);
        }
        else if (isE)
        {
            ConsumeECharge(false, skill.Cooldown);
        }
        else
        {
            qReadyTime = Time.time + skill.Cooldown;
        }

        return true;
    }

    internal bool TryCastNetworkSkill(NetworkSkillSlot slot, Vector3 groundTarget, bool hasGroundTarget)
    {
        if (NetworkSkills == null || !NetworkSkills.IsServer || !CanCast() || IsSkillCasting())
            return false;
        switch (slot)
        {
            case NetworkSkillSlot.Q:
                return TryCast(skillQ, false);
            case NetworkSkillSlot.E:
                if (TryGetWizardResonanceSkill(out WizardSkillSO resonance))
                {
                    WizardCombat combat = GetWizardCombat();
                    if (!hasGroundTarget || ECharges <= 0 || combat == null ||
                        !combat.TryPrepareNetworkResonanceCast(resonance, groundTarget))
                        return false;
                    bool cast = TryCast(skillE, true);
                    if (!cast) combat.CancelPreparedResonancePreviewCast();
                    return cast;
                }
                return TryCast(skillE, true);
            case NetworkSkillSlot.Ultimate:
                return TryUseUltimateAuthoritative();
            default:
                return false;
        }
    }

    private IEnumerator HideSkillEWeaponAfterAnimation()
    {
        // 애니메이션 트리거는 Cast 시점이 아니라 다음 애니메이터 갱신에 반영됨.
        // E 모션 진입을 최대 1초 기다리고, 사망하거나 기한을 넘기면 대기 종료.
        float enterDeadline = Time.time + 1f;
        while (!IsEAnimationActive() && Time.time < enterDeadline &&
               (stateManager == null || stateManager.CurrentState != PlayerState.Dead))
            yield return null;

        // E 모션이 끝나거나 사망하면 무기 숨김.
        while (IsEAnimationActive() &&
               (stateManager == null || stateManager.CurrentState != PlayerState.Dead))
            yield return null;

        skillEWeapon.SetActive(false);
        skillEWeaponRoutine = null;
    }

    private bool IsEAnimationActive()
    {
        return animator.GetCurrentAnimatorStateInfo(0).IsName("SkillE") ||
               (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).IsName("SkillE"));
    }

    private void CompleteDeferredSkill(PlayerSkillSO skill)
    {
        // 현재 대기 중인 스킬의 종료 알림만 처리.
        if (skill == null || deferredCooldownSkill != skill)
            return;

        deferredCooldownSkill = null;
        // 종료 시점부터 Q 또는 E 쿨타임 계산.
        if (skill == skillQ)
            qReadyTime = Time.time + skill.Cooldown;
        else if (skill == skillE)
            CompleteDeferredEChargeCooldown(skill.Cooldown);

        if (animator != null && !string.IsNullOrWhiteSpace(skill.AnimationTrigger))
        {
            // 남은 시전 트리거를 지우고 기본 애니메이션으로 복귀.
            animator.ResetTrigger(skill.AnimationTrigger);
            animator.CrossFade("Idle", 0.05f, 0);
        }

        GetComponent<PlayerController>()?.EndAttackState();
    }

    public void NotifyDashCompleted(NinjaDashSkillSO dashSkill)
    {
        if (dashSkill == null || dashSkill != skillE || !isActiveAndEnabled || IsPlayerDead || stats == null)
            return;

        float speedBonusRatio = playerAugments != null
            ? playerAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.DashMoveSpeed, dashSkill)
            : 0f;
        if (speedBonusRatio <= 0f)
            return;

        float moveSpeedWithoutDashBonus = stats.MoveSpeed -
                                          (dashMoveSpeedBonusActive ? dashMoveSpeedBonus : 0f);
        dashMoveSpeedBonus = Mathf.Max(0f, moveSpeedWithoutDashBonus) * speedBonusRatio;
        dashMoveSpeedBonusEndTime = Time.time + DashMoveSpeedDuration;
        dashMoveSpeedBonusActive = true;
        stats.SetTemporaryBonus(this, AugmentType.MoveSpeed, dashMoveSpeedBonus);
    }

    private void HandlePlayerSkillsChanged()
    {
        RefreshEChargeState();

        if (!dashMoveSpeedBonusActive)
            return;

        float currentRatio = playerAugments != null
            ? playerAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.DashMoveSpeed, skillE)
            : 0f;
        if (currentRatio <= 0f || stats == null)
        {
            ClearDashMoveSpeedBonus();
            return;
        }

        float moveSpeedWithoutDashBonus = stats.MoveSpeed - dashMoveSpeedBonus;
        dashMoveSpeedBonus = Mathf.Max(0f, moveSpeedWithoutDashBonus) * currentRatio;
        stats.SetTemporaryBonus(this, AugmentType.MoveSpeed, dashMoveSpeedBonus);
    }

    private void HandlePlayerDeath()
    {
        CancelWizardResonancePreview();
        ClearDashMoveSpeedBonus();
        movementLocked = false;
    }

    private void UpdateWizardResonancePreviewInput()
    {
        WizardCombat combat = GetWizardCombat();
        if (combat == null || !combat.IsResonancePreviewActive)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            CancelWizardResonancePreview();
            return;
        }

        Mouse mouse = Mouse.current;
        if (mouse == null)
            return;

        if (mouse.rightButton.wasPressedThisFrame)
        {
            CancelWizardResonancePreview();
            return;
        }

        if (mouse.leftButton.wasPressedThisFrame && Time.frameCount > resonancePreviewOpenedFrame &&
            !IsPointerOverUI())
        {
            TryConfirmWizardResonancePreview();
        }
    }

    private bool TryGetWizardResonanceSkill(out WizardSkillSO skill)
    {
        skill = skillE as WizardSkillSO;
        return skill != null && skill.Kind == WizardSkillKind.ResonanceField;
    }

    private WizardCombat GetWizardCombat()
    {
        if (wizardCombat == null)
            wizardCombat = GetComponent<WizardCombat>();
        return wizardCombat;
    }

    private static bool IsPointerOverUI()
    {
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem == null)
            return false;

        if (eventSystem.IsPointerOverGameObject())
            return true;

        Mouse mouse = Mouse.current;
        return mouse != null && eventSystem.IsPointerOverGameObject(mouse.deviceId);
    }

    private void ClearDashMoveSpeedBonus()
    {
        if (!dashMoveSpeedBonusActive && dashMoveSpeedBonus == 0f)
            return;

        dashMoveSpeedBonusActive = false;
        dashMoveSpeedBonus = 0f;
        dashMoveSpeedBonusEndTime = 0f;
        stats?.RemoveTemporaryBonuses(this);
    }

    private void RefreshEChargeState()
    {
        if (!NetworkHealth.CanWrite(gameObject)) return;
        if (playerAugments == null)
            playerAugments = GetComponent<PlayerAugments>();

        int newCapacity = 1;
        if (playerAugments != null && skillE != null)
        {
            float extraCharges = playerAugments.GetCombatAugmentValue(
                PlayerCombatAugmentEffect.DashExtraCharge, skillE);
            newCapacity += Mathf.Max(0, Mathf.RoundToInt(extraCharges));
        }

        if (!eChargeStateInitialized)
        {
            eChargeCapacity = newCapacity;
            eCharges = newCapacity;
            eChargeStateInitialized = true;
        }
        else if (newCapacity > eChargeCapacity)
        {
            // 재획득 시 이미 회복 중인 추가 충전을 다시 대기열에 넣지 않는다.
            int missingCharges = newCapacity - eCharges - ePendingRechargeCharges -
                                 (eDeferredChargePending ? 1 : 0);
            QueueEChargeRecharge(Mathf.Max(0, missingCharges), GetERechargeInterval());
            eChargeCapacity = newCapacity;
        }
        else if (newCapacity < eChargeCapacity)
        {
            eChargeCapacity = newCapacity;
            eCharges = Mathf.Min(eCharges, eChargeCapacity);
        }

        // 순차 회복. 용량이 감소해도 대기 중인 부채와 기존 기한은 보존한다.
        while (ePendingRechargeCharges > 0 && Time.time >= eNextRechargeTime)
        {
            ePendingRechargeCharges--;
            if (eCharges < eChargeCapacity)
                eCharges++;

            if (ePendingRechargeCharges > 0)
                eNextRechargeTime += eRechargeInterval;
            else
            {
                eNextRechargeTime = 0f;
                eRechargeInterval = 0f;
            }
        }
    }

    private float GetERechargeInterval()
    {
        return skillE != null ? Mathf.Max(0f, skillE.Cooldown) : 0f;
    }

    private void ConsumeECharge(bool deferRecharge, float cooldown)
    {
        RefreshEChargeState();
        eCharges = Mathf.Max(0, eCharges - 1);

        if (deferRecharge)
        {
            eDeferredChargePending = true;
            return;
        }

        QueueEChargeRecharge(1, cooldown);
    }

    private void CompleteDeferredEChargeCooldown(float cooldown)
    {
        if (!eDeferredChargePending)
            return;

        eDeferredChargePending = false;
        QueueEChargeRecharge(1, cooldown);
    }

    private void QueueEChargeRecharge(int count, float cooldown)
    {
        if (count <= 0)
            return;

        bool wasEmpty = ePendingRechargeCharges == 0;
        ePendingRechargeCharges += count;
        if (wasEmpty)
        {
            eRechargeInterval = Mathf.Max(0f, cooldown);
            eNextRechargeTime = Time.time + eRechargeInterval;
        }
    }

    // 스킬 애니메이션
    private void PlayAnimation(PlayerSkillSO skill)
    {
        // 닌자 궁극기 준비 중이면 상단 애니메이션을 비우고 스킬 모션 재생.
        if (ninjaUltimate != null && ninjaUltimate.IsReady && animator != null)
            animator.CrossFadeInFixedTime("Empty", 0.05f, 1);
        stateManager?.ChangeState(PlayerState.Attack);

        if (animator != null && !string.IsNullOrWhiteSpace(skill.AnimationTrigger))
            animator.SetTrigger(skill.AnimationTrigger);
    }

    // 일시정지/사망 차단
    private bool CanCast()
    {
        return isActiveAndEnabled &&
               (GameManager.Instance == null || !GameManager.Instance.IsPaused) &&
               !IsPlayerDead;
    }

    // 스킬 모션 중 중복 시전 차단
    private bool IsSkillCasting()
    {
        if (Replica != null) return Replica.BlocksBasicAttack;
        // 이동 잠금·궁극기 돌진·채널링·Q/E/R 모션 중 중복 시전 방지.
        if (movementLocked)
            return true;
        if (ninjaUltimate != null && ninjaUltimate.IsDashing)
            return true;
        if (deferredCooldownSkill != null)
            return true;

        if (animator == null)
            return false;

        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
        if (IsSkillState(current))
            return true;

        return animator.IsInTransition(0) && IsSkillState(animator.GetNextAnimatorStateInfo(0));
    }

    // 스킬 상태명
    private static bool IsSkillState(AnimatorStateInfo state)
    {
        return state.IsName("SkillQ") ||
               state.IsName("SkillE") ||
               state.IsName("UltimateR");
    }
}
