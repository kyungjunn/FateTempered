using UnityEngine;
using UnityEngine.InputSystem;

public sealed class PlayerSkillInput : MonoBehaviour
{
    private PlayerSkillController controller;
    private WizardSkillTargeting targeting;
    private NetworkPlayerSkills networkSkills;

    internal bool CanReadInput
    {
        get
        {
            if (!isActiveAndEnabled) return false;
            NetworkPlayerSkills skills = NetworkSkills;
            return skills == null || skills.IsOwner;
        }
    }

    private NetworkPlayerSkills NetworkSkills
    {
        get
        {
            if (networkSkills == null)
                networkSkills = GetComponent<NetworkPlayerSkills>();
            return networkSkills != null && networkSkills.IsSpawned ? networkSkills : null;
        }
    }

    private void Awake()
    {
        controller = GetComponent<PlayerSkillController>();
        targeting = GetComponent<WizardSkillTargeting>();
    }

    private void OnDisable()
    {
        GetTargeting()?.CancelPreview();
    }

    public void OnSkillQ(InputValue value)
    {
        if (!CanReadInput || !value.isPressed)
            return;

        GetTargeting()?.CancelPreview();
        RequestSkill(NetworkSkillSlot.Q);
    }

    public void OnSkillE(InputValue value)
    {
        if (!CanReadInput || !value.isPressed)
            return;

        WizardSkillTargeting skillTargeting = GetTargeting();
        if (skillTargeting != null && skillTargeting.HandleSkillEInput())
            return;

        RequestSkill(NetworkSkillSlot.E);
    }

    public void OnUltimate(InputValue value)
    {
        if (value.isPressed)
            TryUseUltimate();
    }

    public bool TryUseUltimate()
    {
        if (!CanReadInput)
            return false;

        GetTargeting()?.CancelPreview();
        return RequestSkill(NetworkSkillSlot.Ultimate);
    }

    internal bool RequestSkill(NetworkSkillSlot slot, Vector3 groundTarget = default,
        bool hasGroundTarget = false)
    {
        if (!CanReadInput)
            return false;

        PlayerSkillController skillController = GetController();
        if (skillController == null || skillController.IsSessionEnded)
            return false;

        NetworkPlayerSkills skills = NetworkSkills;
        if (skills != null)
            return skills.RequestSkill(slot, groundTarget, hasGroundTarget);

        return skillController.TryCastLocalSkill(slot);
    }

    private PlayerSkillController GetController()
    {
        if (controller == null)
            controller = GetComponent<PlayerSkillController>();
        return controller;
    }

    private WizardSkillTargeting GetTargeting()
    {
        if (targeting == null)
            targeting = GetComponent<WizardSkillTargeting>();
        return targeting;
    }
}
