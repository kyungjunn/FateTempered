using UnityEngine;

public enum PlayerCombatAugmentEffect
{
    BasicProjectileCount,
    SkillDamage,
    SkillRange,
    SkillDuration,
    DashMoveSpeed,
    DashExtraCharge,
    UltimateStormRange,
    UltimateEndBurst,
    WizardBasicPierce,
    WizardResonanceRadius,
    WizardGravityRadius,
    WizardResidualGravity,
    WizardExtraField,
    WizardFieldCollapse,
    WizardOvercharge
}

// 플레이어 전투 동작을 바꾸는 공용 증강 데이터.
[CreateAssetMenu(fileName = "New Player Combat Augment",
                 menuName = "Game/Player Combat Augment")]
public sealed class PlayerCombatAugmentSO : SkillAugmentSO
{
    [Header("Target")]
    [SerializeField] private PlayerCharacterId requiredCharacter = PlayerCharacterId.Ninja;
    [SerializeField] private PlayerCombatAugmentEffect effect;
    [SerializeField] private PlayerSkillSO targetSkill;

    [Header("Value")]
    [SerializeField, Min(0f)] private float value = 1f;
    [SerializeField, Min(0f)] private float spreadDegrees = 20f;

    public PlayerCharacterId RequiredCharacter => requiredCharacter;
    public PlayerCombatAugmentEffect Effect => effect;
    public PlayerSkillSO TargetSkill => targetSkill;
    public float Value => value;
    public float SpreadDegrees => spreadDegrees;

    public override bool IsAvailableFor(GameObject player)
    {
        if (player == null)
        {
            return false;
        }

        PlayerCharacter character = player.GetComponent<PlayerCharacter>();
        if (character == null ||
            (IsWizardEffect(effect) ? !character.Is(PlayerCharacterId.Wizard) : !character.Is(requiredCharacter)))
        {
            return false;
        }

        bool ultimateEffect = effect == PlayerCombatAugmentEffect.UltimateStormRange ||
                              effect == PlayerCombatAugmentEffect.UltimateEndBurst;
        if (ultimateEffect && player.GetComponent<NinjaUltimate>() == null)
        {
            return false;
        }

        bool wizardEffect = IsWizardEffect(effect);
        bool basicProjectileEffect = effect == PlayerCombatAugmentEffect.BasicProjectileCount ||
                                     effect == PlayerCombatAugmentEffect.WizardBasicPierce;
        if (wizardEffect)
        {
            if (!character.Is(PlayerCharacterId.Wizard) || player.GetComponent<WizardCombat>() == null)
            {
                return false;
            }

            if (effect == PlayerCombatAugmentEffect.WizardBasicPierce)
            {
                if (targetSkill != null)
                {
                    return false;
                }
            }
            else if (!(targetSkill is WizardSkillSO wizardSkill) ||
                     wizardSkill.Kind != RequiredWizardSkillKind(effect))
            {
                return false;
            }
        }

        if (!ultimateEffect && !basicProjectileEffect && targetSkill == null)
        {
            return false;
        }

        if (targetSkill != null)
        {
            PlayerSkillController skillController = player.GetComponent<PlayerSkillController>();
            if (skillController == null || !skillController.HasSkill(targetSkill))
            {
                return false;
            }

            WizardSkillSO targetWizardSkill = targetSkill as WizardSkillSO;
            if (wizardEffect && targetWizardSkill != null &&
                ((targetWizardSkill.Kind == WizardSkillKind.GravityCore && skillController.SkillQ != targetSkill) ||
                 (targetWizardSkill.Kind == WizardSkillKind.ResonanceField && skillController.SkillE != targetSkill) ||
                 (targetWizardSkill.Kind == WizardSkillKind.Overdrive &&
                  (skillController.SkillQ == targetSkill || skillController.SkillE == targetSkill))))
            {
                return false;
            }
        }

        if (basicProjectileEffect &&
            player.GetComponent<ProjectilePlayerAttack>() == null)
        {
            return false;
        }

        return true;
    }

    public override string GetDisplayName()
    {
        return augmentName;
    }

    protected override void OnValidate()
    {
        base.OnValidate();
        value = Mathf.Max(0f, value);
        spreadDegrees = Mathf.Clamp(spreadDegrees, 0f, 360f);

        if (IsWizardEffect(effect))
            requiredCharacter = PlayerCharacterId.Wizard;
    }

    private static bool IsWizardEffect(PlayerCombatAugmentEffect candidate)
    {
        switch (candidate)
        {
            case PlayerCombatAugmentEffect.WizardBasicPierce:
            case PlayerCombatAugmentEffect.WizardResonanceRadius:
            case PlayerCombatAugmentEffect.WizardGravityRadius:
            case PlayerCombatAugmentEffect.WizardResidualGravity:
            case PlayerCombatAugmentEffect.WizardExtraField:
            case PlayerCombatAugmentEffect.WizardFieldCollapse:
            case PlayerCombatAugmentEffect.WizardOvercharge:
                return true;
            default:
                return false;
        }
    }

    private static WizardSkillKind RequiredWizardSkillKind(PlayerCombatAugmentEffect candidate)
    {
        switch (candidate)
        {
            case PlayerCombatAugmentEffect.WizardResonanceRadius:
            case PlayerCombatAugmentEffect.WizardExtraField:
            case PlayerCombatAugmentEffect.WizardFieldCollapse:
                return WizardSkillKind.ResonanceField;
            case PlayerCombatAugmentEffect.WizardGravityRadius:
            case PlayerCombatAugmentEffect.WizardResidualGravity:
                return WizardSkillKind.GravityCore;
            case PlayerCombatAugmentEffect.WizardOvercharge:
                return WizardSkillKind.Overdrive;
            default:
                return (WizardSkillKind)(-1);
        }
    }

}
