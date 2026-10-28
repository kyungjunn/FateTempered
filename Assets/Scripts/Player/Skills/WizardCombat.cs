using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

// Runtime ownership for the wizard's independently tunable Q/E/R effects.
[RequireComponent(typeof(WizardSkillTargeting))]
public sealed class WizardCombat : MonoBehaviour
{
    private const float ResidualGravityDuration = 2f;
    private const float OverchargeTotalExtensionLimit = 3f;

    private sealed class ResonanceField
    {
        public WizardSkillSO Skill;
        public Vector3 Center;
        public float Radius;
        public float EndTime;
        public float NextTickTime;
        public float TickInterval;
        public float ResonanceCooldown;
        public float NextResonanceTime;
        public int TickDamage;
        public int ResonanceDamage;
        public float ResonanceRadius;
        public WizardSkillVisual Visual;
        public readonly SkillAreaQuery TickQuery = new SkillAreaQuery();
        public readonly SkillAreaQuery ResonanceQuery = new SkillAreaQuery();
        public readonly SkillAreaQuery CollapseQuery = new SkillAreaQuery();
    }

    private sealed class ResidualGravityField
    {
        public WizardSkillSO Skill;
        public Vector3 Center;
        public float RemainingDuration;
        public WizardSkillVisual Visual;
        public readonly SkillAreaQuery Query = new SkillAreaQuery();
        public readonly HashSet<NavMeshAgent> AffectedAgents = new HashSet<NavMeshAgent>();
        public readonly HashSet<NavMeshAgent> CurrentAgents = new HashSet<NavMeshAgent>();
        public readonly List<NavMeshAgent> AgentsToRelease = new List<NavMeshAgent>();
    }

    private sealed class SlowControl
    {
        public float BaseSpeed;
        public float AppliedSpeed;
        public float AppliedMultiplier = 1f;
        public readonly Dictionary<ResidualGravityField, float> Contributions =
            new Dictionary<ResidualGravityField, float>();
    }

    private sealed class Overdrive
    {
        public WizardSkillSO Skill;
        public float BaseEndTime;
        public float EndTime;
        public float AppliedOverchargeExtension;
        public float PauseStartedAt;
        public int CreditedKillCount;
        public float NextWaveTime;
        public float WaveInterval;
        public float WaveRadius;
        public float FinalBlastRadius;
        public float FinalDamageMultiplier;
        public float WaveDamageMultiplier;
        public float BasicDamageMultiplier;
        public float BasicVisualScale;
        public int BasicAdditionalPierces;
        public bool WasPaused;
        public bool Cancelled;
        public bool FinalBlastTriggered;
        public WizardSkillVisual Aura;
        public readonly SkillAreaQuery WaveQuery = new SkillAreaQuery();
        public readonly SkillAreaQuery FinalBlastQuery = new SkillAreaQuery();
    }

    private sealed class PullControl
    {
        public EnemyHealth Enemy;
        public EnemyAI AI;
        public bool AIWasEnabled;
        public bool AgentWasStopped;
        public int References;
    }

    [Header("Visuals")]
    [SerializeField] private GameObject gravityCorePrefab;
    [SerializeField] private GameObject gravityVortexPrefab;
    [SerializeField] private GameObject starHitPrefab;
    [SerializeField] private GameObject resonanceHitPrefab;
    [SerializeField] private GameObject resonanceFieldPrefab;
    [SerializeField] private GameObject overdriveAuraPrefab;
    [SerializeField] private GameObject overdriveWavePrefab;
    [SerializeField] private GameObject overdriveFinalPrefab;

    private PlayerSkillController owner;
    private PlayerDamageDealer damageDealer;
    private PlayerStats playerStats;
    private PlayerHealth playerHealth;
    private PlayerStateManager stateManager;
    private PlayerAugments playerAugments;
    private readonly List<ResonanceField> resonanceFields = new List<ResonanceField>();
    private readonly List<ResidualGravityField> residualGravityFields = new List<ResidualGravityField>();
    private Overdrive overdrive;
    private WizardSkillSO preparedCastSkill;
    private Vector3 preparedCastPosition;
    private Vector3 preparedCastNormal;
    private int preparedCastFrame = -1;
    private RaycastHit[] groundHitBuffer = new RaycastHit[16];
    private readonly HashSet<GameObject> activeVisuals = new HashSet<GameObject>();
    private static readonly Dictionary<NavMeshAgent, PullControl> controlledAgents =
        new Dictionary<NavMeshAgent, PullControl>();
    private readonly Dictionary<NavMeshAgent, int> ownedPullReferences =
        new Dictionary<NavMeshAgent, int>();
    private static readonly Dictionary<NavMeshAgent, SlowControl> activeSlowControls =
        new Dictionary<NavMeshAgent, SlowControl>();
    private static readonly List<NavMeshAgent> destroyedSlowAgents = new List<NavMeshAgent>();

    public bool IsOverdriveActive
    {
        get
        {
            if (!NetworkAuthority.CanSimulate(gameObject) || overdrive == null || overdrive.Cancelled || IsOwnerDead())
                return false;

            RefreshOverchargeExtension(overdrive);
            return Time.time < overdrive.EndTime || IsGamePaused;
        }
    }

    // Includes synchronous final-blast damage, but not its lingering visual.
    public bool IsUltimateActive =>
        isActiveAndEnabled && overdrive != null && !overdrive.Cancelled && !IsOwnerDead();

    public float BasicDamageMultiplier =>
        IsOverdriveActive ? overdrive.BasicDamageMultiplier : 1f;

    public float BasicVisualScale =>
        IsOverdriveActive ? overdrive.BasicVisualScale : 1f;

