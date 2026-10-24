using UnityEngine;
using System;

public class PlayerLevelSystem : MonoBehaviour
{
    public int level = 1;
    public int currentExp = 0;
    public int requiredExp = 100;
    public float levelUpExp = 1.2f;

    public Action onExpChanged;
    public Action onLevelUp;

    public void AddExp(int amount)
    {
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

    public float GetExpPercent()
    {
        if (requiredExp <= 0) return 0f;
        return Mathf.Clamp01((float)currentExp / requiredExp);
    }
}
