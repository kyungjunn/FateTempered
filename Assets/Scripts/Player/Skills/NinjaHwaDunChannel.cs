using System.Collections.Generic;
using DigitalRuby.PyroParticles;
using UnityEngine;

// 화둔 지속 관리: 틱 피해, 시전자 충돌 제외, 종료 알림.
public sealed class NinjaHwaDunChannel : MonoBehaviour
{
    private readonly List<EnemyHealth> hitEnemies = new List<EnemyHealth>();

    private PlayerSkillController owner;
    private PlayerSkillSO skill;
    private PlayerDamageDealer damageDealer;
    private FireAuraHitbox hitbox;
    private float tickInterval;
    private float endTime;
    private float nextTickTime;
    private int damage;
    private bool stopped;

    public void Initialize(
        PlayerSkillController skillOwner,
        PlayerSkillSO channelSkill,
        int tickDamage,
        float interval,
        float channelDuration)
    {
        // 시전자/스킬 정보 및 피해 간격, 종료 시각 기록.
        owner = skillOwner;
        skill = channelSkill;
        damageDealer = skillOwner != null ? skillOwner.DamageDealer : null;
        damage = Mathf.Max(1, tickDamage);
        tickInterval = Mathf.Max(0.05f, interval);
        endTime = Time.time + Mathf.Max(0.05f, channelDuration);
        nextTickTime = Time.time;
        hitbox = GetComponent<FireAuraHitbox>();
        // 중첩 적 탐색용 히트박스가 없으면 생성.
        if (hitbox == null)
            hitbox = gameObject.AddComponent<FireAuraHitbox>();

        IgnoreOwnerColliders();
    }

    private void Update()
    {
        // 종료 여부 → 시전자 생존 → 지속시간 → 다음 틱 순서로 검사.
        if (stopped)
            return;

        if (owner == null || IsOwnerDead())
        {
            StopChannel(false);
            return;
        }

        if (Time.time >= endTime)
        {
            StopChannel(true);
            return;
        }

        if (Time.time < nextTickTime)
            return;

        nextTickTime = Time.time + tickInterval;
        // 틱 간격마다 현재 범위 안의 적에게 피해.
        ApplyTickDamage();
    }

    private void StopChannel(bool notifyOwner)
    {
        // 중복 종료 방지 및 히트박스 비활성화.
        if (stopped)
            return;

        stopped = true;
        enabled = false;

        Collider collider = GetComponent<Collider>();
        if (collider != null)
            collider.enabled = false;

        FireBaseScript fire = GetComponent<FireBaseScript>();
        // 화염 이펙트 정지. 이펙트 스크립트가 없으면 오브젝트 제거.
        if (fire != null)
            fire.Stop();
        else
            Destroy(gameObject);

        if (notifyOwner && owner != null)
            // 정상 종료만 컨트롤러에 알려 지연된 쿨타임 시작.
            owner.NotifyChannelFinished(skill);
    }
    private bool IsOwnerDead()
    {
        PlayerStateManager stateManager = owner != null ? owner.GetComponent<PlayerStateManager>() : null;
        return stateManager != null && stateManager.CurrentState == PlayerState.Dead;
    }

    private void IgnoreOwnerColliders()
    {
        // 화염 트리거와 시전자 콜라이더 간 충돌 무시.
        Collider self = GetComponent<Collider>();
        if (self == null || owner == null)
            return;

        Collider[] ownerColliders = owner.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < ownerColliders.Length; i++)
        {
            if (ownerColliders[i] != null && ownerColliders[i] != self)
                Physics.IgnoreCollision(self, ownerColliders[i], true);
        }
    }

    private void ApplyTickDamage()
    {
        // 현재 히트박스에 잡힌 적을 조회하고 각 적에게 틱 피해.
        if (hitbox == null || damageDealer == null)
            return;

        hitbox.GetEnemies(hitEnemies);
        for (int i = 0; i < hitEnemies.Count; i++)
        {
            EnemyHealth enemy = hitEnemies[i];
            if (enemy != null)
                damageDealer.DealDamage(enemy, damage, PlayerDamageType.Skill);
        }
    }
}
