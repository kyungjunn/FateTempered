using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

// 배치 모드에서만 빈 테스트 씬을 사용하며 프로젝트의 실제 씬은 저장하지 않음
[InitializeOnLoad]
public static class LevelOneEnemyValidation
{
    private const string SessionKey = "LevelOneEnemyValidation.Pending";
    private static IEnumerator routine;
    private static int checks;
    private static NavMeshDataInstance navMesh;
    private static double deadline;

    static LevelOneEnemyValidation()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Use Unity batch mode for this validation.");
        try
        {
            LevelOneEnemySetup.CreateMissing();
            ValidateAssets();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(SessionKey, true);
            EditorApplication.EnterPlaymode();
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static void ValidateAssets()
    {
        foreach (var definition in LevelOneEnemySetup.Definitions)
        {
            string name = definition.Name;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LevelOneEnemySetup.PrefabFolder + "/" + name + "Enemy.prefab");
            Require(prefab != null, name + ": prefab exists");
            foreach (Transform child in prefab.GetComponentsInChildren<Transform>(true))
                Require(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) == 0, name + ": scripts resolve");
            var animator = prefab.GetComponentInChildren<Animator>();
            Require(prefab.GetComponentsInChildren<Animator>().Length == 1 && !animator.applyRootMotion,
                name + ": single visual Animator, no root motion");
            Require(animator.avatar != null && animator.avatar.isValid && !animator.avatar.isHuman, name + ": Generic avatar");
            var controller = (AnimatorController)animator.runtimeAnimatorController;
            bool passive = name == "Rabbit";
            Require(controller.parameters.Length == (passive ? 3 : 4), name + ": animator parameters");
            var states = controller.layers[0].stateMachine.states;
            Require(states.Length == (passive ? 3 : definition.HasHit ? 4 : 3), name + ": state count");
            Require(states.Single(state => state.state.name == "Death").state.transitions.Length == 0, name + ": death is terminal");
            var attackClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(LevelOneEnemySetup.AnimationFolder + "/" + name + "/" + name + "_attack.anim");
            if (passive)
            {
                Require(!states.Any(state => state.state.name == "Attack") && prefab.GetComponent<EnemyAttack>() == null
                    && prefab.GetComponent<EnemyAI>() == null && prefab.GetComponent<PassiveEnemyRoamer>() != null,
                    name + ": roams without targeting or attacking");
            }
            else
            {
                Require(states.Single(state => state.state.name == "Attack").state.motion == attackClip,
                    name + ": attack state");
                Require(attackClip.events.Length == 1 && attackClip.events[0].functionName == "OnAttackHit",
                    name + ": one damage event");
                Require(animator.GetComponent<EnemyAnimationEventRelay>() != null, name + ": event relay");
                Require(!new SerializedObject(prefab.GetComponent<EnemyAttack>()).FindProperty("useFallbackHitDelay").boolValue,
                    name + ": event timing only");
                if (name == "Bat")
                {
                    var rangedAttack = prefab.GetComponent<BatRangedAttack>();
                    var wavePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LevelOneEnemySetup.PrefabFolder + "/BatSonicWave.prefab");
                    Require(rangedAttack != null && wavePrefab != null
                        && wavePrefab.GetComponent<BatSonicProjectile>() != null
                        && new SerializedObject(rangedAttack).FindProperty("projectilePrefab").objectReferenceValue == wavePrefab,
                        "Bat: animation event launches its sonic projectile prefab");
                    var waveMaterial = AssetDatabase.LoadAssetAtPath<Material>(LevelOneEnemySetup.MaterialFolder + "/BatSonicWave.mat");
                    Require(waveMaterial != null && waveMaterial.shader.name == "PainAndGain/BatSonicWave",
                        "Bat: sonic wave uses its URP material");
                }
            }
            var stats = AssetDatabase.LoadAssetAtPath<EnemyStats>(LevelOneEnemySetup.StatsFolder + "/" + name + "EnemyStats.asset");
            Require(prefab.GetComponents<EnemyHealth>().Length == 1, name + ": exactly one health component");
            var components = passive
                ? new Component[] { prefab.GetComponent<PassiveEnemyRoamer>(), prefab.GetComponent<EnemyHealth>() }
                : new Component[] { prefab.GetComponent<EnemyAI>(), prefab.GetComponent<EnemyHealth>(), prefab.GetComponent<EnemyAttack>() };
            foreach (var component in components)
                Require(new SerializedObject(component).FindProperty("stats").objectReferenceValue == stats, name + ": shared stats");
            Require(stats.MaxHealth == 20 && stats.AttackDamage == 3 && stats.ExpReward == 50, name + ": starter stats");
            if (name == "Bat")
                Require(stats.AttackRange == 8f && stats.AttackCooldown == 2.4f,
                    "Bat: attacks from range with a longer cooldown");
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>())
                foreach (Material material in renderer.sharedMaterials)
                    Require(material != null && material.shader.name == "Universal Render Pipeline/Lit"
                        && material.GetTexture("_BaseMap") != null && material.GetColor("_BaseColor").a > 0.9f
                        && material.GetColor("_BaseColor").maxColorComponent > 0.1f, name + ": URP material, tint and texture");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var visual = instance.transform.Find("Visual").gameObject;
                Require(Quaternion.Angle(visual.transform.localRotation, Quaternion.identity) < 0.1f,
                    name + ": visual faces the NavMeshAgent movement direction");
                var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(LevelOneEnemySetup.AnimationFolder + "/" + name + "/" + name + "_idle.anim");
                var model = visual.GetComponentInChildren<Animator>().gameObject;
                idle.SampleAnimation(model, 0f);
                var bounds = LevelOneEnemySetup.Measure(visual);
                Require(Mathf.Abs(bounds.size.y - definition.Height) < 0.02f, name + ": normalized model height " + bounds.size.y);
                Require(Mathf.Abs(bounds.min.y - definition.Hover) < 0.02f, name + ": floor clearance");
                var before = visual.GetComponentsInChildren<Transform>().Select(t => t.localToWorldMatrix).ToArray();
                attackClip.SampleAnimation(model, attackClip.length * 0.5f);
                var after = visual.GetComponentsInChildren<Transform>().Select(t => t.localToWorldMatrix).ToArray();
                Require(before.Where((matrix, index) => matrix != after[index]).Any(), name + ": animation drives the rig");
            }
            finally { Object.DestroyImmediate(instance); }
        }
        var quest = AssetDatabase.LoadAssetAtPath<QuestSO>("Assets/Prefabs/Quest/Data/HuntRabbit.asset");
        Require(quest != null && quest.objectiveType == QuestObjectiveType.HuntEventMonster
            && quest.targetAmount == 1 && quest.timeLimit == 45f
            && quest.eventMonsterPrefab == AssetDatabase.LoadAssetAtPath<GameObject>(LevelOneEnemySetup.PrefabFolder + "/RabbitEnemy.prefab"),
            "rabbit quest points to its unique event prefab");
        var beaconPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Quest/RabbitBeacon.prefab");
        Require(quest.eventMonsterBeaconPrefab == beaconPrefab && beaconPrefab.GetComponent<QuestTargetBeacon>() != null,
            "rabbit quest references the beacon prefab");
        var beaconMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Prefabs/Quest/RabbitBeacon.mat");
        Require(beaconMaterial != null && beaconMaterial.shader.name == "PainAndGain/QuestTargetBeacon"
            && beaconMaterial.GetColor("_Tint").a < 1f, "beacon uses a transparent URP material");
        var managers = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Manager/Managers.prefab");
        var spawnEntries = new SerializedObject(managers.GetComponent<SpawnManager>()).FindProperty("enemySpawnEntries");
        foreach (var definition in LevelOneEnemySetup.Definitions.Where(value => value.Name != "Rabbit"))
        {
            var enemyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LevelOneEnemySetup.PrefabFolder
                + "/" + definition.Name + "Enemy.prefab");
            int expectedLevel = definition.Name == "Slime" ? 1 : definition.Name == "Bat" ? 2 : 3;
            bool registered = false;
            for (int i = 0; i < spawnEntries.arraySize; i++)
            {
                var entry = spawnEntries.GetArrayElementAtIndex(i);
                if (entry.FindPropertyRelative("prefab").objectReferenceValue != enemyPrefab) continue;
                registered = entry.FindPropertyRelative("minPlayerLevel").intValue == expectedLevel
                    && entry.FindPropertyRelative("spawnWeight").intValue > 0;
            }
            Require(registered, definition.Name + ": registered as a level-unlocked regular spawn");
        }
        var rabbitPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LevelOneEnemySetup.PrefabFolder + "/RabbitEnemy.prefab");
        Require(Enumerable.Range(0, spawnEntries.arraySize).All(i => spawnEntries.GetArrayElementAtIndex(i)
            .FindPropertyRelative("prefab").objectReferenceValue != rabbitPrefab),
            "Rabbit: remains quest-only and does not auto-spawn");
        Debug.Log("LEVEL_ONE_ASSETS_PASS checks=" + checks);
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(SessionKey, false)) return;
        SessionState.SetBool(SessionKey, false);
        deadline = EditorApplication.timeSinceStartup + 90;
        routine = ValidateRuntime();
        EditorApplication.update += Tick;
    }

    private static void Tick()
    {
        try
        {
            if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Runtime tests exceeded 90 seconds");
            if (routine.MoveNext()) return;
            EditorApplication.update -= Tick;
            if (navMesh.valid) navMesh.Remove();
            Debug.Log("LEVEL_ONE_RUNTIME_PASS checks=" + checks);
            EditorApplication.Exit(0);
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static IEnumerator ValidateRuntime()
    {
        var sources = new List<NavMeshBuildSource>
        {
            new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Box, size = new Vector3(60, 0.2f, 60),
                transform = Matrix4x4.TRS(Vector3.down * 0.1f, Quaternion.identity, Vector3.one), area = 0 }
        };
        var data = NavMeshBuilder.BuildNavMeshData(NavMesh.GetSettingsByIndex(0), sources,
            new Bounds(Vector3.zero, new Vector3(64, 10, 64)), Vector3.zero, Quaternion.identity);
        navMesh = NavMesh.AddNavMeshData(data);
        Require(navMesh.valid, "test NavMesh created");
        var player = new GameObject("ValidationPlayer");
        player.tag = "Player";
        var playerHealth = player.AddComponent<PlayerHealth>();
        foreach (var definition in LevelOneEnemySetup.Definitions)
        {
            string name = definition.Name;
            player.transform.position = Vector3.forward * 6;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LevelOneEnemySetup.PrefabFolder + "/" + name + "Enemy.prefab");
            var enemy = Object.Instantiate(prefab);
            var ai = enemy.GetComponent<EnemyAI>();
            var agent = enemy.GetComponent<NavMeshAgent>();
            var health = enemy.GetComponent<EnemyHealth>();
            var attack = enemy.GetComponent<EnemyAttack>();
            var animator = enemy.GetComponentInChildren<Animator>();
            if (name == "Rabbit")
            {
                int playerBefore = playerHealth.CurrentHealth;
                float startZ = enemy.transform.position.z;
                float roamUntil = Time.time + 1f;
                while (Time.time < roamUntil) yield return null;
                Require(agent.isOnNavMesh && (enemy.transform.position - Vector3.forward * startZ).sqrMagnitude > 0.01f,
                    "Rabbit: moves along NavMesh near player");
                player.transform.position = Vector3.forward * 100;
                Vector3 lastPosition = enemy.transform.position;
                roamUntil = Time.time + 1f;
                while (Time.time < roamUntil) yield return null;
                Require((enemy.transform.position - lastPosition).sqrMagnitude > 0.01f,
                    "Rabbit: keeps roaming when player leaves");
                Require(playerHealth.CurrentHealth == playerBefore && attack == null,
                    "Rabbit: never attacks player");
                health.TakeDamage(100);
                Require(health.IsDead, "Rabbit: player can kill it");
                Object.Destroy(enemy);
                yield return null;
                continue;
            }
            ai.SetTarget(player.transform);
            float until = Time.time + 0.6f;
            while (Time.time < until) yield return null;
            Require(agent.isOnNavMesh && enemy.transform.position.z > 0.1f && ai.CurrentState == EnemyAI.EnemyState.Chase,
                name + ": NavMesh chase");
            player.transform.position = Vector3.forward * 100;
            until = Time.time + 0.3f;
            while (Time.time < until) yield return null;
            Require(ai.CurrentState == EnemyAI.EnemyState.Patrol, name + ": patrol after target leaves range");
            ai.enabled = false;
            agent.isStopped = true;
            agent.ResetPath();
            animator.Rebind();
            animator.Update(0f);
            player.transform.position = enemy.transform.position + Vector3.forward;
            CapsuleCollider playerCollider = null;
            if (name == "Bat")
            {
                playerCollider = player.AddComponent<CapsuleCollider>();
                playerCollider.center = Vector3.up;
                playerCollider.height = 2f;
                playerCollider.radius = 0.3f;
            }
            int previous = playerHealth.CurrentHealth;
            Require(attack.TryAttack(player.transform), name + ": attack starts");
            Require(!attack.TryAttack(player.transform), name + ": cooldown blocks duplicate attack");
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(LevelOneEnemySetup.AnimationFolder + "/" + name + "/" + name + "_attack.anim");
            until = Time.time + clip.length * 0.2f;
            while (Time.time < until) yield return null;
            Require(playerHealth.CurrentHealth == previous, name + ": no immediate damage");
            until = Time.time + clip.length + 0.15f;
            while (Time.time < until) yield return null;
            Require(playerHealth.CurrentHealth == previous - 3, name + ": animation event deals exactly one hit");
            animator.GetComponent<EnemyAnimationEventRelay>().OnAttackHit();
            Require(playerHealth.CurrentHealth == previous - 3, name + ": duplicate event ignored");
            until = Time.time + (name == "Bat" ? 2.5f : 1.3f);
            while (Time.time < until) yield return null;
            Require(attack.TryAttack(player.transform), name + ": next attack starts");
            player.transform.position += Vector3.forward * (name == "Bat" ? 10f : 6f);
            until = Time.time + clip.length + 0.15f;
            while (Time.time < until) yield return null;
            Require(playerHealth.CurrentHealth == previous - 3, name + ": dodged attack misses");
            if (name == "Bat")
            {
                player.transform.position = enemy.transform.position + Vector3.forward * 5f;
                var wavePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LevelOneEnemySetup.PrefabFolder + "/BatSonicWave.prefab");
                Vector3 waveStart = enemy.transform.position + Vector3.up * 0.9f;
                var wave = Object.Instantiate(wavePrefab, waveStart, Quaternion.identity).GetComponent<BatSonicProjectile>();
                wave.Launch(enemy.transform, (player.transform.position + Vector3.up - waveStart).normalized, 3);
                Require(playerHealth.CurrentHealth == previous - 3, "Bat: distant sonic wave does not hit immediately");
                until = Time.time + 0.8f;
                while (Time.time < until) yield return null;
                Require(playerHealth.CurrentHealth == previous - 6, "Bat: sonic wave hits a distant player once");

                wave = Object.Instantiate(wavePrefab, waveStart, Quaternion.identity).GetComponent<BatSonicProjectile>();
                wave.Launch(enemy.transform, (player.transform.position + Vector3.up - waveStart).normalized, 3);
                player.transform.position += Vector3.right * 4f;
                until = Time.time + 0.8f;
                while (Time.time < until) yield return null;
                Require(playerHealth.CurrentHealth == previous - 6, "Bat: player can dodge a wave after launch");

                player.transform.position = enemy.transform.position + Vector3.forward * 5f;
                var wall = new GameObject("SonicWaveTestWall");
                wall.transform.position = enemy.transform.position + Vector3.forward * 2f + Vector3.up;
                var wallCollider = wall.AddComponent<BoxCollider>();
                wallCollider.size = new Vector3(2f, 2f, 0.2f);
                wave = Object.Instantiate(wavePrefab, waveStart, Quaternion.identity).GetComponent<BatSonicProjectile>();
                wave.Launch(enemy.transform, (player.transform.position + Vector3.up - waveStart).normalized, 3);
                until = Time.time + 0.8f;
                while (Time.time < until) yield return null;
                Require(wave == null && playerHealth.CurrentHealth == previous - 6,
                    "Bat: wall blocks the sonic wave");
                Object.Destroy(wall);
                Object.Destroy(playerCollider);
            }
            health.TakeDamage(5);
            until = Time.time + 0.15f;
            while (Time.time < until) yield return null;
            Require(health.CurrentHealth == 15, name + ": receives player damage");
            if (definition.HasHit) Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Hit"), name + ": hit animation plays");
            int deaths = 0;
            Action<EnemyHealth> onDeath = victim => { if (victim == health) deaths++; };
            EnemyHealth.OnEnemyKilled += onDeath;
            health.TakeDamage(100);
            health.TakeDamage(100);
            EnemyHealth.OnEnemyKilled -= onDeath;
            Require(health.IsDead && deaths == 1 && !agent.enabled && !enemy.GetComponent<Collider>().enabled,
                name + ": one death, movement and collider disabled");
            until = Time.time + 0.2f;
            while (Time.time < until) yield return null;
            Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Death"), name + ": death animation plays");
            Object.Destroy(enemy);
            yield return null;
        }
        var questSpawn = new GameObject("RabbitQuestSpawn");
        questSpawn.tag = "EnemySpawnPoint";
        questSpawn.transform.position = Vector3.right * 8;
        var spawnManager = new GameObject("SpawnManager").AddComponent<SpawnManager>();
        var questManager = new GameObject("QuestManager").AddComponent<QuestManager>();
        var rabbitQuest = Object.Instantiate(AssetDatabase.LoadAssetAtPath<QuestSO>("Assets/Prefabs/Quest/Data/HuntRabbit.asset"));
        var rewardPool = ScriptableObject.CreateInstance<QuestRewardPoolSO>();
        var healReward = ScriptableObject.CreateInstance<HealRewardSO>();
        healReward.amount = 1;
        rewardPool.entries.Add(new QuestRewardEntry
        {
            reward = healReward,
            weight = 1
        });
        rabbitQuest.rewardPool = rewardPool;
        typeof(QuestManager).GetMethod("HandlePlayerSpawned",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(questManager, new object[] { player });
        SetPrivate(questManager, "quests", new List<QuestSO> { rabbitQuest });
        int succeeded = 0, failed = 0;
        questManager.onQuestSucceeded += (_, __) => succeeded++;
        questManager.onQuestFailed += (_, __) => failed++;
        InvokePrivate(questManager, "StartRandomQuest");
        Require(questManager.State == QuestState.Active && questManager.CurrentQuest == rabbitQuest,
            "Rabbit quest starts with countdown and event spawn");
        var questRabbit = Object.FindObjectsByType<PassiveEnemyRoamer>(FindObjectsSortMode.None).Single();
        var beacon = questRabbit.GetComponentInChildren<QuestTargetBeacon>();
        var beaconMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Prefabs/Quest/RabbitBeacon.mat");
        Require(beacon != null && beacon.transform.parent == questRabbit.transform,
            "active rabbit alone has the moving beacon");
        var beaconVisual = beacon.GetComponentInChildren<MeshRenderer>();
        Require(beaconVisual != null && beaconVisual.sharedMaterial == beaconMaterial
            && beaconVisual.GetComponent<MeshFilter>().sharedMesh.bounds.size.y > 60f,
            "beacon has a tall transparent visual without a collider");
        Require(beacon.GetComponentInChildren<Collider>() == null, "beacon cannot block the player");
        Vector3 beaconOffset = beacon.transform.position - questRabbit.transform.position;
        float beaconMoveUntil = Time.time + 0.3f;
        while (Time.time < beaconMoveUntil) yield return null;
        Require((beacon.transform.position - questRabbit.transform.position - beaconOffset).sqrMagnitude < 0.001f,
            "beacon follows the roaming rabbit");
        var dummy = new GameObject("OrdinaryKill").AddComponent<EnemyHealth>();
        dummy.TakeDamage(100);
        Require(questManager.CurrentProgress == 0 && questManager.State == QuestState.Active,
            "Ordinary kills do not complete rabbit quest");
        questRabbit.GetComponent<EnemyHealth>().TakeDamage(100);
        Require(succeeded == 1 && questManager.State == QuestState.Countdown,
            "Rabbit kill completes the event quest once");
        Object.Destroy(dummy.gameObject);
        Object.Destroy(questRabbit.gameObject);
        yield return null;
        Require(beacon == null, "beacon disappears after rabbit quest succeeds");
        InvokePrivate(questManager, "StartRandomQuest");
        var timedRabbit = Object.FindObjectsByType<PassiveEnemyRoamer>(FindObjectsSortMode.None).Single();
        var timedBeacon = timedRabbit.GetComponentInChildren<QuestTargetBeacon>();
        Require(timedBeacon != null, "next rabbit quest creates a new beacon");
        typeof(QuestManager).GetProperty("RemainingTime").SetValue(questManager, 0.01f);
        float timeout = Time.time + 0.2f;
        while (Time.time < timeout) yield return null;
        Require(failed == 1 && questManager.State == QuestState.Countdown,
            "Rabbit quest fails when its time expires");
        yield return null;
        Require(timedRabbit == null, "Expired event rabbit is removed");
        Require(timedBeacon == null, "beacon is removed on timeout");
        var regularQuest = ScriptableObject.CreateInstance<QuestSO>();
        regularQuest.objectiveType = QuestObjectiveType.KillEnemies;
        regularQuest.targetAmount = 2;
        regularQuest.rewardPool = rewardPool;
        SetPrivate(questManager, "quests", new List<QuestSO> { regularQuest });
        InvokePrivate(questManager, "StartRandomQuest");
        var ordinaryOne = new GameObject("OrdinaryOne").AddComponent<EnemyHealth>();
        ordinaryOne.TakeDamage(100);
        Require(questManager.CurrentProgress == 1 && questManager.State == QuestState.Active,
            "Existing kill quest still counts ordinary enemies");
        var ordinaryTwo = new GameObject("OrdinaryTwo").AddComponent<EnemyHealth>();
        ordinaryTwo.TakeDamage(100);
        Require(succeeded == 2 && questManager.State == QuestState.Countdown,
            "Existing kill quest still completes");
        Object.Destroy(regularQuest);
        Object.Destroy(rabbitQuest);
        Object.Destroy(rewardPool);
        Object.Destroy(healReward);
        Object.Destroy(player);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("LEVEL_ONE_FAIL: " + message);
        checks++;
        Debug.Log("PASS: " + message);
    }

    private static void SetPrivate(object target, string name, object value)
    {
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(target, value);
    }

    private static void InvokePrivate(object target, string name)
    {
        target.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(target, null);
    }

    private static void Fail(Exception exception)
    {
        EditorApplication.update -= Tick;
        Debug.LogException(exception);
        EditorApplication.Exit(1);
    }
}
