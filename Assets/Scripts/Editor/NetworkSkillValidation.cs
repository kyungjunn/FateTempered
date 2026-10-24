using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class NetworkSkillValidation
{
    private const string Pending = "NetworkSkillValidation.Pending";
    static NetworkSkillValidation()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
            {
                SessionState.SetBool(Pending, false);
                new GameObject("SkillValidationRunner").AddComponent<NetworkSkillValidationRunner>();
            }
        };
    }

    [MenuItem("Tools/Validation/Network Skills")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play mode first.");
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            Require(!UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty, "Save current scenes first.");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetString("NetworkSkillValidation.Result", "Running");
        SessionState.SetBool(Pending, true);
        EditorApplication.EnterPlaymode();
    }

    internal static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static IEnumerator Wait(Func<bool> condition, string message, float timeout = 15f)
    {
        float deadline = Time.realtimeSinceStartup + timeout;
        while (!condition())
        {
            Require(Time.realtimeSinceStartup < deadline, message);
            yield return null;
        }
    }

    private static NetworkManager CreateManager(string name, GameObject prefab)
    {
        var instance = new GameObject(name);
        var transport = instance.AddComponent<UnityTransport>();
        transport.SetConnectionData("127.0.0.1", 17878);
        var manager = instance.AddComponent<NetworkManager>();
        manager.NetworkConfig = new NetworkConfig { NetworkTransport = transport, PlayerPrefab = prefab,
            EnableSceneManagement = false };
        manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(
            AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkCombatSetup.RegistryPath));
        return manager;
    }
    private static void FreezeControl(NetworkObject player)
    {
        if (player.GetComponent<PlayerHealth>() == null)
            player.gameObject.AddComponent<PlayerHealth>();
        player.GetComponent<PlayerController>().enabled = false;
        player.GetComponent<PlayerLook>().enabled = false;
        player.GetComponent<UnityEngine.InputSystem.PlayerInput>().enabled = false;
    }

    private static int TotalHealth(List<EnemyHealth> targets)
    {
        int total = 0;
        foreach (var target in targets) if (target != null) total += target.CurrentHealth;
        return total;
    }

    internal static IEnumerator Validate()
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.transform.localScale = Vector3.one * 10f;
        var camera = new GameObject("SkillTestCamera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0, 5, -5);
        camera.transform.LookAt(new Vector3(0, 0, 2));
        foreach (string character in new[] { "NinjaPlayer", "SwordPlayer", "WizardPlayer" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/" + character + ".prefab");
            var host = CreateManager("SkillHost", prefab);
            Require(host.StartHost(), "Host failed to start.");
            yield return Wait(() => host.LocalClient.PlayerObject != null, "Host player missing.");
            var hostPlayer = host.LocalClient.PlayerObject;
            FreezeControl(hostPlayer);
            var client = CreateManager("SkillClient", prefab);
            Require(client.StartClient(), "Client failed to start.");
            yield return Wait(() => client.IsConnectedClient && client.LocalClient.PlayerObject != null &&
                host.ConnectedClients.Count == 2, "Client player missing.");
            var owner = client.LocalClient.PlayerObject;
            FreezeControl(owner);
            var serverPlayer = host.ConnectedClients[client.LocalClientId].PlayerObject;
            FreezeControl(serverPlayer);
            serverPlayer.GetComponent<Rigidbody>().isKinematic = true;
            serverPlayer.transform.SetPositionAndRotation(new Vector3(0, 1f, 0), Quaternion.identity);
            var requests = owner.GetComponent<NetworkPlayerSkills>();
            var serverSkills = serverPlayer.GetComponent<PlayerSkillController>();
            Require(requests != null && serverSkills.enabled, "Shared skill component/server simulation missing.");
            float poseDeadline = Time.realtimeSinceStartup + 3f;
            while (Vector3.Distance(serverPlayer.transform.position, owner.transform.position) >= 0.2f)
            {
                if (Time.realtimeSinceStartup >= poseDeadline)
                    throw new InvalidOperationException(
                        $"Server-authoritative player pose not replicated. server={serverPlayer.transform.position} owner={owner.transform.position} serverKinematic={serverPlayer.GetComponent<Rigidbody>().isKinematic} ownerKinematic={owner.GetComponent<Rigidbody>().isKinematic} authority={serverPlayer.GetComponent<Unity.Netcode.Components.NetworkTransform>().AuthorityMode}");
                yield return null;
            }

            var targets = new List<EnemyHealth>();
            var replicas = new List<EnemyHealth>();
            var targetIds = new List<ulong>();
            for (int i = 0; i < 3; i++)
            {
                var target = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(NetworkCombatSetup.TargetPath),
                    new Vector3(0, 1f, 1.2f + i * 0.8f), Quaternion.identity);
                var healthSettings = new SerializedObject(target.GetComponent<EnemyHealth>());
                healthSettings.FindProperty("maxHealth").intValue = 50000;
                healthSettings.ApplyModifiedPropertiesWithoutUndo();
                typeof(EnemyHealth).GetProperty("CurrentHealth").SetValue(
                    target.GetComponent<EnemyHealth>(), 50000);
                target.transform.localScale = new Vector3(1.5f, 2f, 1.5f);
                var net = target.GetComponent<NetworkObject>();
                net.Spawn();
                targetIds.Add(net.NetworkObjectId);
                targets.Add(target.GetComponent<EnemyHealth>());
                yield return Wait(() => client.SpawnManager.SpawnedObjects.ContainsKey(net.NetworkObjectId), "Target missing.");
                var replica = client.SpawnManager.SpawnedObjects[net.NetworkObjectId];
                replicas.Add(replica.GetComponent<EnemyHealth>());
                // Multi-manager test shares a Physics scene; keep only server target collision.
                foreach (var collider in replica.GetComponentsInChildren<Collider>()) collider.enabled = false;
            }

            int before = TotalHealth(targets);
            Require(requests.RequestSkill(NetworkSkillSlot.Q), character + " Q request rejected locally.");
            yield return Wait(() => TotalHealth(targets) < before, character + " Q did not deal server damage.");
            yield return Wait(() => TotalHealth(targets) == TotalHealth(replicas), "Q health replication mismatch.");
            Require(serverSkills.QChanneling || serverSkills.QRemaining > 0, "Q consumed no server cooldown/channel state.");
            float initialGauge = owner.GetComponent<PlayerUltimateGauge>().Current;
            owner.GetComponent<PlayerUltimateGauge>().Add(99999f);
            Require(Mathf.Approximately(owner.GetComponent<PlayerUltimateGauge>().Current, initialGauge),
                "Client can forge ultimate gauge.");
            yield return Wait(() => !serverSkills.QChanneling && !serverSkills.IsMovementLocked,
                "Q did not finish.", 20f);
            yield return new WaitForSeconds(1f);
            serverPlayer.GetComponent<Rigidbody>().isKinematic = true;
            serverPlayer.transform.SetPositionAndRotation(new Vector3(0, 1f, 0), Quaternion.identity);
            yield return new WaitForSeconds(0.3f);
            before = TotalHealth(targets);
            if (character == "WizardPlayer")
            {
                Require(requests.RequestSkill(NetworkSkillSlot.E, new Vector3(0, 0, 2), true), "Wizard E request rejected.");
            }
            else Require(requests.RequestSkill(NetworkSkillSlot.E), character + " E request rejected.");
            yield return null;
            yield return null;
            if (character != "WizardPlayer")
                Require(serverSkills.IsMovementLocked || TotalHealth(targets) < before,
                    character + " E accepted but did not start dash. pos=" + serverPlayer.transform.position);
            yield return Wait(() => TotalHealth(targets) < before, character + " E did not deal server damage.", 8f);
            yield return Wait(() => TotalHealth(targets) == TotalHealth(replicas), "E health replication mismatch.");
            yield return Wait(() => !serverSkills.IsMovementLocked, "E movement lock stuck.", 20f);
            yield return new WaitForSeconds(1f);
            serverPlayer.GetComponent<Rigidbody>().isKinematic = true;
            serverPlayer.transform.SetPositionAndRotation(new Vector3(0, 1f, 0), Quaternion.identity);
            serverPlayer.GetComponent<PlayerUltimateGauge>().Add(99999f);
            yield return Wait(() => owner.GetComponent<PlayerUltimateGauge>().IsFull, "Ultimate gauge not replicated.");
            before = TotalHealth(targets);
            Require(requests.RequestSkill(NetworkSkillSlot.Ultimate), character + " R request rejected.");
            yield return Wait(() => !owner.GetComponent<PlayerUltimateGauge>().IsFull, "Server did not consume ultimate gauge.");
            if (character == "NinjaPlayer")
            {
                yield return Wait(() => serverSkills.UltimateRecastReady, "Ninja ready state missing.");
                Require(requests.RequestSkill(NetworkSkillSlot.Ultimate), "Ninja R recast request rejected.");
            }
            yield return Wait(() => TotalHealth(targets) < before, character + " ultimate did not deal server damage.", 20f);
            yield return Wait(() => TotalHealth(targets) == TotalHealth(replicas), "R health replication mismatch.");
            serverPlayer.GetComponent<PlayerHealth>().TakeDamage(999999);
            yield return Wait(() => owner.GetComponent<PlayerHealth>().IsDead, "Player death not replicated.");
            yield return new WaitForSeconds(0.3f);
            Require(!serverSkills.IsMovementLocked, "Death left skill movement locked.");
            client.Shutdown();
            yield return Wait(() => !client.ShutdownInProgress, "Client shutdown stuck.");
            Object.Destroy(client.gameObject);
            host.Shutdown();
            yield return Wait(() => !host.ShutdownInProgress, "Host shutdown stuck.");
            Object.Destroy(host.gameObject);
            yield return null;
            Debug.Log("NETWORK SKILLS PASS: " + character + " Q/E/R, gauge authority, health replication, death cleanup.");
        }
    }
}

public sealed class NetworkSkillValidationRunner : MonoBehaviour
{
    private IEnumerator Start()
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(NetworkSkillValidation.Validate());
        string failure = null;
        while (stack.Count > 0)
        {
            object current = null;
            try
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                current = stack.Peek().Current;
            }
            catch (Exception exception) { failure = exception.ToString(); break; }
            if (current is IEnumerator nested) stack.Push(nested);
            else yield return current;
        }
        string result = failure == null ? "NETWORK_SKILL_VALIDATION_PASS" : "NETWORK_SKILL_VALIDATION_FAIL: " + failure;
        SessionState.SetString("NetworkSkillValidation.Result", result);
        if (failure == null) Debug.Log(result); else Debug.LogError(result);
        EditorApplication.isPlaying = false;
    }
}
