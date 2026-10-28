using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 닌자 궁극기: 준비 버프 → 재시전 돌진 → 경로 폭풍 피해.
public sealed class NinjaUltimate : MonoBehaviour
{
    private const float MinimumDuration = 0.05f;
    private const float MinimumDistance = 0.1f;
    private const int MaximumTrailInstances = 256;

    [Header("References")]
    [SerializeField] private GameObject lightningPrefab;
    [SerializeField] private GameObject readyBodyAuraPrefab;
    [SerializeField] private Transform handAnchor;
    [SerializeField] private Animator animator;

    [Header("Ready")]
    [SerializeField, Min(MinimumDuration)] private float readyDuration = 5f;
    [SerializeField, Min(0f)] private float attackBonusRatio = 0.5f;
    [SerializeField, Min(0f)] private float moveBonusRatio = 0.3f;

    [Header("Dash and Storm")]
    [SerializeField, Min(MinimumDistance)] private float dashDistance = 7f;
    [SerializeField, Min(MinimumDuration)] private float dashDuration = 0.28f;
    [SerializeField, Min(MinimumDuration)] private float stormDuration = 3f;
    [SerializeField, Min(MinimumDistance)] private float stormRadius = 2f;
    [SerializeField, Min(MinimumDuration)] private float stormTickInterval = 0.5f;
    [SerializeField, Min(0f)] private float stormDamageMultiplier = 3f;
    [SerializeField, Min(MinimumDistance)] private float trailSpacing = 2f;
    [SerializeField] private LayerMask enemyLayers = ~0;

    private PlayerStats stats;
    private PlayerDamageDealer damageDealer;
    private PlayerSkillController skillController;
    private PlayerStateManager stateManager;
    private PlayerController playerController;
    private PlayerHealth playerHealth;
    private PlayerAugments playerAugments;

    private readonly List<GameObject> trailEffects = new List<GameObject>();
    private GameObject handEffect;
    private GameObject readyBodyAuraEffect;
    private GameObject endpointBurstEffect;
    private ParticleSystem[] endpointBurstParticles;
    private Coroutine dashAndStormRoutine;
    private bool readyActive;
    private bool isDashing;
    private float readyEndTime;
    private bool dashControlActive;
    private bool movementLockBeforeDash;

    public bool IsReady => readyActive && Time.time < readyEndTime && !IsPlayerDead();
    public bool IsDashing => isDashing && !IsPlayerDead();
    public bool IsUltimateActive =>
        isActiveAndEnabled && !IsPlayerDead() && (IsReady || isDashing || dashAndStormRoutine != null);

    private void Awake()
    {
        CacheComponents();
    }

    private void Update()
    {
        if (!NetworkAuthority.CanSimulate(gameObject)) return;
        // 사망 시 정리, 준비 시간이 끝나면 버프 해제.
        if (IsPlayerDead())
        {
            Cancel();
            return;
        }

        if (readyActive && Time.time >= readyEndTime)
            ExpireReadyState();

        if (endpointBurstEffect != null && !IsPaused() && !IsEndpointBurstPlaying())
            ClearEndpointBurstEffect();
    }

    private void LateUpdate()
    {
        // 준비 중 손의 번개 이펙트를 손 위치에 동기화.
        if (readyActive && handEffect != null && handAnchor != null)
        {
            handEffect.transform.SetPositionAndRotation(handAnchor.position, handAnchor.rotation);
        }
    }

    private void OnDisable()
    {
        Cancel();
    }

