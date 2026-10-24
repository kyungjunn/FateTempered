using UnityEngine;

// 스킬 증강 하나의 실제 동작.
// OnApply에서 효과를 켜고, Release에서 반드시 원복한다.
public abstract class AugmentSkill : MonoBehaviour
{
    // 적용 대상
    protected GameObject Player { get; private set; }
    // 현재 중첩
    public int StackCount { get; private set; }
    // 해제 여부
    public bool IsReleased { get; private set; }

    // 최초 적용
    public void Apply(GameObject player)
    {
        Player = player;
        StackCount = 1;
        IsReleased = false;
        OnApply();
    }

    // 중첩 증가
    public void AddStack()
    {
        StackCount++;
        OnStackChanged();
    }

    // 중첩 감소
    public bool TryRemoveStack()
    {
        if (StackCount <= 1)
        {
            return false;
        }

        StackCount--;
        OnStackChanged();
        return true;
    }

    // 효과 해제
    public void Release()
    {
        if (IsReleased)
        {
            return;
        }

        IsReleased = true;
        OnRemove();
    }

    private void OnDestroy()
    {
        Release();
    }

    protected abstract void OnApply();
    protected abstract void OnRemove();

    protected virtual void OnStackChanged()
    {
    }
}
