using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

// 원본 팩을 수정하지 않고 게임용 프리팹, 클립, 능력치와 URP 재질을 생성
public static class LevelOneEnemySetup
{
    public const string AnimationFolder = "Assets/Animations/Enemy/LevelOne";
    public const string PrefabFolder = "Assets/Prefabs/Enemy/LevelOne";
    public const string StatsFolder = "Assets/ScriptableObjects/Enemy/LevelOne";
    public const string MaterialFolder = "Assets/Prefabs/Enemy/LevelOne/Materials";
    private const string Pack = "Assets/Level 1 Monster Pack";

    public sealed class Definition
    {
        public string Name, Model, Color;
        public float Height, Hover, Speed;
        public bool HasHit;

        public Definition(string name, string model, string color, float height, float hover, float speed, bool hasHit)
        {
            Name = name; Model = model; Color = color;
            Height = height; Hover = hover; Speed = speed; HasHit = hasHit;
        }
    }

    public static readonly Definition[] Definitions =
    {
        new Definition("Slime", "Slime_Level_1", "Blue", 0.7f, 0f, 2.5f, false),
        new Definition("Rabbit", "Rabbit_Level_1", "Cyan", 1.4f, 0f, 3.5f, true),
        new Definition("Bat", "Bat_Level_1", "Violet", 0.6f, 0.6f, 3.5f, true),
        new Definition("Ghost", "Ghost_Lv1", "White", 1f, 0.3f, 3f, true)
    };

    [MenuItem("Tools/Enemies/Create Missing Level One Enemies")]
    public static void CreateMissing()
    {
        foreach (string folder in new[] { AnimationFolder, PrefabFolder, StatsFolder, MaterialFolder })
            EnsureFolder(folder);

        foreach (Definition definition in Definitions)
        {
            var clips = CreateClips(definition);
            AnimatorController controller = CreateController(definition, clips);
            EnemyStats stats = CreateStats(definition, clips["attack"].length);
            CreatePrefab(definition, controller, stats, clips);
        }
        CreateRabbitQuest();
        AssetDatabase.SaveAssets();
        Debug.Log("LEVEL_ONE_SETUP_PASS: four enemies created; existing assets and scenes preserved.");
    }

    public static void BuildBatch()
    {
        try { CreateMissing(); EditorApplication.Exit(0); }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }

