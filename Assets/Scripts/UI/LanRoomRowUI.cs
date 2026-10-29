using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 탐색된 방의 정보 표시와 참가 조작
public sealed class LanRoomRowUI : MonoBehaviour
{
    // 방 이름·정원·주소와 참가 버튼 참조
    [SerializeField] private TMP_Text roomTitleText;
    [SerializeField] private TMP_Text occupancyText;
    [SerializeField] private TMP_Text addressText;
    [SerializeField] private Button joinButton;

    // 현재 탐색 결과와 상위 화면의 참가 요청 콜백
    private LanDiscoveredRoom room;
    private Action<LanDiscoveredRoom> joinAction;

    // 행 재사용 시 이전 클릭 구독 제거와 탐색 결과 연결
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

    // 행 데이터·클릭 구독 초기화와 표시 해제
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

    // 행 파괴 시 참가 버튼 구독 해제
    private void OnDestroy()
    {
        UnsubscribeJoin();
    }

    // 클릭 시 참가 가능 여부 재확인 후 상위 화면에 요청 전달
    private void HandleJoinClicked()
    {
        LanDiscoveredRoom selectedRoom = room;
        Action<LanDiscoveredRoom> callback = joinAction;
        if (selectedRoom == null || !selectedRoom.IsJoinable || callback == null)
            return;

        callback(selectedRoom);
    }

    // 이 행이 등록한 참가 처리만 제거
    private void UnsubscribeJoin()
    {
        if (joinButton != null)
            joinButton.onClick.RemoveListener(HandleJoinClicked);
    }

    // 탐색 정보의 서식 태그 해석 없는 표시
    private static void SetText(TMP_Text label, string value)
    {
        if (label == null)
            return;

        label.richText = false;
        label.text = value ?? string.Empty;
    }

    // 참가 버튼 하위 문구 갱신
    private static void SetChildButtonLabel(Button button, string value)
    {
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        SetText(label, value);
    }
}
