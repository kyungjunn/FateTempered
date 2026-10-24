using UnityEngine;
using TMPro;

// 스탯 패널. 스포너 스냅샷.
public class StatsPanelUI : MonoBehaviour
{
    private PlayerStats playerStats;

    public TextMeshProUGUI damageText;
    public TextMeshProUGUI moveSpeedText;
    public TextMeshProUGUI attackSpeedText;
    public TextMeshProUGUI defenseText;

    private void OnEnable()
    {
        SpawnManager.OnPlayerSpawned += InitializeUI;
        // 이벤트 + 현재 스냅샷
        InitializeUI(SpawnManager.Instance?.SpawnedPlayer);
    }

    private void OnDisable()
    {
        SpawnManager.OnPlayerSpawned -= InitializeUI;
        UnbindPlayer();
    }

    private void InitializeUI(GameObject playerObject)
    {
        UnbindPlayer();

        if (playerObject != null)
        {
            playerStats = playerObject.GetComponent<PlayerStats>();

            if (playerStats != null)
            {
                playerStats.onStatsChanged += Refresh;
            }
        }

        Refresh();
    }

    public void Refresh()
    {
        if (playerStats == null)
        {
            if (damageText != null)
            {
                damageText.text = string.Empty;
            }

            if (moveSpeedText != null)
            {
                moveSpeedText.text = string.Empty;
            }

            if (attackSpeedText != null)
            {
                attackSpeedText.text = string.Empty;
            }

            if (defenseText != null)
            {
                defenseText.text = string.Empty;
            }

            return;
        }

        if (damageText != null)
        {
            damageText.text = $"공격력: {FormatStat(playerStats.AttackDamage)}";
        }

        if (moveSpeedText != null)
        {
            moveSpeedText.text = $"이동속도: {FormatStat(playerStats.MoveSpeed)}";
        }

        if (attackSpeedText != null)
        {
            attackSpeedText.text = $"공격속도: {FormatStat(playerStats.AttackSpeed)}";
        }

        if (defenseText != null)
        {
            defenseText.text = $"방어력: {FormatStat(playerStats.Defense)}";
        }
    }

    private static string FormatStat(float value)
    {
        return value.ToString("0.##");
    }

    private void UnbindPlayer()
    {
        if (playerStats != null)
        {
            playerStats.onStatsChanged -= Refresh;
            playerStats = null;
        }
    }
}
