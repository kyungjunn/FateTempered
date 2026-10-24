using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// 보상 추첨 및 지급 회귀 검증
public static class QuestRewardValidation
{
    [MenuItem("Tools/Quest/Validate Rewards")]
    public static void Run()
    {
        var player = new GameObject("QuestRewardTestPlayer");
        var managerObject = new GameObject("QuestRewardTestManager");
        player.SetActive(false);
        managerObject.SetActive(false);
        var pool = ScriptableObject.CreateInstance<QuestRewardPoolSO>();
        var quest = ScriptableObject.CreateInstance<QuestSO>();
        var experience = ScriptableObject.CreateInstance<ExperienceRewardSO>();
        var nextExperience = ScriptableObject.CreateInstance<ExperienceRewardSO>();
        var invalid = ScriptableObject.CreateInstance<ExperienceRewardSO>();
        var heal = ScriptableObject.CreateInstance<HealRewardSO>();
        var charge = ScriptableObject.CreateInstance<UltimateChargeRewardSO>();
        var augment = ScriptableObject.CreateInstance<AugmentChoiceRewardSO>();
        experience.amount = 25;
        nextExperience.amount = 50;
        invalid.amount = 0;
        heal.amount = 20;
        charge.amount = 25;
        try
        {
            // 가중치 경계 및 무효 후보 제외
            QuestRewardSO selected;
            Require(!pool.TrySelect(0, out selected), "empty pool rejected");
            pool.entries.Add(Entry(experience, 1));
            pool.entries.Add(Entry(heal, 3));
            Require(pool.TrySelect(0, out selected) && selected == experience, "lower boundary");
            Require(pool.TrySelect(0.249f, out selected) && selected == experience, "first weight interval");
            Require(pool.TrySelect(0.25f, out selected) && selected == heal, "weight boundary");
            Require(pool.TrySelect(1, out selected) && selected == heal, "inclusive upper boundary");
            Require(!pool.TrySelect(float.NaN, out selected) && !pool.TrySelect(-1, out selected)
                && !pool.TrySelect(1.01f, out selected), "invalid sample rejected");
            pool.entries.Clear();
            pool.entries.Add(Entry(null, 1));
            pool.entries.Add(Entry(invalid, 1));
            pool.entries.Add(Entry(heal, 0));
            Require(!pool.TrySelect(0, out selected), "invalid entries rejected");
            pool.entries.Add(Entry(experience, 1));
            Require(pool.TrySelect(1, out selected) && selected == experience, "single valid candidate");

            // 실제 시스템 지급 및 상한 검증
            Require(!experience.Apply(player).applied, "missing component rejected");
            var level = player.AddComponent<PlayerLevelSystem>();
            Require(experience.Apply(player).applied && level.currentExp == 25, "experience granted");
            Require(!invalid.Apply(player).applied && !experience.Apply(null).applied, "invalid reward and player rejected");
            var health = player.AddComponent<PlayerHealth>();
            health.Heal(health.MaxHealth);
            Require(heal.Apply(player).grantedAmount == 0, "full health gives no substitute");
            typeof(PlayerHealth).GetProperty("CurrentHealth").SetValue(health, health.MaxHealth - 5);
            var healing = heal.Apply(player);
            Require(healing.grantedAmount == 5 && health.CurrentHealth == health.MaxHealth
                && healing.Description == "체력 5 회복", "healing clamps and reports actual amount");
            var gauge = player.AddComponent<PlayerUltimateGauge>();
            gauge.Add(95);
            var charging = charge.Apply(player);
            Require(charging.grantedAmount == 5 && gauge.IsFull
                && charging.Description == "궁극기 게이지 5 충전", "gauge clamps and reports actual amount");
            Require(charge.Apply(player).grantedAmount == 0, "full gauge gives no substitute");
            Require(!augment.Apply(player).applied, "missing augment session rejected");
            pool.entries.Clear();
            pool.entries.Add(Entry(charge, 1));
            pool.entries.Add(Entry(experience, 1));
            Require(pool.TrySelect(0, candidate => candidate != charge, out selected)
                && selected == experience, "ineligible rewards are skipped");
            Require(!pool.TrySelect(0, _ => false, out selected), "all ineligible rewards rejected");

            // 시작 시 참조 확정 및 중복 지급 차단
            experience.amount = 10;
            pool.entries.Clear();
            pool.entries.Add(Entry(experience, 1));
            quest.rewardPool = pool;
            quest.targetAmount = 1;
            var manager = managerObject.AddComponent<QuestManager>();
            Invoke(manager, "HandlePlayerSpawned", player);
            typeof(QuestManager).GetField("quests", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(manager, new List<QuestSO> { quest });
            int successes = 0, failures = 0;
            manager.onQuestSucceeded += (_, result) => { Require(result.applied, "success result"); successes++; };
            manager.onQuestFailed += (_, __) => failures++;
            Invoke(manager, "StartRandomQuest");
            Require(manager.State == QuestState.Active && manager.CurrentReward == experience, "reward resolved at start");
            typeof(QuestManager).GetProperty("CurrentProgress").SetValue(manager, 1);
            float activeRemainingTime = manager.RemainingTime;
            Invoke(manager, "HandlePlayerSpawned", player);
            Require(manager.State == QuestState.Active && manager.CurrentQuest == quest
                && manager.CurrentReward == experience && manager.CurrentProgress == 1
                && manager.RemainingTime == activeRemainingTime, "same player rebind preserves active quest");
            pool.entries[0] = Entry(nextExperience, 1);
            Invoke(manager, "SucceedCurrentQuest");
            Invoke(manager, "SucceedCurrentQuest");
            Invoke(manager, "FailCurrentQuest");
            Require(level.currentExp == 35 && successes == 1 && failures == 0, "selected reference paid exactly once");
            FieldInfo countdownField = typeof(QuestManager).GetField("countdown", BindingFlags.Instance | BindingFlags.NonPublic);
            float countdown = (float)countdownField.GetValue(manager);
            Invoke(manager, "HandlePlayerSpawned", player);
            Require(manager.State == QuestState.Countdown && manager.CurrentQuest == null
                && (float)countdownField.GetValue(manager) == countdown,
                "same player rebind preserves quest countdown");
            Invoke(manager, "StartRandomQuest");
            Invoke(manager, "FailCurrentQuest");
            Invoke(manager, "FailCurrentQuest");
            Require(level.currentExp == 35 && failures == 1, "failure gives no reward and resolves once");
            Invoke(manager, "StartRandomQuest");
            typeof(PlayerHealth).GetProperty("IsDead").SetValue(health, true);
            Invoke(manager, "SucceedCurrentQuest");
            Invoke(manager, "FailCurrentQuest");
            Invoke(manager, "Update");
            Require(manager.State == QuestState.Stopped && manager.CurrentQuest == null
                && level.currentExp == 35 && successes == 1 && failures == 1, "death cancels without reward or penalty");
            Require(!experience.Apply(player).applied, "dead player cannot receive reward");
            typeof(PlayerHealth).GetProperty("IsDead").SetValue(health, false);
            Invoke(manager, "HandlePlayerSpawned", player);
            Require(manager.State == QuestState.WaitingForAugment && manager.CurrentReward == null, "spawn resets quest state");

            // 직렬화 에셋 연결 및 기존 가중치 유지
            var standard = AssetDatabase.LoadAssetAtPath<QuestRewardPoolSO>("Assets/Prefabs/Quest/Data/StandardRewards.asset");
            Require(standard != null && standard.entries.Count == 4, "standard pool loaded");
            Require(standard.entries[0].reward is ExperienceRewardSO && standard.entries[0].reward.amount == 25
                && standard.entries[0].weight == 4, "experience asset linked");
            Require(standard.entries[1].reward is HealRewardSO && standard.entries[1].reward.amount == 20
                && standard.entries[1].weight == 3, "heal asset linked");
            Require(standard.entries[2].reward is UltimateChargeRewardSO && standard.entries[2].reward.amount == 25
                && standard.entries[2].weight == 2, "charge asset linked");
            Require(standard.entries[3].reward is AugmentChoiceRewardSO && standard.entries[3].reward.amount == 1
                && standard.entries[3].weight == 1, "augment asset linked");
            Debug.Log("QUEST_REWARD_VALIDATION_PASS");
        }
        finally
        {
            Object.DestroyImmediate(managerObject);
            Object.DestroyImmediate(player);
            Object.DestroyImmediate(quest);
            Object.DestroyImmediate(pool);
            Object.DestroyImmediate(experience);
            Object.DestroyImmediate(nextExperience);
            Object.DestroyImmediate(invalid);
            Object.DestroyImmediate(heal);
            Object.DestroyImmediate(charge);
            Object.DestroyImmediate(augment);
        }
    }

    private static QuestRewardEntry Entry(QuestRewardSO reward, int weight)
        => new QuestRewardEntry { reward = reward, weight = weight };

    private static void Invoke(object target, string method, params object[] arguments)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("QUEST_REWARD_FAIL: " + message);
        Debug.Log("QUEST_REWARD_PASS: " + message);
    }
}
