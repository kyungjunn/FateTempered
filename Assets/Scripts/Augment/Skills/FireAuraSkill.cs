using System.Collections.Generic;
using UnityEngine;

// 플레이어 주위를 도는 화염 이펙트. 스택마다 하나씩 늘고 같은 간격으로 재배치된다.
public class FireAuraSkill : AugmentSkill
{
    [Header("Visual")]
    // 궤도에 생성할 화염 이펙트 프리팹
    [SerializeField] private GameObject fireEffectPrefab;
    // 이펙트 크기
    [SerializeField] private float effectScale = 0.3f;

    [Header("Orbit")]
    // 궤도 반경
    [SerializeField] private float orbitRadius = 2.5f;
    // 초당 회전각
    [SerializeField] private float orbitSpeed = 120f;

    [Header("Damage")]
    // 타격 주기
    [SerializeField] private float damageInterval = 0.5f;
    // 공격력 계수
    [SerializeField] private float attackDamageRatio = 0.35f;

    private readonly List<Transform> effects = new List<Transform>();
    private readonly List<EnemyHealth> hitEnemies = new List<EnemyHealth>();

    private PlayerStats playerStats;
    private PlayerDamageDealer damageDealer;
    private float orbitAngle;
    private float damageTimer;

    protected override void OnApply()
    {
        playerStats = Player.GetComponent<PlayerStats>();
        damageDealer = Player.GetComponent<PlayerDamageDealer>();
        SyncEffectCount();
    }

    protected override void OnStackChanged()
    {
        SyncEffectCount();
    }

    protected override void OnRemove()
    {
        for (int i = 0; i < effects.Count; i++)
        {
            if (effects[i] != null)
            {
                Destroy(effects[i].gameObject);
            }
        }

        effects.Clear();
    }

    private void Update()
    {
        if (Time.timeScale <= 0f)
        {
            return;
        }

        orbitAngle = Mathf.Repeat(orbitAngle + orbitSpeed * Time.deltaTime, 360f);
        UpdateEffectPositions();

        damageTimer -= Time.deltaTime;
        if (damageTimer <= 0f)
        {
            damageTimer = damageInterval;
            DamageNearbyEnemies();
        }
    }

    // 이펙트 수 동기화
    private void SyncEffectCount()
    {
        if (fireEffectPrefab == null)
        {
            Debug.LogError($"{nameof(FireAuraSkill)}에 생성할 화염 이펙트 프리팹이 없습니다.", this);
            return;
        }

        while (effects.Count < StackCount)
        {
            effects.Add(CreateEffect(effects.Count));
        }

        while (effects.Count > StackCount)
        {
            int lastIndex = effects.Count - 1;
            Transform effect = effects[lastIndex];
            effects.RemoveAt(lastIndex);

            if (effect != null)
            {
                effect.gameObject.SetActive(false);
                Destroy(effect.gameObject);
            }
        }

        UpdateEffectPositions();
    }

    private Transform CreateEffect(int index)
    {
        GameObject effect = Instantiate(fireEffectPrefab, transform);
        effect.name = $"FireAuraEffect_{index + 1}";
        effect.transform.localScale = Vector3.one * effectScale;
        return effect.transform;
    }

    // 등간격 궤도 배치
    private void UpdateEffectPositions()
    {
        if (effects.Count == 0)
        {
            return;
        }

        float angleStep = 360f / effects.Count;
        for (int i = 0; i < effects.Count; i++)
        {
            if (effects[i] == null)
            {
                continue;
            }

            float radians = (orbitAngle + angleStep * i) * Mathf.Deg2Rad;
            effects[i].localPosition = new Vector3(
                Mathf.Cos(radians) * orbitRadius,
                0.8f,
                Mathf.Sin(radians) * orbitRadius);
        }
    }

    // 각 이펙트의 Trigger 안에 있는 적에게 주기적으로 피해 적용
    private void DamageNearbyEnemies()
    {
        int damage = Mathf.Max(1, Mathf.RoundToInt(
            (playerStats != null ? playerStats.AttackDamage : 1f) * attackDamageRatio));

        for (int i = 0; i < effects.Count; i++)
        {
            Transform effect = effects[i];
            if (effect == null)
            {
                continue;
            }

            FireAuraHitbox hitbox = effect.GetComponent<FireAuraHitbox>();
            if (hitbox == null)
            {
                continue;
            }

            hitbox.GetEnemies(hitEnemies);
            for (int hitIndex = 0; hitIndex < hitEnemies.Count; hitIndex++)
            {
                EnemyHealth enemy = hitEnemies[hitIndex];
                if (enemy != null)
                {
                    if (damageDealer != null)
                    {
                        damageDealer.DealDamage(enemy, damage, PlayerDamageType.Skill);
                    }
                    else
                    {
                        enemy.TakeDamage(damage);
                    }
                }
            }
        }
    }

    private void OnValidate()
    {
        orbitRadius = Mathf.Max(0.1f, orbitRadius);
        effectScale = Mathf.Max(0.05f, effectScale);
        damageInterval = Mathf.Max(0.05f, damageInterval);
        attackDamageRatio = Mathf.Max(0f, attackDamageRatio);
    }
}
