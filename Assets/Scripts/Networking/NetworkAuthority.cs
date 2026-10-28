using Unity.Netcode;
using UnityEngine;

// 서버 권위 판정의 단일 출처. 싱글/네트워크 스크립트 모두 여기를 쓴다.
public static class NetworkAuthority
{
    // 오프라인(Spawn 전 오브젝트) 또는 서버 인스턴스면 true: 게임 상태를 계산·기록해도 된다.
    public static bool CanSimulate(GameObject target)
    {
        if (target == null) return false;
        NetworkObject networkObject = target.GetComponent<NetworkObject>();
        if (networkObject == null || !networkObject.IsSpawned) return true;
        return networkObject.NetworkManager != null && networkObject.NetworkManager.IsServer;
    }

    // 오프라인이거나, 네트워크 전투가 실제로 진행 중이면 true.
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
