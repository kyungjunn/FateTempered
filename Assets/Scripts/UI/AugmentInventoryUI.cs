using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 증강 인벤토리. 전투 씬은 스포너 권위.
public sealed class AugmentInventoryUI : MonoBehaviour
{
    [SerializeField] private RectTransform content;
    [SerializeField] private AugmentInventorySlotUI template;
    [SerializeField, Min(0)] private int defaultSlotCount = 9;
    private readonly List<AugmentInventorySlotUI> slots = new List<AugmentInventorySlotUI>();
    private PlayerAugments playerAugments;

    public void Configure(RectTransform content, AugmentInventorySlotUI template)
    {
        this.content = content;
        this.template = template;
    }

    private void OnEnable()
    {
        SpawnManager.OnPlayerSpawned += Bind;
        // 스포너 스냅샷. 미리보기는 명시적 Bind 유지.
        Bind(SpawnManager.Instance != null ? SpawnManager.Instance.SpawnedPlayer
            : playerAugments != null ? playerAugments.gameObject : null);
    }

    private void OnDisable()
    {
        SpawnManager.OnPlayerSpawned -= Bind;
        if (playerAugments != null) playerAugments.onSkillsChanged -= Refresh;
    }

    public void Bind(GameObject player)
    {
        // 이전 플레이어 구독 해제 · 현재 목록 재연결
        if (playerAugments != null) playerAugments.onSkillsChanged -= Refresh;
        playerAugments = player != null ? player.GetComponent<PlayerAugments>() : null;
        if (playerAugments != null) playerAugments.onSkillsChanged += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (content == null || template == null) return;
        if (slots.Count == 0) slots.AddRange(content.GetComponentsInChildren<AugmentInventorySlotUI>(true));
        int count = playerAugments != null ? playerAugments.OwnedSkills.Count : 0;
        // 기본 슬롯 유지 · 초과 슬롯 재사용
        int capacity = Mathf.Max(defaultSlotCount, count);
        while (slots.Count < capacity)
            slots.Add(Instantiate(template, content));
        int index = 0;
        if (playerAugments != null)
            foreach (var augment in playerAugments.OwnedSkills)
            {
                slots[index].gameObject.SetActive(true);
                slots[index++].SetAugment(augment, playerAugments.GetSkillStackCount(augment));
            }
        for (; index < slots.Count; index++)
        {
            slots[index].SetAugment(null);
            slots[index].gameObject.SetActive(index < capacity);
        }
        LayoutRebuilder.MarkLayoutForRebuild(content);
    }
}
