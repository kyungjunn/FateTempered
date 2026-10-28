using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 전사 E: 적을 통과하며 돌진하고, 경로에 닿은 적에게 1회 피해를 준다.
[CreateAssetMenu(menuName = "Player/Skills/Sword Dash")]
public sealed class SwordDashSkillSO : PlayerSkillSO
{
    [Header("Dash")]
    [SerializeField, Min(0.1f)] private float distance = 5f;
    [SerializeField, Min(0.05f)] private float duration = 0.28f;

    [Header("Damage")]
    [SerializeField, Min(0)] private int dashDamage = 10;
    [SerializeField] private LayerMask enemyLayers = ~0;

    [Header("Obstacle Collision")]
    [SerializeField] private LayerMask obstacleLayers = ~0;
    [SerializeField, Min(0f)] private float collisionSkin = 0.08f;

    [Header("Dash VFX")]
    // ParticleSystem이 붙은 프리팹. 비어 있어도 코드에서 푸른 잔상은 생성한다.
    [SerializeField] private ParticleSystem dashVfxPrefab;
    [SerializeField] private Vector3 dashVfxOffset = new Vector3(0f, 0.15f, -0.15f);
    [SerializeField] private Material trailMaterial;
    [SerializeField] private Vector3 trailOffset = new Vector3(0f, 0.85f, -0.35f);

    [Header("Enemy Hit VFX")]
    // 예: Assets/Hits Effects FREE/Prefab/Basic Hit 2.prefab
    [SerializeField] private GameObject hitVfxPrefab;
    [SerializeField] private Vector3 hitVfxOffset = new Vector3(0f, 0.1f, 0f);
    [SerializeField, Range(0.1f, 3f)] private float hitVfxScale = 1f;

    [Header("Sound")]
    [SerializeField] private AudioClip dashStartSfx;
    [SerializeField] private AudioClip dashHitSfx;

    // 시전 순간부터 E 재충전 시작 (종료 알림에 의존하지 않음).
    public override bool DefersCooldown => false;

    public override bool Cast(PlayerSkillController owner, PlayerDamageType damageType)
    {
        if (owner == null || !NetworkAuthority.CanSimulate(owner.gameObject) ||
            !owner.isActiveAndEnabled || owner.IsPlayerDead ||
            owner.DamageDealer == null)
            return false;

        PlayerCharacter character = owner.GetComponent<PlayerCharacter>();
        CapsuleCollider capsule = owner.GetComponent<CapsuleCollider>();
        if (character == null || !character.Is(PlayerCharacterId.Sword) || capsule == null)
            return false;

        owner.StartSkillRoutine(Dash(owner, capsule, damageType));
        return true;
    }

