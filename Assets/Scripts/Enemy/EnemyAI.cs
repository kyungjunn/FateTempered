using UnityEngine;
using UnityEngine.AI;
using System;
using Unity.VisualScripting;

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
    [SerializeField] private string playerTag = "Player";
    [SerializeField] private float detectionRange = 12f;
    [SerializeField] private float rotationSpeed = 12f;
    [SerializeField] private float repathRate = 0.15f;
    [SerializeField] private bool patrolWhenTargetMissing = true;
    [SerializeField] private float patrolRadius = 8f;
    [SerializeField] private float patrolWaitTime = 0.3f;
    [SerializeField] private float patrolPointTolerance = 0.7f;
    [SerializeField] private int patrolSampleAttempts = 8;

    private NavMeshAgent agent;
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

        ApplyStatsToAgent();
    }

    // 플레이어 스폰 시 이벤트 구독.
    private void OnEnable()
    {
        SpawnManager.OnPlayerSpawned += AssignTarget;
    }

    private void OnDisable()
    {
        SpawnManager.OnPlayerSpawned -= AssignTarget;
    }

    private void Start()
    {
        if (target == null)
        {
            FindTarget();
        }
    }

    private void Update()
    {
        if (enemyHealth.IsDead)
        {
            SetState(EnemyState.Dead);
            StopAgent();
            return;
        }

        if (target == null)
        {
            // 밑에서 이미 범위로 감지 중이기 때문에 매 프레임 타겟 설정하는 코드 주석처리
            //FindTarget();
            Patrol();
            //Debug.Log("Target null");

            return;
        }

        float sqrDistance = (target.position - transform.position).sqrMagnitude;
        float attackRange = enemyAttack.AttackRange;
        float currentDetectionRange = DetectionRange;


        if (sqrDistance <= attackRange * attackRange)
        {
            Attack();
            //Debug.Log("Enemy Attack");

            //return;
        }
        else if (sqrDistance <= currentDetectionRange * currentDetectionRange)
        {
            Chase();
            //Debug.Log("Enemy Chase");

            //return;
        }
        else
        {
            Patrol();
        }
    }

    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
    }

    // 플레이어 스폰 시 타겟 설정
    private void AssignTarget(GameObject player)
    {
        if (player != null)
        {
            target = player.transform;
            //Debug.Log("플레이어 타겟 설정");
        }
    }

    // Player 태그를 가진 오브젝트를 추적 대상으로 설정
    private void FindTarget()
    {
        if (string.IsNullOrWhiteSpace(playerTag))
        {
            return;
        }

        GameObject player = GameObject.FindGameObjectWithTag(playerTag);

        if (player != null)
        {
            target = player.transform;
            //Debug.Log("Find Target ");

        }
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

        agent.SetDestination(target.position);
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
        patrolRadius = Mathf.Max(0.1f, patrolRadius);
        patrolWaitTime = Mathf.Max(0f, patrolWaitTime);
        patrolPointTolerance = Mathf.Max(0.1f, patrolPointTolerance);
        patrolSampleAttempts = Mathf.Max(1, patrolSampleAttempts);

        if (Application.isPlaying)
        {
            ApplyStatsToAgent();
        }
    }
}
