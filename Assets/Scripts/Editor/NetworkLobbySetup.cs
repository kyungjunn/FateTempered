using System;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Creates only the new networking scenes; never rewrites combat prefabs or offline scenes.
public static class NetworkLobbySetup
{
    public const string EntryPath = "Assets/Scenes/NetworkEntry.unity";
    public const string LobbyPath = "Assets/Scenes/TeamLobby.unity";
    private static readonly Color Background = new Color(0.035f, 0.055f, 0.09f, 1f);
    private static readonly Color Surface = new Color(0.075f, 0.11f, 0.16f, 1f);
    private static readonly Color Accent = new Color(0.08f, 0.42f, 0.49f, 1f);
    private static TMP_FontAsset font;

    [MenuItem("Tools/Networking/Create LAN Lobby Scenes")]
    public static void CreateScenes()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play mode before creating LAN scenes.");
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("Save your current scenes before creating LAN scenes.");
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/양진체v0.93 SDF.asset");
        if (font == null) throw new InvalidOperationException("The existing Korean UI font is missing.");
        var setup = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(EntryPath) == null) CreateEntry();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(LobbyPath) == null) CreateLobby();
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            AddScene(scenes, EntryPath);
            AddScene(scenes, LobbyPath);
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log("LAN_SCENES_CREATED: NetworkEntry and TeamLobby enabled; existing offline scenes unchanged.");
        }
        finally { EditorSceneManager.RestoreSceneManagerSetup(setup); }
    }

    private static void AddScene(List<EditorBuildSettingsScene> scenes, string path)
    {
        int index = scenes.FindIndex(scene => scene.path == path);
        if (index < 0) scenes.Add(new EditorBuildSettingsScene(path, true));
        else scenes[index] = new EditorBuildSettingsScene(path, true);
    }

    private static void CreateEntry()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var root = new GameObject("NetworkSession");
        var transport = root.AddComponent<UnityTransport>();
        var manager = root.AddComponent<NetworkManager>();
        manager.NetworkConfig.NetworkTransport = transport;
        manager.NetworkConfig.EnableSceneManagement = true;
        manager.NetworkConfig.ConnectionApproval = true;
        manager.NetworkConfig.TickRate = 30;
        manager.NetworkConfig.LoadSceneTimeOut = 60;
        manager.NetworkConfig.PlayerPrefab = null;
        var registry = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkCombatSetup.RegistryPath);
        if (registry == null) throw new InvalidOperationException("Shared registered combat prefabs are missing.");
        manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Clear();
        manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(registry);
        root.AddComponent<LanRoomDiscovery>();
        var session = root.AddComponent<NetworkGameSession>();
        var sessionData = new SerializedObject(session);
        var players = sessionData.FindProperty("playerPrefabs");
        players.arraySize = 3;
        string[] names = { "SwordPlayer", "NinjaPlayer", "WizardPlayer" };
        for (int i = 0; i < names.Length; i++)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/" + names[i] + ".prefab");
            if (prefab == null) throw new InvalidOperationException("Missing player prefab: " + names[i]);
            players.GetArrayElementAtIndex(i).objectReferenceValue = prefab;
        }
        sessionData.ApplyModifiedPropertiesWithoutUndo();
        var cameraRoot = new GameObject("EntryCamera", typeof(Camera), typeof(AudioListener));
        var camera = cameraRoot.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Background;
        camera.cullingMask = 0;
        cameraRoot.tag = "MainCamera";
        var events = new GameObject("EntryEventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        events.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
        var canvas = Canvas("NetworkEntryCanvas", 0);
        var view = canvas.gameObject.AddComponent<NetworkEntryUI>();
        var fields = new SerializedObject(view);
        Bind(fields, "entryCameraRoot", cameraRoot);
        Bind(fields, "entryEventSystemRoot", events);

        var start = Panel(canvas, "StartPanel", Background);
        Bind(fields, "startPanel", start.gameObject);
        Label(start, "Title", "FATE TEMPERED", 110, 130, 1060, 80, 52);
        Label(start, "Subtitle", "같은 LAN에서 함께 시작하는 전투", 110, 225, 1060, 48, 25);
        Bind(fields, "gameStartButton", Button(start, "GameStartButton", "게임 시작", 110, 330, 320, 64));
        Label(start, "Scope", "2인 협동 · 로컬 프로필 · 로그인 없이 LAN 연결", 110, 420, 1060, 50, 21);

        var browser = Panel(canvas, "BrowserPanel", Background);
        Bind(fields, "browserPanel", browser.gameObject);
        Label(browser, "Title", "팀 찾기", 70, 40, 550, 60, 38);
        Label(browser, "ProfileLabel", "내 프로필", 70, 118, 130, 42);
        Bind(fields, "displayNameInput", Input(browser, "ProfileNameInput", "플레이어 이름", 210, 112, 390, 50));
        Bind(fields, "saveProfileButton", Button(browser, "SaveProfileButton", "이름 저장", 620, 112, 170, 50));
        Bind(fields, "openCreateRoomButton", Button(browser, "CreateRoomButton", "방 만들기", 830, 112, 180, 50));
        Bind(fields, "refreshButton", Button(browser, "RefreshRoomsButton", "새로고침", 1030, 112, 180, 50));
        Label(browser, "RoomHeader", "방 이름                          인원       주소", 90, 182, 1100, 38, 20);
        var content = Scroll(browser, "RoomList", 70, 228, 1140, 315);
        Bind(fields, "roomListContent", content);
        var rowRoot = Box(content, "RoomRowTemplate", 0, 0, 1120, 66, Surface);
        var row = rowRoot.gameObject.AddComponent<LanRoomRowUI>();
        var rowFields = new SerializedObject(row);
        Bind(rowFields, "roomTitleText", Label(rowRoot, "RoomTitle", "", 18, 8, 410, 50));
        Bind(rowFields, "occupancyText", Label(rowRoot, "Occupancy", "", 440, 8, 100, 50));
        Bind(rowFields, "addressText", Label(rowRoot, "Address", "", 560, 8, 330, 50, 18));
        Bind(rowFields, "joinButton", Button(rowRoot, "JoinButton", "참가", 930, 8, 150, 50));
        rowFields.ApplyModifiedPropertiesWithoutUndo();
        rowRoot.gameObject.SetActive(false);
        Bind(fields, "roomRowTemplate", row);
        Bind(fields, "browserBackButton", Button(browser, "BrowserBackButton", "처음으로", 70, 562, 180, 48));
        Label(browser, "LanHelp", "같은 네트워크의 공개 방만 표시됩니다. 인터넷 연결은 지원하지 않습니다.", 280, 565, 930, 42, 18);

        var create = Panel(canvas, "CreateRoomPanel", new Color(0, 0, 0, 0.82f));
        Bind(fields, "createRoomPanel", create.gameObject);
        var card = Box(create, "CreateCard", 310, 185, 660, 330, Surface);
        Label(card, "Title", "새 팀 만들기", 30, 22, 600, 55, 32);
        Bind(fields, "roomNameInput", Input(card, "RoomNameInput", "방 이름", 30, 100, 600, 55));
        Bind(fields, "createRoomConfirmButton", Button(card, "ConfirmCreateButton", "방 만들기", 30, 205, 285, 58));
        Bind(fields, "createRoomCancelButton", Button(card, "CancelCreateButton", "취소", 335, 205, 295, 58));

        var loading = Panel(canvas, "LoadingPanel", Background);
        Bind(fields, "loadingPanel", loading.gameObject);
        Label(loading, "LoadingTitle", "연결 및 준비 중", 210, 240, 860, 80, 38);
        Label(loading, "LoadingHint", "팀원의 동기화가 완료될 때까지 기다려 주세요.", 210, 340, 860, 55, 24);
        Bind(fields, "pendingCancelButton", Button(loading, "PendingCancelButton", "취소하고 나가기", 440, 435, 400, 60));
        Bind(fields, "leaveButton", Button(canvas, "OpenLeaveButton", "나가기 (Esc)", 1040, 24, 210, 46));
        var leave = Panel(canvas, "LeavePanel", new Color(0, 0, 0, 0.8f));
        Bind(fields, "leavePanel", leave.gameObject);
        var leaveCard = Box(leave, "LeaveCard", 330, 220, 620, 250, Surface);
        Label(leaveCard, "Title", "현재 세션에서 나갈까요?", 30, 24, 560, 60, 30);
        Bind(fields, "leaveConfirmButton", Button(leaveCard, "LeaveConfirmButton", "나가기", 30, 135, 265, 60));
        Bind(fields, "leaveCancelButton", Button(leaveCard, "LeaveCancelButton", "계속하기", 315, 135, 275, 60));
        Bind(fields, "statusText", Label(canvas, "Status", "", 70, 630, 1140, 30, 18));
        var errorLabel = Label(canvas, "Error", "", 70, 665, 1140, 38, 18);
        errorLabel.color = new Color(1f, 0.5f, 0.42f);
        Bind(fields, "errorText", errorLabel);
        fields.ApplyModifiedPropertiesWithoutUndo();
        browser.gameObject.SetActive(false);
        create.gameObject.SetActive(false);
        loading.gameObject.SetActive(false);
        leave.gameObject.SetActive(false);
        EditorSceneManager.SaveScene(scene, EntryPath);
    }

    [MenuItem("Tools/Networking/Rebuild Team Lobby Layout")]
    public static void RebuildLobby()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play mode before rebuilding the lobby.");
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("Save loaded scenes before rebuilding the lobby.");
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/양진체v0.93 SDF.asset");
        var setup = EditorSceneManager.GetSceneManagerSetup();
        try { CreateLobby(); }
        finally { EditorSceneManager.RestoreSceneManagerSetup(setup); }
        Debug.Log("TEAM_LOBBY_LAYOUT_REBUILT");
    }

    private static Image Portrait(Transform parent, string name, Sprite sprite, float x, float y, float size)
    {
        var rect = Box(parent, name, x, y, size, size, Color.white);
        var image = rect.GetComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        image.enabled = sprite != null;
        return image;
    }

    private static void CreateLobby()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var networkObject = new GameObject("LobbyState", typeof(NetworkObject), typeof(NetworkLobbyState))
            .GetComponent<NetworkObject>();
        var canvas = Canvas("TeamLobbyCanvas", 10);
        var view = canvas.gameObject.AddComponent<TeamLobbyUI>();
        var fields = new SerializedObject(view);
        var panel = Panel(canvas, "LobbyPanel", new Color(0.24f, 0.32f, 0.43f));
        Bind(fields, "lobbyPanel", panel.gameObject);
        var left = Box(panel, "TeamPanel", 55, 95, 715, 525, new Color(0.13f, 0.19f, 0.25f, 0.82f));
        var right = Box(panel, "ChampionPanel", 815, 95, 410, 525, new Color(0.13f, 0.19f, 0.25f, 0.82f));
        Bind(fields, "roomTitleText", Label(left, "RoomTitle", "팀 로비", 25, 14, 665, 45, 30));
        Bind(fields, "member0Portrait", Portrait(left, "HostPortrait", null, 60, 65, 220));
        Bind(fields, "member1Portrait", Portrait(left, "TeammatePortrait", null, 425, 65, 220));
        Bind(fields, "member0Text", Label(left, "HostMember", "", 25, 290, 330, 85, 20));
        Bind(fields, "member1Text", Label(left, "GuestMember", "", 380, 290, 310, 85, 20));
        Label(right, "ChampionLabel", "챔피언 선택", 22, 15, 245, 42, 25);
        string[] names = { "Sword", "Ninja", "Wizard" };
        string[] buttonFields = { "swordButton", "ninjaButton", "wizardButton" };
        var portraits = fields.FindProperty("championPortraits");
        portraits.arraySize = names.Length;
        for (int i = 0; i < names.Length; i++)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Images/CharacterPortraits/" + names[i] + "Idle.png");
            if (sprite == null) throw new InvalidOperationException("Missing champion portrait: " + names[i]);
            portraits.GetArrayElementAtIndex(i).objectReferenceValue = sprite;
            float y = 75 + i * 110;
            Portrait(right, names[i] + "Portrait", sprite, 22, y, 95);
            Bind(fields, buttonFields[i], Button(right, names[i] + "Button", names[i], 135, y + 15, 250, 65));
        }
        var notices = Scroll(left, "SystemNotices", 25, 390, 665, 110);
        var notice = Label(notices, "NoticeText", "", 0, 0, 635, 95, 18);
        notice.alignment = TextAlignmentOptions.TopLeft;
        notice.textWrappingMode = TextWrappingModes.Normal;
        var noticeSize = notice.gameObject.AddComponent<ContentSizeFitter>();
        noticeSize.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        Bind(fields, "noticeText", notice);
        Bind(fields, "readyButton", Button(right, "ReadyButton", "게임 준비", 22, 435, 365, 65));
        Bind(fields, "startButton", Button(right, "StartMatchButton", "게임 시작", 22, 435, 365, 65));
        var loading = Panel(canvas, "LobbyLoadingPanel", Background);
        Bind(fields, "loadingPanel", loading.gameObject);
        Label(loading, "LoadingTitle", "팀과 함께 전장으로 이동 중", 150, 265, 1000, 100, 36);
        Bind(fields, "leaveButton", Button(canvas, "LeaveButton", "나가기", 1110, 108, 95, 38));
        Bind(fields, "statusText", Label(canvas, "LobbyStatus", "", 70, 630, 1140, 55, 20));
        loading.gameObject.SetActive(false);
        fields.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.SaveScene(scene, LobbyPath);
        var validate = typeof(NetworkObject).GetMethod("OnValidate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (validate == null) throw new InvalidOperationException("NGO scene identity validation is unavailable.");
        validate.Invoke(networkObject, null);
        EditorSceneManager.SaveScene(scene, LobbyPath);
    }

    private static RectTransform Canvas(string name, int order)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = order;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.matchWidthOrHeight = 0.5f;
        return (RectTransform)go.transform;
    }

    private static RectTransform Panel(Transform parent, string name, Color color)
    {
        var rect = Box(parent, name, 0, 0, 1280, 720, color);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return rect;
    }

    private static RectTransform Box(Transform parent, string name, float x, float y, float width, float height, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        Place(rect, x, y, width, height);
        go.GetComponent<Image>().color = color;
        return rect;
    }

    private static TMP_Text Label(Transform parent, string name, string value, float x, float y, float width, float height, float size = 24)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        Place(rect, x, y, width, height);
        var text = go.GetComponent<TextMeshProUGUI>();
        text.font = font;
        text.text = value;
        text.fontSize = size;
        text.color = new Color(0.9f, 0.94f, 0.97f);
        text.richText = false;
        text.raycastTarget = false;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    private static Button Button(Transform parent, string name, string caption, float x, float y, float width, float height)
    {
        var rect = Box(parent, name, x, y, width, height, Accent);
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = rect.GetComponent<Image>();
        var label = Label(rect, "Label", caption, 12, 0, width - 24, height, 22);
        label.alignment = TextAlignmentOptions.Center;
        return button;
    }

    private static TMP_InputField Input(Transform parent, string name, string hint, float x, float y, float width, float height)
    {
        var rect = Box(parent, name, x, y, width, height, Surface);
        var input = rect.gameObject.AddComponent<TMP_InputField>();
        var viewport = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
        viewport.SetParent(rect, false);
        Place(viewport, 12, 4, width - 24, height - 8);
        var text = (TextMeshProUGUI)Label(viewport, "Text", "", 0, 0, width - 24, height - 8, 22);
        var placeholder = Label(viewport, "Placeholder", hint, 0, 0, width - 24, height - 8, 22);
        placeholder.color = new Color(0.5f, 0.61f, 0.69f);
        input.textViewport = viewport;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.targetGraphic = rect.GetComponent<Image>();
        input.lineType = TMP_InputField.LineType.SingleLine;
        return input;
    }

    private static RectTransform Scroll(Transform parent, string name, float x, float y, float width, float height)
    {
        var root = Box(parent, name, x, y, width, height, Surface);
        var scroll = root.gameObject.AddComponent<ScrollRect>();
        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
        viewport.SetParent(root, false);
        Place(viewport, 8, 8, width - 16, height - 16);
        var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)).GetComponent<RectTransform>();
        content.SetParent(viewport, false);
        Place(content, 0, 0, width - 16, height - 16);
        var layout = content.GetComponent<VerticalLayoutGroup>();
        layout.childControlHeight = false;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.spacing = 8;
        content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        return content;
    }

    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void Bind(SerializedObject target, string field, Object value)
    {
        var property = target.FindProperty(field);
        if (property == null) throw new InvalidOperationException(target.targetObject.GetType().Name + " has no serialized field " + field);
        property.objectReferenceValue = value;
    }
}
