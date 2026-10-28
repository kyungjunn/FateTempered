using UnityEngine;
using UnityEngine.AI;
using System;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(EnemyAttack))]
[RequireComponent(typeof(EnemyHealth))]
// 적의 이동 상태와 추적 행동을 관리하는 AI
public class EnemyAI : MonoBehaviour
{
    // 적 AI 상태
    public enum EnemyState
    {
        Idle,
        Patrol,
        Chase,
        Attack,
        Dead
    }

    [SerializeField] private EnemyStats stats;
    private Transform target;
    [SerializeField] private float detectionRange = 12f;
    [SerializeField] private float rotationSpeed = 12f;
    [SerializeField] private float repathRate = 0.15f;
    [SerializeField] private bool patrolWhenTargetMissing = true;
    [SerializeField] private float patrolRadius = 8f;
    [SerializeField] private float patrolWaitTime = 0.3f;
    [SerializeField] private float patrolPointTolerance = 0.7f;
    [SerializeField] private int patrolSampleAttempts = 8;

    [SerializeField] private float retargetInterval = 0.2f;

    [Header("Crowd")]
    [Tooltip("같은 플레이어를 쫓는 적끼리 둘레 각도를 나눠 포위한다.")]
    [SerializeField] private bool surroundTarget = true;
    [Tooltip("포위 슬롯로 우회하기 시작하는 거리(공격 사거리 배수). 이보다 멀면 플레이어로 직진.")]
    [SerializeField] private float surroundEngageRangeMultiplier = 3f;
    [Tooltip("주변 적과 이 거리(에이전트 반경 합 배수)보다 가까우면 밀어낸다.")]
    [SerializeField] private float separationRadiusMultiplier = 1.25f;
    [SerializeField] private float separationSpeed = 2f;
    [SerializeField] private LayerMask separationLayers = ~0;

    private static readonly Collider[] separationHits = new Collider[16];
    private Transform slotTarget;

    private NavMeshAgent agent;
    private EnemyAggro aggro;
    private float nextRetargetTime;
    private EnemyAttack enemyAttack;
    private EnemyHealth enemyHealth;
    private EnemyState currentState;
    private Vector3 patrolAnchorPosition;
    private float nextRepathTime;
    private float nextPatrolTime;
    private bool waitingForNextPatrolPoint;

    public EnemyState CurrentState => currentState;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        enemyAttack = GetComponent<EnemyAttack>();
        enemyHealth = GetComponent<EnemyHealth>();
        aggro = GetComponent<EnemyAggro>();
        if (aggro == null) aggro = gameObject.AddComponent<EnemyAggro>();

