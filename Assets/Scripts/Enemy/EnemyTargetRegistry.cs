using System.Collections.Generic;
using UnityEngine;

// 적이 노릴 수 있는 플레이어 목록. 오프라인·네트워크 공통.
public static class EnemyTargetRegistry
{
    private static readonly List<PlayerHealth> players = new List<PlayerHealth>(2);

    public static IReadOnlyList<PlayerHealth> Players => players;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        players.Clear();
    }

    public static void Register(PlayerHealth player)
    {
        if (player != null && !players.Contains(player)) players.Add(player);
    }

    public static void Unregister(PlayerHealth player)
    {
        players.Remove(player);
    }

    public static bool IsAlive(PlayerHealth player)
    {
        return player != null && player.isActiveAndEnabled && !player.IsDead;
    }

    public static PlayerHealth FindClosest(Vector3 from, float maxRange)
    {
        PlayerHealth closest = null;
        float best = maxRange > 0f ? maxRange * maxRange : float.PositiveInfinity;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (!IsAlive(player)) continue;
            float sqr = (player.transform.position - from).sqrMagnitude;
            if (sqr > best) continue;
            best = sqr;
            closest = player;
        }
        return closest;
    }

    // 살아 있는 플레이어 중 가장 가까운 거리의 제곱. 없으면 false.
    public static bool TryGetClosestSqrDistance(Vector3 from, out float sqrDistance)
    {
        sqrDistance = float.PositiveInfinity;
        bool found = false;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (!IsAlive(player)) continue;
            sqrDistance = Mathf.Min(sqrDistance, (player.transform.position - from).sqrMagnitude);
            found = true;
        }
        return found;
    }
}
