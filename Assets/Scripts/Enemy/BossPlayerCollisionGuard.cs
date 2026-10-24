using UnityEngine;

// 물리 충돌의 수직 밀림을 막으면서 보스 몸통은 수평으로 통과하지 못하게 한다.
public class BossPlayerCollisionGuard : MonoBehaviour
{
    private Collider[] bossColliders;
    private CapsuleCollider bodyCollider;
    private CapsuleCollider playerCollider;
    private Rigidbody playerBody;
    private Transform playerRoot;
    private EnemyHealth bossHealth;

    private void Awake()
    {
        bossColliders = GetComponentsInChildren<Collider>();
        bodyCollider = GetComponent<CapsuleCollider>();
        bossHealth = GetComponent<EnemyHealth>();
    }

    private void OnEnable()
    {
        SpawnManager.OnPlayerSpawned += BindPlayer;
    }

    private void Start()
    {
        GameObject player = GameObject.FindGameObjectWithTag("Player");

        if (player != null)
        {
            BindPlayer(player);
        }
    }

    private void OnDisable()
    {
        SpawnManager.OnPlayerSpawned -= BindPlayer;
    }

    private void BindPlayer(GameObject player)
    {
        if (player == null)
        {
            return;
        }

        playerRoot = player.transform;
        playerBody = player.GetComponent<Rigidbody>();
        playerCollider = player.GetComponentInChildren<CapsuleCollider>();

        if (bossColliders == null || bossColliders.Length == 0)
        {
            bossColliders = GetComponentsInChildren<Collider>();
        }

        Collider[] playerColliders = player.GetComponentsInChildren<Collider>();

        foreach (Collider bossCollider in bossColliders)
        {
            if (bossCollider == null || bossCollider.isTrigger)
            {
                continue;
            }

            foreach (Collider playerPart in playerColliders)
            {
                // 화염 오라 등 스킬의 Trigger는 보스 피격 감지를 위해 유지
                if (playerPart != null && !playerPart.isTrigger)
                {
                    Physics.IgnoreCollision(bossCollider, playerPart, true);
                }
            }
        }
    }

    private void LateUpdate()
    {
        if (bossHealth != null && bossHealth.IsDead)
        {
            return;
        }

        if (bodyCollider == null || !bodyCollider.enabled || playerCollider == null
            || !playerCollider.enabled || playerRoot == null)
        {
            return;
        }

        Vector3 bossCenter = bodyCollider.transform.TransformPoint(bodyCollider.center);
        Vector3 playerCenter = playerCollider.transform.TransformPoint(playerCollider.center);
        float bossRadius = WorldRadius(bodyCollider);
        float playerRadius = WorldRadius(playerCollider);
        float bossHalfHeight = Mathf.Max(
            bossRadius, bodyCollider.height * Mathf.Abs(bodyCollider.transform.lossyScale.y) * 0.5f);
        float playerHalfHeight = Mathf.Max(
            playerRadius, playerCollider.height * Mathf.Abs(playerCollider.transform.lossyScale.y) * 0.5f);

        if (Mathf.Abs(playerCenter.y - bossCenter.y) >= bossHalfHeight + playerHalfHeight)
        {
            return;
        }

        Vector3 outward = playerCenter - bossCenter;
        outward.y = 0f;
        float distance = outward.magnitude;
        float minimumDistance = bossRadius + playerRadius + 0.05f;

        if (distance >= minimumDistance)
        {
            return;
        }

        outward = distance > 0.001f ? outward / distance : -transform.forward;
        outward.y = 0f;
        outward.Normalize();

        Vector3 position = playerRoot.position;
        position += outward * (minimumDistance - distance);
        playerRoot.position = position;

        if (playerBody != null)
        {
            playerBody.position = position;
            Vector3 velocity = playerBody.linearVelocity;
            float inwardSpeed = Vector3.Dot(velocity, outward);
            if (inwardSpeed < 0f)
            {
                playerBody.linearVelocity = velocity - outward * inwardSpeed;
            }
        }
    }

    private static float WorldRadius(CapsuleCollider capsule)
    {
        Vector3 scale = capsule.transform.lossyScale;
        return capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
    }
}
