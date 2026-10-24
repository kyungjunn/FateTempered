using System;
using UnityEngine;

// 발사 시에만 높이를 보정한다. 좌우 조준과 발사 후 직선 이동은 유지한다.
public sealed class ProjectileHeightAim
{
    private Collider[] candidates = new Collider[32];
    private RaycastHit[] obstacles = new RaycastHit[32];

    public Vector3 Resolve(Vector3 origin, Vector3 direction, float range, Transform owner,
        float horizontalAngle, float verticalAngle)
    {
        Vector3 forward = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.001f || range <= 0f) return direction.normalized;

        int count;
        while (true)
        {
            count = Physics.OverlapSphereNonAlloc(origin, range, candidates, ~0, QueryTriggerInteraction.Collide);
            if (count < candidates.Length) break;
            Array.Resize(ref candidates, candidates.Length * 2);
        }

        Vector3 result = direction.normalized;
        float bestAlignment = -1f;
        float bestDistance = float.PositiveInfinity;
        float minimumAlignment = Mathf.Cos(horizontalAngle * Mathf.Deg2Rad);
        for (int i = 0; i < count; i++)
        {
            Collider collider = candidates[i];
            candidates[i] = null;
            if (owner != null && collider.transform.IsChildOf(owner)) continue;
            EnemyHealth enemy = collider.GetComponentInParent<EnemyHealth>();
            if (enemy == null || enemy.IsDead) continue;

            Vector3 offset = collider.bounds.center - origin;
            Vector3 planar = Vector3.ProjectOnPlane(offset, Vector3.up);
            float along = Vector3.Dot(planar, forward);
            if (along <= 0.01f || offset.sqrMagnitude > range * range) continue;
            float alignment = Vector3.Dot(planar.normalized, forward);
            if (alignment < minimumAlignment) continue;
            if (Mathf.Abs(Mathf.Atan2(offset.y, along) * Mathf.Rad2Deg) > verticalAngle) continue;

            Vector3 corrected = (forward * along + Vector3.up * offset.y).normalized;
            // 좌우 방향을 바꾸지 않고 실제로 피격 영역을 통과할 수 있는 적만 선택한다.
            if (!collider.Raycast(new Ray(origin, corrected), out RaycastHit targetHit, range)) continue;
            if (alignment < bestAlignment ||
                (Mathf.Approximately(alignment, bestAlignment) && targetHit.distance >= bestDistance)) continue;
            if (IsBlocked(origin, corrected, targetHit.distance, owner, enemy)) continue;

            bestAlignment = alignment;
            bestDistance = targetHit.distance;
            result = corrected;
        }
        return result;
    }

    private bool IsBlocked(Vector3 origin, Vector3 direction, float distance, Transform owner, EnemyHealth target)
    {
        int count;
        while (true)
        {
            count = Physics.RaycastNonAlloc(origin, direction, obstacles, distance, ~0, QueryTriggerInteraction.Ignore);
            if (count < obstacles.Length) break;
            Array.Resize(ref obstacles, obstacles.Length * 2);
        }

        for (int i = 0; i < count; i++)
        {
            Collider collider = obstacles[i].collider;
            if (owner != null && collider.transform.IsChildOf(owner)) continue;
            if (collider.GetComponentInParent<EnemyHealth>() == target) continue;
            if (collider.GetComponentInParent<DamageProjectile>() != null) continue;
            return true;
        }
        return false;
    }
}
