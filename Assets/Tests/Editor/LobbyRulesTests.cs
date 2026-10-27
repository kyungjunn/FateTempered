using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Pure readiness predicate coverage; process-level RPC/scene behavior is tested separately.
public static class LobbyRulesTests
{
    [MenuItem("Tools/Validation/LAN Lobby Readiness Rules")]
    public static void Run()
    {
        var check = typeof(NetworkLobbyState).GetMethod("CanStartFromSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
        if (check == null) throw new InvalidOperationException("Lobby readiness predicate is missing.");
        var host = new NetworkLobbyMemberSnapshot
        {
            ClientId = 0, Champion = (int)PlayerCharacterId.Sword, Occupied = true,
            Connected = true, Synchronized = true, IsHost = true
        };
        var guest = new NetworkLobbyMemberSnapshot
        {
            ClientId = 1, Champion = (int)PlayerCharacterId.Wizard, Occupied = true,
            Connected = true, Synchronized = true, Ready = true
        };
        var snapshot = new NetworkLobbySnapshot { Phase = NetworkLobbyPhase.Lobby, Member0 = host };
        Require(Evaluate(check, snapshot), "Solo host with a selected champion may start.");
        snapshot.Member0.Champion = -1;
        Require(!Evaluate(check, snapshot), "Unselected host cannot start solo.");
        snapshot.Member0 = host;
        snapshot.Member1 = guest;
        Require(Evaluate(check, snapshot), "Synchronized ready teammate permits shared start.");
        snapshot.Member1.Ready = false;
        Require(!Evaluate(check, snapshot), "Unready teammate blocks shared start.");
        snapshot.Member1 = guest;
        snapshot.Member1.Champion = -1;
        Require(!Evaluate(check, snapshot), "Ready without champion cannot start.");
        snapshot.Member1.Champion = 999;
        Require(!Evaluate(check, snapshot), "Unknown champion cannot start.");
        snapshot.Member1 = guest;
        snapshot.Member1.Champion = host.Champion;
        Require(Evaluate(check, snapshot), "Duplicate champions are permitted.");
        snapshot.Member1 = guest;
        snapshot.Member1.Synchronized = false;
        Require(!Evaluate(check, snapshot), "Unsynchronized teammate blocks start.");
        snapshot.Member1 = guest;
        snapshot.Member1.Connected = false;
        Require(!Evaluate(check, snapshot), "Disconnected committed teammate blocks start.");
        snapshot.Member1 = guest;
        snapshot.Member1.IsHost = true;
        Require(!Evaluate(check, snapshot), "Two host slots cannot start.");
        snapshot.Member1 = guest;
        foreach (var phase in new[] { NetworkLobbyPhase.LoadingMatch, NetworkLobbyPhase.Running, NetworkLobbyPhase.Stopping })
        {
            snapshot.Phase = phase;
            Require(!Evaluate(check, snapshot), "No second start after lobby lock.");
        }
        snapshot.Phase = NetworkLobbyPhase.Lobby;
        snapshot.Member0.Synchronized = false;
        Require(!Evaluate(check, snapshot), "Unprepared host cannot start.");
        snapshot.Member0 = host;
        var changed = snapshot;
        changed.Member1.Ready = false;
        Require(!snapshot.Equals(changed), "Readiness changes must replicate.");
        changed = snapshot;
        changed.Member1.Champion = (int)PlayerCharacterId.Ninja;
        Require(!snapshot.Equals(changed), "Champion changes must replicate.");
        changed = snapshot;
        changed.Phase = NetworkLobbyPhase.LoadingMatch;
        Require(!snapshot.Equals(changed), "Phase changes must replicate.");
        Debug.Log("LOBBY_RULES_TESTS_PASSED: solo, shared-ready, invalid champions, synchronization and locked phases");
    }

    private static bool Evaluate(MethodInfo check, NetworkLobbySnapshot snapshot)
    {
        return (bool)check.Invoke(null, new object[] { snapshot });
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Lobby readiness regression: " + reason);
    }
}
