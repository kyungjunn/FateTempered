using UnityEngine;
using System;

public class PlayerLevelSystem : MonoBehaviour
{
    // 서버 계산·클라이언트 복제 대상 성장 상태
    public int level = 1;
    public int currentExp = 0;
    public int requiredExp = 100;
    public float levelUpExp = 1.2f;

    public Action onExpChanged;
    public Action onLevelUp;

    // 오프라인 또는 서버만 계산한다. 클라이언트는 NetworkPlayerProgress 스냅샷을 받는다.
    public void AddExp(int amount)
    {
        if (amount <= 0 || !NetworkAuthority.CanSimulate(gameObject)) return;
        currentExp += amount;

        while (currentExp >= requiredExp)
        {
            currentExp -= requiredExp;
            LevelUp();
        }

        onExpChanged?.Invoke();
    }

    void LevelUp()
    {
        level++;
        requiredExp = Mathf.RoundToInt(requiredExp * levelUpExp);
        onLevelUp?.Invoke();

        // 네트워크 증강 선택은 아직 지원하지 않는다. 레벨만 오른다.
        if (NetworkGameSession.IsNetworkSessionActive) return;

        if (UIManager.Instance != null)
        {
            UIManager.Instance.RequestAugmentChoice();
        }
        else
        {
            Debug.LogError("UIManager 인스턴스 찾을 수 없음!");
        }

        Debug.Log($"레벨업! 다음 레벨 요구 경험치: {requiredExp}");
    }

    // 클라이언트 전용 성장 스냅샷 적용 및 변경 알림
    public void ApplyReplicated(int replicatedLevel, int replicatedExp, int replicatedRequiredExp)
    {
        if (NetworkAuthority.CanSimulate(gameObject)) return;
        bool leveledUp = replicatedLevel > level;
        level = Mathf.Max(1, replicatedLevel);
        currentExp = Mathf.Max(0, replicatedExp);
        requiredExp = Mathf.Max(1, replicatedRequiredExp);
        if (leveledUp) onLevelUp?.Invoke();
        onExpChanged?.Invoke();
    }

    public float GetExpPercent()
    {
        if (requiredExp <= 0) return 0f;
        return Mathf.Clamp01((float)currentExp / requiredExp);
    }
}
