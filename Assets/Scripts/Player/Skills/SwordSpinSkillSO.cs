using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 전사 Q: 전방 180도 부채꼴 범위를 빠르게 베는 스킬.
// 기존 SwordSpinSkill.asset의 참조와 필드는 그대로 유지한다.
[CreateAssetMenu(menuName = "Player/Skills/Sword Spin")]
public sealed class SwordSpinSkillSO : PlayerSkillSO
{
    [Header("Forward 180 Degree Slash")]
    [SerializeField, Min(0.1f)] private float radius = 3.8f;
    [SerializeField, Range(1f, 180f)] private float slashAngle = 180f;
    [SerializeField, Min(0f)] private float damageMultiplier = 2f;
    [SerializeField] private Vector3 centerOffset = new Vector3(0f, 0.8f, 0f);
    [SerializeField] private LayerMask enemyLayers = ~0;

    [Header("Animation & Timing")]
    [SerializeField, Min(0f)] private float hitDelay = 0.18f;
    [SerializeField, Min(0.05f)] private float castDuration = 0.52f;
    [SerializeField, Min(0.1f)] private float animationSpeed = 2.6f;
    // 캐릭터 모델 자체는 회전시키지 않는다. 공격 모션만 빠르게 재생한다.

    [Header("Original Spin VFX")]
    [SerializeField] private ParticleSystem spinVfxPrefab;
    // 구형 SwordSpinVFX는 360도 이펙트이므로 기본적으로 재생하지 않음.
    [SerializeField] private bool playOriginalSpinVfx = false;
    [SerializeField] private Vector3 vfxOffset = new Vector3(0f, 0.8f, 0f);
    [SerializeField, Min(0.1f)] private float spinVfxScale = 1.5f;

    [Header("Wide Slash VFX")]
    [SerializeField] private GameObject slashVfxPrefab;
    // X=좌/우, Y=높이, Z=캐릭터 전방 거리(로컬 좌표)
    [SerializeField] private Vector3 slashVfxOffset = new Vector3(0f, 0.15f, 0.8f);
    [SerializeField, Min(0.1f)] private float slashVfxScale = 1.5f;
    [SerializeField, Min(0.1f)] private float vfxLifetime = 1.6f;

    [Header("Hit VFX & Feedback")]
    [SerializeField] private GameObject hitVfxPrefab;
    [SerializeField, Min(0.1f)] private float hitVfxScale = 1f;
    [SerializeField, Min(0f)] private float cameraShake = 0.12f;
    [SerializeField] private AudioClip swingSfx;
    [SerializeField] private AudioClip impactSfx;

    [Header("Extra Visible Forward Slash Arc")]
    [SerializeField] private bool showSlashRing = true;
    [SerializeField] private Color ringColor = new Color(0.22f, 0.78f, 1f, 0.9f);
    [SerializeField, Min(0.05f)] private float ringDuration = 0.28f;
    [SerializeField, Min(0.01f)] private float ringWidth = 0.22f;

    public override bool Cast(PlayerSkillController owner, PlayerDamageType damageType)
    {
        if (owner == null || !NetworkHealth.CanWrite(owner.gameObject) ||
            !owner.isActiveAndEnabled || owner.IsPlayerDead ||
            owner.Stats == null || owner.DamageDealer == null)
            return false;

        PlayerCharacter character = owner.GetComponent<PlayerCharacter>();
        if (character == null || !character.Is(PlayerCharacterId.Sword))
            return false;

        owner.StartCoroutine(ExecuteSpin(owner, damageType));
        return true;
    }

