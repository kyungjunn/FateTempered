#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Only runs in an explicit development-player invocation. Normal game startup is untouched.
public sealed class LanRoomScenario : MonoBehaviour
{
    [Serializable]
    private sealed class Report
    {
        public string role;
        public bool passed;
        public string error;
        public int cycles;
        public string endpoint;
        public string discoveryScope;
        public List<string> events = new List<string>();
        public List<string> eventTimes = new List<string>();
    }

    private readonly Stack<IEnumerator> routines = new Stack<IEnumerator>();
    private Report report;
    private string directory;
    private bool host;
    private bool nonLoopback;
    private bool finished;
    private bool mapFaultInjected;
    private bool runProbes;
    private string rejectionReason;
    private bool probeConnected;
    private NetworkGameSession Session => NetworkGameSession.Instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartRequestedScenario()
    {
        string[] args = Environment.GetCommandLineArgs();
        string role = Argument(args, "--lan-test-role");
        if (role != "host" && role != "client" && role != "probe") return;
        if (FindFirstObjectByType<LanRoomScenario>() != null) return;
        var runner = new GameObject("LanRoomScenario").AddComponent<LanRoomScenario>();
        DontDestroyOnLoad(runner.gameObject);
        runner.host = role == "host";
        runner.runProbes = Array.IndexOf(args, "--lan-test-probes") >= 0;
        runner.nonLoopback = Array.IndexOf(args, "--lan-test-nonloopback") >= 0;
        runner.directory = Argument(args, "--lan-test-dir");
        runner.report = new Report { role = role, discoveryScope = runner.nonLoopback ? "non-loopback directed-interface experiment" : "loopback permitted; not two-device LAN proof" };
        if (string.IsNullOrWhiteSpace(runner.directory))
        {
            runner.Finish(false, "--lan-test-dir is required.");
            return;
        }
        Directory.CreateDirectory(runner.directory);
        Application.runInBackground = true;
        runner.routines.Push(role == "probe" ? runner.ProbeAdmissions() : runner.Run());
    }

