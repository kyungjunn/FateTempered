using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class LanRoomRowUI : MonoBehaviour
{
    [SerializeField] private TMP_Text roomTitleText;
    [SerializeField] private TMP_Text occupancyText;
    [SerializeField] private TMP_Text addressText;
    [SerializeField] private Button joinButton;

    private LanDiscoveredRoom room;
    private Action<LanDiscoveredRoom> joinAction;

    public void Bind(LanDiscoveredRoom discoveredRoom, Action<LanDiscoveredRoom> onJoin)
    {
        UnsubscribeJoin();
        room = discoveredRoom;
        joinAction = onJoin;

        if (room == null)
        {
            Clear();
            return;
        }

        SetText(roomTitleText, room.RoomName);
        SetText(occupancyText, room.Occupancy + "/" + room.Capacity);
        SetText(addressText, (room.Address != null ? room.Address.ToString() : "") + ":" + room.GamePort);

        bool canJoin = room.IsJoinable && joinAction != null;
        if (joinButton != null)
        {
            joinButton.interactable = canJoin;
            joinButton.onClick.AddListener(HandleJoinClicked);
            SetChildButtonLabel(joinButton, "참가");
        }

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);
    }

    public void Clear()
    {
        UnsubscribeJoin();
        room = null;
        joinAction = null;
        SetText(roomTitleText, string.Empty);
        SetText(occupancyText, string.Empty);
        SetText(addressText, string.Empty);
        if (joinButton != null)
            joinButton.interactable = false;
        if (gameObject.activeSelf)
            gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        UnsubscribeJoin();
    }

    private void HandleJoinClicked()
    {
        LanDiscoveredRoom selectedRoom = room;
        Action<LanDiscoveredRoom> callback = joinAction;
        if (selectedRoom == null || !selectedRoom.IsJoinable || callback == null)
            return;

        callback(selectedRoom);
    }

    private void UnsubscribeJoin()
    {
        if (joinButton != null)
            joinButton.onClick.RemoveListener(HandleJoinClicked);
    }

    private static void SetText(TMP_Text label, string value)
    {
        if (label == null)
            return;

        label.richText = false;
        label.text = value ?? string.Empty;
    }

    private static void SetChildButtonLabel(Button button, string value)
    {
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        SetText(label, value);
    }
}
