using UnityEngine;

// 사망 결과 저장 후 현재 씬에서 세션 종료 요청
public class GameEndHandler : MonoBehaviour
{
    private PlayerStateManager stateManager;
    private PlayerStats stats;
    private PlayerLevelSystem levelSystem;
    private float combatStartTime;

    private bool ended;

    private void Awake()
    {
        RunStats.Reset(); // 새 판 시작: 킬 수 초기화
        ended = false;
    }

    private void OnEnable()
    {
        SpawnManager.OnPlayerSpawned += BindPlayer;
        // 이벤트 + 현재 스냅샷
        if (SpawnManager.Instance != null)
            BindPlayer(SpawnManager.Instance.SpawnedPlayer);
    }

    private void OnDisable()
    {
        SpawnManager.OnPlayerSpawned -= BindPlayer;
        UnbindPlayer();
    }

    private void UnbindPlayer()
    {
        if (stateManager != null) stateManager.OnDead -= HandleDead;
        stateManager = null;
        stats = null;
        levelSystem = null;
    }

    private void BindPlayer(GameObject player)
    {
        UnbindPlayer();
        if (player == null || ended) return;
        if (!player.TryGetComponent(out stateManager)
            || !player.TryGetComponent(out stats)
            || !player.TryGetComponent(out levelSystem))
        {
            Debug.LogError("GameEndHandler requires player state, stats and level components.", this);
            UnbindPlayer();
            return;
        }

        combatStartTime = Time.time;
        stateManager.OnDead += HandleDead;
    }

    private void HandleDead()
    {
        if (ended) return; // 두 번 실행 방지
        ended = true;

        GameResult.Data = BuildResult(); // 사망 순간 수치
        // 결과 보존 후 세션 종료
        GameManager.Instance?.NotifyPlayerUnavailable(stateManager.gameObject);
    }

    private ResultData BuildResult()
    {
        return new ResultData
        {
            isVictory = false,
            survivalTime = Time.time - combatStartTime,
            monsterKills = RunStats.Kills,
            finalLevel = levelSystem.level,

            attackDamage = stats.AttackDamage,
            hp = 0f,                // 죽은 순간이라 현재 체력은 0
            maxHp = stats.HP,       // PlayerStats.HP는 최대 체력
            speed = stats.MoveSpeed,
            attackSpeed = stats.AttackSpeed,
            defense = stats.Defense
        };
    }

}
