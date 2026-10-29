using UnityEngine;

public enum WizardSkillKind
{
    GravityCore,
    ResonanceField,
    Overdrive
}

[CreateAssetMenu(menuName = "Player/Skills/Wizard Skill")]
public sealed class WizardSkillSO : PlayerSkillSO
{
    [SerializeField] private WizardSkillKind kind;

    [Header("Gravity Core")]
    [SerializeField, Min(0.1f)] private float gravityCoreRange = 18f;
    [SerializeField, Min(0.1f)] private float gravityCoreSpeed = 12f;
    [SerializeField, Min(0.01f)] private float gravityCoreCollisionRadius = 0.2f;
    [SerializeField, Min(0.1f)] private float gravityCorePullRadius = 4f;
    [SerializeField, Min(0.05f)] private float gravityCorePullDuration = 1.5f;
    [SerializeField, Min(0f)] private float gravityCorePullSpeed = 3.5f;
    [SerializeField, Min(0.1f)] private float gravityCoreBlastRadius = 2.5f;
    [SerializeField, Min(0f)] private float gravityCoreDamageMultiplier = 1.5f;

    [Header("Resonance Field")]
    [SerializeField, Min(0.1f)] private float resonanceFieldRange = 12f;
    [SerializeField, Min(0.1f)] private float resonanceFieldDuration = 5f;
    [SerializeField, Min(0.1f)] private float resonanceFieldRadius = 3f;
    [SerializeField, Min(0.05f)] private float resonanceFieldTickInterval = 1f;
    [SerializeField, Min(0f)] private float resonanceFieldTickDamageMultiplier = 0.35f;
    [SerializeField, Min(0f)] private float resonanceDamageMultiplier = 0.65f;
    [SerializeField, Min(0.01f)] private float resonanceCooldown = 0.35f;
    [SerializeField, Min(0.1f)] private float resonanceRadius = 2f;

    [Header("Overdrive")]
    [SerializeField, Min(0.1f)] private float overdriveDuration = 6f;
    [SerializeField, Min(0.1f)] private float overdriveWaveRadius = 3.5f;
    [SerializeField, Min(0.05f)] private float overdriveWaveInterval = 0.8f;
    [SerializeField, Min(0f)] private float overdriveWaveDamageMultiplier = 0.45f;
    [SerializeField, Min(0.1f)] private float overdriveFinalBlastRadius = 7.5f;
    [SerializeField, Min(0f)] private float overdriveFinalDamageMultiplier = 4f;
    [SerializeField, Min(0f)] private float overdriveBasicDamageMultiplier = 2f;
    [SerializeField, Min(0.1f)] private float overdriveBasicVisualScale = 1.3f;
    [SerializeField, Min(0)] private int overdriveBasicAdditionalPierces = 1;

    public WizardSkillKind Kind => kind;

    public float GravityCoreRange => gravityCoreRange;
    public float GravityCoreSpeed => gravityCoreSpeed;
    public float GravityCoreCollisionRadius => gravityCoreCollisionRadius;
    public float GravityCorePullRadius => gravityCorePullRadius;
    public float GravityCorePullDuration => gravityCorePullDuration;
    public float GravityCorePullSpeed => gravityCorePullSpeed;
    public float GravityCoreBlastRadius => gravityCoreBlastRadius;
    public float GravityCoreDamageMultiplier => gravityCoreDamageMultiplier;

    public float ResonanceFieldRange => resonanceFieldRange;
    public float ResonanceFieldDuration => resonanceFieldDuration;
    public float ResonanceFieldRadius => resonanceFieldRadius;
    public float ResonanceFieldTickInterval => resonanceFieldTickInterval;
    public float ResonanceFieldTickDamageMultiplier => resonanceFieldTickDamageMultiplier;
    public float ResonanceDamageMultiplier => resonanceDamageMultiplier;
    public float ResonanceCooldown => resonanceCooldown;
    public float ResonanceRadius => resonanceRadius;

    public float OverdriveDuration => overdriveDuration;
    public float OverdriveWaveRadius => overdriveWaveRadius;
    public float OverdriveWaveInterval => overdriveWaveInterval;
    public float OverdriveWaveDamageMultiplier => overdriveWaveDamageMultiplier;
    public float OverdriveFinalBlastRadius => overdriveFinalBlastRadius;
    public float OverdriveFinalDamageMultiplier => overdriveFinalDamageMultiplier;
    public float OverdriveBasicDamageMultiplier => overdriveBasicDamageMultiplier;
    public float OverdriveBasicVisualScale => overdriveBasicVisualScale;
    public int OverdriveBasicAdditionalPierces => overdriveBasicAdditionalPierces;

    public override bool Cast(PlayerSkillController owner, PlayerDamageType damageType)
    {
        // 서버·오프라인만 시전.
        if (owner == null || !NetworkAuthority.CanSimulate(owner.gameObject) || owner.DamageDealer == null)
            return false;

        WizardCombat combat = owner.GetComponent<WizardCombat>();
        return combat != null && combat.TryCast(this);
    }

    private void OnValidate()
    {
        gravityCoreRange = Mathf.Max(0.1f, gravityCoreRange);
        gravityCoreSpeed = Mathf.Max(0.1f, gravityCoreSpeed);
        gravityCoreCollisionRadius = Mathf.Max(0.01f, gravityCoreCollisionRadius);
        gravityCorePullRadius = Mathf.Max(0.1f, gravityCorePullRadius);
        gravityCorePullDuration = Mathf.Max(0.05f, gravityCorePullDuration);
        gravityCorePullSpeed = Mathf.Max(0f, gravityCorePullSpeed);
        gravityCoreBlastRadius = Mathf.Max(0.1f, gravityCoreBlastRadius);
        gravityCoreDamageMultiplier = Mathf.Max(0f, gravityCoreDamageMultiplier);

        resonanceFieldRange = Mathf.Max(0.1f, resonanceFieldRange);
        resonanceFieldDuration = Mathf.Max(0.1f, resonanceFieldDuration);
        resonanceFieldRadius = Mathf.Max(0.1f, resonanceFieldRadius);
        resonanceFieldTickInterval = Mathf.Max(0.05f, resonanceFieldTickInterval);
        resonanceFieldTickDamageMultiplier = Mathf.Max(0f, resonanceFieldTickDamageMultiplier);
        resonanceDamageMultiplier = Mathf.Max(0f, resonanceDamageMultiplier);
        resonanceCooldown = Mathf.Max(0.01f, resonanceCooldown);
        resonanceRadius = Mathf.Max(0.1f, resonanceRadius);

        overdriveDuration = Mathf.Max(0.1f, overdriveDuration);
        overdriveWaveRadius = Mathf.Max(0.1f, overdriveWaveRadius);
        overdriveWaveInterval = Mathf.Max(0.05f, overdriveWaveInterval);
        overdriveWaveDamageMultiplier = Mathf.Max(0f, overdriveWaveDamageMultiplier);
        overdriveFinalBlastRadius = Mathf.Max(0.1f, overdriveFinalBlastRadius);
        overdriveFinalDamageMultiplier = Mathf.Max(0f, overdriveFinalDamageMultiplier);
        overdriveBasicDamageMultiplier = Mathf.Max(0f, overdriveBasicDamageMultiplier);
        overdriveBasicVisualScale = Mathf.Max(0.1f, overdriveBasicVisualScale);
        overdriveBasicAdditionalPierces = Mathf.Max(0, overdriveBasicAdditionalPierces);
    }
}