    public bool TryActivate()
    {
        if (!NetworkAuthority.CanSimulate(gameObject)) return false;
        // 참조/상태 확인 후 손 이펙트 생성.
        CacheComponents();

        if (readyActive && Time.time >= readyEndTime)
            ExpireReadyState();

        if (!CanUseAbility() || readyActive || isDashing || dashAndStormRoutine != null ||
            !HasRequiredReferences())
        {
            return false;
        }

        GameObject effect = SpawnEffect(handAnchor.position, handAnchor.rotation, handAnchor.parent, Vector3.one, true);
        if (effect == null)
            return false;

        float attackBonus = stats.AttackDamage * SafeNonNegative(attackBonusRatio, 0f);
        // 현재 능력치를 기준으로 임시 공격력/이동속도 보너스 산출.
        float moveBonus = stats.MoveSpeed * SafeNonNegative(moveBonusRatio, 0f);

        handEffect = effect;
        readyBodyAuraEffect = Instantiate(readyBodyAuraPrefab, transform, false);
        readyBodyAuraEffect.transform.localPosition = Vector3.up;
        readyBodyAuraEffect.transform.localRotation = Quaternion.identity;
        readyBodyAuraEffect.transform.localScale = Vector3.one * 0.75f;
        foreach (ParticleSystem particle in readyBodyAuraEffect.GetComponentsInChildren<ParticleSystem>(true))
        {
            particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = particle.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
        }
        ConfigureParticles(readyBodyAuraEffect, true);
        NetworkSkillPresentation.Track(gameObject, readyBodyAuraEffect, readyBodyAuraPrefab, true, true);
        // 준비 종료 시각, 버프, 상단 애니메이션 설정.
        readyEndTime = Time.time + SafeAtLeast(readyDuration, MinimumDuration, 5f);
        readyActive = true;
        stats.SetTemporaryBonus(this, AugmentType.AttackDamage, attackBonus);
        stats.SetTemporaryBonus(this, AugmentType.MoveSpeed, moveBonus);
        animator.CrossFadeInFixedTime("UltimateReady", 0.05f, 1);
        return true;
    }

    public bool TryRecast()
    {
        if (!NetworkAuthority.CanSimulate(gameObject)) return false;
        // 준비 상태·조작 가능 여부를 확인한 뒤 재시전.
        CacheComponents();

        if (readyActive && Time.time >= readyEndTime)
            ExpireReadyState();

        if (!IsReady || !CanUseAbility() || isDashing || dashAndStormRoutine != null ||
            !HasRequiredReferences() || skillController.IsMovementLocked)
        {
            return false;
        }

        float buffedAttackDamage = stats.AttackDamage;
        // 준비 버프가 사라지기 전 폭풍 피해를 확정.
        int stormDamage = CalculateStormDamage(buffedAttackDamage);
        Vector3 direction = transform.forward;
        direction.y = 0f;
        if (direction.sqrMagnitude <= Mathf.Epsilon)
            direction = Vector3.forward;
        else
            direction.Normalize();

        ExpireReadyState();

        // 기존 이동 잠금 상태 보관 후 돌진 중 조작 잠금.
        movementLockBeforeDash = skillController.IsMovementLocked;
        dashControlActive = true;
        skillController.SetMovementLocked(true);
        isDashing = true;
        stateManager.ChangeState(PlayerState.Attack);
        animator.CrossFadeInFixedTime("UltimateDash", 0.05f, 1);

        dashAndStormRoutine = StartCoroutine(DashAndStorm(direction, buffedAttackDamage, stormDamage));
        return true;
    }

    public void Cancel()
    {
        // 진행 중 코루틴 중단 → 버프/이펙트/이동 상태 정리.
        CacheComponents();

        Coroutine activeRoutine = dashAndStormRoutine;
        dashAndStormRoutine = null;
        if (activeRoutine != null)
            StopCoroutine(activeRoutine);

        readyActive = false;
        readyEndTime = 0f;
        stats?.RemoveTemporaryBonuses(this);
        DestroyEffect(ref handEffect);
        DestroyEffect(ref readyBodyAuraEffect);
        ClearEndpointBurstEffect();
        FinishDashControl();
        ClearTrailEffects();
        ClearUltimateAnimationIfActive();
    }

