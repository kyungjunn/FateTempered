using System.Collections.Generic;
using UnityEngine;

// 물리 충돌의 수직 밀림을 막으면서 보스 몸통은 수평으로 통과하지 못하게 한다.
// 시뮬레이션 인스턴스(오프라인/서버)에서 모든 플레이어에 적용한다.
public class BossPlayerCollisionGuard : MonoBehaviour
{
    private Collider[] bossColliders;
    private CapsuleCollider bodyCollider;
    private EnemyHealth bossHealth;
    private readonly HashSet<PlayerHealth> ignoredPlayers = new HashSet<PlayerHealth>();

    private void Awake()
    {
        bossColliders = GetComponentsInChildren<Collider>();
        bodyCollider = GetComponent<CapsuleCollider>();
        bossHealth = GetComponent<EnemyHealth>();
    }

    private void OnDisable()
    {
        ignoredPlayers.Clear();
    }

    private void IgnorePlayerCollisions(PlayerHealth player)
    {
        if (!ignoredPlayers.Add(player)) return;

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
        // 서버·오프라인만 충돌 무시 유지; 클라이언트 비활성.
        if (!NetworkAuthority.CanSimulate(gameObject))
        {
            enabled = false;
            return;
        }
        if ((bossHealth != null && bossHealth.IsDead) || bodyCollider == null || !bodyCollider.enabled)
        {
            return;
        }

        IReadOnlyList<PlayerHealth> players = EnemyTargetRegistry.Players;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || !player.isActiveAndEnabled) continue;
            IgnorePlayerCollisions(player);
            PushOut(player);
        }
    }

    private void PushOut(PlayerHealth player)
    {
        CapsuleCollider playerCollider = player.GetComponentInChildren<CapsuleCollider>();
        if (playerCollider == null || !playerCollider.enabled) return;

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

        Transform playerRoot = player.transform;
        Vector3 position = playerRoot.position + outward * (minimumDistance - distance);
        playerRoot.position = position;

        if (player.TryGetComponent(out Rigidbody playerBody))
        {
            playerBody.position = position;
            if (playerBody.isKinematic) return;
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
