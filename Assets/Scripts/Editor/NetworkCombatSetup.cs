using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

public static class NetworkCombatSetup
{
    public const string TargetPath = "Assets/Prefabs/Enemy/NetworkCombatTarget.prefab";
    public const string RegistryPath = "Assets/Prefabs/Manager/NetworkTestPrefabs.asset";

    [MenuItem("Tools/Networking/Configure Shared Combat Test")]
    public static void Configure()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode first.");
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("Save scene changes before configuring combat.");

        const string basePath = "Assets/Prefabs/Player/BasePlayer.prefab";
        var root = PrefabUtility.LoadPrefabContents(basePath);
        try
        {
            if (root.GetComponent<NetworkPlayerCombat>() == null) root.AddComponent<NetworkPlayerCombat>();

            if (root.GetComponent<NetworkHealth>() == null) root.AddComponent<NetworkHealth>();
            PrefabUtility.SaveAsPrefabAsset(root, basePath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        var registry = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(RegistryPath);
        if (registry == null) throw new InvalidOperationException("NetworkTest prefab registry is missing.");
        foreach (var entry in new List<NetworkPrefab>(registry.PrefabList)) registry.Remove(entry);
        foreach (string name in new[] { "NinjaPlayer", "SwordPlayer", "WizardPlayer" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/" + name + ".prefab");
            registry.Add(new NetworkPrefab { Prefab = prefab });
        }

        // Only actual projectile assets are changed; existing character-specific visuals remain intact.
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