    private static Dictionary<string, AnimationClip> CreateClips(Definition definition)
    {
        string folder = AnimationFolder + "/" + definition.Name;
        EnsureFolder(folder);
        AnimationClip[] sources = AssetDatabase.LoadAllAssetsAtPath(Pack + "/Models/" + definition.Model + ".fbx")
            .OfType<AnimationClip>().ToArray();
        var result = new Dictionary<string, AnimationClip>();
        foreach (string action in new[] { "idle", "move", "attack", "damage", "die" })
        {
            if (action == "damage" && !definition.HasHit) continue;
            string path = folder + "/" + definition.Name + "_" + action + ".anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                string sourceName = definition.Name.ToLowerInvariant() + "_" + action;
                var source = sources.Single(value => value.name == sourceName);
                clip = Object.Instantiate(source);
                clip.name = definition.Name + "_" + action;
                clip.legacy = false;
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = action == "idle" || action == "move";
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                // 타격 시점의 초깃값. 원본 FBX가 아닌 복제 클립에서 모션에 맞게 조절 가능
                AnimationUtility.SetAnimationEvents(clip, action == "attack"
                    ? new[] { new AnimationEvent { functionName = "OnAttackHit", time = clip.length * 0.5f } }
                    : Array.Empty<AnimationEvent>());
                AssetDatabase.CreateAsset(clip, path);
            }
            result.Add(action, clip);
        }
        return result;
    }

    private static AnimatorController CreateController(Definition definition, Dictionary<string, AnimationClip> clips)
    {
        string path = AnimationFolder + "/" + definition.Name + "Enemy.controller";
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        if (controller != null) return controller;
        controller = AnimatorController.CreateAnimatorControllerAtPath(path);
        bool passive = definition.Name == "Rabbit";
        controller.AddParameter("MoveSpeed", AnimatorControllerParameterType.Float);
        foreach (string trigger in passive ? new[] { "Hit", "Death" } : new[] { "Attack", "Hit", "Death" })
            controller.AddParameter(trigger, AnimatorControllerParameterType.Trigger);
        var machine = controller.layers[0].stateMachine;
        var locomotion = machine.AddState("Locomotion", new Vector3(280, 80));
        var movement = new BlendTree { name = "Movement", blendParameter = "MoveSpeed", useAutomaticThresholds = false };
        AssetDatabase.AddObjectToAsset(movement, controller);
        movement.AddChild(clips["idle"], 0f);
        movement.AddChild(clips["move"], 1f);
        locomotion.motion = movement;
        machine.defaultState = locomotion;
        var death = machine.AddState("Death", new Vector3(520, 280));
        death.motion = clips["die"];
        // 사망 상태에서는 복귀하지 않으며 토끼는 공격 상태를 만들지 않는다
        var living = new List<AnimatorState> { locomotion };
        AnimatorState attack = null;
        if (!passive)
        {
            attack = machine.AddState("Attack", new Vector3(520, 80));
            attack.motion = clips["attack"];
            living.Add(attack);
        }
        if (definition.HasHit)
        {
            var hit = machine.AddState("Hit", new Vector3(280, 280));
            hit.motion = clips["damage"];
            living.Add(hit);
            AddTriggeredTransition(locomotion, hit, "Hit");
            if (attack != null) AddTriggeredTransition(attack, hit, "Hit");
            AddReturnTransition(hit, locomotion);
        }
        if (attack != null)
        {
            AddTriggeredTransition(locomotion, attack, "Attack");
            AddReturnTransition(attack, locomotion);
        }
        foreach (AnimatorState state in living)
        {
            var transition = AddTriggeredTransition(state, death, "Death");
            // 사망은 진행 중인 블렌딩보다 우선하도록 전이 목록의 첫 위치에 배치
            state.transitions = new[] { transition }.Concat(state.transitions.Where(value => value != transition)).ToArray();
        }
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static AnimatorStateTransition AddTriggeredTransition(AnimatorState from, AnimatorState to, string trigger)
    {
        var transition = from.AddTransition(to);
        transition.hasExitTime = false;
        transition.hasFixedDuration = true;
        transition.duration = 0.05f;
        transition.interruptionSource = TransitionInterruptionSource.SourceThenDestination;
        transition.AddCondition(AnimatorConditionMode.If, 0f, trigger);
        return transition;
    }

    private static void AddReturnTransition(AnimatorState from, AnimatorState to)
    {
        var transition = from.AddTransition(to);
        transition.hasExitTime = true;
        transition.exitTime = 1f;
        transition.hasFixedDuration = true;
        transition.duration = 0.05f;
        transition.interruptionSource = TransitionInterruptionSource.SourceThenDestination;
    }

    private static EnemyStats CreateStats(Definition definition, float attackLength)
    {
        string path = StatsFolder + "/" + definition.Name + "EnemyStats.asset";
        var stats = AssetDatabase.LoadAssetAtPath<EnemyStats>(path);
        if (stats != null) return stats;
        stats = ScriptableObject.CreateInstance<EnemyStats>();
        Set(stats, "enemyName", definition.Name);
        Set(stats, "maxHealth", 20);
        Set(stats, "expReward", 50);
        Set(stats, "attackDamage", 3);
        Set(stats, "moveSpeed", definition.Speed);
        Set(stats, "stoppingDistance", 1.1f);
        Set(stats, "attackRange", definition.Name == "Bat" ? 8f : 1.7f);
        Set(stats, "attackCooldown", definition.Name == "Bat" ? 2.4f : Mathf.Max(1.2f, attackLength + 0.2f));
        AssetDatabase.CreateAsset(stats, path);
        return stats;
    }

    private static void CreatePrefab(Definition definition, AnimatorController controller, EnemyStats stats,
        Dictionary<string, AnimationClip> clips)
    {
        string path = PrefabFolder + "/" + definition.Name + "Enemy.prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
        var root = new GameObject(definition.Name + "Enemy");
        try
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(Pack + "/Prefabs/" + definition.Name + "/"
                + definition.Name + "_" + definition.Color + ".prefab");
            if (source == null) throw new InvalidOperationException("Missing source prefab: " + definition.Name);
            // 클립이 모델 루트의 Transform을 기록해도 크기와 지면 높이는 유지
            var modelScale = new GameObject("Visual");
            modelScale.transform.SetParent(root.transform, false);
            modelScale.transform.localRotation = Quaternion.identity;
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(source, modelScale.transform);
            PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            visual.name = "Model";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            var animator = visual.GetComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            clips["idle"].SampleAnimation(visual, 0f);
            Bounds bounds = Measure(visual);
            if (bounds.size.y <= 0.0001f) throw new InvalidOperationException("Empty model bounds: " + definition.Name);
            modelScale.transform.localScale *= definition.Height / bounds.size.y;
            bounds = Measure(visual);
            modelScale.transform.localPosition += new Vector3(-bounds.center.x, definition.Hover - bounds.min.y, -bounds.center.z);

            foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>())
                renderer.sharedMaterials = renderer.sharedMaterials.Select(CreateMaterial).ToArray();

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = definition.Name == "Slime" ? 0.35f
                : definition.Name == "Rabbit" ? 0.35f : 0.3f;
            agent.height = Mathf.Max(1f, definition.Height + definition.Hover);
            agent.speed = stats.MoveSpeed;
            agent.acceleration = stats.Acceleration;
            agent.stoppingDistance = stats.StoppingDistance;
            var collider = root.AddComponent<CapsuleCollider>();
            collider.radius = agent.radius;
            collider.height = Mathf.Max(definition.Height, collider.radius * 2f);
            collider.center = Vector3.up * (definition.Hover + collider.height / 2f);
            var enemyAnimator = root.AddComponent<EnemyAnimator>();
            Set(enemyAnimator, "animator", animator);
            Set(enemyAnimator, "agent", agent);
            var health = root.AddComponent<EnemyHealth>();
            Set(health, "stats", stats);
            Set(health, "destroyDelay", Mathf.Max(2f, clips["die"].length + 0.2f));
            if (definition.Name == "Rabbit")
            {
                var roamer = root.AddComponent<PassiveEnemyRoamer>();
                Set(roamer, "stats", stats);
            }
            else
            {
                var attack = definition.Name == "Bat"
                    ? (EnemyAttack)root.AddComponent<BatRangedAttack>()
                    : root.AddComponent<EnemyAttack>();
                Set(attack, "stats", stats);
                Set(attack, "useFallbackHitDelay", false);
                if (attack is BatRangedAttack)
                {
                    var wave = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/BatSonicWave.prefab");
                    if (wave == null) throw new InvalidOperationException("Missing bat sonic projectile prefab");
                    Set(attack, "attackDamage", stats.AttackDamage);
                    Set(attack, "attackRange", stats.AttackRange);
                    Set(attack, "attackCooldown", stats.AttackCooldown);
                    Set(attack, "projectilePrefab", wave);
                }
                var relay = visual.AddComponent<EnemyAnimationEventRelay>();
                Set(relay, "enemyAttack", attack);
                Set(root.AddComponent<EnemyAI>(), "stats", stats);
            }
            Set(root.AddComponent<EnemyHealthBar>(), "worldOffset", Vector3.up * (definition.Height + definition.Hover + 0.3f));
            root.AddComponent<EnemyHitFeedback>();
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log("LEVEL_ONE_PREFAB: " + path + " height=" + definition.Height + " attackSeconds=" + clips["attack"].length);
        }
        finally { Object.DestroyImmediate(root); }
    }

    private static void CreateRabbitQuest()
    {
        const string folder = "Assets/Prefabs/Quest/Data";
        EnsureFolder(folder);
        string path = folder + "/HuntRabbit.asset";
        var existing = AssetDatabase.LoadAssetAtPath<QuestSO>(path);
        if (existing != null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/RabbitEnemy.prefab");
            if (existing.eventMonsterPrefab != prefab)
            {
                existing.eventMonsterPrefab = prefab;
                EditorUtility.SetDirty(existing);
            }
            return;
        }
        var quest = ScriptableObject.CreateInstance<QuestSO>();
        quest.questName = "도망치는 토끼";
        quest.description = "45초 안에 토끼를 찾아 처치하세요";
        quest.objectiveType = QuestObjectiveType.HuntEventMonster;
        quest.targetAmount = 1;
        quest.timeLimit = 45f;
        quest.rewardPool = AssetDatabase.LoadAssetAtPath<QuestRewardPoolSO>("Assets/Prefabs/Quest/Data/StandardRewards.asset");
        quest.eventMonsterPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/RabbitEnemy.prefab");
        quest.penaltyType = QuestPenaltyType.StatReduce;
        quest.statReduceMin = 0.1f;
        quest.statReduceMax = 0.2f;
        AssetDatabase.CreateAsset(quest, path);
    }

    private static Material CreateMaterial(Material source)
    {
        if (source == null) throw new InvalidOperationException("Missing source material");
        string path = MaterialFolder + "/" + source.name + "_URP.mat";
        var result = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (result != null) return result;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) throw new InvalidOperationException("URP Lit shader not found");
        result = new Material(shader) { name = source.name + "_URP" };
        // 구형 셰이더가 지원되지 않는 환경에서도 저장된 색상을 그대로 사용
        var colors = new SerializedObject(source).FindProperty("m_SavedProperties.m_Colors");
        Color tint = Color.white;
        for (int i = 0; i < colors.arraySize; i++)
        {
            var entry = colors.GetArrayElementAtIndex(i);
            if (entry.FindPropertyRelative("first").stringValue == "_Color")
                tint = entry.FindPropertyRelative("second").colorValue;
        }
        var texture = source.GetTexture("_MainTex");
        result.SetTexture("_BaseMap", texture);
        result.SetColor("_BaseColor", tint);
        // URP의 최초 머티리얼 업그레이드가 참조하는 호환 프로퍼티도 동기화
        result.SetTexture("_MainTex", texture);
        result.SetColor("_Color", tint);
        result.SetTextureScale("_BaseMap", source.GetTextureScale("_MainTex"));
        result.SetTextureOffset("_BaseMap", source.GetTextureOffset("_MainTex"));
        result.SetFloat("_Smoothness", 0.25f);
        AssetDatabase.CreateAsset(result, path);
        return result;
    }

    public static Bounds Measure(GameObject visual)
    {
        Bounds? result = null;
        foreach (var renderer in visual.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mesh = new Mesh();
            try
            {
                // 로컬 메시를 얻은 뒤 TransformPoint에서 계층 배율을 한 번만 적용
                renderer.BakeMesh(mesh, true);
                foreach (Vector3 vertex in mesh.vertices)
                {
                    Vector3 point = renderer.transform.TransformPoint(vertex);
                    if (result.HasValue) { var bounds = result.Value; bounds.Encapsulate(point); result = bounds; }
                    else result = new Bounds(point, Vector3.zero);
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }
        return result ?? throw new InvalidOperationException("No skinned mesh vertices");
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        string parent = path.Substring(0, slash);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
    }

    public static void Set(Object target, string name, object value)
    {
        var serialized = new SerializedObject(target);
        var property = serialized.FindProperty(name) ?? throw new InvalidOperationException("Missing property: " + name);
        if (value is Object reference) property.objectReferenceValue = reference;
        else if (value is int integer) property.intValue = integer;
        else if (value is float number) property.floatValue = number;
        else if (value is bool boolean) property.boolValue = boolean;
        else if (value is string text) property.stringValue = text;
        else if (value is Vector3 vector) property.vector3Value = vector;
        else throw new ArgumentException("Unsupported property type");
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
