using UnityEngine;
using UnityEngine.InputSystem; // 새로운 Input System

public class PlayerInputHandler : MonoBehaviour
{
    public Vector2 MoveInput { get; private set; }
    public Vector2 LookInput { get; private set; }

    public bool AttackTriggered { get; set; }
    public bool JumpTriggered { get; set; }

    public PlayerLevelSystem levelSystem;

    public void OnMove(InputValue value)
    {
        MoveInput = value.Get<Vector2>();
    }

    public void OnLook(InputValue value)
    {
        LookInput = value.Get<Vector2>();
    }

    public void OnAttack(InputValue value)
    {
        if (value.isPressed)
        {
            AttackTriggered = true;
        }
    }
    
    public void OnJump(InputValue value)
    {
        if (value.isPressed)
        {
            JumpTriggered = true;
        }
    }

    // 경험치 획득 테스트 input (X)
    public void OnExpTest(InputValue value)
    {
        if (value.isPressed)
        {
            levelSystem.AddExp(25);
        }
    }
}
