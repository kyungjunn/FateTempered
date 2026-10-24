using UnityEngine;

public class PlayerAttackStateBehaviour : StateMachineBehaviour
{
    public override void OnStateExit(
        Animator animator,
        AnimatorStateInfo stateInfo,
        int layerIndex)
    {
        PlayerController controller =
            animator.GetComponentInParent<PlayerController>();

        if (controller != null)
        {
            controller.EndAttackState();
        }
    }
}