    private IEnumerator DashAndStorm(Vector3 direction, float buffedAttackDamage, int stormDamage)
    {
        // 돌진 시작 지점, 제한된 거리/시간, 이펙트 간격 준비.
        Vector3 dashStart = transform.position;
        float distance = SafeAtLeast(dashDistance, MinimumDistance, 7f);
        float duration = SafeAtLeast(dashDuration, MinimumDuration, 0.28f);
        float nextTrailDistance = 0f;
        int trailCount = 0;
        HashSet<EnemyHealth> hitThisTick = new HashSet<EnemyHealth>();

        try
        {
            // 공용 이동은 위치만 변경. 이동 거리마다 번개 흔적 생성.
            yield return SkillDashMovement.Move(transform, direction, distance, duration,
                (start, end, traveledDistance) =>
                {
                    SpawnTrailEffects(dashStart, direction, distance, traveledDistance,
                        ref nextTrailDistance, ref trailCount);
                }, IsPaused, IsPlayerDead);

            if (IsPlayerDead())
                yield break;

            Vector3 dashEnd = transform.position;
            ApplyEndpointBurst(dashEnd, buffedAttackDamage, hitThisTick);
            // 돌진 종료 위치 확정 후 조작 복원. 경로 전체에 폭풍 유지.
            FinishDashControl();

            float stormEndTime = Time.time + SafeAtLeast(stormDuration, MinimumDuration, 3f);
            float nextTickTime = Time.time;
            float tickInterval = SafeAtLeast(stormTickInterval, MinimumDuration, 0.5f);
            while (Time.time < stormEndTime)
            {
                if (IsPlayerDead())
                    yield break;

                if (IsPaused())
                {
                    yield return null;
                    continue;
                }

                if (Time.time >= nextTickTime)
                {
                    // 틱마다 경로 안의 적을 새로 조회해 피해 적용.
                    float radius = GetAugmentedStormRadius();
                    foreach (GameObject effect in trailEffects)
                    {
                        if (effect != null)
                            effect.transform.localScale = Vector3.one * (radius * 5f);
                    }
                    ApplyStormDamage(dashStart, dashEnd, radius, stormDamage, hitThisTick);
                    nextTickTime = Time.time + tickInterval;
                }

                yield return null;
            }
        }
        finally
        {
            // 사망·중단·정상 종료 모두 조작과 이펙트 정리.
            FinishDashControl();
            ClearTrailEffects();
            ClearUltimateAnimationIfActive();
            dashAndStormRoutine = null;
        }
    }

    private void SpawnTrailEffects(
        Vector3 dashStart,
        Vector3 direction,
        float distance,
        float traveledDistance,
        ref float nextTrailDistance,
        ref int trailCount)
    {
        // 지나온 거리만큼 간격을 채워 이펙트 생성; 최대 개수 제한.
        float spacing = SafeAtLeast(trailSpacing, MinimumDistance, 2f);
        while (nextTrailDistance <= traveledDistance && nextTrailDistance <= distance &&
               trailCount < MaximumTrailInstances)
        {
            Vector3 position = dashStart + direction * nextTrailDistance;
            GameObject effect = SpawnEffect(position, transform.rotation, null,
                Vector3.one * (GetAugmentedStormRadius() * 5f), false);
            if (effect != null)
                trailEffects.Add(effect);

            trailCount++;
            nextTrailDistance += spacing;
        }
    }

    private void ApplyStormDamage(
        Vector3 dashStart,
        Vector3 dashEnd,
        float radius,
        int damage,
        HashSet<EnemyHealth> hitThisTick)
    {
        // 틱당 적 1회 피해: 여러 콜라이더를 가진 적 중복 방지.
        if (damage <= 0 || damageDealer == null)
            return;

        hitThisTick.Clear();
        Collider[] overlaps = Physics.OverlapCapsule(
            dashStart, dashEnd, radius, enemyLayers, QueryTriggerInteraction.Collide);

        for (int i = 0; i < overlaps.Length; i++)
        {
            EnemyHealth enemy = overlaps[i].GetComponentInParent<EnemyHealth>();
            if (enemy != null && !enemy.IsDead && hitThisTick.Add(enemy))
                damageDealer.DealDamage(enemy, damage, PlayerDamageType.Skill);
        }
    }

    private void ApplyEndpointBurst(
        Vector3 endpoint,
        float buffedAttackDamage,
        HashSet<EnemyHealth> hitThisTick)
    {
        // Read at application so removing the augment during the dash cancels the pending burst.
        float damageMultiplier = GetCombatAugmentValue(PlayerCombatAugmentEffect.UltimateEndBurst);
        if (damageMultiplier <= 0f || IsPlayerDead())
            return;

        float radius = GetAugmentedStormRadius();
        int damage = CalculateBurstDamage(buffedAttackDamage, damageMultiplier);
        SpawnEndpointBurst(endpoint, radius);
        ApplyEndpointBurstDamage(endpoint, radius, damage, hitThisTick);
    }