    private static string Argument(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void Update()
    {
        if (finished || report == null) return;
        try
        {
            for (int budget = 0; budget < 32 && routines.Count > 0; budget++)
            {
                IEnumerator current = routines.Peek();
                if (!current.MoveNext()) { routines.Pop(); continue; }
                if (current.Current is IEnumerator nested) { routines.Push(nested); continue; }
                return;
            }
            if (routines.Count == 0) Finish(true, null);
        }
        catch (Exception exception) { Finish(false, exception.ToString()); }
    }

    private IEnumerator Run()
    {
        yield return Wait(() => Session != null, "NetworkEntry session startup");
        Click("GameStartButton");
        yield return Wait(() => Session.State == NetworkGameSessionState.Browsing, "browser");
        for (int cycle = 1; cycle <= 5; cycle++)
        {
            string roomName = "LAN Test " + cycle;
            int chosen = host ? (cycle - 1) % 3 : cycle % 3;
            if (cycle == 4) chosen = 0;
            if (host)
            {
                SetInput("ProfileNameInput", "TestHost");
                Click("SaveProfileButton");
                Click("CreateRoomButton");
                SetInput("RoomNameInput", roomName);
                Click("ConfirmCreateButton");
                yield return Wait(() => Session.State == NetworkGameSessionState.Lobby && Session.Lobby != null, "host lobby");
                Require(PlayerCount() == 0, "No player objects before match start.");
                Require(!Session.CanStart, "Unselected host cannot start.");
                Mark("host-lobby", cycle, Session.RoomInstanceId.ToString());
            }
            else
            {
                yield return Wait(() => MarkExists("host-lobby", cycle), "host advertising");
                // Same-machine processes share OS preferences; serialize writes around admission.
                SetInput("ProfileNameInput", "TestGuest");
                Click("SaveProfileButton");
                yield return Wait(() => JoinButton(roomName) != null, "LAN room discovery", 45);
                Click(JoinButton(roomName));
                yield return Wait(() => Session.State == NetworkGameSessionState.Lobby && Session.Lobby != null, "client lobby sync");
            }
            yield return Wait(() => Session.Lobby != null && Session.Lobby.Snapshot.MemberCount == 2, "two committed lobby members");
            Require(Session.Lobby.Snapshot.Member0.DisplayName == "TestHost" &&
                Session.Lobby.Snapshot.Member1.DisplayName == "TestGuest", "Distinct selected profiles replicate.");
            Require(PlayerCount() == 0, "Both peers have zero lobby PlayerObjects.");
            if (host)
            {
                Session.RequestStart();
                Require(Session.Lobby.Snapshot.Phase == NetworkLobbyPhase.Lobby,
                    "Unselected host cannot bypass the disabled Start button.");
            }
            Click(new[] { "SwordButton", "NinjaButton", "WizardButton" }[chosen]);
            yield return Wait(() => LocalMember().Champion == chosen, "champion replication");
            if (!host)
            {
                Click("ReadyButton");
                yield return Wait(() => LocalMember().Ready, "ready replication");
                Session.RequestStart();
                int alternate = (chosen + 1) % 3;
                Click(new[] { "SwordButton", "NinjaButton", "WizardButton" }[alternate]);
                yield return Wait(() => LocalMember().Champion == alternate && !LocalMember().Ready,
                    "champion change clears readiness");
                Require(Session.Lobby.Snapshot.Phase == NetworkLobbyPhase.Lobby,
                    "Guest cannot start even when ready.");
                Click(new[] { "SwordButton", "NinjaButton", "WizardButton" }[chosen]);
                yield return Wait(() => LocalMember().Champion == chosen, "champion reselection");
                Click("ReadyButton");
                yield return Wait(() => LocalMember().Ready, "readiness restored explicitly");
                Mark("guest-ready", cycle, "ready");
            }
            yield return Wait(() => Session.Lobby.Notices.Count > 0, "replicated join notice");
            yield return Screenshot(cycle, "lobby");
            Mark(host ? "host-lobby-captured" : "guest-lobby-captured", cycle, "captured");
            if (host)
            {
                yield return Wait(() => Session.CanStart && MarkExists("guest-lobby-captured", cycle), "all-ready start gate");
                if (runProbes && cycle == 1)
                {
                    Mark("probe-full-room", 1, Session.RoomInstanceId.ToString());
                    yield return Wait(() => MarkExists("probe-full-complete", 1), "stale protocol and capacity probes");
                }
                if (cycle == 5) SceneManager.sceneLoaded += InjectInvalidMap;
                Click("StartMatchButton");
            }
            if (cycle == 5)
            {
                yield return Wait(() => Session.State == NetworkGameSessionState.Browsing &&
                    !string.IsNullOrEmpty(Session.Error), "shared start failure returns visible error", 100);
                Require(!host || mapFaultInjected, "Failure injection actually reached the host Map.");
                Require(PlayerCount() == 0 && !NetworkGameSession.IsNetworkSessionActive &&
                    !SceneManager.GetSceneByName("Map").isLoaded &&
                    !SceneManager.GetSceneByName("InGame").isLoaded &&
                    !SceneManager.GetSceneByName("TeamLobby").isLoaded,
                    "Shared failed start drains every player and owned scene.");
                yield return Screenshot(cycle, "failed-start");
                Mark(host ? "host-failure-drained" : "guest-failure-drained", cycle, "drained");
                yield return Wait(() => MarkExists(host ? "guest-failure-drained" : "host-failure-drained", cycle),
                    "both peers drained failed start");
                continue;
            }
            yield return Wait(() => Session.State == NetworkGameSessionState.Running && GameManager.Instance != null &&
                GameManager.Instance.CurrentPlayer != null && PlayerCount() == 2, "shared world and local owner", 100);
            Require(SceneManager.GetSceneByName("NetworkEntry").isLoaded, "Bootstrap survives scene synchronization.");
            Require(SceneManager.GetSceneByName("InGame").isLoaded && SceneManager.GetSceneByName("Map").isLoaded, "Both gameplay scenes loaded.");
            GameObject player = GameManager.Instance.CurrentPlayer;
            Require(player.GetComponent<PlayerCharacter>().CharacterId == (PlayerCharacterId)chosen, "Selected champion spawned.");
            Require(player.GetComponent<NetworkObject>().IsOwner, "HUD player is local owner.");
            Require(!GameManager.Instance.IsPaused && Time.timeScale == 1f, "Network combat is not globally paused.");
            Require(FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length == 1, "Exactly one active EventSystem.");
            int listeners = 0;
            foreach (var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None)) if (listener.enabled) listeners++;
            Require(listeners == 1, "Exactly one active audio listener.");
            Require(FindObjectsByType<EnemyAI>(FindObjectsSortMode.None).Length == 0, "Unsupported enemy spawning disabled.");
            var level = player.GetComponent<PlayerLevelSystem>();
            int xp = level.currentExp;
            level.AddExp(500);
            Require(level.currentExp == xp, "Direct network XP mutation rejected.");
            Click("OpenLeaveButton");
            Require(Time.timeScale == 1f && !GameManager.Instance.IsPaused, "Local menu does not pause server time.");
            Require(!player.GetComponent<PlayerInput>().enabled, "Menu blocks local input.");
            Click("LeaveCancelButton");
            Require(player.GetComponent<PlayerInput>().enabled, "Menu restores live owner input.");
            yield return Screenshot(cycle, "match");
            if (host && runProbes && cycle == 1)
            {
                Mark("probe-locked-room", 1, Session.RoomInstanceId.ToString());
                yield return Wait(() => MarkExists("probe-locked-complete", 1), "locked admission rejection");
            }
            Mark(host ? "host-match" : "guest-match", cycle, Session.RoomInstanceId.ToString());
            yield return Wait(() => MarkExists(host ? "guest-match" : "host-match", cycle), "peer world evidence");
            if (!host)
            {
                Click("OpenLeaveButton");
                Click("LeaveConfirmButton");
            }
            else
            {
                yield return Wait(() => PlayerCount() == 1 && Session.Lobby.Snapshot.MemberCount == 1, "teammate leave isolation");
                Require(Session.State == NetworkGameSessionState.Running && GameManager.Instance.CurrentPlayer == player,
                    "Teammate departure leaves host combat active.");
                Click("OpenLeaveButton");
                Click("LeaveConfirmButton");
            }
            yield return Wait(() => Session.State == NetworkGameSessionState.Browsing, "drained shutdown", 60);
            Require(!NetworkGameSession.IsNetworkSessionActive && PlayerCount() == 0, "No stale player or network mode after shutdown.");
            Require(!SceneManager.GetSceneByName("Map").isLoaded && !SceneManager.GetSceneByName("InGame").isLoaded &&
                !SceneManager.GetSceneByName("TeamLobby").isLoaded, "All owned scenes unloaded.");
            Mark(host ? "host-stopped" : "guest-stopped", cycle, "stopped");
            yield return Wait(() => MarkExists(host ? "guest-stopped" : "host-stopped", cycle), "both peers ready for restart");
            report.cycles = cycle;
            Record("cycle " + cycle + " complete");
        }
        yield return HostLossAndSolo();
    }