    public int BasicAdditionalPierces
    {
        get
        {
            int overdrivePierces = IsOverdriveActive ? overdrive.BasicAdditionalPierces : 0;
            return overdrivePierces + Mathf.Max(0, Mathf.RoundToInt(
                GetCombatAugmentValue(PlayerCombatAugmentEffect.WizardBasicPierce)));
        }
    }

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        ResolveReferences();
        if (damageDealer != null)
            damageDealer.OnTargetDamaged += HandleTargetDamaged;
        if (stateManager != null)
            stateManager.OnDead += HandleOwnerDeath;
        if (owner != null)
            owner.ActiveSkillsCancelled += CancelAllEffects;
    }

    private void OnDisable()
    {
        if (damageDealer != null)
            damageDealer.OnTargetDamaged -= HandleTargetDamaged;
        if (stateManager != null)
            stateManager.OnDead -= HandleOwnerDeath;
        if (owner != null)
            owner.ActiveSkillsCancelled -= CancelAllEffects;

        GetComponent<WizardSkillTargeting>()?.CancelPreview();
        CancelAllEffects();
    }

    private void Update()
    {
        if (IsOwnerDead())
        {
            CancelAllEffects();
            return;
        }

        if (IsGamePaused)
            return;

        if (!NetworkAuthority.CanSimulate(gameObject))
            return;

        RefreshOverchargeExtension(overdrive);
        UpdateResidualGravityFields();
        EnforceResonanceFieldCapacity();
        UpdateResonanceFields();
    }

    public bool TryCast(WizardSkillSO skill)
    {
        if (!NetworkAuthority.CanSimulate(gameObject) ||
            !isActiveAndEnabled || !gameObject.activeInHierarchy || skill == null ||
            owner == null || damageDealer == null || IsOwnerDead() || IsGamePaused)
        {
            return false;
        }

        switch (skill.Kind)
        {
            case WizardSkillKind.GravityCore:
                return TryCastGravityCore(skill);
            case WizardSkillKind.ResonanceField:
                return TryCastResonanceField(skill);
            case WizardSkillKind.Overdrive:
                return TryCastOverdrive(skill);
            default:
                return false;
        }
    }

    internal bool TryPrepareResonanceCast(WizardSkillSO skill, Vector3 position, Vector3 normal)
    {
        ClearPreparedResonanceCast();
        if (!CanRunEffects() || IsGamePaused || skill == null || skill != owner.SkillE ||
            skill.Kind != WizardSkillKind.ResonanceField || owner.ECharges <= 0 ||
            !IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) ||
            !IsFinite(normal.x) || !IsFinite(normal.y) || !IsFinite(normal.z) ||
            normal.sqrMagnitude < 0.001f)
        {
            return false;
        }

        Vector3 offset = position - owner.transform.position;
        offset.y = 0f;
        if (offset.sqrMagnitude > skill.ResonanceFieldRange * skill.ResonanceFieldRange ||
            normal.normalized.y < Mathf.Cos(50f * Mathf.Deg2Rad))
        {
            return false;
        }

        preparedCastSkill = skill;
        preparedCastPosition = position;
        preparedCastNormal = normal.normalized;
        preparedCastFrame = Time.frameCount;
        return true;
    }

    internal void CancelPreparedResonanceCast()
    {
        ClearPreparedResonanceCast();
    }

    public bool TryPrepareNetworkResonanceCast(WizardSkillSO skill, Vector3 candidate)
    {
        ClearPreparedResonanceCast();
        if (!CanRunEffects() || IsGamePaused || skill == null || skill != owner.SkillE ||
            skill.Kind != WizardSkillKind.ResonanceField || owner.ECharges <= 0 ||
            !IsFinite(candidate.x) || !IsFinite(candidate.y) || !IsFinite(candidate.z))
            return false;

        Vector3 offset = candidate - owner.transform.position;
        offset.y = 0f;
        if (offset.sqrMagnitude > skill.ResonanceFieldRange * skill.ResonanceFieldRange)
            return false;

        // The request names a surface point, not an arbitrary point above/below the map.
        RaycastHit nearest = default;
        float nearestDistance = float.PositiveInfinity;
        foreach (RaycastHit hit in Physics.RaycastAll(candidate + Vector3.up * 0.5f,
            Vector3.down, 1f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null || IsOwnerCollider(hit.collider) ||
                hit.collider.GetComponentInParent<EnemyHealth>() != null ||
                hit.collider.GetComponentInParent<PlayerHealth>() != null ||
                hit.distance >= nearestDistance)
                continue;

            nearest = hit;
            nearestDistance = hit.distance;
        }

        if (float.IsPositiveInfinity(nearestDistance) ||
            nearest.normal.y < Mathf.Cos(50f * Mathf.Deg2Rad) ||
            (nearest.point - candidate).sqrMagnitude > 0.25f * 0.25f)
            return false;

        preparedCastSkill = skill;
        preparedCastPosition = nearest.point;
        preparedCastNormal = nearest.normal.normalized;
        preparedCastFrame = Time.frameCount;
        return true;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private bool TryCastGravityCore(WizardSkillSO skill)
    {
        Transform origin = owner.SkillOrigin;
        if (origin == null || origin.forward.sqrMagnitude < 0.001f)
            return false;

        StartCoroutine(RunGravityCore(skill, origin.position, origin.forward.normalized));
        return true;
    }

    private bool TryCastResonanceField(WizardSkillSO skill)
    {
        Vector3 groundPosition;
        Vector3 groundNormal;
        if (preparedCastSkill == skill && preparedCastFrame == Time.frameCount)
        {
            groundPosition = preparedCastPosition;
            groundNormal = preparedCastNormal;
            ClearPreparedResonanceCast();
        }
        else
        {
            ClearPreparedResonanceCast();
            NetworkObject networkObject = GetComponent<NetworkObject>();
            if (networkObject != null && networkObject.IsSpawned)
                return false;
            if (!TryFindGroundPosition(skill.ResonanceFieldRange, out groundPosition, out groundNormal))
                return false;
        }

        ResonanceField field = new ResonanceField
        {
            Skill = skill,
            Center = groundPosition,
            Radius = skill.ResonanceFieldRadius,
            EndTime = Time.time + skill.ResonanceFieldDuration,
            NextTickTime = Time.time,
            TickInterval = skill.ResonanceFieldTickInterval,
            ResonanceCooldown = skill.ResonanceCooldown,
            TickDamage = CalculateDamage(skill.ResonanceFieldTickDamageMultiplier),
            ResonanceDamage = CalculateDamage(skill.ResonanceDamageMultiplier),
            ResonanceRadius = skill.ResonanceRadius
        };
        field.Visual = CreateVisual(resonanceFieldPrefab, "WizardResonanceField",
            groundPosition + groundNormal * 0.035f,
            field.Radius / 3f, true);
        if (field.Visual != null)
        {
            field.Visual.transform.rotation = Quaternion.FromToRotation(Vector3.up, groundNormal);
            AddGroundBoundary(field.Visual, field.Radius, new Color(0.68f, 0.46f, 1f, 0.82f), 0.075f);
        }
        resonanceFields.Add(field);
        EnforceResonanceFieldCapacity();
        return true;
    }

    private bool TryCastOverdrive(WizardSkillSO skill)
    {
        if (overdrive != null)
            return false;

        Overdrive state = new Overdrive
        {
            Skill = skill,
            BaseEndTime = Time.time + skill.OverdriveDuration,
            EndTime = Time.time + skill.OverdriveDuration,
            NextWaveTime = Time.time,
            WaveInterval = skill.OverdriveWaveInterval,
            WaveRadius = skill.OverdriveWaveRadius,
            FinalBlastRadius = skill.OverdriveFinalBlastRadius,
            FinalDamageMultiplier = skill.OverdriveFinalDamageMultiplier,
            WaveDamageMultiplier = skill.OverdriveWaveDamageMultiplier,
            BasicDamageMultiplier = skill.OverdriveBasicDamageMultiplier,
            BasicVisualScale = skill.OverdriveBasicVisualScale,
            BasicAdditionalPierces = skill.OverdriveBasicAdditionalPierces
        };
        state.Aura = CreateVisual(overdriveAuraPrefab, "WizardOverdriveAura",
            Vector3.up, 1f, true, owner.transform);
        overdrive = state;
        StartCoroutine(RunOverdrive(state));
        return true;
    }

    private IEnumerator RunGravityCore(WizardSkillSO skill, Vector3 position, Vector3 direction)
    {
        WizardSkillVisual coreVisual = CreateVisual(gravityCorePrefab, "WizardGravityCore",
            position, 0.16f, true);
        if (coreVisual != null)
            coreVisual.transform.rotation = Quaternion.LookRotation(direction);
        WizardSkillVisual vortexVisual = null;

        HashSet<NavMeshAgent> pulledAgents = new HashSet<NavMeshAgent>();
        SkillAreaQuery pullQuery = new SkillAreaQuery();
        RaycastHit[] hitBuffer = new RaycastHit[16];
        Collider[] overlapBuffer = new Collider[16];
        float traveled = 0f;
        bool completedNormally = false;

        try
        {
            bool hasInitialImpact = TryFindCoreOverlap(position, skill.GravityCoreCollisionRadius,
                ref overlapBuffer);
            while (!hasInitialImpact && traveled < skill.GravityCoreRange)
            {
                if (!CanRunEffects())
                    yield break;
                if (IsGamePaused)
                {
                    yield return null;
                    continue;
                }

                float step = Mathf.Min(Time.deltaTime * skill.GravityCoreSpeed,
                    skill.GravityCoreRange - traveled);
                if (step <= 0f)
                {
                    yield return null;
                    continue;
                }

                if (TryFindCoreImpact(position, direction, step, skill.GravityCoreCollisionRadius,
                        ref hitBuffer, out RaycastHit impact))
                {
                    position += direction * impact.distance;
                    if (coreVisual != null)
                        coreVisual.transform.position = position;
                    break;
                }

                position += direction * step;
                traveled += step;
                if (coreVisual != null)
                    coreVisual.transform.position = position;
                yield return null;
            }

            if (TryFindVortexFloor(position, out RaycastHit floor))
            {
                float pullRadius = GetAugmentedGravityPullRadius(skill);
                vortexVisual = CreateVisual(gravityVortexPrefab, "WizardGravityVortex",
                    floor.point + floor.normal * 0.04f, pullRadius / 4f, true);
                if (vortexVisual != null)
                {
                    vortexVisual.transform.rotation = Quaternion.FromToRotation(Vector3.up, floor.normal);
                    AddGroundBoundary(vortexVisual, pullRadius, new Color(0.42f, 0.75f, 1f, 0.68f), 0.07f);
                }
            }
            float pullEndTime = Time.time + skill.GravityCorePullDuration;
            while (Time.time < pullEndTime)
            {
                if (!CanRunEffects())
                    yield break;
                if (IsGamePaused)
                {
                    yield return null;
                    continue;
                }

                float pullRadius = GetAugmentedGravityPullRadius(skill);
                if (vortexVisual != null)
                    vortexVisual.transform.localScale = Vector3.one * (pullRadius / 4f);
                PullNearbyEnemies(position, pullRadius, skill.GravityCorePullSpeed,
                    Time.deltaTime, pullQuery, pulledAgents, ref hitBuffer);
                yield return null;
            }

            ReleasePulledAgents(pulledAgents);
            if (!CanRunEffects())
                yield break;

            SkillAreaQuery blastQuery = new SkillAreaQuery();
            blastQuery.DealDamage(
                damageDealer,
                position,
                skill.GravityCoreBlastRadius,
                CalculateDamage(skill.GravityCoreDamageMultiplier),
                PlayerDamageType.Skill,
                owner.transform.position);

            if (CanRunEffects())
            {
                PlayBurst(starHitPrefab, "WizardGravityBlast",
                    position + Vector3.up * 0.04f, skill.GravityCoreBlastRadius / 2f);
            }

            completedNormally = true;
        }
        finally
        {
            ReleasePulledAgents(pulledAgents);
            DestroyVisual(coreVisual);
            DestroyVisual(vortexVisual);
        }

        if (completedNormally && CanRunEffects())
            StartResidualGravityField(skill, position);
    }

    private IEnumerator RunOverdrive(Overdrive state)
    {
        try
        {
            while (true)
            {
                if (!CanRunEffects() || state.Cancelled)
                    yield break;

                RefreshOverchargeExtension(state);
                if (IsGamePaused)
                {
                    yield return null;
                    continue;
                }

                if (Time.time >= state.EndTime)
                    break;

                if (Time.time >= state.NextWaveTime)
                {
                    state.NextWaveTime = Time.time + state.WaveInterval;
                    Vector3 center = owner.transform.position;
                    state.WaveQuery.DealDamage(
                        damageDealer,
                        center,
                        state.WaveRadius,
                        CalculateDamage(state.WaveDamageMultiplier),
                        PlayerDamageType.Skill,
                        center);
                    if (CanRunEffects() && !state.Cancelled)
                    {
                        PlayBurst(overdriveWavePrefab, "WizardOverdriveWave",
                            center + Vector3.up * 0.04f, state.WaveRadius / 4f);
                    }
                }

                yield return null;
            }

            if (!CanRunEffects() || state.Cancelled || state.FinalBlastTriggered)
                yield break;

            state.FinalBlastTriggered = true;
            DestroyVisual(state.Aura);
            state.Aura = null;

            Vector3 finalCenter = owner.transform.position;
            state.FinalBlastQuery.DealDamage(
                damageDealer,
                finalCenter,
                state.FinalBlastRadius,
                CalculateDamage(state.FinalDamageMultiplier),
                PlayerDamageType.Skill,
                finalCenter);

            if (CanRunEffects())
            {
                PlayBurst(overdriveFinalPrefab, "WizardOverdriveBlast",
                    finalCenter + Vector3.up * 0.04f, state.FinalBlastRadius / 5f);
            }
        }
        finally
        {
            if (overdrive == state)
                overdrive = null;
            DestroyVisual(state.Aura);
            state.Aura = null;
        }
    }

    private bool TryFindVortexFloor(Vector3 position, out RaycastHit floor)
    {
        floor = default;
        float nearest = float.PositiveInfinity;
        foreach (RaycastHit hit in Physics.RaycastAll(position + Vector3.up * 2f, Vector3.down,
            20f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (IsOwnerCollider(hit.collider) || hit.collider.GetComponentInParent<EnemyHealth>() != null ||
                hit.distance >= nearest)
                continue;
            nearest = hit.distance;
            floor = hit;
        }
        return nearest < float.PositiveInfinity && Vector3.Dot(floor.normal, Vector3.up) >= 0.5f;
    }

    private void PlayBurst(GameObject prefab, string name,
        Vector3 position, float scale)
    {
        WizardSkillVisual visual = CreateVisual(prefab, name, position, scale, false);
        if (visual != null)
            StartCoroutine(CleanupBurst(visual));
    }

    private IEnumerator CleanupBurst(WizardSkillVisual visual)
    {
        // Particle lifetime owns the tail, with a bound for malformed assets.
        float endTime = Time.time + 12f;
        yield return null;
        while (visual != null && visual.IsAlive() && Time.time < endTime)
        {
            if (!CanRunEffects())
                break;
            yield return null;
        }
        DestroyVisual(visual);
    }

    private bool TryFindGroundPosition(float range, out Vector3 groundPosition, out Vector3 groundNormal)
    {
        return TryFindGroundCandidate(range, out groundPosition, out groundNormal, out bool withinRange) &&
               withinRange;
    }

    internal bool TryFindGroundCandidate(float range, out Vector3 groundPosition, out Vector3 groundNormal,
        out bool withinRange)
    {
        groundPosition = default;
        groundNormal = Vector3.up;
        withinRange = false;
        Camera aimCamera = Camera.main;
        if (aimCamera == null || owner == null)
            return false;

        Ray aimRay = aimCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        int hitCount;
        while (true)
        {
            hitCount = Physics.RaycastNonAlloc(aimRay, groundHitBuffer, aimCamera.farClipPlane, ~0,
                QueryTriggerInteraction.Ignore);
            if (hitCount < groundHitBuffer.Length)
                break;

            System.Array.Resize(ref groundHitBuffer, groundHitBuffer.Length * 2);
        }
        bool hasNearest = false;
        RaycastHit nearest = default;
        for (int i = 0; i < hitCount; i++)
        {
            Collider hitCollider = groundHitBuffer[i].collider;
            if (hitCollider == null || IsOwnerCollider(hitCollider) ||
                hitCollider.GetComponentInParent<EnemyHealth>() != null)
            {
                continue;
            }

            if (!hasNearest || groundHitBuffer[i].distance < nearest.distance)
            {
                nearest = groundHitBuffer[i];
                hasNearest = true;
            }
        }

        if (!hasNearest || nearest.normal.y < Mathf.Cos(50f * Mathf.Deg2Rad))
            return false;

        Vector3 flatOffset = nearest.point - owner.transform.position;
        flatOffset.y = 0f;
        groundPosition = nearest.point;
        groundNormal = nearest.normal.normalized;
        withinRange = flatOffset.sqrMagnitude <= range * range;
        return true;
    }

    private bool TryFindCoreOverlap(Vector3 center, float radius, ref Collider[] overlapBuffer)
    {
        int count;
        while (true)
        {
            count = Physics.OverlapSphereNonAlloc(center, radius, overlapBuffer, ~0,
                QueryTriggerInteraction.Collide);
            if (count < overlapBuffer.Length)
                break;

            System.Array.Resize(ref overlapBuffer, overlapBuffer.Length * 2);
        }

        for (int i = 0; i < count; i++)
        {
            Collider collider = overlapBuffer[i];
            overlapBuffer[i] = null;
            if (collider == null || IsOwnerCollider(collider))
                continue;

            EnemyHealth enemy = collider.GetComponentInParent<EnemyHealth>();
            if ((enemy != null && enemy.IsDead) || (enemy == null && collider.isTrigger))
                continue;

            return true;
        }

        return false;
    }

    private bool TryFindCoreImpact(Vector3 origin, Vector3 direction, float distance, float radius,
        ref RaycastHit[] hitBuffer, out RaycastHit nearestHit)
    {
        nearestHit = default;
        float nearestDistance = float.PositiveInfinity;

        int count;
        while (true)
        {
            count = Physics.SphereCastNonAlloc(origin, radius, direction, hitBuffer, distance,
                ~0, QueryTriggerInteraction.Collide);
            if (count < hitBuffer.Length)
                break;

            System.Array.Resize(ref hitBuffer, hitBuffer.Length * 2);
        }

        bool found = false;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = hitBuffer[i];
            hitBuffer[i] = default;
            if (hit.collider == null || IsOwnerCollider(hit.collider) || hit.distance >= nearestDistance)
                continue;

            EnemyHealth enemy = hit.collider.GetComponentInParent<EnemyHealth>();
            if ((enemy != null && enemy.IsDead) || (enemy == null && hit.collider.isTrigger))
                continue;

            nearestHit = hit;
            nearestDistance = hit.distance;
            found = true;
        }

        return found;
    }

    private void PullNearbyEnemies(Vector3 center, float radius, float pullSpeed, float deltaTime,
        SkillAreaQuery query, HashSet<NavMeshAgent> pulledAgents, ref RaycastHit[] obstacleBuffer)
    {
        if (pullSpeed <= 0f || deltaTime <= 0f)
            return;

        IReadOnlyList<EnemyHealth> enemies = query.Collect(center, radius);
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyHealth enemy = enemies[i];
            if (enemy == null || enemy.IsDead || enemy.GetComponentInParent<BossEnemyAttack>() != null)
                continue;

            NavMeshAgent agent = enemy.GetComponentInParent<NavMeshAgent>();
            if (agent == null || !agent.enabled || !agent.isOnNavMesh)
                continue;

            AcquirePull(agent, enemy, pulledAgents);
            MoveAgentToward(agent, center, pullSpeed, deltaTime, ref obstacleBuffer);
        }
    }

    private void AcquirePull(NavMeshAgent agent, EnemyHealth enemy, HashSet<NavMeshAgent> pulledAgents)
    {
        if (!pulledAgents.Add(agent))
            return;

        if (!controlledAgents.TryGetValue(agent, out PullControl control))
        {
            EnemyAI ai = enemy.GetComponentInParent<EnemyAI>();
            control = new PullControl
            {
                Enemy = enemy,
                AI = ai,
                AIWasEnabled = ai != null && ai.enabled,
                AgentWasStopped = agent.isStopped,
                References = 0
            };
            controlledAgents.Add(agent, control);
            if (control.AIWasEnabled)
                ai.enabled = false;
        }

        control.References++;
        ownedPullReferences.TryGetValue(agent, out int ownedReferences);
        ownedPullReferences[agent] = ownedReferences + 1;
        if (agent.enabled && agent.isOnNavMesh)
            agent.isStopped = true;
    }

    private void MoveAgentToward(NavMeshAgent agent, Vector3 center, float pullSpeed, float deltaTime,
        ref RaycastHit[] obstacleBuffer)
    {
        if (agent == null || !agent.enabled || !agent.isOnNavMesh || pullSpeed <= 0f || deltaTime <= 0f)
            return;

        Vector3 current = agent.nextPosition;
        Vector3 towardCenter = center - current;
        towardCenter.y = 0f;
        float distance = towardCenter.magnitude;
        float stoppingGap = Mathf.Max(0.3f, agent.radius * 0.65f);
        float remainingDistance = distance - stoppingGap;
        if (remainingDistance <= 0.00001f)
            return;

        Vector3 stoppingPosition = current + towardCenter * (remainingDistance / distance);
        float maxMoveFraction = pullSpeed * deltaTime / remainingDistance;
        float lerpFraction = Mathf.Min(1f - Mathf.Exp(-pullSpeed * deltaTime), maxMoveFraction);
        Vector3 requestedPosition = Vector3.Lerp(current, stoppingPosition, lerpFraction);
        if (!NavMesh.SamplePosition(requestedPosition, out NavMeshHit sampledPosition,
                Mathf.Max(0.2f, agent.radius), agent.areaMask))
        {
            return;
        }

        requestedPosition = sampledPosition.position;
        if (NavMesh.Raycast(current, requestedPosition, out NavMeshHit navMeshHit, agent.areaMask))
            requestedPosition = navMeshHit.position;

        Vector3 horizontalMove = requestedPosition - current;
        horizontalMove.y = 0f;
        float requestedDistance = horizontalMove.magnitude;
        if (requestedDistance <= 0.00001f)
            return;

        Vector3 horizontalDirection = horizontalMove / requestedDistance;
        float allowedDistance = requestedDistance;
        float capsuleRadius = Mathf.Max(0.05f, agent.radius * 0.8f);
        float innerHeight = Mathf.Max(0f, agent.height - capsuleRadius * 2f);
        Vector3 bottom = current + Vector3.up * capsuleRadius;
        Vector3 top = current + Vector3.up * (capsuleRadius + innerHeight);
        int hitCount;
        while (true)
        {
            hitCount = Physics.CapsuleCastNonAlloc(bottom, top, capsuleRadius, horizontalDirection,
                obstacleBuffer, requestedDistance, ~0, QueryTriggerInteraction.Ignore);
            if (hitCount < obstacleBuffer.Length)
                break;

            System.Array.Resize(ref obstacleBuffer, obstacleBuffer.Length * 2);
        }

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = obstacleBuffer[i];
            obstacleBuffer[i] = default;
            if (hit.collider == null || IsOwnerCollider(hit.collider) ||
                hit.collider.GetComponentInParent<EnemyHealth>() != null)
            {
                continue;
            }

            allowedDistance = Mathf.Min(allowedDistance, Mathf.Max(0f, hit.distance - 0.05f));
        }

        if (allowedDistance > 0.00001f)
            agent.Move(horizontalDirection * allowedDistance);
    }

    private void ReleasePulledAgents(HashSet<NavMeshAgent> pulledAgents)
    {
        foreach (NavMeshAgent agent in pulledAgents)
            ReleasePull(agent);
        pulledAgents.Clear();
    }

    private void ReleasePull(NavMeshAgent agent)
    {
        if (object.ReferenceEquals(agent, null) ||
            !ownedPullReferences.TryGetValue(agent, out int ownedReferences))
            return;

        if (ownedReferences == 1)
            ownedPullReferences.Remove(agent);
        else
            ownedPullReferences[agent] = ownedReferences - 1;
        if (!controlledAgents.TryGetValue(agent, out PullControl control))
            return;
        control.References--;
        if (control.References > 0)
            return;

        controlledAgents.Remove(agent);
        if (control.Enemy == null || control.Enemy.IsDead)
            return;

        if (agent != null && agent.enabled && agent.isOnNavMesh)
            agent.isStopped = control.AgentWasStopped;
        if (control.AI != null && control.AIWasEnabled)
            control.AI.enabled = true;
    }

    private void RestoreAllPulls()
    {
        var agents = new List<NavMeshAgent>(ownedPullReferences.Keys);
        foreach (NavMeshAgent agent in agents)
        {
            while (ownedPullReferences.ContainsKey(agent))
                ReleasePull(agent);
        }
    }

    private void HandleTargetDamaged(EnemyHealth enemy, Vector3 hitPosition, int actualDamage,
        PlayerDamageType damageType)
    {
        if (!CanRunEffects() || enemy == null || actualDamage <= 0)
            return;

        Overdrive activeOverdrive = overdrive;
        if (enemy.IsDead && activeOverdrive != null && !activeOverdrive.Cancelled &&
            !activeOverdrive.FinalBlastTriggered && Time.time < activeOverdrive.EndTime &&
            GetCombatAugmentValue(PlayerCombatAugmentEffect.WizardOvercharge, activeOverdrive.Skill) > 0f)
        {
            activeOverdrive.CreditedKillCount++;
            RefreshOverchargeExtension(activeOverdrive);
        }

        // Finish every field's hit even if an earlier kill opened the paused level-up UI.
        if (damageType != PlayerDamageType.BasicAttack)
            return;

        for (int i = 0; i < resonanceFields.Count; i++)
        {
            ResonanceField field = resonanceFields[i];
            if (field == null || Time.time >= field.EndTime || Time.time < field.NextResonanceTime)
                continue;

            Vector3 offset = hitPosition - field.Center;
            if (Mathf.Abs(offset.y) > field.Radius)
                continue;
            offset.y = 0f;
            if (offset.sqrMagnitude > field.Radius * field.Radius)
                continue;

            // Reserve this field's independent cooldown before re-entrant damage callbacks.
            field.NextResonanceTime = Time.time + field.ResonanceCooldown;
            float radius = field.ResonanceRadius * (1f + Mathf.Max(0f,
                GetCombatAugmentValue(PlayerCombatAugmentEffect.WizardResonanceRadius, field.Skill)));
            field.ResonanceQuery.DealDamage(
                damageDealer,
                hitPosition,
                radius,
                field.ResonanceDamage,
                PlayerDamageType.Skill,
                owner.transform.position);

            if (!CanRunEffects())
                return;

            PlayBurst(resonanceHitPrefab, "WizardResonanceBurst",
                hitPosition + Vector3.up * 0.04f, radius / 2f);
        }
    }

    private void UpdateResonanceFields()
    {
        int index = 0;
        while (index < resonanceFields.Count)
        {
            ResonanceField field = resonanceFields[index];
            if (field == null)
            {
                resonanceFields.RemoveAt(index);
                continue;
            }

            if (Time.time >= field.EndTime)
            {
                RemoveResonanceField(field, naturalExpiry: true);
                continue;
            }

            index++;
            if (Time.time < field.NextTickTime)
                continue;

            field.NextTickTime = Time.time + field.TickInterval;
            field.TickQuery.DealDamage(
                damageDealer,
                field.Center,
                field.Radius,
                field.TickDamage,
                PlayerDamageType.Skill,
                field.Center);
        }
    }

    private void EnforceResonanceFieldCapacity()
    {
        int extraFields = Mathf.Max(0, Mathf.RoundToInt(GetCombatAugmentValue(
            PlayerCombatAugmentEffect.WizardExtraField, owner != null ? owner.SkillE : null)));
        int capacity = 1 + extraFields;
        while (resonanceFields.Count > capacity)
            RemoveResonanceField(resonanceFields[0]);
    }

    private void RemoveResonanceField(ResonanceField field, bool naturalExpiry = false)
    {
        if (field == null)
            return;

        resonanceFields.Remove(field);
        DestroyVisual(field.Visual);
        field.Visual = null;

        float collapseDamageMultiplier = naturalExpiry
            ? GetCombatAugmentValue(PlayerCombatAugmentEffect.WizardFieldCollapse, field.Skill)
            : 0f;
        if (collapseDamageMultiplier <= 0f || !CanRunEffects())
            return;

        field.CollapseQuery.DealDamage(
            damageDealer,
            field.Center,
            field.Radius,
            CalculateDamage(collapseDamageMultiplier),
            PlayerDamageType.Skill,
            field.Center);
        if (CanRunEffects())
            PlayBurst(resonanceHitPrefab, "WizardFieldCollapse",
                field.Center + Vector3.up * 0.04f, field.Radius / 2f);
    }

    // Practical authoring radii: circle 3, vortex/slash 4, hit 2, final 5 units.
    // Portal is reduced to a compact core; body aura retains its authored size.
    private WizardSkillVisual CreateVisual(GameObject prefab, string objectName,
        Vector3 position, float scale, bool continuous, Transform parent = null)
    {
        if (prefab == null)
            return null;

        GameObject visualObject = Instantiate(prefab, parent);
        visualObject.name = objectName;
        if (parent != null)
        {
            visualObject.transform.localPosition = position;
        }
        else
        {
            visualObject.transform.position = position;
        }

        visualObject.transform.localScale = Vector3.one * scale;
        WizardSkillVisual visual = visualObject.GetComponent<WizardSkillVisual>();
        activeVisuals.Add(visualObject);
        visual.Initialize(continuous, parent != null);
        NetworkSkillPresentation.Track(owner.gameObject, visualObject, prefab, continuous, parent != null);
        return visual;
    }

    private static Material CreateLineMaterial(GameObject sourcePrefab)
    {
        if (sourcePrefab == null)
            return null;

        Material source = null;
        foreach (Renderer renderer in sourcePrefab.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer.sharedMaterial != null)
            {
                source = renderer.sharedMaterial;
                break;
            }
        }

        if (source == null)
            return null;

        Material lineMaterial = new Material(source);
        if (lineMaterial.HasProperty("_BaseMap"))
            lineMaterial.SetTexture("_BaseMap", Texture2D.whiteTexture);
        if (lineMaterial.HasProperty("_MainTex"))
            lineMaterial.SetTexture("_MainTex", Texture2D.whiteTexture);
        if (lineMaterial.HasProperty("_BaseColor"))
            lineMaterial.SetColor("_BaseColor", Color.white);
        if (lineMaterial.HasProperty("_Color"))
            lineMaterial.SetColor("_Color", Color.white);
        return lineMaterial;
    }

    internal Material CreateResonanceLineMaterial()
    {
        return CreateLineMaterial(resonanceFieldPrefab);
    }

    private void AddGroundBoundary(WizardSkillVisual visual, float radius, Color color, float width)
    {
        if (visual == null || radius <= 0f)
            return;

        float rootScale = Mathf.Abs(visual.transform.lossyScale.x);
        if (rootScale <= 0.001f)
            return;

        Material lineMaterial = CreateLineMaterial(resonanceFieldPrefab);
        if (lineMaterial == null)
            return;

        visual.OwnGroundBoundaryMaterial(lineMaterial);
        GameObject lineObject = new GameObject("WizardGroundBoundary");
        lineObject.transform.SetParent(visual.transform, false);
        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = true;
        line.positionCount = 64;
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;
        line.sharedMaterial = lineMaterial;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Tile;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;

        float localRadius = radius / rootScale;
        float localSurfaceOffset = 0.02f / rootScale;
        for (int i = 0; i < line.positionCount; i++)
        {
            float angle = Mathf.PI * 2f * i / line.positionCount;
            line.SetPosition(i, new Vector3(Mathf.Cos(angle) * localRadius,
                localSurfaceOffset, Mathf.Sin(angle) * localRadius));
        }
    }

    private void ClearPreparedResonanceCast()
    {
        preparedCastSkill = null;
        preparedCastPosition = default;
        preparedCastNormal = default;
        preparedCastFrame = -1;
    }

    private void DestroyVisual(WizardSkillVisual visual)
    {
        if (visual == null)
            return;

        GameObject visualObject = visual.gameObject;
        activeVisuals.Remove(visualObject);
        Destroy(visualObject);
    }

    private void CancelAllEffects()
    {
        GetComponent<WizardSkillTargeting>()?.CancelPreview();
        ClearPreparedResonanceCast();
        if (overdrive != null)
            overdrive.Cancelled = true;
        StopAllCoroutines();

        while (resonanceFields.Count > 0)
            RemoveResonanceField(resonanceFields[0]);
        ClearResidualGravityFields();
        overdrive = null;
        RestoreAllPulls();

        foreach (GameObject visual in activeVisuals)
        {
            if (visual != null)
                Destroy(visual);
        }
        activeVisuals.Clear();
    }

    private void HandleOwnerDeath()
    {
        CancelAllEffects();
    }

    private bool CanRunEffects()
    {
        return NetworkAuthority.CanSimulate(gameObject) &&
               isActiveAndEnabled && owner != null && damageDealer != null && !IsOwnerDead();
    }

    private bool IsOwnerDead()
    {
        return (playerHealth != null && playerHealth.IsDead) ||
               (stateManager != null && stateManager.CurrentState == PlayerState.Dead);
    }

    internal bool IsOwnerCollider(Collider other)
    {
        if (owner == null || other == null)
            return false;

        Transform colliderTransform = other.transform;
        return colliderTransform == owner.transform || colliderTransform.IsChildOf(owner.transform);
    }

    private float GetCombatAugmentValue(PlayerCombatAugmentEffect effect, PlayerSkillSO targetSkill = null)
    {
        return playerAugments != null
            ? playerAugments.GetCombatAugmentValue(effect, targetSkill)
            : 0f;
    }

    private float GetAugmentedGravityPullRadius(WizardSkillSO skill)
    {
        return skill.GravityCorePullRadius * (1f + Mathf.Max(0f,
            GetCombatAugmentValue(PlayerCombatAugmentEffect.WizardGravityRadius, skill)));
    }

    private void StartResidualGravityField(WizardSkillSO skill, Vector3 center)
    {
        if (GetCombatAugmentValue(PlayerCombatAugmentEffect.WizardResidualGravity, skill) <= 0f)
            return;

        ResidualGravityField field = new ResidualGravityField
        {
            Skill = skill,
            Center = center,
            RemainingDuration = ResidualGravityDuration
        };
        Vector3 visualPosition = center;
        Vector3 visualNormal = Vector3.up;
        if (TryFindVortexFloor(center, out RaycastHit floor))
        {
            visualPosition = floor.point + floor.normal * 0.04f;
            visualNormal = floor.normal;
        }

        float radius = GetAugmentedGravityPullRadius(skill);
        field.Visual = CreateVisual(gravityVortexPrefab, "WizardResidualGravity",
            visualPosition, radius / 4f, true);
        if (field.Visual != null)
        {
            field.Visual.transform.rotation = Quaternion.FromToRotation(Vector3.up, visualNormal);
            AddGroundBoundary(field.Visual, radius, new Color(0.66f, 0.45f, 1f, 0.52f), 0.06f);
        }
        residualGravityFields.Add(field);
    }

    private void UpdateResidualGravityFields()
    {
        PruneDestroyedSlowControls();
        for (int i = residualGravityFields.Count - 1; i >= 0; i--)
        {
            ResidualGravityField field = residualGravityFields[i];
            float slowFraction = Mathf.Clamp01(GetCombatAugmentValue(
                PlayerCombatAugmentEffect.WizardResidualGravity, field.Skill));
            if (field.Skill == null || field.RemainingDuration <= 0f || slowFraction <= 0f)
            {
                RemoveResidualGravityField(field);
                continue;
            }

            field.RemainingDuration -= Time.deltaTime;
            float radius = GetAugmentedGravityPullRadius(field.Skill);
            if (field.Visual != null)
                field.Visual.transform.localScale = Vector3.one * (radius / 4f);
            IReadOnlyList<EnemyHealth> enemies = field.Query.Collect(field.Center, radius);
            field.CurrentAgents.Clear();
            for (int enemyIndex = 0; enemyIndex < enemies.Count; enemyIndex++)
            {
                EnemyHealth enemy = enemies[enemyIndex];
                NavMeshAgent agent = enemy != null ? enemy.GetComponentInParent<NavMeshAgent>() : null;
                if (agent == null || !agent.enabled || !agent.isOnNavMesh)
                    continue;

                field.CurrentAgents.Add(agent);
                field.AffectedAgents.Add(agent);
                ApplySlowContribution(agent, field, slowFraction);
            }

            field.AgentsToRelease.Clear();
            foreach (NavMeshAgent agent in field.AffectedAgents)
            {
                if (agent == null || !field.CurrentAgents.Contains(agent))
                    field.AgentsToRelease.Add(agent);
            }

            for (int agentIndex = 0; agentIndex < field.AgentsToRelease.Count; agentIndex++)
            {
                NavMeshAgent agent = field.AgentsToRelease[agentIndex];
                RemoveSlowContribution(agent, field);
                field.AffectedAgents.Remove(agent);
            }

            if (field.RemainingDuration <= 0f)
                RemoveResidualGravityField(field);
        }
    }

    private void RemoveResidualGravityField(ResidualGravityField field)
    {
        if (field == null)
            return;

        residualGravityFields.Remove(field);
        DestroyVisual(field.Visual);
        field.Visual = null;
        foreach (NavMeshAgent agent in field.AffectedAgents)
            RemoveSlowContribution(agent, field);
        field.AffectedAgents.Clear();
        field.CurrentAgents.Clear();
        field.AgentsToRelease.Clear();
    }

    private void ClearResidualGravityFields()
    {
        while (residualGravityFields.Count > 0)
            RemoveResidualGravityField(residualGravityFields[0]);
    }

    private static void ApplySlowContribution(NavMeshAgent agent, ResidualGravityField source,
        float slowFraction)
    {
        if (agent == null || source == null)
            return;

        if (!activeSlowControls.TryGetValue(agent, out SlowControl control))
        {
            control = new SlowControl
            {
                BaseSpeed = agent.speed,
                AppliedSpeed = agent.speed
            };
            activeSlowControls.Add(agent, control);
        }
        else
        {
            PreserveExternalSpeedChanges(agent, control);
        }

        control.Contributions[source] = Mathf.Clamp01(slowFraction);
        ApplyCompositeSlow(agent, control);
    }

    private static void RemoveSlowContribution(NavMeshAgent agent, ResidualGravityField source)
    {
        if (object.ReferenceEquals(agent, null) || source == null ||
            !activeSlowControls.TryGetValue(agent, out SlowControl control))
        {
            return;
        }

        if (agent != null)
            PreserveExternalSpeedChanges(agent, control);
        control.Contributions.Remove(source);
        if (control.Contributions.Count == 0)
        {
            if (agent != null)
                agent.speed = control.BaseSpeed;
            activeSlowControls.Remove(agent);
            return;
        }

        if (agent != null)
            ApplyCompositeSlow(agent, control);
    }

    private static void PreserveExternalSpeedChanges(NavMeshAgent agent, SlowControl control)
    {
        if (agent == null || Mathf.Approximately(agent.speed, control.AppliedSpeed) ||
            control.AppliedMultiplier <= 0.0001f)
        {
            return;
        }

        control.BaseSpeed = Mathf.Max(0f, agent.speed / control.AppliedMultiplier);
    }

    private static void ApplyCompositeSlow(NavMeshAgent agent, SlowControl control)
    {
        float multiplier = 1f;
        foreach (float slowFraction in control.Contributions.Values)
            multiplier *= 1f - Mathf.Clamp01(slowFraction);

        control.AppliedMultiplier = multiplier;
        control.AppliedSpeed = control.BaseSpeed * multiplier;
        agent.speed = control.AppliedSpeed;
    }

    private static void PruneDestroyedSlowControls()
    {
        destroyedSlowAgents.Clear();
        foreach (KeyValuePair<NavMeshAgent, SlowControl> pair in activeSlowControls)
        {
            if (pair.Key == null)
                destroyedSlowAgents.Add(pair.Key);
        }

        for (int i = 0; i < destroyedSlowAgents.Count; i++)
            activeSlowControls.Remove(destroyedSlowAgents[i]);
    }

    private void RefreshOverchargeExtension(Overdrive state)
    {
        if (!NetworkAuthority.CanSimulate(gameObject) ||
            state == null || state.Cancelled || state.FinalBlastTriggered)
            return;

        if (IsGamePaused)
        {
            if (!state.WasPaused)
            {
                state.PauseStartedAt = Time.time;
                state.WasPaused = true;
            }
        }
        else if (state.WasPaused)
        {
            state.BaseEndTime += Mathf.Max(0f, Time.time - state.PauseStartedAt);
            state.WasPaused = false;
        }

        float secondsPerKill = Mathf.Max(0f, GetCombatAugmentValue(
            PlayerCombatAugmentEffect.WizardOvercharge, state.Skill));
        state.AppliedOverchargeExtension = Mathf.Min(
            OverchargeTotalExtensionLimit,
            state.CreditedKillCount * secondsPerKill);
        state.EndTime = state.BaseEndTime + state.AppliedOverchargeExtension;
    }

    private int CalculateDamage(float multiplier)
    {
        float baseDamage = playerStats != null ? playerStats.AttackDamage : 10f;
        return Mathf.Max(0, Mathf.RoundToInt(baseDamage * multiplier));
    }

    private void ResolveReferences()
    {
        owner = GetComponent<PlayerSkillController>();
        damageDealer = GetComponent<PlayerDamageDealer>();
        playerStats = GetComponent<PlayerStats>();
        playerHealth = GetComponent<PlayerHealth>();
        stateManager = GetComponent<PlayerStateManager>();
        playerAugments = GetComponent<PlayerAugments>();
    }

    private static bool IsGamePaused => Time.timeScale <= 0f ||
        (GameManager.Instance != null && GameManager.Instance.IsPaused);
}
