using System.Collections.Generic;
using UnityEngine;

// 화염 이펙트 Trigger와 겹친 적을 추적한다.
public class FireAuraHitbox : MonoBehaviour
{
    private readonly Dictionary<EnemyHealth, int> contactCounts = new Dictionary<EnemyHealth, int>();
    private readonly List<EnemyHealth> staleEnemies = new List<EnemyHealth>();

    public void GetEnemies(List<EnemyHealth> result)
    {
        result.Clear();
        staleEnemies.Clear();

        foreach (KeyValuePair<EnemyHealth, int> contact in contactCounts)
        {
            if (contact.Key == null || contact.Key.IsDead)
            {
                staleEnemies.Add(contact.Key);
                continue;
            }

            result.Add(contact.Key);
        }

        for (int i = 0; i < staleEnemies.Count; i++)
        {
            contactCounts.Remove(staleEnemies[i]);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        EnemyHealth enemy = other.GetComponentInParent<EnemyHealth>();
        if (enemy == null)
        {
            return;
        }

        contactCounts.TryGetValue(enemy, out int count);
        contactCounts[enemy] = count + 1;
    }

    private void OnTriggerExit(Collider other)
    {
        EnemyHealth enemy = other.GetComponentInParent<EnemyHealth>();
        if (enemy == null || !contactCounts.TryGetValue(enemy, out int count))
        {
            return;
        }

        if (count <= 1)
        {
            contactCounts.Remove(enemy);
        }
        else
        {
            contactCounts[enemy] = count - 1;
        }
    }

    private void OnDisable()
    {
        contactCounts.Clear();
    }
}
