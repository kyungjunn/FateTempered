using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 전사 R: 빠른 연속 참격 4회 + 대형 광역 VFX.
// 궁극기 게이지 확인/소비는 PlayerSkillController가 담당한다.
[CreateAssetMenu(menuName = "Player/Skills/Sword Ultimate Slash")]
public sealed class SwordUltimateSlashSO : PlayerSkillSO
{
    [Header("Area Damage")]
    [SerializeField, Min(0.1f)] private float radius = 7f;
    [SerializeField, Min(0f)] private float damageMultiplier = 4f;
    [SerializeField] private Vector3 attackCenterOffset = new Vector3(0f, 0.8f, 0f);
    [SerializeField] private LayerMask enemyLayers = ~0;

    [Header("Rapid Slashes")]
    [SerializeField, Range(1, 8)] private int slashCount = 4;
    [SerializeField, Min(0.06f)] private float slashInterval = 0.24f;
    [SerializeField, Min(0f)] private float impactDelay = 0.12f;
    [SerializeField, Min(0f)] private float recoveryTime = 0.16f;
    [SerializeField, Min(0.1f)] private float slashAnimationSpeed = 5.5f;

    [Header("VFX")]
    [SerializeField] private GameObject slashVfxPrefab;
    [SerializeField] private Vector3 slashVfxOffset = new Vector3(0f, 0.15f, 0f);
    [SerializeField, Min(0.1f)] private float slashVfxScale = 2.2f;
    [SerializeField, Min(1f)] private float finalSlashScale = 1.3f;
    [SerializeField] private GameObject hitVfxPrefab;
    [SerializeField, Min(0.1f)] private float hitVfxScale = 1f;
    [SerializeField, Min(0.1f)] private float vfxLifetime = 1.6f;

    [Header("SFX")]
    [SerializeField] private AudioClip swingSfx;
    [SerializeField] private AudioClip impactSfx;

    public override bool Cast(PlayerSkillController owner, PlayerDamageType damageType)
    {
        if (owner == null || !NetworkHealth.CanWrite(owner.gameObject) ||
            !owner.isActiveAndEnabled || owner.IsPlayerDead ||
            owner.Stats == null || owner.DamageDealer == null)
            return false;

        PlayerCharacter character = owner.GetComponent<PlayerCharacter>();
        if (character == null || !character.Is(PlayerCharacterId.Sword))
            return false;

        owner.StartSkillRoutine(ExecuteUltimate(owner, damageType));
        return true;
    }

