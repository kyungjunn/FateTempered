using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 실제 증강 획득·아이콘·이름·중첩 회귀 검증
[InitializeOnLoad]
public static class AugmentInventoryValidation
{
    private const string Pending = "AugmentInventoryValidation.Pending";
    private static IEnumerator routine;
    private static double deadline;

    static AugmentInventoryValidation()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Batch mode required.");
        try
        {
            // 저장된 에셋 검증 · Inspector 설정 변경 없음
            var main = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/MainCanvas.prefab");
            Require(main.GetComponentsInChildren<AugmentInventoryUI>(true).Length == 1, "MainCanvas nested inventory binding");
            foreach (string guid in AssetDatabase.FindAssets("t:AugmentSO", new[] { "Assets/Resources/Augments/Data" }))
            {
                var augment = AssetDatabase.LoadAssetAtPath<AugmentSO>(AssetDatabase.GUIDToAssetPath(guid));
                Require(augment.icon != null && AssetDatabase.GetAssetPath(augment.icon).StartsWith("Assets/Images/AugmentIcons/"),
                    "authored icon linked: " + augment.name);
            }
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Pending, true);
            EditorApplication.EnterPlaymode();
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
        SessionState.SetBool(Pending, false);
        routine = Validate();
        deadline = EditorApplication.timeSinceStartup + 90;
        EditorApplication.update += Tick;
    }

    private static void Tick()
    {
        try
        {
            if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Inventory validation timeout.");
            if (routine.MoveNext()) return;
            EditorApplication.update -= Tick;
            Debug.Log("AUGMENT_INVENTORY_VALIDATION_PASS");
            EditorApplication.Exit(0);
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static void Fail(Exception exception)
    {
        EditorApplication.update -= Tick;
        Debug.LogException(exception);
        EditorApplication.Exit(1);
    }

    private static IEnumerator Validate()
    {
        var canvasObject = new GameObject("InventoryTestCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 720);
        var panel = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/CharacterInfoPanel.prefab"), canvasObject.transform);
        panel.SetActive(true);
        var inventory = panel.GetComponentInChildren<AugmentInventoryUI>(true);
        var player = new GameObject("InventoryTestPlayer");
        player.tag = "Player";
        var augments = player.AddComponent<PlayerAugments>();
        inventory.Bind(player);
        yield return null;
        Require(VisibleSlots(panel).Length == 9 && FilledSlots(panel).Length == 0, "initial empty nine slots");
        Require(panel.GetComponentsInChildren<MonoBehaviour>(true).All(component => component != null), "no missing scripts");
        Require(panel.GetComponentsInChildren<MonoBehaviour>(true).All(component => !(component is IPointerEnterHandler) && !(component is IPointerClickHandler)),
            "no hover or click behavior");
        Require(panel.GetComponentsInChildren<Transform>(true).All(child => child.name != "AugmentTooltip"), "tooltip removed from prefab");

        // 화염 궤도 실제 프리팹 획득 및 무제한 중첩
        var fire = Resources.Load<SkillAugmentSO>("Augments/Data/FireAura_Data");
        var firePrefab = Resources.Load<GameObject>(fire.SkillResourcePath);
        Require(augments.TryAcquireLoadedSkill(fire, firePrefab) == SkillAcquireStatus.Instantiated, "fire orbit actual acquisition");
        var slot = FilledSlots(panel).Single();
        VerifySlot(slot, fire, "Lv.1");
        Require(augments.TryAcquireLoadedSkill(fire, firePrefab) == SkillAcquireStatus.Stacked, "fire orbit second acquisition");
        Require(FilledSlots(panel).Length == 1, "stack acquisition without duplicate icon");
        VerifySlot(slot, fire, "Lv.2");
        augments.TryRemoveRandomSkill(out _);
        VerifySlot(slot, fire, "Lv.1");
        augments.TryRemoveRandomSkill(out _);
        Require(FilledSlots(panel).Length == 0, "last stack removes slot");

        // 중첩 불가 증강 이름만 표시
        var giant = Resources.Load<SkillAugmentSO>("Augments/Data/Giant_Data");
        augments.TryAcquireLoadedSkill(giant, Resources.Load<GameObject>(giant.SkillResourcePath));
        VerifySlot(FilledSlots(panel).Single(), giant, null);
        Require(augments.TryAcquireLoadedSkill(giant, Resources.Load<GameObject>(giant.SkillResourcePath)) == SkillAcquireStatus.AlreadyMax,
            "nonstackable duplicate rejected");
        augments.ReleaseAll();

        // 유한 중첩 증강 레벨 증가
        var life = Resources.Load<SkillAugmentSO>("Augments/Data/BasicAttackLifeSteal_Data");
        var effect = Resources.Load<GameObject>(life.SkillResourcePath);
        augments.TryAcquireLoadedSkill(life, effect);
        VerifySlot(FilledSlots(panel).Single(), life, "Lv.1");
        augments.TryAcquireLoadedSkill(life, effect);
        VerifySlot(FilledSlots(panel).Single(), life, "Lv.2");
        panel.SetActive(false);
        augments.TryAcquireLoadedSkill(life, effect);
        var definitions = new List<SkillAugmentSO>();
        for (int i = 0; i < 14; i++)
        {
            var definition = Object.Instantiate(life);
            definition.augmentName = "추가 증강 " + i;
            definitions.Add(definition);
            augments.TryAcquireLoadedSkill(definition, effect);
        }
        panel.SetActive(true);
        yield return null;
        Require(FilledSlots(panel).Length == 15 && VisibleSlots(panel).Length == 15, "reopen includes acquisitions beyond nine slots");
        VerifySlot(FilledSlots(panel).Single(item => item.Augment == life), life, "Lv.3");
        Require(panel.GetComponentInChildren<ScrollRect>(true).vertical, "overflow inventory scroll enabled");
        augments.ReleaseAll();
        Require(FilledSlots(panel).Length == 0 && VisibleSlots(panel).Length == 9, "release clears icons");
        Require(VisibleSlots(panel).All(item => item.GetComponentsInChildren<TextMeshProUGUI>(true).All(text => text.text.Length == 0)),
            "empty slots clear all labels");
        augments.TryAcquireLoadedSkill(fire, firePrefab);
        var replacement = new GameObject("ReplacementPlayer");
        replacement.AddComponent<PlayerAugments>();
        inventory.Bind(replacement);
        Require(FilledSlots(panel).Length == 0, "replacement player clears old icons");
        augments.TryAcquireLoadedSkill(fire, firePrefab);
        Require(FilledSlots(panel).Length == 0, "old player event unsubscribed");
        inventory.Bind(null);
        Require(FilledSlots(panel).Length == 0, "null player clears inventory");
        Object.Destroy(panel);
        Object.Destroy(canvasObject);
        Object.Destroy(player);
        Object.Destroy(replacement);
        yield return null;
        foreach (var definition in definitions) Object.Destroy(definition);
    }

    private static void VerifySlot(AugmentInventorySlotUI slot, SkillAugmentSO augment, string level)
    {
        Require(slot.Augment == augment, "slot ownership: " + augment.name);
        var icon = slot.transform.Find("Icon").GetComponent<Image>();
        Require(icon.enabled && icon.sprite == augment.icon && icon.sprite != null, "visible icon: " + augment.name);
        var name = slot.transform.Find("Name").GetComponent<TextMeshProUGUI>();
        Require(name.text == augment.augmentName, "ability name: " + augment.name);
        var label = slot.transform.Find("Icon/Level").GetComponent<TextMeshProUGUI>();
        Require(level == null ? !label.gameObject.activeSelf && label.text == string.Empty
            : label.gameObject.activeSelf && label.text == level, "stack-only level: " + augment.name + " " + level);
    }

    private static AugmentInventorySlotUI[] VisibleSlots(GameObject panel)
        => panel.GetComponentsInChildren<AugmentInventorySlotUI>(true).Where(slot => slot.gameObject.activeSelf).ToArray();

    private static AugmentInventorySlotUI[] FilledSlots(GameObject panel)
        => VisibleSlots(panel).Where(slot => slot.Augment != null).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("AUGMENT_INVENTORY_FAIL: " + message);
        Debug.Log("AUGMENT_INVENTORY_PASS: " + message);
    }
}