        ApplyStatsToAgent();
    }

    private void Update()
    {
        if (!NetworkAuthority.CanSimulate(gameObject))
        {
            enabled = false;
            return;
        }
        if (enemyHealth.IsDead)
        {
            SetState(EnemyState.Dead);
            StopAgent();
            ReleaseSlot();
            return;
        }

        if (Time.time >= nextRetargetTime)
        {
            target = aggro.SelectTarget(transform.position, DetectionRange);
            nextRetargetTime = Time.time + retargetInterval;
        }

        if (target == null)
        {
            ReleaseSlot();
            Patrol();
            return;
        }

        if (surroundTarget && slotTarget != target)
        {
            EnemySurroundSlots.Assign(this, target);
            slotTarget = target;
        }

        // 거리 제한은 EnemyAggro.SelectTarget이 이미 적용했다.
        float sqrDistance = (target.position - transform.position).sqrMagnitude;
        float attackRange = enemyAttack.AttackRange;

        // 무리 안에서는 사거리에 들어와도 자기 슬롯에 닿은 뒤 공격해 한쪽에 몰리지 않게 한다.
        if (sqrDistance <= attackRange * attackRange && !NeedsToReachSlot())
        {
            Attack();
        }
        else
        {
            Chase();
        }

        ApplySeparation();
    }

    private void OnDisable()
    {
        ReleaseSlot();
    }

    private void ReleaseSlot()
    {
        if (slotTarget == null) return;
        EnemySurroundSlots.Release(this);
        slotTarget = null;
    }

    // 근처까지 오면 플레이어 둘레의 자기 슬롯을 향한다. 멀리서는 직진.
    private Vector3 GetChaseDestination()
    {
        Vector3 targetPosition = target.position;
        if (!surroundTarget || EnemySurroundSlots.CountFor(target) <= 1)
            return targetPosition;

        float attackRange = enemyAttack.AttackRange;
        float engage = attackRange * surroundEngageRangeMultiplier;
        if ((targetPosition - transform.position).sqrMagnitude > engage * engage)
            return targetPosition;

        return TryGetSlotPosition(out Vector3 slot) ? slot : targetPosition;
    }

    // 플레이어 둘레의 자기 슬롯 위치. NavMesh 밖이면 false.
    private bool TryGetSlotPosition(out Vector3 slot)
    {
        slot = default;
        float attackRange = enemyAttack.AttackRange;
        float angle = EnemySurroundSlots.GetSlotAngle(this, target);
        float ring = Mathf.Max(0.5f, attackRange * 0.8f);
        Vector3 point = target.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ring;
        if (!CanUseAgent() || !NavMesh.SamplePosition(point, out NavMeshHit hit, 1.5f, agent.areaMask))
            return false;
        slot = hit.position;
        return true;
    }

    // 슬롯에서 멀면 아직 공격하지 않고 슬롯으로 이동한다.
    // 이미 시작한 공격 주기는 끊지 않는다.
    private bool NeedsToReachSlot()
    {
        if (!surroundTarget || EnemySurroundSlots.CountFor(target) <= 1 || enemyAttack.IsAttackCycleActive)
            return false;
        if (!TryGetSlotPosition(out Vector3 slot))
            return false;
        Vector3 offset = slot - transform.position;
        offset.y = 0f;
        float tolerance = Mathf.Max(0.4f, agent.radius * 1.5f);
        return offset.sqrMagnitude > tolerance * tolerance;
    }

    // NavMesh 회피만으로는 공격 중(정지) 적끼리 겹친다. 정지·추적 중 모두 가볍게 밀어낸다.
    private void ApplySeparation()
    {
        if (!CanUseAgent() || separationSpeed <= 0f) return;

        float radius = agent.radius;
        float probe = radius * 2f * separationRadiusMultiplier;
        int count = Physics.OverlapSphereNonAlloc(transform.position, probe, separationHits, separationLayers,
            QueryTriggerInteraction.Ignore);
        Vector3 push = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            Collider hit = separationHits[i];
            separationHits[i] = null;
            if (hit == null) continue;
            EnemyAI other = hit.GetComponentInParent<EnemyAI>();
            if (other == null || other == this || other.enemyHealth.IsDead) continue;

            Vector3 away = transform.position - other.transform.position;
            away.y = 0f;
            float minDistance = (radius + other.agent.radius) * separationRadiusMultiplier;
            float distance = away.magnitude;
            if (distance >= minDistance) continue;
            if (distance < 0.001f)
            {
                // 완전히 겹치면 인스턴스 순서로 방향을 정해 둘이 같은 쪽으로 밀리지 않게 한다.
                away = GetInstanceID() < other.GetInstanceID() ? transform.right : -transform.right;
                distance = 0f;
            }
            push += away.normalized * ((minDistance - distance) / minDistance);
        }

        if (push.sqrMagnitude < 0.0001f) return;
        agent.Move(Vector3.ClampMagnitude(push, 1f) * (separationSpeed * Time.deltaTime));
    }

    // 대기 상태
    private void Idle()
    {
        SetState(EnemyState.Idle);
        StopAgent();
    }

    // 플레이어가 탐지 범위 밖이면 시작 지점 주변을 랜덤 정찰
    private void Patrol()
    {
        if (!patrolWhenTargetMissing)
        {
            Idle();
            return;
        }

        bool justEnteredPatrol = currentState != EnemyState.Patrol;
        SetState(EnemyState.Patrol);

        if (!CanUseAgent())
        {
            return;
        }

        agent.isStopped = false;

        if (justEnteredPatrol)
        {
            patrolAnchorPosition = transform.position;
            waitingForNextPatrolPoint = false;
            nextPatrolTime = Time.time;

            if (agent.hasPath)
            {
                agent.ResetPath();
            }
        }

        if (agent.pathPending)
        {
            return;
        }

        if (!HasReachedDestination())
        {
            waitingForNextPatrolPoint = false;
            return;
        }

        if (!waitingForNextPatrolPoint)
        {
            nextPatrolTime = justEnteredPatrol ? Time.time : Time.time + PatrolWaitTime;
            waitingForNextPatrolPoint = true;
        }

        if (Time.time < nextPatrolTime)
        {
            return;
        }

        if (TrySetRandomPatrolDestination())
        {
            waitingForNextPatrolPoint = false;
            return;
        }

        nextPatrolTime = Time.time + PatrolWaitTime;
    }

    // NavMeshAgent를 이용해 플레이어 추적
    private void Chase()
    {
        SetState(EnemyState.Chase);

        if (!CanUseAgent())
        {
            return;
        }

        agent.isStopped = false;

        if (Time.time < nextRepathTime)
        {
            return;
        }

        agent.SetDestination(GetChaseDestination());
        nextRepathTime = Time.time + RepathRate;
    }

    // 공격 상태
    private void Attack()
    {
        SetState(EnemyState.Attack);
        StopAgent();

        // 보스는 일반 공격 준비 중에만 방향을 돌리고, 강공격과 대기 중에는 후방을 노릴 틈을 준다.
        if ((!enemyAttack.LockFacingDuringAttack || !enemyAttack.IsAttackCycleActive)
            && enemyAttack.ShouldFaceTarget)
        {
            FaceTarget();
        }

        enemyAttack.TryAttack(target);
    }

    // 공격 중 플레이어 방향으로 회전
    private void FaceTarget()
    {
        Vector3 direction = target.position - transform.position;
        direction.y = 0f;

        if (direction.sqrMagnitude < 0.001f)
        {
            return;
        }

        Quaternion targetRotation = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, RotationSpeed * Time.deltaTime);
    }

    // 이동 정지 및 경로 초기화
    private void StopAgent()
    {
        if (!CanUseAgent())
        {
            return;
        }

        agent.isStopped = true;

        if (agent.hasPath)
        {
            agent.ResetPath();
        }

        waitingForNextPatrolPoint = false;
    }

    private bool CanUseAgent()
    {
        return agent != null && agent.enabled && agent.isOnNavMesh;
    }

    private bool HasReachedDestination()
    {
        if (!agent.hasPath || agent.pathStatus == NavMeshPathStatus.PathInvalid)
        {
            return true;
        }

        if (float.IsInfinity(agent.remainingDistance))
        {
            return true;
        }

        float reachedDistance = Mathf.Max(PatrolPointTolerance, agent.stoppingDistance + 0.1f);
        return agent.remainingDistance <= reachedDistance;
    }

    private bool TrySetRandomPatrolDestination()
    {
        float currentPatrolRadius = PatrolRadius;

        for (int i = 0; i < patrolSampleAttempts; i++)
        {
            Vector2 randomPoint = UnityEngine.Random.insideUnitCircle * currentPatrolRadius;
            Vector3 samplePosition = patrolAnchorPosition + new Vector3(randomPoint.x, 0f, randomPoint.y);

            if (!NavMesh.SamplePosition(samplePosition, out NavMeshHit hit, 2f, agent.areaMask))
            {
                continue;
            }

            float sqrTolerance = PatrolPointTolerance * PatrolPointTolerance;

            if ((hit.position - transform.position).sqrMagnitude <= sqrTolerance)
            {
                continue;
            }

            if (agent.SetDestination(hit.position))
            {
                patrolAnchorPosition = transform.position;
                return true;
            }
        }

        return false;
    }

    // 현재 상태 갱신
    private void SetState(EnemyState nextState)
    {
        currentState = nextState;
    }

    private float DetectionRange => stats != null ? stats.DetectionRange : detectionRange;
    private float RotationSpeed => stats != null ? stats.RotationSpeed : rotationSpeed;
    private float RepathRate => stats != null ? stats.RepathRate : repathRate;
    private float PatrolRadius => stats != null && stats.PatrolRadius > 0f ? stats.PatrolRadius : patrolRadius;
    private float PatrolWaitTime => Mathf.Max(0f, stats != null ? stats.PatrolWaitTime : patrolWaitTime);
    private float PatrolPointTolerance => Mathf.Max(0.1f, stats != null ? stats.PatrolPointTolerance : patrolPointTolerance);

    // EnemyStats 데이터가 있으면 NavMeshAgent에 이동 설정 적용
    private void ApplyStatsToAgent()
    {
        if (stats == null || agent == null)
        {
            return;
        }

        agent.speed = stats.MoveSpeed;
        agent.acceleration = stats.Acceleration;
        agent.stoppingDistance = stats.StoppingDistance;
    }

    private void OnValidate()
    {
        detectionRange = Mathf.Max(0.1f, detectionRange);
        rotationSpeed = Mathf.Max(0.1f, rotationSpeed);
        repathRate = Mathf.Max(0.02f, repathRate);
        retargetInterval = Mathf.Max(0.05f, retargetInterval);
        patrolRadius = Mathf.Max(0.1f, patrolRadius);
        patrolWaitTime = Mathf.Max(0f, patrolWaitTime);
        patrolPointTolerance = Mathf.Max(0.1f, patrolPointTolerance);
        patrolSampleAttempts = Mathf.Max(1, patrolSampleAttempts);
        surroundEngageRangeMultiplier = Mathf.Max(1f, surroundEngageRangeMultiplier);
        separationRadiusMultiplier = Mathf.Max(1f, separationRadiusMultiplier);
        separationSpeed = Mathf.Max(0f, separationSpeed);

        if (Application.isPlaying)
        {
            ApplyStatsToAgent();
        }
    }
}
