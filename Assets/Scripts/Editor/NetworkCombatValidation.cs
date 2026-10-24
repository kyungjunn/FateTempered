using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class NetworkCombatValidation
{
    private const string Pending = "NetworkCombatValidation.Pending";
    static NetworkCombatValidation()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            new GameObject("NetworkCombatValidationRunner").AddComponent<NetworkCombatValidationRunner>();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Batch mode only.");
        NetworkCombatSetup.Configure();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Pending, true);
        EditorApplication.EnterPlaymode();
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerator Wait(Func<bool> condition, string message)
    {
        float end = Time.realtimeSinceStartup + 12f;
        while (!condition())
        {
            Require(Time.realtimeSinceStartup < end, message);
            yield return null;
        }
    }

    private static NetworkManager CreateManager(string name, GameObject prefab)
    {
        var go = new GameObject(name);
        var transport = go.AddComponent<UnityTransport>();
        transport.SetConnectionData("127.0.0.1", 17877);
        var manager = go.AddComponent<NetworkManager>();
        manager.NetworkConfig = new NetworkConfig
        {
            NetworkTransport = transport, EnableSceneManagement = false, PlayerPrefab = prefab
        };
        manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(
            AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkCombatSetup.RegistryPath));
        return manager;
    }

    private static void FreezeInput(NetworkObject player)
    {
        player.GetComponent<PlayerController>().enabled = false;
        player.GetComponent<PlayerLook>().enabled = false;
        player.GetComponent<UnityEngine.InputSystem.PlayerInput>().enabled = false;
        player.GetComponent<Rigidbody>().isKinematic = true;
    }

    internal static IEnumerator Validate()
    {
        var camera = new GameObject("ValidationCamera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        foreach (string character in new[] { "NinjaPlayer", "SwordPlayer", "WizardPlayer" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/" + character + ".prefab");
            Require(prefab.GetComponent<NetworkPlayerCombat>() != null && prefab.GetComponent<NetworkHealth>() != null,
                character + " does not inherit shared network combat and health.");
            var host = CreateManager("CombatHost", prefab);
            Require(host.StartHost(), character + " host start failed.");
            yield return Wait(() => host.LocalClient.PlayerObject != null, "Host player missing.");
            FreezeInput(host.LocalClient.PlayerObject);
            host.LocalClient.PlayerObject.transform.position = new Vector3(50, 1, 50);
            var client = CreateManager("CombatClient", prefab);
            Require(client.StartClient(), "Client start failed.");
            yield return Wait(() => client.IsConnectedClient && client.LocalClient.PlayerObject != null &&
                host.ConnectedClients.Count == 2, "Client player missing.");
            var owner = client.LocalClient.PlayerObject;
            FreezeInput(owner);
            var serverPlayer = host.ConnectedClients[client.LocalClientId].PlayerObject;
            FreezeInput(serverPlayer);
            serverPlayer.transform.SetPositionAndRotation(new Vector3(0, 1, 0), Quaternion.identity);
            yield return Wait(() => Vector3.Distance(serverPlayer.transform.position, owner.transform.position) < 0.1f,
                "Player position not synchronized before combat.");
            // A forged local stat must not affect server damage calculations.
            owner.GetComponent<PlayerStats>().baseAttackDamage = 100000;
            serverPlayer.GetComponent<PlayerStats>().baseAttackDamage = 10;
            Vector3 targetPosition = new Vector3(0, 1.9f, 1.4f);
            var ranged = serverPlayer.GetComponent<ProjectilePlayerAttack>();
            if (ranged != null)
            {
                var origin = (Transform)typeof(ProjectilePlayerAttack).GetField("projectileOrigin",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ranged);
                targetPosition = origin.position + origin.forward * 2f;
            }
            var targetObject = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                NetworkCombatSetup.TargetPath), targetPosition, Quaternion.identity);
            var targetNetwork = targetObject.GetComponent<NetworkObject>();
            targetNetwork.Spawn();
            ulong targetId = targetNetwork.NetworkObjectId;
            var serverHealth = targetObject.GetComponent<EnemyHealth>();
            yield return Wait(() => client.SpawnManager.SpawnedObjects.ContainsKey(targetId), "Target not replicated.");
            var replicaObject = client.SpawnManager.SpawnedObjects[targetId];
            var clientHealth = replicaObject.GetComponent<EnemyHealth>();
            // In-process managers share one Physics scene. Remove only replica collision from this fixture.
            foreach (var collider in replicaObject.GetComponentsInChildren<Collider>()) collider.enabled = false;
            int initial = serverHealth.CurrentHealth;
            clientHealth.TakeDamage(99999);
            Require(clientHealth.CurrentHealth == initial && serverHealth.CurrentHealth == initial,
                "Client mutated authoritative enemy health.");
            int playerInitial = owner.GetComponent<PlayerHealth>().CurrentHealth;
            owner.GetComponent<PlayerHealth>().TakeDamage(99999);
            Require(owner.GetComponent<PlayerHealth>().CurrentHealth == playerInitial,
                "Client mutated authoritative player health.");
            owner.GetComponent<IPlayerBasicAttack>().TryAttack();
            bool replicatedProjectileSeen = false;
            float hitDeadline = Time.realtimeSinceStartup + 12f;
            while (serverHealth.CurrentHealth == initial)
            {
                foreach (var spawned in client.SpawnManager.SpawnedObjects.Values)
                    if (spawned.GetComponent<NetworkProjectile>() != null) replicatedProjectileSeen = true;
                Require(Time.realtimeSinceStartup < hitDeadline, character + " basic attack did not hit.");
                yield return null;
            }
            if (ranged != null) Require(replicatedProjectileSeen, "Projectile did not spawn on the client.");
            int afterHit = serverHealth.CurrentHealth;
            string multiplierField = ranged != null ? "damageMultiplier" : "areaDamageMultiplier";
            object attack = ranged != null ? (object)ranged : serverPlayer.GetComponent<MeleePlayerAttack>();
            float multiplier = (float)attack.GetType().GetField(multiplierField,
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(attack);
            int expectedDamage = Mathf.Max(1, Mathf.RoundToInt(serverPlayer.GetComponent<PlayerStats>().AttackDamage * multiplier));
            Require(initial - afterHit == expectedDamage, "Damage was not calculated from server stats.");
            yield return Wait(() => clientHealth.CurrentHealth == serverHealth.CurrentHealth,
                "Damage result did not replicate.");
            for (int i = 0; i < 5; i++) owner.GetComponent<IPlayerBasicAttack>().TryAttack();
            yield return new WaitForSeconds(0.15f);
            Require(serverHealth.CurrentHealth == afterHit, "Server cooldown accepted attack spam.");
            Require(Mathf.Approximately(Time.timeScale, 1f), "Network melee cosmetics changed simulation time.");
            // An instance the client does not own cannot issue the host player's attack.
            var remoteHost = client.SpawnManager.SpawnedObjects[host.LocalClient.PlayerObject.NetworkObjectId];
            remoteHost.GetComponent<IPlayerBasicAttack>().TryAttack();
            Require(serverHealth.CurrentHealth == afterHit, "Remote actor accepted local attack.");
            var serverPlayerHealth = serverPlayer.GetComponent<PlayerHealth>();
            serverPlayerHealth.TakeDamage(7);
            yield return Wait(() => owner.GetComponent<PlayerHealth>().CurrentHealth == serverPlayerHealth.CurrentHealth,
                "Player health result did not replicate.");
            Require(owner.GetComponent<PlayerHealth>().Heal(9999) == 0, "Client healing bypassed authority.");

            // A third client must see the current damaged target, not prefab default HP.
            var lateClient = CreateManager("CombatLateClient", prefab);
            Require(lateClient.StartClient(), "Late client start failed.");
            yield return Wait(() => lateClient.IsConnectedClient && lateClient.SpawnManager.SpawnedObjects.ContainsKey(targetId),
                "Late client target missing.");
            Require(lateClient.SpawnManager.SpawnedObjects[targetId].GetComponent<EnemyHealth>().CurrentHealth == afterHit,
                "Late client did not receive authoritative health snapshot.");
            FreezeInput(lateClient.LocalClient.PlayerObject);
            serverPlayerHealth.TakeDamage(99999);
            yield return Wait(() => owner.GetComponent<PlayerHealth>().IsDead, "Player death not replicated.");
            owner.GetComponent<IPlayerBasicAttack>().TryAttack();
            yield return new WaitForSeconds(0.3f);
            Require(serverHealth.CurrentHealth == afterHit, "Dead player could issue attacks.");
            serverHealth.TakeDamage(99999);
            yield return Wait(() => clientHealth.IsDead, "Server death state not replicated.");
            yield return Wait(() => !client.SpawnManager.SpawnedObjects.ContainsKey(targetId), "Dead target not despawned.");
            lateClient.Shutdown();
            client.Shutdown();
            yield return Wait(() => !lateClient.ShutdownInProgress && !client.ShutdownInProgress,
                "Clients failed to shut down.");
            Object.Destroy(lateClient.gameObject);
            Object.Destroy(client.gameObject);
            host.Shutdown();
            yield return Wait(() => !host.ShutdownInProgress, "Host failed to shut down.");
            Object.Destroy(host.gameObject);
            yield return null;
            Debug.Log("COMBAT PASS: " + character + " server attack, spoofed stat rejection, cooldown, health, late join and death.");
        }
    }
}

public sealed class NetworkCombatValidationRunner : MonoBehaviour
{
    private IEnumerator Start()
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(NetworkCombatValidation.Validate());
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
        if (failure == null) Debug.Log("NETWORK_COMBAT_VALIDATION_PASS");
        else Debug.LogError("NETWORK_COMBAT_VALIDATION_FAIL: " + failure);
        EditorApplication.Exit(failure == null ? 0 : 1);
    }
}
