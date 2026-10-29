using System;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;

// 에디터 도구: 서버 권위 적 구성·런타임 NGO 프리팹 등록
public static class NetworkEnemySetup
{
    // 네트워크 전투용 적 프리팹 경로
    public static readonly string[] EnemyPaths =
    {
        "Assets/Prefabs/Enemy/SkeletonEnemy.prefab",
        "Assets/Prefabs/Enemy/GiantEnemy.prefab",
        "Assets/Prefabs/Enemy/TreeBossEnemy.prefab",
        "Assets/Prefabs/Enemy/LevelOne/BatEnemy.prefab",
        "Assets/Prefabs/Enemy/LevelOne/GhostEnemy.prefab",
        "Assets/Prefabs/Enemy/LevelOne/RabbitEnemy.prefab",
        "Assets/Prefabs/Enemy/LevelOne/SlimeEnemy.prefab",
    };

    // 박쥐 음파·성장 동기화 대상 공용 플레이어
    public const string BatProjectilePath = "Assets/Prefabs/Enemy/LevelOne/BatSonicWave.prefab";
    public const string PlayerBasePath = "Assets/Prefabs/Player/BasePlayer.prefab";

    // 적·투사체·플레이어 구성 일괄 저장
    [MenuItem("Tools/Networking/Configure Network Enemies")]
    public static void Configure()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode first.");

        foreach (string path in EnemyPaths) ConfigureEnemy(path);
        ConfigureProjectile(BatProjectilePath);
        ConfigurePlayer();
        RegisterAll();
        AssetDatabase.SaveAssets();
        Debug.Log("NETWORK_ENEMY_SETUP_DONE");
    }

    // 공용 스폰 허용 목록에 적·음파 등록
    public static void RegisterAll()
    {
        var registry = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkCombatSetup.RegistryPath);
        if (registry == null) throw new InvalidOperationException("Runtime NGO prefab registry is missing.");
        foreach (string path in EnemyPaths) Register(registry, path);
        Register(registry, BatProjectilePath);
        EditorUtility.SetDirty(registry);
    }

    // 프리팹 존재 검사·중복 등록 방지
    private static void Register(NetworkPrefabsList registry, string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new InvalidOperationException("Missing prefab: " + path);
        foreach (NetworkPrefab entry in registry.PrefabList)
            if (entry.Prefab == prefab) return;
        registry.Add(new NetworkPrefab { Prefab = prefab });
    }

    // 적 구성: 서버 위치·애니메이션·체력·AI 권한·어그로
    private static void ConfigureEnemy(string path)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            GetOrAdd<NetworkObject>(root);
            var sync = GetOrAdd<NetworkTransform>(root);
            sync.AuthorityMode = NetworkTransform.AuthorityModes.Server;
            sync.Interpolate = true;
            sync.SyncScaleX = sync.SyncScaleY = sync.SyncScaleZ = false;
            // 수평 회전만 동기화; 크기·기울기 제외
            sync.SyncRotAngleX = sync.SyncRotAngleZ = false;

            Animator animator = root.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                var networkAnimator = root.GetComponent<NetworkAnimator>();
                if (networkAnimator == null) networkAnimator = root.AddComponent<NetworkAnimator>();
                networkAnimator.Animator = animator;
                networkAnimator.AuthorityMode = NetworkAnimator.AuthorityModes.Server;
            }

            GetOrAdd<NetworkHealth>(root);
            GetOrAdd<EnemyNetworkRole>(root);
            GetOrAdd<EnemyAggro>(root);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // 음파 구성: 서버 위치 복제·클라이언트 보간
    private static void ConfigureProjectile(string path)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            GetOrAdd<NetworkObject>(root);
            var sync = GetOrAdd<NetworkTransform>(root);
            sync.AuthorityMode = NetworkTransform.AuthorityModes.Server;
            sync.Interpolate = true;
            sync.SyncScaleX = sync.SyncScaleY = sync.SyncScaleZ = false;
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // 공용 플레이어: 레벨 시스템 필수·성장 데이터 동기화
    private static void ConfigurePlayer()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PlayerBasePath);
        try
        {
            if (root.GetComponent<PlayerLevelSystem>() == null)
                throw new InvalidOperationException("BasePlayer must have PlayerLevelSystem.");
            GetOrAdd<NetworkPlayerProgress>(root);
            PrefabUtility.SaveAsPrefabAsset(root, PlayerBasePath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // 기존 컴포넌트 재사용·누락 시 추가
    private static T GetOrAdd<T>(GameObject root) where T : Component
    {
        T component = root.GetComponent<T>();
        return component != null ? component : root.AddComponent<T>();
    }
}
