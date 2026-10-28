using System.Collections.Generic;
using UnityEngine;

// 같은 플레이어를 쫓는 적들에게 플레이어 둘레의 각도를 고르게 나눠 준다.
// 시뮬레이션 인스턴스(오프라인·서버)의 EnemyAI만 사용한다.
public static class EnemySurroundSlots
{
    private static readonly Dictionary<Transform, List<EnemyAI>> chasers = new Dictionary<Transform, List<EnemyAI>>();
    private static readonly List<EnemyAI> sorted = new List<EnemyAI>(32);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        chasers.Clear();
    }

    public static void Assign(EnemyAI enemy, Transform target)
    {
        if (enemy == null) return;
        Release(enemy);
        if (target == null) return;
        if (!chasers.TryGetValue(target, out List<EnemyAI> list))
        {
            list = new List<EnemyAI>(8);
            chasers.Add(target, list);
        }
        list.Add(enemy);
    }

    public static void Release(EnemyAI enemy)
    {
        Transform key = null;
        foreach (KeyValuePair<Transform, List<EnemyAI>> pair in chasers)
        {
            if (!pair.Value.Remove(enemy)) continue;
            if (pair.Value.Count == 0) key = pair.Key;
            break;
        }
        if (!ReferenceEquals(key, null)) chasers.Remove(key);
    }

    public static int CountFor(Transform target)
    {
        return target != null && chasers.TryGetValue(target, out List<EnemyAI> list) ? list.Count : 0;
    }

    // 현재 방위각 순서를 유지한 채 균등 분할한 각도(라디안). 혼자면 자기 방위각.
    public static float GetSlotAngle(EnemyAI enemy, Transform target)
    {
        Vector3 center = target.position;
        float own = Bearing(enemy.transform.position, center);
        if (!chasers.TryGetValue(target, out List<EnemyAI> list) || list.Count <= 1) return own;

        list.RemoveAll(e => e == null);
        sorted.Clear();
        sorted.AddRange(list);
        sorted.Sort((a, b) => Bearing(a.transform.position, center).CompareTo(Bearing(b.transform.position, center)));

        int count = sorted.Count;
        int index = sorted.IndexOf(enemy);
        if (index < 0) return own;

        // 기준 각을 현재 방위각들의 원형 평균 편차로 잡아 슬롯이 무리 전체와 함께 회전하게 한다.
        float step = Mathf.PI * 2f / count;
        float sin = 0f, cos = 0f;
        for (int i = 0; i < count; i++)
        {
            float offset = Bearing(sorted[i].transform.position, center) - i * step;
            sin += Mathf.Sin(offset);
            cos += Mathf.Cos(offset);
        }
        float baseAngle = Mathf.Atan2(sin, cos);
        return baseAngle + index * step;
    }

    private static float Bearing(Vector3 position, Vector3 center)
    {
        return Mathf.Atan2(position.z - center.z, position.x - center.x);
    }
}
