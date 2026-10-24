using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
// Enemy AI 상태와 NavMeshAgent 이동 값을 Animator 파라미터에 반영
public class EnemyAnimator : MonoBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private NavMeshAgent agent;
    [SerializeField] private float moveSpeedDampTime = 0.1f;

    private EnemyAttack enemyAttack;

    private static readonly int MoveSpeedHash = Animator.StringToHash("MoveSpeed");
    private static readonly int AttackHash = Animator.StringToHash("Attack");
    private static readonly int StrongAttackHash = Animator.StringToHash("StrongAttack");
    private static readonly int HitHash = Animator.StringToHash("Hit");
    private static readonly int DeathHash = Animator.StringToHash("Death");

    private void Awake()
    {
        if (animator == null)
        {
            // 크기 조절용 자식 모델에 Animator가 있는 적도 같은 제어 코드를 사용
            animator = GetComponentInChildren<Animator>();
        }

        if (agent == null)
        {
            agent = GetComponent<NavMeshAgent>();
        }

        enemyAttack = GetComponent<EnemyAttack>();
    }

    private void Update()
    {
        if (animator == null || agent == null)
        {
            return;
        }

        float moveSpeed = 0f;

        // 실제 이동 속도를 0~1 값으로 변환해 이동 애니메이션에 전달
        if (agent.enabled && agent.speed > 0.01f)
        {
            moveSpeed = Mathf.Clamp01(agent.velocity.magnitude / agent.speed);
        }

        animator.SetFloat(MoveSpeedHash, moveSpeed, moveSpeedDampTime, Time.deltaTime);
    }

    public void PlayAttack()
    {
        SetTrigger(AttackHash);
    }

    public void PlayStrongAttack()
    {
        ResetTrigger(AttackHash);
        SetTrigger(StrongAttackHash);
    }

    public void PlayHit()
    {
        SetTrigger(HitHash);
    }

    public void PlayDeath()
    {
        SetTrigger(DeathHash);
    }

    public void ApplyAttackDamageFromAnimationEvent()
    {
        enemyAttack?.ApplyAttackDamageFromAnimationEvent();
    }

    // 일부 공격 클립이 OnAttackHit 이벤트 이름을 사용해도 같은 타격 처리로 연결
    public void OnAttackHit()
    {
        enemyAttack?.OnAttackHit();
    }

    private void SetTrigger(int triggerHash)
    {
        if (animator == null)
        {
            return;
        }

        animator.SetTrigger(triggerHash);
    }

    private void ResetTrigger(int triggerHash)
    {
        if (animator != null)
        {
            animator.ResetTrigger(triggerHash);
        }
    }

    private void OnValidate()
    {
        moveSpeedDampTime = Mathf.Max(0f, moveSpeedDampTime);
    }
}
