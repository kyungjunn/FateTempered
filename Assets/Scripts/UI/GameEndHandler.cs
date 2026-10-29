using UnityEngine;

// 오프라인: 플레이어 HP 0 → 결과 캡처 → 세션 종료.
// 네트워크 판 종료(전원 쓰러짐)는 NetworkGameSession이 결과를 받아 GameResult에 기록한다.
public class GameEndHandler : MonoBehaviour
{
    private PlayerStateManager stateManager;
    private float combatStartTime;
    private bool ended;

    private void OnEnable()
    {
        GameManager.OnLocalPlayerChanged += BindPlayer;
        BindPlayer(GameManager.Instance?.CurrentPlayer);
    }

    private void OnDisable()
    {
        GameManager.OnLocalPlayerChanged -= BindPlayer;
        UnbindPlayer();
    }

    private void UnbindPlayer()
    {
        if (stateManager != null) stateManager.OnDead -= HandleDead;
        stateManager = null;
    }

    private void BindPlayer(GameObject player)
    {
        // 오프라인 정리가 마지막 사망 알림보다 먼저 올 수 있으므로 다음 바인딩까지 유지한다.
        // 네트워크: 로컬 사망 결과 경로 사용 안 함.
        if (player == null && !NetworkGameSession.IsNetworkSessionActive) return;
        UnbindPlayer();
        if (player == null || ended || NetworkGameSession.IsNetworkSessionActive) return;
        if (!player.TryGetComponent(out stateManager))
        {
            Debug.LogError("GameEndHandler requires PlayerStateManager.", this);
            return;
        }

        combatStartTime = Time.time;
        stateManager.OnDead += HandleDead;
    }

    private void HandleDead()
    {
        // 네트워크 판 종료는 세션이 결과 기록.
        if (ended || NetworkGameSession.IsNetworkSessionActive) return;
        ended = true;

        GameObject player = stateManager.gameObject;
        MatchResult result = new MatchResult
        {
            MatchSeconds = Time.time - combatStartTime,
            Players = new[] { PlayerResult.Capture(player, LocalPlayerProfile.LoadDisplayName(), 0) }
        };
        GameManager.Instance?.NotifyPlayerUnavailable(player);
        GameResult.Data = result;
    }
}
