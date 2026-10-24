using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 일반 공격과 강공격을 선택하고 각각 다른 피해와 애니메이션을 적용
public class BossEnemyAttack : EnemyAttack
{
    [SerializeField] private BossEnemyStats bossStats;
    [SerializeField] private int strongAttackDamage = 24;
    [SerializeField] private float strongAttackRadius = 6.5f;
    [SerializeField, Range(0f, 1f)] private float strongAttackChance = 0.35f;
    [SerializeField] private float strongAttackCooldown = 2.8f;
    [SerializeField] private float strongAttackMinInterval = 5f;
    [SerializeField] private float strongAttackFallbackHitDelay = 2.15f;
    [SerializeField, Range(1f, 90f)] private float attackFacingAngle = 25f;

    [Header("Strong Attack Area")]
    [SerializeField] private LayerMask strongAttackTargetLayers = ~0;
    [SerializeField] private float indicatorGroundOffset = 0.06f;
    [SerializeField] private int indicatorSegments = 64;
    [SerializeField] private Color indicatorFillColor = new Color(0.95f, 0.02f, 0.015f, 0.18f);
    [SerializeField] private Color indicatorChargeColor = new Color(1f, 0.025f, 0.02f, 0.55f);
    [SerializeField] private Color indicatorOutlineColor = new Color(1f, 0.12f, 0.06f, 1f);

    private bool useStrongAttack;
    private bool hasQueuedAttack;
    private bool queuedStrongAttack;
    private float nextStrongAttackTime;
    private Vector3 strongAttackCenter;
    private GameObject strongAttackIndicator;
    private Transform indicatorCharge;
    private float indicatorStartTime;
    private bool indicatorHit;
    private Mesh indicatorMesh;
    private Material indicatorFillMaterial;
    private Material indicatorChargeMaterial;
    private Material indicatorOutlineMaterial;
    private Coroutine indicatorHideRoutine;

    public bool IsUsingStrongAttack => useStrongAttack;
    public override bool LockFacingDuringAttack => true;
    public override bool ShouldFaceTarget
    {
        get
        {
            if (!IsAttackReady) return false;
            QueueAttackIfNeeded();
            return !queuedStrongAttack;
        }
    }

    protected override int CurrentAttackDamage => useStrongAttack ? StrongAttackDamage : base.CurrentAttackDamage;
    protected override float CurrentAttackCooldown => useStrongAttack ? StrongAttackCooldown : base.CurrentAttackCooldown;
    protected override float CurrentFallbackHitDelay => useStrongAttack
        ? StrongAttackFallbackHitDelay
        : base.CurrentFallbackHitDelay;

    public override bool TryAttack(Transform target)
    {
        if (target == null || !IsAttackReady || !IsTargetInRange(target))
        {
            return false;
        }

        QueueAttackIfNeeded();

        // 강공격은 보스 중심 범위 공격이므로 플레이어가 뒤에 있어도 발동
        if (!queuedStrongAttack && !IsFacingTarget(target))
        {
            return false;
        }

        return base.TryAttack(target);
    }

    protected override void PrepareAttack(Transform target)
    {
        QueueAttackIfNeeded();
        useStrongAttack = queuedStrongAttack;
        hasQueuedAttack = false;

        if (useStrongAttack)
        {
            nextStrongAttackTime = Time.time + StrongAttackMinInterval;
            strongAttackCenter = transform.position + Vector3.up * indicatorGroundOffset;
            ShowStrongAttackIndicator();
            return;
        }

        HideStrongAttackIndicator();
    }

    private void QueueAttackIfNeeded()
    {
        if (hasQueuedAttack)
        {
            return;
        }

        bool strongAttackReady = Time.time >= nextStrongAttackTime;
        queuedStrongAttack = strongAttackReady && Random.value < StrongAttackChance;
        hasQueuedAttack = true;
    }

    protected override void PlayAttackAnimation()
    {
        if (useStrongAttack)
        {
            EnemyAnimator?.PlayStrongAttack();
            return;
        }

        base.PlayAttackAnimation();
    }

    protected override bool IsTargetInRangeForDamage(Transform target)
    {
        if (!useStrongAttack)
        {
            return base.IsTargetInRangeForDamage(target);
        }

        Vector3 offset = target.position - strongAttackCenter;
        offset.y = 0f;
        return offset.sqrMagnitude <= StrongAttackRadius * StrongAttackRadius;
    }

