using System;
using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class NetworkTestValidation
{
    private const string Pending = "NetworkTestValidation.Pending";

    static NetworkTestValidation()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            new GameObject("NetworkTestValidationRunner").AddComponent<NetworkTestValidationRunner>();
        };
    }

    [MenuItem("Tools/Validation/Network Test")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop play mode before validation.");
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            Require(!UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty, "Save scene changes first.");
        EditorSceneManager.OpenScene("Assets/Scenes/NetworkTest.unity");
        SessionState.SetString("NetworkTestValidation.Result", "Running");
        SessionState.SetBool(Pending, true);
        EditorApplication.EnterPlaymode();
    }

    internal static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static IEnumerator WaitFor(Func<bool> condition, string message)
    {
        float deadline = Time.realtimeSinceStartup + 15f;
        while (!condition())
        {
            Require(Time.realtimeSinceStartup < deadline, message);
            yield return null;
        }
    }

    internal static IEnumerator Validate()
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "MovementValidationGround";
        ground.transform.localScale = Vector3.one * 20f;
        var host = Object.FindFirstObjectByType<NetworkManager>();
        Require(host != null, "NetworkManager missing.");
        // This movement fixture counts player objects only; combat targets are covered separately.
        var sessionSettings = new SerializedObject(host.GetComponent<NetworkTestSession>());
        sessionSettings.FindProperty("combatTargetPrefab").objectReferenceValue = null;
        sessionSettings.ApplyModifiedPropertiesWithoutUndo();
        Require(host.NetworkConfig.EnableSceneManagement, "Scene management must be enabled in the saved scene.");
        Require(host.NetworkConfig.NetworkTransport is UnityTransport, "UnityTransport missing.");
        var prefab = host.NetworkConfig.PlayerPrefab;
        Require(prefab != null && prefab.GetComponent<NetworkObject>() != null, "Player prefab missing NetworkObject.");
        Require(AssetDatabase.GetAssetPath(prefab) == "Assets/Prefabs/Player/NinjaPlayer.prefab",
            "The test must use the real NinjaPlayer variant.");
        foreach (string name in new[] { "BasePlayer", "NinjaPlayer", "SwordPlayer", "WizardPlayer" })
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/" + name + ".prefab");
            Require(asset.GetComponentsInChildren<NetworkObject>(true).Length == 1,
                name + " must have exactly one root NetworkObject.");
            Require(asset.GetComponent<PlayerNetworkOwnership>() != null, name + " is missing shared ownership.");
            Require(asset.GetComponent<NetworkAnimator>().Animator == asset.GetComponentInChildren<Animator>(),
                name + " must synchronize its own visual Animator.");
        }
        var offline = Object.Instantiate(prefab, new Vector3(10, 1, 10), Quaternion.identity);
        yield return null;
        Require(offline.GetComponent<PlayerController>().enabled &&
            offline.GetComponent<UnityEngine.InputSystem.PlayerInput>().enabled &&
            !offline.GetComponent<Rigidbody>().isKinematic, "Offline player behavior changed.");
        Object.Destroy(offline);
        yield return null;
        Require(prefab.GetComponent<NetworkTransform>().AuthorityMode == NetworkTransform.AuthorityModes.Server,
            "Skill movement requires server-authoritative transforms.");
        Require(host.StartHost(), "Host failed to start with saved scene configuration.");
        yield return WaitFor(() => host.LocalClient != null && host.LocalClient.PlayerObject != null,
            "Host player did not spawn.");
        Require(host.LocalClient.PlayerObject.IsOwner, "Host does not own its player.");
        host.Shutdown();
        yield return WaitFor(() => !host.ShutdownInProgress && !host.IsListening, "Host shutdown timed out.");
        yield return null;

        // Multiple managers share one Unity scene only in this in-process transport test.
        // The saved scene retains NGO scene management for real separate-process clients.
        host.NetworkConfig.EnableSceneManagement = false;
        Require(host.StartHost(), "Host restart failed.");
        var clientObject = new GameObject("ValidationClient");
        var transport = clientObject.AddComponent<UnityTransport>();
        transport.SetConnectionData("127.0.0.1", 7777);
        var client = clientObject.AddComponent<NetworkManager>();
        client.NetworkConfig = new NetworkConfig();
        client.NetworkConfig.NetworkTransport = transport;
        client.NetworkConfig.EnableSceneManagement = false;
        client.NetworkConfig.ConnectionApproval = host.NetworkConfig.ConnectionApproval;
        client.NetworkConfig.PlayerPrefab = prefab;
        client.NetworkConfig.Prefabs.NetworkPrefabsLists.Clear();
        client.NetworkConfig.Prefabs.NetworkPrefabsLists.AddRange(host.NetworkConfig.Prefabs.NetworkPrefabsLists);
        Require(client.StartClient(), "Client failed to start.");
        yield return WaitFor(() => client.IsConnectedClient && client.LocalClient.PlayerObject != null
            && host.ConnectedClients.Count == 2 && client.SpawnManager.SpawnedObjects.Count == 2,
            "Two peers did not connect and spawn both players.");
        ulong clientId = client.LocalClientId;
        var owner = client.LocalClient.PlayerObject;
        var replica = host.ConnectedClients[clientId].PlayerObject;
        Require(owner.IsOwner && !replica.IsOwner, "Client player ownership is incorrect.");
        Require(host.SpawnManager.SpawnedObjects.Count == 2, "Host has duplicate player spawns.");
        Require(owner.GetComponent<PlayerController>().enabled &&
            owner.GetComponent<UnityEngine.InputSystem.PlayerInput>().enabled &&
            owner.GetComponent<Rigidbody>().isKinematic, "Client must have input but not physics authority.");
        Require(!replica.GetComponent<PlayerController>().enabled &&
            !replica.GetComponent<PlayerLook>().enabled &&
            replica.GetComponent<PlayerSkillController>().enabled &&
            !replica.GetComponent<UnityEngine.InputSystem.PlayerInput>().enabled &&
            !replica.GetComponent<Rigidbody>().isKinematic &&
            !replica.GetComponentInChildren<Unity.Cinemachine.CinemachineCamera>().enabled,
            "Server replica must simulate physics/skills without local input or camera.");
        owner.GetComponent<PlayerController>().enabled = false;
        owner.GetComponent<PlayerLook>().enabled = false;
        Vector3 start = replica.transform.position;
        for (int i = 0; i < 20; i++)
        {
            owner.GetComponent<NetworkPlayerMotor>().SubmitInput(Vector2.up, replica.transform.eulerAngles.y, false);
            yield return null;
        }
        Require(Vector3.Distance(start, replica.transform.position) > 0.05f,
            "Server motor did not move the Ninja.");
        owner.GetComponent<NetworkPlayerMotor>().SubmitInput(Vector2.zero, replica.transform.eulerAngles.y, false);
        float catchUp = Time.realtimeSinceStartup + 8f;
        while (Vector3.Distance(replica.transform.position, owner.transform.position) >= 1.25f)
        {
            if (Time.realtimeSinceStartup >= catchUp)
                throw new InvalidOperationException(
                    "Original Ninja movement did not replicate. server=" + replica.transform.position +
                    " owner=" + owner.transform.position);
            yield return null;
        }
        var ownerAnimator = owner.GetComponentInChildren<Animator>();
        var replicaAnimator = replica.GetComponentInChildren<Animator>();
        yield return WaitFor(() => Mathf.Abs(ownerAnimator.GetFloat("MoveSpeed") -
            replicaAnimator.GetFloat("MoveSpeed")) < 0.01f,
            "Ninja locomotion animation did not replicate.");
        owner.GetComponent<NetworkPlayerMotor>().SubmitInput(Vector2.zero, 0f, false);
        yield return new WaitForSeconds(0.5f);
        Vector3 target = new Vector3(8f, 1f, 7f);
        replica.GetComponent<Rigidbody>().isKinematic = true;
        replica.transform.position = target;
        yield return WaitFor(() => Vector3.Distance(owner.transform.position, target) < 0.1f,
            "Server movement did not replicate to owner.");
        var hostOwner = host.LocalClient.PlayerObject;
        var hostReplica = client.SpawnManager.SpawnedObjects[hostOwner.NetworkObjectId];
        Require(!hostReplica.IsOwner, "Client incorrectly owns the host player.");
        hostOwner.GetComponent<PlayerController>().enabled = false;
        hostOwner.GetComponent<PlayerLook>().enabled = false;
        hostOwner.GetComponent<Rigidbody>().isKinematic = true;
        target = new Vector3(-8f, 1f, 7f);
        hostOwner.transform.position = target;
        yield return WaitFor(() => Vector3.Distance(hostReplica.transform.position, target) < 0.1f,
            "Host movement did not replicate to client.");
        client.Shutdown();
        yield return WaitFor(() => !client.ShutdownInProgress && host.ConnectedClients.Count == 1
            && host.SpawnManager.SpawnedObjects.Count == 1, "Disconnected player was not despawned.");
        Object.Destroy(clientObject);
        host.Shutdown();
        yield return WaitFor(() => !host.ShutdownInProgress && !host.IsListening, "Final shutdown timed out.");
    }
}

public sealed class NetworkTestValidationRunner : MonoBehaviour
{
    private IEnumerator Start()
    {
        // Flatten nested coroutines so all assertion failures are captured and Play mode is always stopped.
        var stack = new System.Collections.Generic.Stack<IEnumerator>();
        stack.Push(NetworkTestValidation.Validate());
        string error = null;
        while (stack.Count > 0)
        {
            object current = null;
            try
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                current = stack.Peek().Current;
            }
            catch (Exception exception)
            {
                error = exception.ToString();
                break;
            }
            if (current is IEnumerator nested) stack.Push(nested);
            else yield return current;
        }
        string result = error == null
            ? "PASS: BasePlayer inheritance, offline Ninja behavior, actual Ninja host/client spawn, owner input/camera/physics isolation, original movement and animator replication, bidirectional transforms, disconnect cleanup."
            : "FAIL: " + error;
        SessionState.SetString("NetworkTestValidation.Result", result);
        if (error == null) Debug.Log(result);
        else Debug.LogError(result);
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(error == null ? 0 : 1);
            yield break;
        }
        EditorApplication.isPlaying = false;
    }
}
