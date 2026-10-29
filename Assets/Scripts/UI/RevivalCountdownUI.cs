using System;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using Unity.Netcode;

public sealed class RevivalCountdownUI : MonoBehaviour
{
    [SerializeField] private GameObject countdownPanel;
    [SerializeField] private TMP_Text countdownText;

    private NetworkGameSession session;
    private NetworkLobbyState lobby;
    private readonly StringBuilder displayBuilder = new StringBuilder(128);
    private string lastDisplayedText;

    private void Awake()
    {
        SetVisible(false);
    }

    private void OnEnable()
    {
        EnsureSessionBinding();
        Render();
    }

    private void OnDisable()
    {
        UnbindSession();
    }

    private void Update()
    {
        // Late-bind if the persistent canvas enabled before networking was initialized.
        EnsureSessionBinding();
        Render();
    }

    private void EnsureSessionBinding()
    {
        NetworkGameSession currentSession = NetworkGameSession.Instance;
        if (!ReferenceEquals(session, currentSession))
        {
            UnbindSession();
            session = currentSession;
            if (session != null)
                session.Changed += HandleSessionChanged;
        }

        BindLobby(session != null ? session.Lobby : null);
    }

    private void UnbindSession()
    {
        if (!ReferenceEquals(session, null))
            session.Changed -= HandleSessionChanged;
        session = null;
        BindLobby(null);
    }

    private void BindLobby(NetworkLobbyState currentLobby)
    {
        if (ReferenceEquals(lobby, currentLobby))
            return;

        if (!ReferenceEquals(lobby, null))
            lobby.Changed -= HandleLobbyChanged;

        lobby = currentLobby;
        if (lobby != null)
            lobby.Changed += HandleLobbyChanged;
    }

    private void HandleSessionChanged(NetworkGameSession changedSession)
    {
        if (changedSession != session)
            return;

        BindLobby(session.Lobby);
        Render();
    }

    private void HandleLobbyChanged(NetworkLobbyState changedLobby)
    {
        if (changedLobby == lobby)
            Render();
    }

    private void Render()
    {
        if (session == null || session.State != NetworkGameSessionState.Running ||
            lobby == null || !lobby.IsSpawned)
        {
            Hide();
            return;
        }

        NetworkLobbySnapshot roster = lobby.Snapshot;
        if (roster.Phase != NetworkLobbyPhase.Running)
        {
            Hide();
            return;
        }

        var revives = lobby.Revives;
        if (revives.StartRevision != roster.StartRevision)
        {
            Hide();
            return;
        }

        NetworkManager manager = session.NetworkManager;
        if (manager == null || !manager.IsListening)
        {
            Hide();
            return;
        }

        double serverNow = manager.ServerTime.Time;
        displayBuilder.Clear();
        bool hasLine = false;
        AppendCountdown(ref hasLine, revives.Member0.ClientId, revives.Member0.Pending,
            revives.Member0.ReviveAtServerTime, roster, serverNow);
        AppendCountdown(ref hasLine, revives.Member1.ClientId, revives.Member1.Pending,
            revives.Member1.ReviveAtServerTime, roster, serverNow);

        if (!hasLine)
        {
            Hide();
            return;
        }

        string rendered = displayBuilder.ToString();
        SetVisible(true);
        if (countdownText != null && rendered != lastDisplayedText)
        {
            countdownText.text = rendered;
            lastDisplayedText = rendered;
        }
    }

    private void AppendCountdown(ref bool hasLine, ulong clientId, bool pending,
        double reviveAtServerTime, NetworkLobbySnapshot roster, double serverNow)
    {
        if (!pending)
            return;

        double remaining = Math.Max(0d, reviveAtServerTime - serverNow);
        int seconds = (int)Math.Ceiling(remaining);
        if (hasLine)
            displayBuilder.AppendLine();

        displayBuilder.Append(GetMemberName(roster, clientId));
        displayBuilder.Append(" 부활까지 ");
        displayBuilder.Append(seconds.ToString(CultureInfo.InvariantCulture));
        displayBuilder.Append("초");
        hasLine = true;
    }

    private static string GetMemberName(NetworkLobbySnapshot roster, ulong clientId)
    {
        if (roster.Member0.Occupied && roster.Member0.ClientId == clientId &&
            !string.IsNullOrEmpty(roster.Member0.DisplayName))
        {
            return roster.Member0.DisplayName;
        }
        if (roster.Member1.Occupied && roster.Member1.ClientId == clientId &&
            !string.IsNullOrEmpty(roster.Member1.DisplayName))
        {
            return roster.Member1.DisplayName;
        }
        return "플레이어";
    }

    private void Hide()
    {
        SetVisible(false);
        if (countdownText != null && !string.IsNullOrEmpty(lastDisplayedText))
        {
            countdownText.text = string.Empty;
            lastDisplayedText = string.Empty;
        }
    }

    private void SetVisible(bool visible)
    {
        if (countdownPanel != null && countdownPanel.activeSelf != visible)
            countdownPanel.SetActive(visible);
    }
}