    private void ApplyEndpointBurstDamage(
        Vector3 endpoint,
        float radius,
        int damage,
        HashSet<EnemyHealth> hitThisTick)
    {
        if (damage <= 0 || damageDealer == null)
            return;

        hitThisTick.Clear();
        Collider[] overlaps = Physics.OverlapSphere(
            endpoint, radius, enemyLayers, QueryTriggerInteraction.Collide);

        for (int i = 0; i < overlaps.Length; i++)
        {
            EnemyHealth enemy = overlaps[i].GetComponentInParent<EnemyHealth>();
            if (enemy != null && !enemy.IsDead && hitThisTick.Add(enemy))
                damageDealer.DealDamage(enemy, damage, PlayerDamageType.Skill);
        }
    }

    private void SpawnEndpointBurst(Vector3 endpoint, float radius)
    {
        ClearEndpointBurstEffect();
        float effectScale = SafeAtLeast(radius, MinimumDistance, 2f) * 5f;
        endpointBurstEffect = SpawnEffect(
            endpoint, transform.rotation, null, Vector3.one * effectScale, true, false);
        endpointBurstParticles = endpointBurstEffect != null
            ? endpointBurstEffect.GetComponentsInChildren<ParticleSystem>(true)
            : null;
    }

    private bool IsEndpointBurstPlaying()
    {
        if (endpointBurstParticles == null)
            return false;

        for (int i = 0; i < endpointBurstParticles.Length; i++)
        {
            if (endpointBurstParticles[i] != null && endpointBurstParticles[i].IsAlive(true))
                return true;
        }

        return false;
    }

    private void ClearEndpointBurstEffect()
    {
        DestroyEffect(ref endpointBurstEffect);
        endpointBurstParticles = null;
    }

    private GameObject SpawnEffect(
        Vector3 position,
        Quaternion rotation,
        Transform parent,
        Vector3 scale,
        bool enableBloom,
        bool loopParticles = true)
    {
        // 이펙트 생성 후 부모/크기/블룸/파티클 상태 적용.
        if (lightningPrefab == null)
            return null;

        GameObject effect = Instantiate(lightningPrefab, position, rotation);
        if (parent != null)
            effect.transform.SetParent(parent, true);

        effect.transform.localScale = scale;
        foreach (var volume in effect.GetComponentsInChildren<UnityEngine.Rendering.Volume>(true))
            volume.enabled = enableBloom;
        ConfigureParticles(effect, loopParticles);
        NetworkSkillPresentation.Track(gameObject, effect, lightningPrefab, loopParticles, parent != null);
        return effect;
    }

