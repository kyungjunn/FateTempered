using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

// 에디터 도구: 공용 전투 프리팹·네트워크 테스트 씬 구성
public static class NetworkCombatSetup
{
    // 테스트 표적·런타임 스폰 허용 프리팹 목록
    public const string TargetPath = "Assets/Prefabs/Enemy/NetworkCombatTarget.prefab";
    public const string RegistryPath = "Assets/Prefabs/Manager/NetworkTestPrefabs.asset";

    // 플레이어 → 투사체 → 적 등록 → 테스트 씬 참조 연결
    [MenuItem("Tools/Networking/Configure Shared Combat Test")]
    public static void Configure()
    {
        // 실행 중·미저장 씬 보호
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode first.");
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("Save scene changes before configuring combat.");

        // 공용 플레이어: 서버 전투·체력 동기화 컴포넌트
        const string basePath = "Assets/Prefabs/Player/BasePlayer.prefab";
        var root = PrefabUtility.LoadPrefabContents(basePath);
        try
        {
            if (root.GetComponent<NetworkPlayerCombat>() == null) root.AddComponent<NetworkPlayerCombat>();

            if (root.GetComponent<NetworkHealth>() == null) root.AddComponent<NetworkHealth>();
            PrefabUtility.SaveAsPrefabAsset(root, basePath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        // 기존 등록 목록 재구성: 캐릭터별 플레이어 프리팹
        var registry = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(RegistryPath);
        if (registry == null) throw new InvalidOperationException("NetworkTest prefab registry is missing.");
        foreach (var entry in new List<NetworkPrefab>(registry.PrefabList)) registry.Remove(entry);
        foreach (string name in new[] { "NinjaPlayer", "SwordPlayer", "WizardPlayer" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/" + name + ".prefab");
            registry.Add(new NetworkPrefab { Prefab = prefab });
        }

        // 실제 피해 투사체만 서버 위치 동기화 적용; 캐릭터별 연출 유지
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs/Projectile" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset.GetComponent<DamageProjectile>() == null) continue;
            var projectile = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (projectile.GetComponent<NetworkObject>() == null) projectile.AddComponent<NetworkObject>();
                var sync = projectile.GetComponent<NetworkTransform>();
                if (sync == null) sync = projectile.AddComponent<NetworkTransform>();
                sync.AuthorityMode = NetworkTransform.AuthorityModes.Server;
                sync.Interpolate = true;
                sync.SyncScaleX = sync.SyncScaleY = sync.SyncScaleZ = false;
                if (projectile.GetComponent<NetworkProjectile>() == null) projectile.AddComponent<NetworkProjectile>();
                PrefabUtility.SaveAsPrefabAsset(projectile, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(projectile); }
            registry.Add(new NetworkPrefab { Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path) });
        }

        // 테스트 표적 없을 때만 생성: 체력 500·경험치 보상 없음
        var target = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
        if (target == null)
        {
            var instance = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            try
            {
                instance.name = "NetworkCombatTarget";
                var health = instance.AddComponent<EnemyHealth>();
                var settings = new SerializedObject(health);
                settings.FindProperty("maxHealth").intValue = 500;
                settings.FindProperty("expReward").intValue = 0;
                settings.FindProperty("destroyDelay").floatValue = 2f;
                settings.ApplyModifiedPropertiesWithoutUndo();
                instance.AddComponent<NetworkObject>();
                instance.AddComponent<NetworkHealth>();
                target = PrefabUtility.SaveAsPrefabAsset(instance, TargetPath);
            }
            finally { Object.DestroyImmediate(instance); }
        }
        registry.Add(new NetworkPrefab { Prefab = target });
        NetworkEnemySetup.RegisterAll();
        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        // 테스트 세션 참조: 캐릭터 ID 순서·공용 표적
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/NetworkTest.unity");
        var manager = Object.FindFirstObjectByType<NetworkManager>();
        var session = manager.GetComponent<NetworkTestSession>();
        var serialized = new SerializedObject(session);
        var players = serialized.FindProperty("playerPrefabs");
        players.arraySize = 3;
        string[] characters = { "SwordPlayer", "NinjaPlayer", "WizardPlayer" };
        for (int i = 0; i < characters.Length; i++)
            players.GetArrayElementAtIndex(i).objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Player/" + characters[i] + ".prefab");
        serialized.FindProperty("combatTargetPrefab").objectReferenceValue = target;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("Shared server combat assets configured for Ninja, Sword and Wizard.");
    }
}