    private IEnumerator Dash(PlayerSkillController owner, CapsuleCollider capsule,
        PlayerDamageType damageType)
    {
        Vector3 direction = owner.transform.forward;
        direction.y = 0f;
        direction = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.forward;

        // 동일 대쉬에서 같은 적에게 중복 피해를 주지 않기 위한 기록.
        HashSet<EnemyHealth> damagedEnemies = new HashSet<EnemyHealth>();
        HashSet<EnemyHealth> passThroughEnemies = new HashSet<EnemyHealth>();
        List<Collider> ignoredColliders = new List<Collider>();

        bool previousMovementLock = owner.IsMovementLocked;
        ParticleSystem dashVfx = null;
        TrailRenderer dashTrail = null;
        Material temporaryTrailMaterial = null;
        owner.SetMovementLocked(true);

        try
        {
            // 돌진 경로의 적은 미리 물리 충돌 해제. 실제 통과할 때만 피해 발생.
            IgnoreEnemiesAhead(owner.transform, capsule, direction,
                passThroughEnemies, ignoredColliders);

            // 먼지 파티클: 연결된 프리팹의 모든 활성 ParticleSystem을 실행한다.
            if (dashVfxPrefab != null)
            {
                dashVfx = Instantiate(dashVfxPrefab, owner.transform);
                dashVfx.transform.localPosition = dashVfxOffset;
                dashVfx.transform.localRotation = Quaternion.identity;

                foreach (ParticleSystem ps in dashVfx.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (!ps.gameObject.activeInHierarchy)
                        continue;

                    // 파티클이 현재 위치에 묶이지 않고 이동 자리에 남는다.
                    ParticleSystem.MainModule main = ps.main;
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                    ps.Clear(true);
                    ps.Play(true);
                }

                // 추가되는 런타임 Trail과 중복되지 않도록 프리팹의 Trail은 끈다.
                foreach (TrailRenderer trail in dashVfx.GetComponentsInChildren<TrailRenderer>(true))
                    trail.emitting = false;
                NetworkSkillPresentation.Track(owner.gameObject, dashVfx.gameObject,
                    dashVfxPrefab.gameObject, true, true);
            }

            // 프리팹 연결 누락이 있어도 반드시 하나의 잔상 생성.
            dashTrail = CreateDashTrail(owner.transform, out temporaryTrailMaterial);
            NetworkSkillPresentation.TrackTrail(owner.gameObject, dashTrail);
            NetworkSkillPresentation.PlaySound(owner.gameObject, dashStartSfx);

            // PlayerSkillController가 SkillE 트리거를 등록하도록 한 프레임 양보.
            yield return null;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (owner == null || !owner.isActiveAndEnabled || owner.IsPlayerDead)
                    yield break;

                if (Time.timeScale <= 0f ||
                    (GameManager.Instance != null && GameManager.Instance.IsPaused))
                {
                    yield return null;
                    continue;
                }

                float delta = Mathf.Min(Time.deltaTime, duration - elapsed);
                if (delta <= 0f)
                {
                    yield return null;
                    continue;
                }

                float requested = distance * (delta / duration);
                float allowed = GetSafeStep(owner.transform, capsule, direction, requested);

                // 이동 전 현재 위치~다음 위치를 검사. 적의 모든 콜라이더를 통과 처리하고
                // 실제 지나가는 적에게만 최초 1회 피해를 준다.
                DetectDashHits(owner, capsule, direction, allowed, damageType,
                    damagedEnemies, passThroughEnemies, ignoredColliders);

                if (allowed > 0f)
                    owner.transform.position += direction * allowed;

                elapsed += delta;
                if (allowed + 0.001f < requested)
                    break; // 적은 통과하지만 벽/지형 충돌 시에는 종료.

                yield return null;
            }
        }
        finally
        {
            // 대쉬가 끝나면 적과의 일반 물리 충돌을 다시 켠다.
            if (capsule != null && capsule.enabled && capsule.gameObject.activeInHierarchy)
            {
                foreach (Collider enemyCollider in ignoredColliders)
                {
                    if (enemyCollider != null && enemyCollider.enabled &&
                        enemyCollider.gameObject.activeInHierarchy)
                        Physics.IgnoreCollision(capsule, enemyCollider, false);
                }
            }

            if (dashVfx != null)
            {
                foreach (ParticleSystem ps in dashVfx.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps.gameObject.activeInHierarchy)
                        ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                }
                dashVfx.transform.SetParent(null, true);
                Destroy(dashVfx.gameObject, 1.5f);
            }

            if (dashTrail != null)
            {
                // 이미 생성된 Trail이 자연스럽게 사라진다.
                dashTrail.emitting = false;
                dashTrail.transform.SetParent(null, true);
                Destroy(dashTrail.gameObject, dashTrail.time + 0.2f);
            }
            if (temporaryTrailMaterial != null)
                Destroy(temporaryTrailMaterial, 1f);