    private IEnumerator ExecuteSpin(PlayerSkillController owner, PlayerDamageType damageType)
    {
        // Cast()가 실행된 뒤 PlayerSkillController에서 SkillQ 트리거를 넣기 때문에
        // 한 프레임 기다린 뒤 재생 속도와 모션 시작점을 설정한다.
        bool movementWasLocked = owner.IsMovementLocked;
        owner.SetMovementLocked(true);

        Animator animator = owner.GetComponentInChildren<Animator>();
        float previousAnimatorSpeed = animator != null ? animator.speed : 1f;
        GameObject ringObject = null;
        Material ringMaterial = null;
        LineRenderer slashRing = null;
        float ringStartedAt = -1f;

        try
        {
            yield return null;
            if (owner == null || !owner.isActiveAndEnabled || owner.IsPlayerDead)
                yield break;

            if (animator != null && animator.HasState(0, Animator.StringToHash("Base Layer.SkillQ")))
            {
                animator.ResetTrigger("SkillQ");
                animator.speed = animationSpeed;
                animator.Play("Base Layer.SkillQ", 0, 0f);
            }

            NetworkSkillPresentation.PlaySound(owner.gameObject, swingSfx);

            float elapsed = 0f;
            bool hitApplied = false;
            float activeDuration = Mathf.Max(castDuration, hitDelay + 0.05f);

            while (elapsed < activeDuration)
            {
                if (owner == null || !owner.isActiveAndEnabled || owner.IsPlayerDead)
                    yield break;

                if (Time.timeScale <= 0f ||
                    (GameManager.Instance != null && GameManager.Instance.IsPaused))
                {
                    yield return null;
                    continue;
                }

                if (!hitApplied && elapsed >= hitDelay)
                {
                    hitApplied = true;
                    ringStartedAt = elapsed;

                    PlayerAugments augments = owner.GetComponent<PlayerAugments>();
                    float rangeBonus = augments != null
                        ? augments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillRange, this)
                        : 0f;
                    float effectiveRadius = radius * Mathf.Max(0f, 1f + rangeBonus);
                    float rangeVisualScale = effectiveRadius / Mathf.Max(0.1f, radius);

                    // 기존 둥근 회전 VFX는 요청 시에만 사용한다.
                    if (playOriginalSpinVfx && spinVfxPrefab != null)
                    {
                        SpawnVfx(owner, spinVfxPrefab.gameObject,
                            owner.transform.position + vfxOffset,
                            owner.transform.rotation,
                            spinVfxScale * rangeVisualScale);
                    }

                    // 전방향 Slash prefab(Charge slash blue 등) 권장.
                    // 오프셋은 플레이어 로컬 방향을 기준으로 계산한다.
                    SpawnVfx(owner, slashVfxPrefab,
                        owner.transform.position + owner.transform.TransformDirection(slashVfxOffset),
                        owner.transform.rotation,
                        slashVfxScale * rangeVisualScale);

                    Vector3 facing = owner.transform.forward;
                    facing.y = 0f;
                    facing = facing.sqrMagnitude > 0.0001f
                        ? facing.normalized : Vector3.forward;

                    if (showSlashRing)
                    {
                        // 360도 원이 아닌 캐릭터 전방 180도 반원형 검격 이펙트.
                        slashRing = CreateRing(
                            owner.transform.position + new Vector3(0f, 0.32f, 0f),
                            facing, out ringObject, out ringMaterial);
                        if (slashRing != null)
                        {
                            UpdateRing(slashRing, effectiveRadius, 0f);
                            NetworkSkillPresentation.TrackLine(owner.gameObject, slashRing);
                        }
                    }

                    int actualHitCount = DealSpinDamage(
                        owner, damageType,
                        owner.transform.position + centerOffset,
                        effectiveRadius, facing);

                    if (actualHitCount > 0)
                    {
                        NetworkSkillPresentation.PlaySound(owner.gameObject, impactSfx);
                        NetworkSkillPresentation.Shake(owner.gameObject, cameraShake);
                    }
                }

                if (slashRing != null && ringStartedAt >= 0f)
                {
                    float t = Mathf.Clamp01((elapsed - ringStartedAt) / ringDuration);
                    PlayerAugments augments = owner.GetComponent<PlayerAugments>();
                    float rangeBonus = augments != null
                        ? augments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillRange, this)
                        : 0f;
                    float effectiveRadius = radius * Mathf.Max(0f, 1f + rangeBonus);
                    UpdateRing(slashRing, effectiveRadius, t);
                }

                elapsed += Time.deltaTime;

                yield return null;
            }
        }
        finally
        {
            if (ringObject != null)
                Destroy(ringObject);
            if (ringMaterial != null)
                Destroy(ringMaterial);

            if (animator != null)
            {
                animator.speed = previousAnimatorSpeed;
                animator.ResetTrigger("SkillQ");
            }

            if (owner != null)
            {
                owner.SetMovementLocked(owner.IsPlayerDead ? false : movementWasLocked);

                if (owner.isActiveAndEnabled && !owner.IsPlayerDead)
                {
                    if (animator != null && IsQActive(animator))
                        animator.CrossFadeInFixedTime("Base Layer.Locomotion", 0.05f, 0);

                    owner.GetComponent<PlayerController>()?.EndAttackState();
                }
            }
        }
    }

    private int DealSpinDamage(
        PlayerSkillController owner, PlayerDamageType damageType,
        Vector3 center, float effectiveRadius, Vector3 facing)
    {
        PlayerAugments augments = owner.GetComponent<PlayerAugments>();
        float damageBonus = augments != null
            ? augments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillDamage, this)
            : 0f;
        int damage = Mathf.Max(0, Mathf.RoundToInt(
            owner.Stats.AttackDamage * damageMultiplier * Mathf.Max(0f, 1f + damageBonus)));
        if (damage <= 0)
            return 0;

        Collider[] hits = Physics.OverlapSphere(
            center, effectiveRadius, enemyLayers, QueryTriggerInteraction.Collide);
        HashSet<EnemyHealth> damagedEnemies = new HashSet<EnemyHealth>();
        int hitCount = 0;

        foreach (Collider hit in hits)
        {
            if (hit == null)
                continue;

            EnemyHealth enemy = hit.GetComponentInParent<EnemyHealth>();
            if (enemy == null || enemy.IsDead || damagedEnemies.Contains(enemy))
                continue;

            // 캐릭터가 보는 방향의 앞쪽 반원(180도)에 있는 적만 피해 처리.
            // Y를 제거해 몬스터 키 차이 때문에 측면 판정이 달라지지 않게 한다.
            Vector3 toEnemy = enemy.transform.position - owner.transform.position;
            toEnemy.y = 0f;
            if (toEnemy.sqrMagnitude > 0.0001f)
            {
                float minDot = Mathf.Cos(slashAngle * 0.5f * Mathf.Deg2Rad);
                if (Vector3.Dot(facing, toEnemy.normalized) < minDot - 0.0001f)
                    continue;
            }

            // 앞쪽에 있는 같은 적을 여러 콜라이더로 두 번 공격하지 않음.
            damagedEnemies.Add(enemy);
            Vector3 hitPosition = hit.bounds.center;
            int dealt = owner.DamageDealer.DealDamage(
                enemy, damage, damageType, owner.transform.position);
            if (dealt <= 0)
                continue;

            hitCount++;
            SpawnVfx(owner, hitVfxPrefab, hitPosition, Quaternion.identity, hitVfxScale);
        }

        return hitCount;
    }

    private void SpawnVfx(PlayerSkillController owner, GameObject prefab,
        Vector3 position, Quaternion rotation, float scale)
    {
        if (prefab == null)
            return;

        GameObject instance = Instantiate(
            prefab, position, rotation * prefab.transform.rotation);
        instance.transform.localScale *= scale;

        // 원본 프리팹은 수정하지 않고 생성된 이펙트만 1회 재생.
        foreach (ParticleSystem particle in instance.GetComponentsInChildren<ParticleSystem>(true))
        {
            ParticleSystem.MainModule main = particle.main;
            main.loop = false;
            particle.Clear(false);
            particle.Play(false);
        }

        NetworkSkillPresentation.Track(owner.gameObject, instance, prefab, false, false);
        Destroy(instance, vfxLifetime);
    }

    private LineRenderer CreateRing(
        Vector3 worldPosition, Vector3 facing,
        out GameObject ringObject, out Material material)
    {
        ringObject = null;
        material = null;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null)
            return null;

        ringObject = new GameObject("SwordQ_ForwardSlashArc");
        ringObject.transform.position = worldPosition;
        ringObject.transform.rotation = Quaternion.LookRotation(facing, Vector3.up);
        LineRenderer line = ringObject.AddComponent<LineRenderer>();
        material = new Material(shader);
        line.sharedMaterial = material;
        line.useWorldSpace = false;
        line.loop = false;
        line.positionCount = 65;
        line.numCornerVertices = 3;
        line.numCapVertices = 3;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.startColor = ringColor;
        line.endColor = ringColor;
        line.widthMultiplier = ringWidth;
        return line;
    }

    private void UpdateRing(LineRenderer line, float effectiveRadius, float t)
    {
        float currentRadius = Mathf.Lerp(effectiveRadius * 0.65f, effectiveRadius, t);
        float alpha = ringColor.a * (1f - t);
        Color color = new Color(ringColor.r, ringColor.g, ringColor.b, alpha);
        line.startColor = color;
        line.endColor = color;
        line.widthMultiplier = ringWidth * (1f - t * 0.65f);

        int count = line.positionCount;
        for (int i = 0; i < count; i++)
        {
            // -90°(왼쪽) ~ +90°(오른쪽), forward(+Z) 중심 반원.
            float angle = Mathf.Lerp(-slashAngle * 0.5f, slashAngle * 0.5f,
                i / (float)(count - 1)) * Mathf.Deg2Rad;
            line.SetPosition(i, new Vector3(
                Mathf.Sin(angle) * currentRadius, 0f,
                Mathf.Cos(angle) * currentRadius));
        }
    }

    private static bool IsQActive(Animator animator)
    {
        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
        if (current.IsName("SkillQ") || current.IsName("Base Layer.SkillQ"))
            return true;

        if (!animator.IsInTransition(0))
            return false;

        AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
        return next.IsName("SkillQ") || next.IsName("Base Layer.SkillQ");
    }

    private void OnValidate()
    {
        radius = Mathf.Max(0.1f, radius);
        slashAngle = Mathf.Clamp(slashAngle, 1f, 180f);
        damageMultiplier = Mathf.Max(0f, damageMultiplier);
        hitDelay = Mathf.Max(0f, hitDelay);
        castDuration = Mathf.Max(hitDelay + 0.05f, castDuration);
        animationSpeed = Mathf.Max(0.1f, animationSpeed);
        spinVfxScale = Mathf.Max(0.1f, spinVfxScale);
        slashVfxScale = Mathf.Max(0.1f, slashVfxScale);
        hitVfxScale = Mathf.Max(0.1f, hitVfxScale);
        vfxLifetime = Mathf.Max(0.1f, vfxLifetime);
        ringDuration = Mathf.Max(0.05f, ringDuration);
        ringWidth = Mathf.Max(0.01f, ringWidth);
    }
}
