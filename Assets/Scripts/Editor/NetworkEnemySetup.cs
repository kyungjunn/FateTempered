using System;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;

// 적 프리팹을 서버 권위 네트워크 적으로 구성하고 런타임 NGO 프리팹 목록에 등록한다.
public static class NetworkEnemySetup
{
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

    public const string BatProjectilePath = "Assets/Prefabs/Enemy/LevelOne/BatSonicWave.prefab";
    public const string PlayerBasePath = "Assets/Prefabs/Player/BasePlayer.prefab";

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

    public static void RegisterAll()
    {
        var registry = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkCombatSetup.RegistryPath);
        if (registry == null) throw new InvalidOperationException("Runtime NGO prefab registry is missing.");
        foreach (string path in EnemyPaths) Register(registry, path);
        Register(registry, BatProjectilePath);
        EditorUtility.SetDirty(registry);
    }

    private static void Register(NetworkPrefabsList registry, string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new InvalidOperationException("Missing prefab: " + path);
        foreach (NetworkPrefab entry in registry.PrefabList)
            if (entry.Prefab == prefab) return;
        registry.Add(new NetworkPrefab { Prefab = prefab });
    }

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

    private static T GetOrAdd<T>(GameObject root) where T : Component
    {
        T component = root.GetComponent<T>();
        return component != null ? component : root.AddComponent<T>();
    }
}
