// 캐릭터 고유 Q/E/R 스킬 데이터.
using UnityEngine;

public abstract class PlayerSkillSO : ScriptableObject
{
    [SerializeField] private Sprite icon;
    public Sprite Icon => icon;

    // 재사용 대기
    [SerializeField, Min(0f)] private float cooldown = 1f;
    // 애니메이터 트리거
    [SerializeField] private string animationTrigger;

    public float Cooldown => cooldown;
    public string AnimationTrigger => animationTrigger;
    public virtual bool DefersCooldown => false;

    // 스킬 발동. 실패 시 false
    public abstract bool Cast(PlayerSkillController owner, PlayerDamageType damageType);
}