    protected override void DealDamage(Transform target)
    {
        if (!useStrongAttack)
        {
            base.DealDamage(target);
            return;
        }

        indicatorHit = true;
        SetIndicatorCharge(1f);
        HashSet<PlayerHealth> damagedPlayers = new HashSet<PlayerHealth>();
        Collider[] hits = Physics.OverlapSphere(
            strongAttackCenter,
            StrongAttackRadius,
            strongAttackTargetLayers,
            QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            PlayerHealth playerHealth = hit.GetComponentInParent<PlayerHealth>();

            if (playerHealth != null && damagedPlayers.Add(playerHealth))
            {
                playerHealth.TakeDamage(StrongAttackDamage);
            }
        }

        // 플레이어 콜라이더 구성이 바뀌어도 현재 타겟에는 범위 피해가 적용되도록 보완
        if (damagedPlayers.Count == 0 && target != null)
        {
            PlayerHealth targetHealth = target.GetComponentInParent<PlayerHealth>();

            if (targetHealth != null)
            {
                targetHealth.TakeDamage(StrongAttackDamage);
            }
        }
    }

    private void Update()
    {
        if (strongAttackIndicator == null || !strongAttackIndicator.activeSelf || indicatorHit)
        {
            return;
        }

        float duration = Mathf.Max(0.01f, StrongAttackFallbackHitDelay);
        SetIndicatorCharge((Time.time - indicatorStartTime) / duration);
    }

    private void SetIndicatorCharge(float progress)
    {
        if (indicatorCharge == null) return;

        float scale = Mathf.Clamp01(progress);
        indicatorCharge.localScale = new Vector3(scale, 1f, scale);
    }

    private void ShowStrongAttackIndicator()
    {
        if (strongAttackIndicator == null)
        {
            CreateStrongAttackIndicator();
        }

        if (strongAttackIndicator == null)
        {
            return;
        }

        strongAttackIndicator.transform.position = strongAttackCenter;
        indicatorStartTime = Time.time;
        indicatorHit = false;
        SetIndicatorCharge(0f);
        strongAttackIndicator.SetActive(true);

        if (indicatorHideRoutine != null)
        {
            StopCoroutine(indicatorHideRoutine);
        }

        indicatorHideRoutine = StartCoroutine(HideIndicatorAfterDelay());
    }

    private IEnumerator HideIndicatorAfterDelay()
    {
        yield return new WaitForSeconds(StrongAttackFallbackHitDelay + 0.15f);
        indicatorHideRoutine = null;

        if (strongAttackIndicator != null)
        {
            strongAttackIndicator.SetActive(false);
        }
    }

    private void HideStrongAttackIndicator()
    {
        if (indicatorHideRoutine != null)
        {
            StopCoroutine(indicatorHideRoutine);
            indicatorHideRoutine = null;
        }

        if (strongAttackIndicator != null)
        {
            strongAttackIndicator.SetActive(false);
        }
    }

    private void CreateStrongAttackIndicator()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");

        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        if (shader == null)
        {
            Debug.LogWarning("Strong attack indicator shader could not be found.", this);
            return;
        }

        strongAttackIndicator = new GameObject("StrongAttackIndicator");
        strongAttackIndicator.transform.rotation = Quaternion.identity;

        MeshFilter meshFilter = strongAttackIndicator.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = strongAttackIndicator.AddComponent<MeshRenderer>();

        indicatorMesh = CreateCircleMesh(StrongAttackRadius, indicatorSegments);
        meshFilter.sharedMesh = indicatorMesh;

        indicatorFillMaterial = CreateIndicatorMaterial(shader, indicatorFillColor);
        meshRenderer.sharedMaterial = indicatorFillMaterial;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        var chargeObject = new GameObject("ChargeFill");
        indicatorCharge = chargeObject.transform;
        indicatorCharge.SetParent(strongAttackIndicator.transform, false);
        indicatorCharge.localPosition = Vector3.up * 0.008f;
        chargeObject.AddComponent<MeshFilter>().sharedMesh = indicatorMesh;
        MeshRenderer chargeRenderer = chargeObject.AddComponent<MeshRenderer>();
        indicatorChargeMaterial = CreateIndicatorMaterial(shader, indicatorChargeColor);
        chargeRenderer.sharedMaterial = indicatorChargeMaterial;
        chargeRenderer.shadowCastingMode = ShadowCastingMode.Off;
        chargeRenderer.receiveShadows = false;
        SetIndicatorCharge(0f);

        LineRenderer outline = strongAttackIndicator.AddComponent<LineRenderer>();
        outline.useWorldSpace = false;
        outline.loop = true;
        outline.positionCount = indicatorSegments;
        outline.widthMultiplier = 0.1f;
        outline.numCapVertices = 2;
        outline.numCornerVertices = 2;
        outline.shadowCastingMode = ShadowCastingMode.Off;
        outline.receiveShadows = false;

        for (int i = 0; i < indicatorSegments; i++)
        {
            float angle = i * Mathf.PI * 2f / indicatorSegments;
            outline.SetPosition(i, new Vector3(Mathf.Cos(angle) * StrongAttackRadius, 0.015f, Mathf.Sin(angle) * StrongAttackRadius));
        }

