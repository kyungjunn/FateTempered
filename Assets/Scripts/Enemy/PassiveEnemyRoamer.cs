using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(EnemyHealth))]
// 플레이어와 무관하게 이동 가능한 NavMesh 구역을 계속 순회하는 이벤트 적
public sealed class PassiveEnemyRoamer : MonoBehaviour
{
    [SerializeField] private EnemyStats stats;
    [SerializeField] private float roamRadius = 10f;
    [SerializeField] private float waitTime = 0.2f;
    [SerializeField] private float arrivalTolerance = 0.5f;
    [SerializeField] private int sampleAttempts = 12;

    private NavMeshAgent agent;
    private EnemyHealth health;
    private float nextDestinationTime;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        health = GetComponent<EnemyHealth>();
        if (stats == null) return;
        agent.speed = stats.MoveSpeed;
        agent.acceleration = stats.Acceleration;
        agent.stoppingDistance = stats.StoppingDistance;
    }

    private void Update()
    {
        if (health.IsDead || !agent.enabled || !agent.isOnNavMesh || agent.pathPending) return;
        if (agent.hasPath && agent.pathStatus == NavMeshPathStatus.PathComplete
            && !float.IsInfinity(agent.remainingDistance)
            && agent.remainingDistance > Mathf.Max(arrivalTolerance, agent.stoppingDistance + 0.1f)) return;
        if (Time.time < nextDestinationTime) return;

        for (int i = 0; i < sampleAttempts; i++)
        {
            Vector2 offset = Random.insideUnitCircle * roamRadius;
            Vector3 candidate = transform.position + new Vector3(offset.x, 0f, offset.y);
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, agent.areaMask)) continue;
            if ((hit.position - transform.position).sqrMagnitude < arrivalTolerance * arrivalTolerance) continue;
            var path = new NavMeshPath();
            if (!agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            if (!agent.SetPath(path)) continue;
            nextDestinationTime = Time.time + waitTime;
            return;
        }

        nextDestinationTime = Time.time + Mathf.Max(waitTime, 0.25f);
    }

    private void OnValidate()
    {
        roamRadius = Mathf.Max(0.5f, roamRadius);
        waitTime = Mathf.Max(0f, waitTime);
        arrivalTolerance = Mathf.Max(0.1f, arrivalTolerance);
        sampleAttempts = Mathf.Max(1, sampleAttempts);
    }
}
