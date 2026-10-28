using System;
using UnityEngine;

public class PlayerStateManager : MonoBehaviour
{
    // 죽는 순간 딱 한 번 울리는 이벤트 (GameEndHandler가 받아서 엔딩으로 넘김)
    public event Action OnDead;
    // 쓰러진 상태에서 부활하는 순간 한 번 울리는 이벤트
    public event Action OnRevived;

    [SerializeField]
    private PlayerState currentState = PlayerState.Idle;

    public PlayerState CurrentState => currentState;

    public void ChangeState(PlayerState newState)
    {
        if (currentState == PlayerState.Dead)
        {
            return;
        }

        if (currentState == newState)
        {
            return;
        }

        currentState = newState;

        // 죽는 순간 이벤트 발생 (이 부분이 빠져 있었음)
        if (newState == PlayerState.Dead)
        {
            OnDead?.Invoke();
        }
    }

    // Dead에서 나가는 유일한 경로. 부활은 PlayerHealth.Revive만 호출한다.
    public void Revive()
    {
        if (currentState != PlayerState.Dead)
        {
            return;
        }

        currentState = PlayerState.Idle;
        OnRevived?.Invoke();
    }
}
