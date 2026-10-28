using DigitalRuby.PyroParticles;
using UnityEngine;

// 화둔 설정/시전: 증강 적용, 화염 이펙트와 지속 피해 판정 생성.
[CreateAssetMenu(menuName = "Player/Skills/Ninja HwaDun")]
public sealed class NinjaHwaDunSkillSO : PlayerSkillSO
{
    [SerializeField] private GameObject flamethrowerPrefab;
    [SerializeField, Min(0.05f)] private float duration = 2f;
    [SerializeField, Min(0.05f)] private float tickInterval = 0.25f;
    [SerializeField, Min(0f)] private float damageMultiplier = 1f;
    [SerializeField] private Vector3 hitboxSize = new Vector3(1.2f, 1.2f, 6f);
    [SerializeField] private Vector3 hitboxCenter = new Vector3(0f, 0f, 3f);

    // 채널 종료 후 쿨타임 시작.
    public override bool DefersCooldown => true;

    public override bool Cast(PlayerSkillController owner, PlayerDamageType damageType)
    {
        // 필수 참조 및 기존 화둔 채널 중복 검사.
        if (flamethrowerPrefab == null || owner == null || owner.SkillOrigin == null ||
            owner.DamageDealer == null || !NetworkAuthority.CanSimulate(owner.gameObject))
            return false;

        if (owner.GetComponentInChildren<NinjaHwaDunChannel>(true) != null)
            return false;

        // 전투 증강의 피해/범위/지속시간 보너스를 각각 조회.
        Transform origin = owner.SkillOrigin;
        PlayerAugments playerAugments = owner.GetComponent<PlayerAugments>();
        float combatDamageBonus = playerAugments != null
            ? playerAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillDamage, this)
            : 0f;
        float combatRangeBonus = playerAugments != null
            ? playerAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillRange, this)
            : 0f;
        float combatDurationBonus = playerAugments != null
            ? playerAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillDuration, this)
            : 0f;
        float effectiveDamageMultiplier = damageMultiplier * Mathf.Max(0f, 1f + combatDamageBonus);
        // 음수 범위/지속시간을 방지한 최종 시전 값.
        float effectiveRangeMultiplier = Mathf.Max(0f, 1f + combatRangeBonus);
        float effectiveDuration = Mathf.Max(0.05f, duration + combatDurationBonus);

        // 시전 위치에 붙이고 증강 범위만큼 크기 변경.
        GameObject instance = Object.Instantiate(flamethrowerPrefab, origin.position, origin.rotation, origin);
        instance.name = "NinjaHwaDunFlamethrower";
        instance.transform.localScale *= effectiveRangeMultiplier;

        // 자식 파티클에도 부모 크기 변경 적용.
        ParticleSystem[] particleSystems = instance.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < particleSystems.Length; i++)
        {
            ParticleSystem.MainModule main = particleSystems[i].main;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        }

        // 이펙트 자체의 자동 종료 대신 채널이 종료 시점 관리.
        FireBaseScript fire = instance.GetComponent<FireBaseScript>();
        if (fire != null)
            fire.Duration = 99999f;

        // 지속 피해 대상 탐색용 트리거 영역 확보.
        BoxCollider box = instance.GetComponent<BoxCollider>();
        if (box == null)
            box = instance.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = hitboxSize;
        box.center = hitboxCenter;

        // 트리거 이벤트용 중력 없는 운동학 Rigidbody 확보.
        Rigidbody body = instance.GetComponent<Rigidbody>();
        if (body == null)
            body = instance.AddComponent<Rigidbody>();
        body.useGravity = false;
        body.isKinematic = true;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        // 틱 피해와 간격/지속시간을 채널 컴포넌트에 전달.
        int damage = Mathf.Max(1, Mathf.RoundToInt(
            (owner.Stats != null ? owner.Stats.AttackDamage : 10f) * effectiveDamageMultiplier));
        NinjaHwaDunChannel channel = instance.AddComponent<NinjaHwaDunChannel>();
        channel.Initialize(owner, this, damage, tickInterval, effectiveDuration);
        NetworkSkillPresentation.Track(owner.gameObject, instance, flamethrowerPrefab, true, true);
        return true;
    }

    private void OnValidate()
    {
        duration = Mathf.Max(0.05f, duration);
        tickInterval = Mathf.Max(0.05f, tickInterval);
        damageMultiplier = Mathf.Max(0f, damageMultiplier);
        hitboxSize = new Vector3(
            Mathf.Max(0.1f, hitboxSize.x),
            Mathf.Max(0.1f, hitboxSize.y),
            Mathf.Max(0.1f, hitboxSize.z));
    }
}