    private IEnumerator ProbeAdmissions()
    {
        yield return Wait(() => Session != null, "probe bootstrap");
        yield return Wait(() => MarkExists("probe-full-room", 1), "full room probe barrier", 100);
        Guid room = Guid.Parse(File.ReadAllText(Path.Combine(directory, "probe-full-room-1")));
        Require(RoomConnectionPayload.TryEncode(Guid.NewGuid(), "Probe", out byte[] stale, out _), "Encode stale probe.");
        yield return RejectProbe(stale, "stale");
        Require(RoomConnectionPayload.TryEncode(room, "Probe", out byte[] payload, out _), "Encode valid probe.");
        byte[] wrongVersion = (byte[])payload.Clone();
        wrongVersion[0]++;
        yield return RejectProbe(wrongVersion, "protocol");
        yield return RejectProbe(payload, "full");
        Mark("probe-full-complete", 1, "rejected");
        yield return Wait(() => MarkExists("probe-locked-room", 1), "locked room probe barrier", 100);
        yield return RejectProbe(payload, "not accepting");
        Mark("probe-locked-complete", 1, "rejected");
    }

    private IEnumerator RejectProbe(byte[] payload, string expectedReason)
    {
        NetworkManager manager = Session.NetworkManager;
        rejectionReason = null;
        probeConnected = false;
        manager.NetworkConfig.ConnectionData = payload;
        manager.NetworkConfig.PlayerPrefab = null;
        manager.GetComponent<UnityTransport>().SetConnectionData("127.0.0.1", 7777);
        manager.OnClientConnectedCallback += ProbeConnected;
        manager.OnClientDisconnectCallback += ProbeDisconnected;
        Require(manager.StartClient(), "Probe client starts real transport.");
        yield return Wait(() => rejectionReason != null || probeConnected, "server rejection: " + expectedReason);
        manager.OnClientConnectedCallback -= ProbeConnected;
        manager.OnClientDisconnectCallback -= ProbeDisconnected;
        Require(!probeConnected && rejectionReason.IndexOf(expectedReason, StringComparison.OrdinalIgnoreCase) >= 0,
            "Server rejects with expected reason " + expectedReason + "; actual: " + rejectionReason);
        Record("authoritative rejection: " + rejectionReason);
        if (manager.IsListening && !manager.ShutdownInProgress) manager.Shutdown();
        yield return Wait(() => !manager.IsListening && !manager.ShutdownInProgress, "probe transport drain");
        Require(PlayerCount() == 0, "Rejected probe never receives PlayerObjects.");
    }

