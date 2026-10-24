using UnityEngine;

// 플레이어 전투 동작을 변경하는 공용 런타임 증강.
public sealed class PlayerCombatAugment : AugmentSkill
{
    private PlayerCombatAugmentSO definition;

    public void Configure(PlayerCombatAugmentSO source)
    {
        definition = source;
    }

    public float GetValue(PlayerCombatAugmentEffect effect, PlayerSkillSO targetSkill)
    {
        if (IsReleased || definition == null || definition.Effect != effect ||
            !MatchesTarget(targetSkill))
        {
            return 0f;
        }

        return definition.Value * StackCount;
    }

    public float GetSpread(PlayerCombatAugmentEffect effect, PlayerSkillSO targetSkill)
    {
        if (IsReleased || definition == null || definition.Effect != effect ||
            !MatchesTarget(targetSkill))
        {
            return 0f;
        }

        // 펼침 각도는 패턴 전체의 폭이며, 중첩마다 투사체 수만 증가한다.
        return definition.SpreadDegrees;
    }

    protected override void OnApply()
    {
    }

    protected override void OnRemove()
    {
    }

    private bool MatchesTarget(PlayerSkillSO targetSkill)
    {
        return definition.TargetSkill == null || definition.TargetSkill == targetSkill;
    }
}
