using Unity.Netcode;
using UnityEngine;

// 기존 공격 애니메이션 이벤트에서 근접 타격 대신 음파를 발사
public sealed class BatRangedAttack : EnemyAttack
{
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField, Min(0f)] private float muzzleHeight = 0.9f;
    [SerializeField, Min(0f)] private float forwardOffset = 0.65f;
    [SerializeField, Min(0f)] private float targetHeight = 1f;

    protected override void DealDamage(Transform target)
    {
        if (projectilePrefab == null || target == null)
        {
            Debug.LogWarning("박쥐 음파 프리팹이 지정되지 않았습니다.", this);
            return;
        }

        Vector3 spawnPosition = transform.position + Vector3.up * muzzleHeight + transform.forward * forwardOffset;
        Vector3 direction = target.position + Vector3.up * targetHeight - spawnPosition;
        if (direction.sqrMagnitude < 0.001f) direction = transform.forward;
        direction.Normalize();

        GameObject instance = Instantiate(projectilePrefab, spawnPosition, Quaternion.LookRotation(direction));
        if (!instance.TryGetComponent(out BatSonicProjectile projectile))
        {
            Debug.LogWarning("박쥐 음파 프리팹에 BatSonicProjectile이 없습니다.", this);
            Destroy(instance);
            return;
        }

        // 네트워크 적은 서버가 투사체도 Spawn한다. 같은 씬에 두어 Map 언로드 시 함께 정리.
        NetworkObject owner = GetComponent<NetworkObject>();
        if (owner != null && owner.IsSpawned)
        {
            if (!instance.TryGetComponent(out NetworkObject projectileObject))
            {
                Debug.LogError("네트워크 박쥐 음파 프리팹에 NetworkObject가 필요합니다.", this);
                Destroy(instance);
                return;
            }
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(instance, gameObject.scene);
            projectileObject.Spawn(true);
            projectile.Launch(transform, direction, CurrentAttackDamage);
            return;
        }

        projectile.Launch(transform, direction, CurrentAttackDamage);
    }

    protected override void OnValidate()
    {
        base.OnValidate();
        muzzleHeight = Mathf.Max(0f, muzzleHeight);
        forwardOffset = Mathf.Max(0f, forwardOffset);
        targetHeight = Mathf.Max(0f, targetHeight);
    }
}
