using Unity.Netcode;
using UnityEngine;

// 싱글·네트워크 공용 시뮬레이션 권위 판정
public static class NetworkAuthority
{
    // 오프라인·스폰 전 오브젝트·서버의 게임 상태 계산·기록 허용
    public static bool CanSimulate(GameObject target)
    {
        if (target == null) return false;
        NetworkObject networkObject = target.GetComponent<NetworkObject>();
        // NetworkObject 미존재·스폰 전에는 기존 오프라인 로직 유지
        if (networkObject == null || !networkObject.IsSpawned) return true;
        return networkObject.NetworkManager != null && networkObject.NetworkManager.IsServer;
    }

    // 오프라인 즉시 허용, 네트워크는 세션·GameManager 전투 상태 모두 확인
    public static bool IsCombatReady
    {
        get
        {
            if (!NetworkGameSession.IsNetworkSessionActive) return true;
            GameManager gameManager = GameManager.Instance;
            return NetworkGameSession.IsCombatRunning && gameManager != null &&
                gameManager.IsActiveSession && gameManager.IsNetworkCombatActive;
        }
    }
}