        indicatorOutlineMaterial = CreateIndicatorMaterial(shader, indicatorOutlineColor);
        outline.sharedMaterial = indicatorOutlineMaterial;
        strongAttackIndicator.SetActive(false);
    }

    private static Mesh CreateCircleMesh(float radius, int segments)
    {
        Mesh mesh = new Mesh { name = "StrongAttackAreaMesh" };
        Vector3[] vertices = new Vector3[segments + 2];
        int[] triangles = new int[segments * 3];

        vertices[0] = Vector3.zero;

        for (int i = 0; i <= segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            vertices[i + 1] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        }

        for (int i = 0; i < segments; i++)
        {
            int triangleIndex = i * 3;
            triangles[triangleIndex] = 0;
            triangles[triangleIndex + 1] = i + 2;
            triangles[triangleIndex + 2] = i + 1;
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        return mesh;
    }

    private static Material CreateIndicatorMaterial(Shader shader, Color color)
    {
        Material material = new Material(shader)
        {
            name = "StrongAttackIndicatorMaterial",
            color = color,
            hideFlags = HideFlags.HideAndDontSave,
            renderQueue = (int)RenderQueue.Transparent
        };

        material.SetOverrideTag("RenderType", "Transparent");

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_SrcBlend"))
        {
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        }

        if (material.HasProperty("_DstBlend"))
        {
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 0f);
        }

        if (material.HasProperty("_Cull"))
        {
            material.SetFloat("_Cull", (float)CullMode.Off);
        }

        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        return material;
    }

    private void OnDestroy()
    {
        if (strongAttackIndicator != null)
        {
            Destroy(strongAttackIndicator);
        }

        if (indicatorMesh != null)
        {
            Destroy(indicatorMesh);
        }

        if (indicatorFillMaterial != null)
        {
            Destroy(indicatorFillMaterial);
        }

        if (indicatorChargeMaterial != null)
        {
            Destroy(indicatorChargeMaterial);
        }

        if (indicatorOutlineMaterial != null)
        {
            Destroy(indicatorOutlineMaterial);
        }
    }

    private BossEnemyStats BossStats => bossStats != null ? bossStats : Stats as BossEnemyStats;
    private float AttackFacingAngle => BossStats != null ? BossStats.AttackFacingAngle : attackFacingAngle;
    private int StrongAttackDamage => BossStats != null ? BossStats.StrongAttackDamage : strongAttackDamage;
    private float StrongAttackRadius => BossStats != null ? BossStats.StrongAttackRadius : strongAttackRadius;
    private float StrongAttackChance => BossStats != null ? BossStats.StrongAttackChance : strongAttackChance;
    private float StrongAttackCooldown => BossStats != null ? BossStats.StrongAttackCooldown : strongAttackCooldown;
    private float StrongAttackMinInterval => BossStats != null
        ? BossStats.StrongAttackMinInterval
        : strongAttackMinInterval;
    private float StrongAttackFallbackHitDelay => BossStats != null
        ? BossStats.StrongAttackFallbackHitDelay
        : strongAttackFallbackHitDelay;

    protected override void OnValidate()
    {
        base.OnValidate();
        strongAttackDamage = Mathf.Max(1, strongAttackDamage);
        strongAttackRadius = Mathf.Max(0.1f, strongAttackRadius);
        strongAttackChance = Mathf.Clamp01(strongAttackChance);
        strongAttackCooldown = Mathf.Max(0.1f, strongAttackCooldown);
        strongAttackMinInterval = Mathf.Max(0f, strongAttackMinInterval);
        strongAttackFallbackHitDelay = Mathf.Max(0f, strongAttackFallbackHitDelay);
        attackFacingAngle = Mathf.Clamp(attackFacingAngle, 1f, 90f);
        indicatorGroundOffset = Mathf.Max(0.01f, indicatorGroundOffset);
        indicatorSegments = Mathf.Clamp(indicatorSegments, 16, 128);
    }

    private bool IsFacingTarget(Transform target)
    {
        if (target == null)
        {
            return false;
        }

        Vector3 directionToTarget = target.position - transform.position;
        directionToTarget.y = 0f;

        if (directionToTarget.sqrMagnitude < 0.001f)
        {
            return true;
        }

        return Vector3.Angle(transform.forward, directionToTarget) <= AttackFacingAngle;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = indicatorOutlineColor;
        Vector3 center = transform.position + Vector3.up * indicatorGroundOffset;
        Vector3 previousPoint = center + Vector3.right * StrongAttackRadius;

        for (int i = 1; i <= indicatorSegments; i++)
        {
            float angle = i * Mathf.PI * 2f / indicatorSegments;
            Vector3 currentPoint = center + new Vector3(
                Mathf.Cos(angle) * StrongAttackRadius,
                0f,
                Mathf.Sin(angle) * StrongAttackRadius);
            Gizmos.DrawLine(previousPoint, currentPoint);
            previousPoint = currentPoint;
        }
    }
}