    private static void ConfigureParticles(GameObject effect, bool loopParticles)
    {
        ParticleSystem[] particles = effect.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < particles.Length; i++)
        {
            ParticleSystem particle = particles[i];
            ParticleSystem.MainModule main = particle.main;
            main.loop = loopParticles;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.useUnscaledTime = false;
            particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particle.Play(true);
        }
    }

    private int CalculateStormDamage(float buffedAttackDamage)
    {
        float multiplier = SafeNonNegative(stormDamageMultiplier, 3f);
        if (multiplier <= 0f)
            return 0;

        return Mathf.Max(1, Mathf.RoundToInt(buffedAttackDamage * multiplier));
    }

    private int CalculateBurstDamage(float buffedAttackDamage, float multiplier)
    {
        multiplier = SafeNonNegative(multiplier, 0f);
        if (multiplier <= 0f)
            return 0;

        return Mathf.Max(1, Mathf.RoundToInt(buffedAttackDamage * multiplier));
    }

    private float GetAugmentedStormRadius()
    {
        // Sample live so removing the augment stops affecting pending storm ticks and burst radius.
        float baseRadius = SafeAtLeast(stormRadius, MinimumDistance, 2f);
        float rangeBonus = GetCombatAugmentValue(PlayerCombatAugmentEffect.UltimateStormRange);
        return SafeAtLeast(baseRadius * (1f + rangeBonus), MinimumDistance, baseRadius);
    }

    private float GetCombatAugmentValue(PlayerCombatAugmentEffect effect)
    {
        return playerAugments != null
            ? SafeNonNegative(playerAugments.GetCombatAugmentValue(effect, null), 0f)
            : 0f;
    }

    private bool CanUseAbility()
    {
        return isActiveAndEnabled && !IsPaused() && !IsPlayerDead();
    }

    private bool HasRequiredReferences()
    {
        return lightningPrefab != null && readyBodyAuraPrefab != null && handAnchor != null && animator != null && animator.layerCount > 1 &&
               stats != null && damageDealer != null && skillController != null && stateManager != null;
    }

    private void CacheComponents()
    {
        if (stats == null)
            stats = GetComponent<PlayerStats>();
        if (damageDealer == null)
            damageDealer = GetComponent<PlayerDamageDealer>();
        if (skillController == null)
            skillController = GetComponent<PlayerSkillController>();
        if (stateManager == null)
            stateManager = GetComponent<PlayerStateManager>();
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
        if (playerAugments == null)
            playerAugments = GetComponent<PlayerAugments>();
        if (animator == null)
            animator = GetComponentInChildren<Animator>(true);
    }

    private bool IsPlayerDead()
    {
        return (stateManager != null && stateManager.CurrentState == PlayerState.Dead) ||
               (playerHealth != null && playerHealth.IsDead);
    }

    private static bool IsPaused()
    {
        return GameManager.Instance != null && GameManager.Instance.IsPaused;
    }

    private void ExpireReadyState()
    {
        // 준비 만료 또는 재시전 시 임시 보너스와 손 이펙트 제거.
        readyActive = false;
        readyEndTime = 0f;
        stats?.RemoveTemporaryBonuses(this);
        DestroyEffect(ref handEffect);
        DestroyEffect(ref readyBodyAuraEffect);
        ClearUltimateAnimationIfActive();
    }

    private void FinishDashControl()
    {
        // 돌진 전 이동 잠금 복원 및 살아 있으면 공격 상태 해제.
        bool restoreState = dashControlActive;
        isDashing = false;
        ClearUltimateAnimationIfActive();
        if (!restoreState)
            return;

        dashControlActive = false;
        if (skillController != null)
            skillController.SetMovementLocked(movementLockBeforeDash);

        if (IsPlayerDead())
            return;

        if (playerController != null)
            playerController.EndAttackState();
        else if (stateManager != null)
            stateManager.ChangeState(PlayerState.Idle);
    }

    private void ClearUltimateAnimationIfActive()
    {
        if (animator == null || animator.layerCount <= 1)
            return;

        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(1);
        bool transitioning = animator.IsInTransition(1);
        AnimatorStateInfo next = transitioning ? animator.GetNextAnimatorStateInfo(1) : default;

        if (transitioning && !IsUltimateAnimation(next))
            return;

        if (IsUltimateAnimation(current) || (transitioning && IsUltimateAnimation(next)))
            animator.CrossFadeInFixedTime("Empty", 0.05f, 1);
    }

    private static bool IsUltimateAnimation(AnimatorStateInfo state)
    {
        return state.IsName("UltimateReady") || state.IsName("UltimateDash");
    }

    private void ClearTrailEffects()
    {
        // 돌진 경로에 남은 번개 오브젝트 일괄 제거.
        for (int i = 0; i < trailEffects.Count; i++)
        {
            if (trailEffects[i] != null)
                Destroy(trailEffects[i]);
        }

        trailEffects.Clear();
    }

    private static void DestroyEffect(ref GameObject effect)
    {
        if (effect != null)
            Destroy(effect);
        effect = null;
    }

    private void OnValidate()
    {
        readyDuration = SafeAtLeast(readyDuration, MinimumDuration, 5f);
        attackBonusRatio = SafeNonNegative(attackBonusRatio, 0.5f);
        moveBonusRatio = SafeNonNegative(moveBonusRatio, 0.3f);
        dashDistance = SafeAtLeast(dashDistance, MinimumDistance, 7f);
        dashDuration = SafeAtLeast(dashDuration, MinimumDuration, 0.28f);
        stormDuration = SafeAtLeast(stormDuration, MinimumDuration, 3f);
        stormRadius = SafeAtLeast(stormRadius, MinimumDistance, 2f);
        stormTickInterval = SafeAtLeast(stormTickInterval, MinimumDuration, 0.5f);
        stormDamageMultiplier = SafeNonNegative(stormDamageMultiplier, 3f);
        trailSpacing = SafeAtLeast(trailSpacing, MinimumDistance, 2f);
    }

    private static float SafeAtLeast(float value, float minimum, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return fallback;
        return Mathf.Max(minimum, value);
    }

    private static float SafeNonNegative(float value, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return fallback;
        return Mathf.Max(0f, value);
    }
}