    private IEnumerator ExecuteUltimate(PlayerSkillController owner, PlayerDamageType damageType)
    {
        bool previousMovementLock = owner.IsMovementLocked;
        owner.SetMovementLocked(true);

        Animator animator = owner.GetComponentInChildren<Animator>();
        float previousAnimationSpeed = animator != null ? animator.speed : 1f;

        // 공격력 x4의 기존 총 피해를 4회로 나눈다. 네 번 모두 맞아도 총 피해는 그대로.
        PlayerAugments augments = owner.GetComponent<PlayerAugments>();
        float damageBonus = augments != null
            ? augments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillDamage, this)
            : 0f;
        float rangeBonus = augments != null
            ? augments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillRange, this)
            : 0f;
        float effectiveRadius = radius * Mathf.Max(0f, 1f + rangeBonus);
        int totalDamage = Mathf.Max(0, Mathf.RoundToInt(
            owner.Stats.AttackDamage * damageMultiplier * Mathf.Max(0f, 1f + damageBonus)));

        try
        {
            // Cast가 먼저 호출되고, 그 다음에 PlayerSkillController가 UltimateR 트리거를 설정한다.
            // 한 프레임 대기하여 초기 트리거를 반영한 뒤 직접 반복 재생을 제어한다.
            yield return null;
            if (owner == null || !owner.isActiveAndEnabled || owner.IsPlayerDead)
                yield break;

            if (animator != null)
            {
                animator.ResetTrigger("UltimateR");
                animator.speed = slashAnimationSpeed;
            }

            int startedSlashes = 0;
            int landedSlashes = 0;
            float elapsed = 0f;
            float totalDuration =
                (slashCount - 1) * slashInterval + impactDelay + recoveryTime;

            while (elapsed < totalDuration || landedSlashes < slashCount)
            {
                if (owner == null || !owner.isActiveAndEnabled || owner.IsPlayerDead)
                    yield break;

                if (Time.timeScale <= 0f ||
                    (GameManager.Instance != null && GameManager.Instance.IsPaused))
                {
                    yield return null;
                    continue;
                }

                // 매 타격마다 모션을 처음부터 다시 재생한다.
                // Optional: UltimateR_Alt 상태가 있으면 번갈아 재생한다.
                while (startedSlashes < slashCount &&
                       elapsed >= startedSlashes * slashInterval)
                {
                    PlaySlashAnimation(animator, startedSlashes);
                    NetworkSkillPresentation.PlaySound(owner.gameObject, swingSfx);
                    startedSlashes++;
                }

                // 모션이 시작된 뒤 impactDelay 시점에 VFX와 피해를 맞춘다.
                while (landedSlashes < startedSlashes &&
                       elapsed >= landedSlashes * slashInterval + impactDelay)
                {
                    float scale = slashVfxScale;
                    if (landedSlashes == slashCount - 1)
                        scale *= finalSlashScale;

                    // 연속 베기의 이펙트 방향을 매번 회전해 주변 전체를 쓸어낸다.
                    Quaternion slashRotation = owner.transform.rotation *
                        Quaternion.Euler(0f, 360f * landedSlashes / slashCount, 0f);
                    SpawnVfx(
                        owner,
                        slashVfxPrefab,
                        owner.transform.position + slashVfxOffset,
                        slashRotation,
                        scale);

                    // 중간 타격은 일부 피해, 모든 타격 적중 시 총 x4 피해.
                    int damageThisSlash = totalDamage / slashCount;
                    if (landedSlashes < totalDamage % slashCount)
                        damageThisSlash++;

                    if (damageThisSlash > 0)
                    {
                        DealAreaDamage(
                            owner,
                            damageType,
                            owner.transform.position + attackCenterOffset,
                            effectiveRadius,
                            damageThisSlash,
                            landedSlashes == slashCount - 1);
                    }

                    landedSlashes++;
                }

                elapsed += Time.deltaTime;
                yield return null;
            }
        }
        finally
        {
            if (animator != null)
            {
                animator.speed = previousAnimationSpeed;
                animator.ResetTrigger("UltimateR");
            }

            if (owner != null)
            {
                owner.SetMovementLocked(
                    owner.IsPlayerDead ? false : previousMovementLock);

                if (owner.isActiveAndEnabled && !owner.IsPlayerDead)
                {
                    if (animator != null)
                    {
                        // R 애니메이션은 자동 종료 Transition을 쓰지 않고 직접 복귀한다.
                        animator.CrossFadeInFixedTime("Base Layer.Locomotion", 0.06f, 0);
                    }
                    owner.GetComponent<PlayerController>()?.EndAttackState();
                }
            }
        }
    }

    private static void PlaySlashAnimation(Animator animator, int slashIndex)
    {
        if (animator == null)
            return;

        const string firstState = "UltimateR";
        const string alternateState = "UltimateR_Alt";
        bool canUseAlternate = slashIndex % 2 == 1 &&
            animator.HasState(0, Animator.StringToHash("Base Layer." + alternateState));

        // 같은 상태를 Play(..., 0f)하면 클립 재생 시간을 0으로 초기화한다.
        animator.Play("Base Layer." + (canUseAlternate ? alternateState : firstState), 0, 0f);
    }

    private void DealAreaDamage(
        PlayerSkillController owner,
        PlayerDamageType damageType,
        Vector3 center,
        float effectiveRadius,
        int damage,
        bool isFinalSlash)
    {
        Collider[] colliders = Physics.OverlapSphere(
            center, effectiveRadius, enemyLayers, QueryTriggerInteraction.Collide);

        // 매 참격에서 Collider가 여러 개인 적을 1번만 공격한다.
        // 다음 참격에서는 같은 적을 다시 공격할 수 있다.
        HashSet<EnemyHealth> hitThisSlash = new HashSet<EnemyHealth>();
        bool playedImpactSfx = false;

        foreach (Collider hit in colliders)
        {
            if (hit == null)
                continue;

            EnemyHealth enemy = hit.GetComponentInParent<EnemyHealth>();
            if (enemy == null || enemy.IsDead || !hitThisSlash.Add(enemy))
                continue;

            Vector3 hitPosition = hit.bounds.center;
            int actualDamage = owner.DamageDealer.DealDamage(
                enemy, damage, damageType, owner.transform.position);

            if (actualDamage <= 0)
                continue;

            SpawnVfx(owner, hitVfxPrefab, hitPosition, Quaternion.identity, hitVfxScale);

            // 기존 EnemyHealth가 피격음을 재생하므로 별도의 큰 적중음은 마무리에만 사용.
            if (isFinalSlash && !playedImpactSfx)
            {
                NetworkSkillPresentation.PlaySound(owner.gameObject, impactSfx);
                playedImpactSfx = true;
            }
        }
    }

    private void SpawnVfx(PlayerSkillController owner, GameObject prefab,
        Vector3 position, Quaternion rotation, float scale)
    {
        if (prefab == null)
            return;

        GameObject instance = Instantiate(
            prefab, position, rotation * prefab.transform.rotation);
        instance.transform.localScale *= scale;

        // 원본 에셋은 건드리지 않고, 생성된 이펙트만 반복 재생을 끈다.
        foreach (ParticleSystem particle in
                 instance.GetComponentsInChildren<ParticleSystem>(true))
        {
            ParticleSystem.MainModule main = particle.main;
            main.loop = false;
            particle.Clear(false);
            particle.Play(false);
        }

        NetworkSkillPresentation.Track(owner.gameObject, instance, prefab, false, false);
        Destroy(instance, vfxLifetime);
    }

    private void OnValidate()
    {
        radius = Mathf.Max(0.1f, radius);
        damageMultiplier = Mathf.Max(0f, damageMultiplier);
        slashCount = Mathf.Clamp(slashCount, 1, 8);
        slashInterval = Mathf.Max(0.06f, slashInterval);
        impactDelay = Mathf.Clamp(impactDelay, 0f, slashInterval);
        recoveryTime = Mathf.Max(0f, recoveryTime);
        slashAnimationSpeed = Mathf.Max(0.1f, slashAnimationSpeed);
        slashVfxScale = Mathf.Max(0.1f, slashVfxScale);
        finalSlashScale = Mathf.Max(1f, finalSlashScale);
        hitVfxScale = Mathf.Max(0.1f, hitVfxScale);
        vfxLifetime = Mathf.Max(0.1f, vfxLifetime);
    }
}