    private void ProbeConnected(ulong clientId) { probeConnected = true; }
    private void ProbeDisconnected(ulong clientId) { rejectionReason = Session.NetworkManager.DisconnectReason ?? ""; }

    private void InjectInvalidMap(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Map") return;
        SceneManager.sceneLoaded -= InjectInvalidMap;
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.CompareTag("PlayerSpawnPoint"))
                {
                    child.gameObject.tag = "Untagged";
                    mapFaultInjected = true;
                    Record("injected missing authored spawn marker in runtime host scene only");
                    return;
                }
        throw new InvalidOperationException("Map fault injection found no authored spawn marker.");
    }

    private IEnumerator HostLossAndSolo()
    {
        const string roomName = "Host Loss Test";
        if (host)
        {
            SetInput("ProfileNameInput", "TestHost");
            Click("SaveProfileButton");
            Click("CreateRoomButton");
            SetInput("RoomNameInput", roomName);
            Click("ConfirmCreateButton");
            yield return Wait(() => Session.State == NetworkGameSessionState.Lobby, "host-loss room");
            Mark("host-loss-advertising", 4, "ready");
        }
        else
        {
            yield return Wait(() => MarkExists("host-loss-advertising", 4), "host-loss advertisement");
            SetInput("ProfileNameInput", "TestGuest");
            Click("SaveProfileButton");
            yield return Wait(() => JoinButton(roomName) != null, "host-loss room discovery");
            Click(JoinButton(roomName));
        }
        yield return Wait(() => Session.State == NetworkGameSessionState.Lobby && Session.Lobby != null &&
            Session.Lobby.Snapshot.MemberCount == 2, "host-loss committed roster");
        Mark(host ? "host-loss-host-synced" : "host-loss-guest-synced", 4, "ready");
        yield return Wait(() => MarkExists(host ? "host-loss-guest-synced" : "host-loss-host-synced", 4),
            "both peers synchronized before host departure");
        if (host) Click("LeaveButton");
        yield return Wait(() => Session.State == NetworkGameSessionState.Browsing, "host departure cleanup", 60);
        Require(PlayerCount() == 0 && !SceneManager.GetSceneByName("TeamLobby").isLoaded,
            "Host departure leaves no lobby or player objects.");
        if (!host)
        {
            Require(!string.IsNullOrEmpty(Session.Error), "Guest receives a visible host departure reason.");
            Mark("host-loss-guest-stopped", 4, "stopped");
            yield break;
        }
        yield return Wait(() => MarkExists("host-loss-guest-stopped", 4), "guest observed host departure");
        Click("CreateRoomButton");
        SetInput("RoomNameInput", "Solo Test");
        Click("ConfirmCreateButton");
        yield return Wait(() => Session.State == NetworkGameSessionState.Lobby, "solo lobby");
        yield return Wait(() => GameObject.Find("SwordButton") != null, "solo champion controls");
        Click("SwordButton");
        yield return Wait(() => Session.CanStart, "solo selected host eligible");
        Click("StartMatchButton");
        yield return Wait(() => Session.State == NetworkGameSessionState.Running &&
            GameManager.Instance != null && GameManager.Instance.CurrentPlayer != null, "solo combat");
        Require(PlayerCount() == 1, "Solo start spawns exactly one player.");
        yield return Screenshot(4, "solo");
        Click("OpenLeaveButton");
        Click("LeaveConfirmButton");
        yield return Wait(() => Session.State == NetworkGameSessionState.Browsing, "solo cleanup", 60);
        Require(PlayerCount() == 0 && !NetworkGameSession.IsNetworkSessionActive, "Solo cleanup drains network state.");
        Session.BackToStart();
        yield return Wait(() => Session.State == NetworkGameSessionState.Entry && Session.Discovery.IsFullyStopped,
            "discovery drained before offline transition");
        SceneManager.LoadScene("InGame", LoadSceneMode.Single);
        yield return Wait(() => GameManager.Instance != null && GameManager.Instance.IsActiveSession &&
            GameManager.Instance.CurrentPlayer != null, "offline startup after network session", 100);
        GameManager offline = GameManager.Instance;
        Require(!offline.IsNetworkCombatActive && !NetworkGameSession.IsNetworkSessionActive && PlayerCount() == 0,
            "Offline startup never spawns network PlayerObjects.");
        var level = offline.CurrentPlayer.GetComponent<PlayerLevelSystem>();
        int xp = level.currentExp;
        level.AddExp(1);
        Require(level.currentExp == xp + 1, "Offline XP mutation is restored after networking.");
        offline.PauseGame();
        Require(offline.IsPaused && Time.timeScale == 0f, "Offline pause is restored.");
        offline.ResumeGame();
        Require(!offline.IsPaused && Time.timeScale == 1f, "Offline resume is restored.");
        yield return Screenshot(6, "offline-after-network");
        offline.EndCombatSession(offline.SessionId);
        Require(!offline.IsActiveSession && offline.CurrentPlayer == null, "Offline session ends after networking.");
        Record("offline startup XP pause resume and teardown after network verified");
    }

    private NetworkLobbyMemberSnapshot LocalMember()
    {
        var snapshot = Session.Lobby.Snapshot;
        return snapshot.Member0.ClientId == Session.NetworkManager.LocalClientId ? snapshot.Member0 : snapshot.Member1;
    }

    private Button JoinButton(string roomName)
    {
        foreach (var row in FindObjectsByType<LanRoomRowUI>(FindObjectsSortMode.None))
        {
            var room = (LanDiscoveredRoom)typeof(LanRoomRowUI).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(row);
            if (room == null || room.RoomName != roomName || !room.IsJoinable || (nonLoopback && IPAddress.IsLoopback(room.Address))) continue;
            var button = row.GetComponentInChildren<Button>();
            if (button == null || !button.interactable) continue;
            report.endpoint = room.Endpoint.ToString();
            return button;
        }
        return null;
    }

    private int PlayerCount()
    {
        int count = 0;
        foreach (var value in FindObjectsByType<NetworkObject>(FindObjectsSortMode.None))
            if (value.IsSpawned && value.IsPlayerObject) count++;
        return count;
    }

    private IEnumerator Wait(Func<bool> predicate, string description, float seconds = 40)
    {
        double deadline = Time.realtimeSinceStartupAsDouble + seconds;
        while (!predicate())
        {
            if (Time.realtimeSinceStartupAsDouble > deadline)
                throw new TimeoutException(description + ": " + (Session != null ? Session.State + " / " + Session.Status + " / " + Session.Error : "no session"));
            yield return null;
        }
        Record("observed " + description);
    }

    private IEnumerator Screenshot(int cycle, string phase)
    {
        string path = Path.Combine(directory, report.role + "-" + cycle + "-" + phase + ".png");
        yield return null;
        ScreenCapture.CaptureScreenshot(path);
        yield return Wait(() => File.Exists(path) && new FileInfo(path).Length > 1024, "screenshot " + phase, 15);
    }

    private void Click(string name)
    {
        var found = GameObject.Find(name);
        Require(found != null, "Visible button exists: " + name);
        Click(found.GetComponent<Button>());
    }

    private void Click(Button button)
    {
        Require(button != null && button.gameObject.activeInHierarchy && button.interactable, "Button is actionable.");
        Record("click " + button.name);
        button.onClick.Invoke();
    }

    private void SetInput(string name, string value)
    {
        var found = GameObject.Find(name);
        Require(found != null, "Visible input exists: " + name);
        found.GetComponent<TMP_InputField>().text = value;
        Record("input " + name + " = " + value);
    }

    private void Mark(string name, int cycle, string value) => File.WriteAllText(Path.Combine(directory, name + "-" + cycle), value);
    private bool MarkExists(string name, int cycle) => File.Exists(Path.Combine(directory, name + "-" + cycle));
    private void Record(string value)
    {
        report.events.Add(value);
        report.eventTimes.Add(DateTime.UtcNow.ToString("o"));
        Debug.Log("LAN_SCENARIO " + report.role + ": " + value);
    }
    private static void Require(bool value, string description) { if (!value) throw new InvalidOperationException(description); }
    private void Finish(bool passed, string error)
    {
        SceneManager.sceneLoaded -= InjectInvalidMap;
        finished = true;
        report.passed = passed;
        report.error = error;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, report.role + ".json"), JsonUtility.ToJson(report, true));
        }
        Debug.Log("LAN_SCENARIO_RESULT " + JsonUtility.ToJson(report));
        Application.Quit(passed ? 0 : 1);
    }
}
#endif