            if (owner != null)
            {
                owner.SetMovementLocked(owner.IsPlayerDead ? false : previousMovementLock);

                if (owner.isActiveAndEnabled && !owner.IsPlayerDead)
                {
                    Animator animator = owner.GetComponentInChildren<Animator>();
                    if (animator != null)
                    {
                        animator.ResetTrigger("SkillE");
                        animator.CrossFadeInFixedTime("Locomotion", 0.05f, 0);
                    }

                    owner.GetComponent<PlayerController>()?.EndAttackState();
                }
            }
        }
    }

    // 충돌 무시만 미리 적용한다. 이 과정에서는 피해를 주지 않는다.
    private void IgnoreEnemiesAhead(Transform actor, CapsuleCollider capsule,
        Vector3 direction, HashSet<EnemyHealth> passThroughEnemies,
        List<Collider> ignoredColliders)
    {
        GetCapsule(actor, capsule, out Vector3 bottom, out Vector3 top, out float radius);

        foreach (Collider hit in Physics.OverlapCapsule(bottom, top, radius,
                     enemyLayers, QueryTriggerInteraction.Collide))
            IgnoreEnemyCollisions(hit, capsule, passThroughEnemies, ignoredColliders);

        foreach (RaycastHit hit in Physics.CapsuleCastAll(bottom, top, radius,
                     direction, distance, enemyLayers, QueryTriggerInteraction.Collide))
            IgnoreEnemyCollisions(hit.collider, capsule, passThroughEnemies, ignoredColliders);
    }

    // 시작 지점, 이동 구간, 도착 지점을 모두 검사해 빠른 대쉬에서도 적을 놓치지 않는다.
    private void DetectDashHits(PlayerSkillController owner, CapsuleCollider capsule,
        Vector3 direction, float stepDistance, PlayerDamageType damageType,
        HashSet<EnemyHealth> damagedEnemies, HashSet<EnemyHealth> passThroughEnemies,
        List<Collider> ignoredColliders)
    {
        GetCapsule(owner.transform, capsule,
            out Vector3 bottom, out Vector3 top, out float radius);

        foreach (Collider hit in Physics.OverlapCapsule(bottom, top, radius,
                     enemyLayers, QueryTriggerInteraction.Collide))
            HandleEnemyHit(hit, owner, capsule, damageType, damagedEnemies,
                passThroughEnemies, ignoredColliders);

        if (stepDistance <= 0f)
            return;

        foreach (RaycastHit hit in Physics.CapsuleCastAll(bottom, top, radius,
                     direction, stepDistance, enemyLayers, QueryTriggerInteraction.Collide))
            HandleEnemyHit(hit.collider, owner, capsule, damageType, damagedEnemies,
                passThroughEnemies, ignoredColliders);

        Vector3 offset = direction * stepDistance;
        foreach (Collider hit in Physics.OverlapCapsule(bottom + offset, top + offset, radius,
                     enemyLayers, QueryTriggerInteraction.Collide))
            HandleEnemyHit(hit, owner, capsule, damageType, damagedEnemies,
                passThroughEnemies, ignoredColliders);
    }

    private void HandleEnemyHit(Collider hit, PlayerSkillController owner,
        CapsuleCollider capsule, PlayerDamageType damageType,
        HashSet<EnemyHealth> damagedEnemies, HashSet<EnemyHealth> passThroughEnemies,
        List<Collider> ignoredColliders)
    {
        if (hit == null)
            return;

        EnemyHealth enemy = hit.GetComponentInParent<EnemyHealth>();
        if (enemy == null || enemy.IsDead)
            return;

        // 움직이는 적이 새로 경로에 들어왔을 때도 통과할 수 있도록 처리.
        IgnoreEnemyCollisions(hit, capsule, passThroughEnemies, ignoredColliders);

        if (dashDamage > 0 && damagedEnemies.Add(enemy))
        {
            // 적이 이 타격에 죽으면 Collider가 꺼지므로 타격 위치부터 저장한다.
            Vector3 hitPosition = hit.bounds.center + hitVfxOffset;
            int appliedDamage = owner.DamageDealer.DealDamage(
                enemy, dashDamage, damageType, owner.transform.position);
            if (appliedDamage > 0)
            {
                SpawnHitVfx(owner, hitPosition);
                // EnemyHealth에서도 기본 피격음을 재생하므로 같은 클립의 중복 재생 방지.
                if (dashHitSfx != null &&
                    (AudioManager.Instance == null ||
                     dashHitSfx != AudioManager.Instance.enemyHitClip))
                    NetworkSkillPresentation.PlaySound(owner.gameObject, dashHitSfx);
            }
        }
    }

    private void IgnoreEnemyCollisions(Collider hit, CapsuleCollider capsule,
        HashSet<EnemyHealth> passThroughEnemies, List<Collider> ignoredColliders)
    {
        if (hit == null)
            return;

        EnemyHealth enemy = hit.GetComponentInParent<EnemyHealth>();
        if (enemy == null || enemy.IsDead || !passThroughEnemies.Add(enemy))
            return;

        // 자식 히트박스를 포함한 적의 모든 Collider를 무시한다.
        foreach (Collider enemyCollider in enemy.GetComponentsInChildren<Collider>())
        {
            if (enemyCollider == null || !enemyCollider.enabled ||
                !enemyCollider.gameObject.activeInHierarchy || enemyCollider == capsule ||
                Physics.GetIgnoreCollision(capsule, enemyCollider))
                continue;

            Physics.IgnoreCollision(capsule, enemyCollider, true);
            ignoredColliders.Add(enemyCollider);
        }
    }

    // 적은 GetSafeStep에서 장애물로 취급하지 않지만, 벽/기둥 등은 그대로 차단한다.
    private float GetSafeStep(Transform actor, CapsuleCollider capsule,
        Vector3 direction, float requested)
    {
        if (requested <= 0f)
            return 0f;

        GetCapsule(actor, capsule, out Vector3 bottom, out Vector3 top, out float radius);
        float allowed = requested;

        foreach (RaycastHit hit in Physics.CapsuleCastAll(bottom, top, radius,
                     direction, requested + collisionSkin, obstacleLayers,
                     QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null ||
                hit.collider.transform.IsChildOf(actor) ||
                hit.collider.GetComponentInParent<EnemyHealth>() != null)
                continue;

            if (hit.normal.y > 0.7f) // 땅/완만한 경사면
                continue;

            allowed = Mathf.Min(allowed, Mathf.Max(0f, hit.distance - collisionSkin));
        }

        return allowed;
    }

    private static void GetCapsule(Transform actor, CapsuleCollider capsule,
        out Vector3 bottom, out Vector3 top, out float radius)
    {
        Vector3 scale = actor.lossyScale;
        radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)) * 0.9f;
        float height = Mathf.Max(capsule.height * Mathf.Abs(scale.y), radius * 2f);
        float halfSegment = Mathf.Max(0f, height * 0.5f - radius);
        Vector3 center = actor.TransformPoint(capsule.center);
        top = center + Vector3.up * halfSegment;
        bottom = center - Vector3.up * halfSegment +
            Vector3.up * Mathf.Min(0.12f, halfSegment * 2f);
    }

    // Sprite 기본 셰이더로도 보이는 런타임 잔상. 머티리얼 지정 시 해당 소재 우선.
    private TrailRenderer CreateDashTrail(Transform actor, out Material ownedMaterial)
    {
        ownedMaterial = null;

        GameObject trailObject = new GameObject("SwordDashTrail_Runtime");
        trailObject.transform.SetParent(actor, false);
        trailObject.transform.localPosition = trailOffset;
        trailObject.transform.localRotation = Quaternion.identity;

        TrailRenderer trail = trailObject.AddComponent<TrailRenderer>();
        trail.time = 0.27f;
        trail.minVertexDistance = 0.03f;
        trail.widthMultiplier = 0.65f;
        trail.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
        trail.alignment = LineAlignment.View;
        trail.numCornerVertices = 2;
        trail.numCapVertices = 2;
        trail.autodestruct = false;

        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.2f, 0.8f, 1f), 0f),
                new GradientColorKey(new Color(0.05f, 0.3f, 1f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(0.9f, 0f),
                new GradientAlphaKey(0f, 1f)
            });
        trail.colorGradient = gradient;

        if (trailMaterial != null)
        {
            trail.sharedMaterial = trailMaterial;
        }
        else
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");

            if (shader != null)
            {
                ownedMaterial = new Material(shader);
                trail.sharedMaterial = ownedMaterial;
            }
            else
            {
                Debug.LogWarning("[SwordDash] Trail shader를 찾지 못했습니다. " +
                    "SwordDashSkill 에셋의 Trail Material에 Trail67.mat을 연결해주세요.");
            }
        }

        trail.Clear();
        trail.emitting = true;
        return trail;
    }

    private void SpawnHitVfx(PlayerSkillController owner, Vector3 position)
    {
        if (hitVfxPrefab == null)
            return;

        GameObject effect = Instantiate(hitVfxPrefab, position, Quaternion.identity);
        effect.transform.localScale *= hitVfxScale;

        // 임팩트 프리팹의 모든 자식 파티클까지 한번만 재생하고 자동 제거.
        float maxLifetime = 0.2f;
        foreach (ParticleSystem ps in effect.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (!ps.gameObject.activeInHierarchy)
                continue;

            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            maxLifetime = Mathf.Max(maxLifetime,
                main.duration + main.startLifetime.constantMax + main.startDelay.constantMax);
            ps.Clear(true);
            ps.Play(true);
        }
        NetworkSkillPresentation.Track(owner.gameObject, effect, hitVfxPrefab, false, false);
        Destroy(effect, Mathf.Clamp(maxLifetime + 0.5f, 0.75f, 5f));
    }

    private void OnValidate()
    {
        distance = Mathf.Max(0.1f, distance);
        duration = Mathf.Max(0.05f, duration);
        dashDamage = Mathf.Max(0, dashDamage);
        collisionSkin = Mathf.Max(0f, collisionSkin);
        hitVfxScale = Mathf.Clamp(hitVfxScale, 0.1f, 3f);
    }
}
