using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class WizardAugmentValidation
{
    private const string Pending = "WizardAugmentValidation.Pending";
    private static readonly string[] ResourceNames =
    {
        "WizardBasicPierce", "WizardResonanceRadius", "WizardGravityRadius",
        "WizardResidualGravity", "WizardExtraField", "WizardFieldCollapse", "WizardOvercharge"
    };
    private static readonly string[] EffectNames =
    {
        "WizardBasicPierce", "WizardResonanceRadius", "WizardGravityRadius",
        "WizardResidualGravity", "WizardExtraField", "WizardFieldCollapse", "WizardOvercharge"
    };
    private static readonly float[] Values = { 1f, 0.25f, 0.25f, 0.3f, 1f, 1.5f, 0.5f };
    private static readonly int[] MaxStacks = { 2, 2, 2, 1, 1, 2, 2 };
    private static readonly string[] TargetFields =
    {
        null, "skillE", "skillQ", "skillQ", "skillE", "skillE", "ultimate"
    };

    private static GameObject fixtureRoot;
    private static PlayerCombatAugmentSO[] augments;
    private static GameObject combatAugmentPrefab;
    private static bool finished;

    static WizardAugmentValidation()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Batch mode required.");
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Pending, true);
            EditorApplication.EnterPlaymode();
        }
        catch (Exception exception)
        {
            SessionState.SetBool(Pending, false);
            Fail(exception);
        }
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
        SessionState.SetBool(Pending, false);
        try
        {
            GameObject host = new GameObject("WizardAugmentValidationRunner");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<WizardAugmentValidationRunner>().StartCoroutine(ValidateRoutine());
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private static IEnumerator ValidateRoutine()
    {
        Stack<IEnumerator> routines = new Stack<IEnumerator>();
        routines.Push(Validate());
        while (routines.Count > 0)
        {
            object current;
            try
            {
                IEnumerator routine = routines.Peek();
                if (!routine.MoveNext())
                {
                    (routines.Pop() as IDisposable)?.Dispose();
                    continue;
                }
                current = routine.Current;
            }
            catch (Exception exception)
            {
                while (routines.Count > 0)
                    (routines.Pop() as IDisposable)?.Dispose();
                Fail(exception);
                yield break;
            }

            if (current is IEnumerator nested)
                routines.Push(nested);
            else
                yield return current;
        }

        if (finished) yield break;
        finished = true;
        Debug.Log("WIZARD_AUGMENT_VALIDATION_PASS");
        EditorApplication.Exit(0);
    }

    private static void Fail(Exception exception)
    {
        if (finished) return;
        finished = true;
        Debug.LogException(exception);
        if (EditorApplication.isPlaying)
            EditorApplication.ExitPlaymode();
        EditorApplication.Exit(1);
    }

    private static IEnumerator Validate()
    {
        Time.timeScale = 1f;
        List<string> unexpectedLogs = new List<string>();
        Application.LogCallback logHandler = (condition, stackTrace, type) =>
        {
            if ((type != LogType.Error && type != LogType.Exception && type != LogType.Assert) ||
                condition.Contains("No graphic device is available") ||
                stackTrace.Contains("UnityEditor.Search.SearchDatabase"))
            {
                return;
            }

            unexpectedLogs.Add(type + ": " + condition);
        };
        Application.logMessageReceived += logHandler;
        fixtureRoot = new GameObject("WizardAugmentValidationFixtures");

        try
        {
            yield return null;
            LoadAndValidateData();
            TestAcquisitionAndFiltering();
            ClearFixtures();
            yield return TestBasicPiercing();
            ClearFixtures();
            yield return TestResonanceAndFieldCapacity();
            ClearFixtures();
            yield return TestCollapseCancellation("death");
            ClearFixtures();
            yield return TestCollapseCancellation("disable");
            ClearFixtures();
            yield return TestCollapseCancellation("revocation");
            ClearFixtures();
            yield return TestGravityRadiusAndResidualSlow();
            ClearFixtures();
            yield return TestResidualSlowRevocation();
            ClearFixtures();
            yield return TestResidualSlowCancellation();
            ClearFixtures();
            yield return TestOvercharge();
            ClearFixtures();
            yield return TestNoAugmentBaselineAndPause();

            ClearFixtures();
            fixtureRoot = null;
            yield return null;
            Require(unexpectedLogs.Count == 0,
                "unexpected Unity errors/exceptions: " + string.Join(" | ", unexpectedLogs));
        }
        finally
        {
            if (fixtureRoot != null)
            {
                ClearFixtures();
                Object.DestroyImmediate(fixtureRoot);
                fixtureRoot = null;
            }
            Application.logMessageReceived -= logHandler;
            Time.timeScale = 1f;
        }
    }

    private static void LoadAndValidateData()
    {
        augments = new PlayerCombatAugmentSO[ResourceNames.Length];
        WizardSkillSO q = Resources.Load<WizardSkillSO>("../ScriptableObjects/Skills/Wizard/WizardSkillQ");
        q = AssetDatabase.LoadAssetAtPath<WizardSkillSO>("Assets/ScriptableObjects/Skills/Wizard/WizardSkillQ.asset");
        WizardSkillSO e = AssetDatabase.LoadAssetAtPath<WizardSkillSO>("Assets/ScriptableObjects/Skills/Wizard/WizardSkillE.asset");
        WizardSkillSO r = AssetDatabase.LoadAssetAtPath<WizardSkillSO>("Assets/ScriptableObjects/Skills/Wizard/WizardUltimate.asset");
        Require(q != null && e != null && r != null, "Q/E/R skill assets load");

        PlayerSkillSO[] expectedTargets = { null, e, q, q, e, e, r };
        for (int i = 0; i < ResourceNames.Length; i++)
        {
            PlayerCombatAugmentSO augment = Resources.Load<PlayerCombatAugmentSO>(
                "Augments/Data/" + ResourceNames[i] + "_Data");
            Require(augment != null && augment.icon != null, "augment resource and icon: " + ResourceNames[i]);
            Require(augment.RequiredCharacter == PlayerCharacterId.Wizard, "wizard-only data: " + ResourceNames[i]);
            Require(augment.Effect.ToString() == EffectNames[i], "appended effect enum: " + ResourceNames[i]);
            Require(Mathf.Approximately(augment.Value, Values[i]) && augment.maxStacks == MaxStacks[i],
                "contract value and stack cap: " + ResourceNames[i]);
            Require(augment.TargetSkill == expectedTargets[i], "correct Q/E/R target: " + ResourceNames[i]);
            string iconPath = AssetDatabase.GetAssetPath(augment.icon);
            Require(iconPath.StartsWith("Assets/Images/"), "authored icon path: " + ResourceNames[i]);
            augments[i] = augment;
        }

        combatAugmentPrefab = Resources.Load<GameObject>("Augments/Skills/PlayerCombatAugment");
        Require(combatAugmentPrefab != null && combatAugmentPrefab.GetComponent<PlayerCombatAugment>() != null,
            "shared player combat augment prefab");
    }

    private static void TestAcquisitionAndFiltering()
    {
        GameObject wizard = CreateWizardPlayer("WizardAugmentFiltering", Vector3.zero);
        GameObject sword = new GameObject("SwordAugmentFiltering");
        sword.transform.SetParent(fixtureRoot.transform, false);
        sword.AddComponent<PlayerCharacter>().SetCharacterId(PlayerCharacterId.Sword);
        PlayerAugments playerAugments = wizard.GetComponent<PlayerAugments>();
        for (int i = 0; i < augments.Length; i++)
        {
            Require(augments[i].IsAvailableFor(wizard) && !augments[i].IsAvailableFor(sword),
                "wizard-only filtering: " + ResourceNames[i]);
        }
        Require(sword.GetComponent<PlayerAugments>() == null ||
                sword.GetComponent<PlayerAugments>().TryAcquireLoadedSkill(augments[0], combatAugmentPrefab) == SkillAcquireStatus.Invalid,
            "direct acquisition rejects a non-wizard");

        PlayerCombatAugmentSO pierce = augments[0];
        Require(playerAugments.TryAcquireLoadedSkill(pierce, combatAugmentPrefab) == SkillAcquireStatus.Instantiated,
            "basic pierce first stack acquired");
        Require(playerAugments.GetCombatAugmentValue(Effect(pierce), null) == 1f,
            "basic pierce first stack contributes one");
        Require(playerAugments.TryAcquireLoadedSkill(pierce, combatAugmentPrefab) == SkillAcquireStatus.Stacked,
            "basic pierce second stack acquired");
        Require(playerAugments.TryAcquireLoadedSkill(pierce, combatAugmentPrefab) == SkillAcquireStatus.AlreadyMax,
            "basic pierce acquisition respects its two-stack cap");
        Require(playerAugments.GetSkillStackCount(pierce) == 2 &&
                playerAugments.GetCombatAugmentValue(Effect(pierce), null) == 2f,
            "basic pierce stacks add to the live value");
        Require(RemoveOneStack(playerAugments, pierce) && playerAugments.GetSkillStackCount(pierce) == 1 &&
                playerAugments.GetCombatAugmentValue(Effect(pierce), null) == 1f,
            "removing one stack updates the live value");
        Require(RemoveAugment(playerAugments, pierce) && !playerAugments.HasSkill(pierce) &&
                playerAugments.GetCombatAugmentValue(Effect(pierce), null) == 0f,
            "removing the final stack clears the value");
    }

    private static IEnumerator TestBasicPiercing()
    {
        GameObject player = CreateWizardPlayer("WizardPierceValidation", new Vector3(300f, 0f, 0f));
        PlayerAugments playerAugments = player.GetComponent<PlayerAugments>();
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        WizardCombat combat = player.GetComponent<WizardCombat>();
        ProjectilePlayerAttack attack = player.GetComponent<ProjectilePlayerAttack>();
        PlayerCombatAugmentSO pierce = augments[0];
        Require(combat.BasicAdditionalPierces == 0, "no-augment basic attack has no extra pierces");
        Require(playerAugments.TryAcquireLoadedSkill(pierce, combatAugmentPrefab) == SkillAcquireStatus.Instantiated &&
                playerAugments.TryAcquireLoadedSkill(pierce, combatAugmentPrefab) == SkillAcquireStatus.Stacked,
            "two basic pierce stacks acquired before R");
        Require(combat.BasicAdditionalPierces == 2, "two augmentation pierces apply outside R");
        FillGauge(player.GetComponent<PlayerUltimateGauge>());
        Require(skills.TryUseUltimate() && combat.IsOverdriveActive, "R starts with basic pierce augmentation");
        Require(combat.BasicAdditionalPierces == 3, "R pierce adds to both augmentation stacks");

        DamageProjectile prefab = CreateProjectilePrefab("WizardPierceValidationProjectile");
        SetField(attack, "projectilePrefab", prefab);
        SetField(attack, "speed", 5f);
        SetField(attack, "maxDistance", 18f);
        EnemyHealth[] targets = new EnemyHealth[5];
        for (int i = 0; i < targets.Length; i++)
            targets[i] = CreateEnemy("PierceTarget" + i, 100, player.transform.position + Vector3.up * 0.5f +
                Vector3.forward * (5f + i * 3f));
        Physics.SyncTransforms();
        yield return null;

        Require(attack.TryAttack(), "augmented R basic projectile launches");
        DamageProjectile launched = FindLaunchedProjectile(prefab);
        Require(launched != null && GetField<int>(launched, "remainingPierces") == 3,
            "launched projectile snapshots additive R plus augmentation pierces");
        Require(RemoveAugment(playerAugments, pierce), "remove basic pierce while projectile is in flight");
        Require(combat.BasicAdditionalPierces == 1 && GetField<int>(launched, "remainingPierces") == 3,
            "revocation changes future attacks but not an already launched projectile");
        yield return WaitForCondition(() => targets[3].CurrentHealth < targets[3].MaxHealth || launched == null,
            "snapshot projectile reaches the fourth enemy", 360);
        int expectedDamage = Mathf.RoundToInt(player.GetComponent<PlayerStats>().AttackDamage * combat.BasicDamageMultiplier);
        for (int i = 0; i < 4; i++)
            Require(targets[i].CurrentHealth == targets[i].MaxHealth - expectedDamage,
                "three extra pierces hit target " + i + " exactly once");
        Require(targets[4].CurrentHealth == targets[4].MaxHealth,
            "three extra pierces stop before a fifth target");
    }

    private static IEnumerator TestResonanceAndFieldCapacity()
    {
        GameObject player = CreateWizardPlayer("WizardFieldValidation", new Vector3(0f, 0f, 40f));
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        PlayerDamageDealer dealer = player.GetComponent<PlayerDamageDealer>();
        PlayerAugments playerAugments = player.GetComponent<PlayerAugments>();
        WizardCombat combat = player.GetComponent<WizardCombat>();
        WizardSkillSO fieldSkill = skills.SkillE as WizardSkillSO;
        Require(fieldSkill != null, "E skill assigned for field augments");
        Require(playerAugments.TryAcquireLoadedSkill(augments[1], combatAugmentPrefab) == SkillAcquireStatus.Instantiated &&
                playerAugments.TryAcquireLoadedSkill(augments[4], combatAugmentPrefab) == SkillAcquireStatus.Instantiated &&
                playerAugments.TryAcquireLoadedSkill(augments[5], combatAugmentPrefab) == SkillAcquireStatus.Instantiated,
            "resonance radius, extra field, and collapse augments acquired");
        CreateGround("WizardFieldGround", new Vector3(0f, -0.1f, 44f), new Vector3(40f, 0.2f, 30f));
        Camera camera = CreateAimCamera("WizardFieldCamera", new Vector3(0f, 8f, 44f), new Vector3(0f, 0f, 44f));
        EnemyHealth first = CreateEnemy("FieldOneTarget", 1000, new Vector3(0f, 0.5f, 44f));
        EnemyHealth firstNeighbor = CreateEnemy("FieldOneResonanceNeighbor", 1000, new Vector3(2.6f, 0.5f, 44f));
        EnemyHealth second = CreateEnemy("FieldTwoTarget", 1000, new Vector3(8f, 0.5f, 44f));
        EnemyHealth secondNeighbor = CreateEnemy("FieldTwoResonanceNeighbor", 1000, new Vector3(10.6f, 0.5f, 44f));
        Physics.SyncTransforms();
        yield return null;

        Require(CastE(skills, fieldSkill), "first augmented resonance field placed");
        IList fields = GetResonanceFields(combat);
        Require(fields.Count == 1, "first field is tracked");
        object firstField = fields[0];
        SetECastReady(skills);
        AimAt(camera, 8f, 44f);
        Physics.SyncTransforms();
        yield return null;
        Require(CastE(skills, fieldSkill), "second field placed with the extra-field augment");
        fields = GetResonanceFields(combat);
        Require(fields.Count == 2, "two simultaneous fields are retained");
        object secondField = FindFieldByCenterX(fields, 8f);
        Require(secondField != null, "second field has its own runtime state");
        Require(Mathf.Abs(GetField<float>(firstField, "Radius") - fieldSkill.ResonanceFieldRadius) < 0.01f &&
                Mathf.Abs(GetField<float>(secondField, "Radius") - fieldSkill.ResonanceFieldRadius) < 0.01f,
            "resonance-radius augmentation leaves both field tick radii unchanged");
        Require(Mathf.Abs(GetField<float>(firstField, "ResonanceRadius") - fieldSkill.ResonanceRadius) < 0.01f &&
                Mathf.Abs(playerAugments.GetCombatAugmentValue(Effect(augments[1]), fieldSkill) - 0.25f) < 0.01f,
            "resonance splash uses its separate base radius and 25-percent augment");

        int firstNeighborHealth = firstNeighbor.CurrentHealth;
        dealer.DealDamage(first, 5, PlayerDamageType.BasicAttack, first.transform.position);
        Require(firstNeighbor.CurrentHealth == firstNeighborHealth - Mathf.RoundToInt(
                player.GetComponent<PlayerStats>().AttackDamage * fieldSkill.ResonanceDamageMultiplier),
            "increased resonance splash reaches a target outside the unaugmented splash radius");
        int secondNeighborHealth = secondNeighbor.CurrentHealth;
        dealer.DealDamage(second, 5, PlayerDamageType.BasicAttack, second.transform.position);
        Require(secondNeighbor.CurrentHealth == secondNeighborHealth - Mathf.RoundToInt(
                player.GetComponent<PlayerStats>().AttackDamage * fieldSkill.ResonanceDamageMultiplier),
            "a second field has an independent resonance cooldown");

        SetECastReady(skills);
        AimAt(camera, -8f, 44f);
        Physics.SyncTransforms();
        yield return null;
        Require(CastE(skills, fieldSkill), "third placement evicts the oldest field");
        fields = GetResonanceFields(combat);
        object thirdField = FindFieldByCenterX(fields, -8f);
        Require(fields.Count == 2 && !ContainsReference(fields, firstField) &&
                ContainsReference(fields, secondField) && thirdField != null,
            "oldest field is evicted while the newer two remain");
        Require(GameObject.Find("WizardFieldCollapse") == null,
            "replacement eviction does not trigger collapse");

        int collapseTargetHealth = second.CurrentHealth;
        SetField(secondField, "EndTime", Time.time + 0.15f);
        SetField(secondField, "NextTickTime", Time.time + 1f);
        yield return WaitForCondition(() => !ContainsReference(GetResonanceFields(combat), secondField),
            "field reaches natural expiry", 60);
        Require(GameObject.Find("WizardFieldCollapse") != null,
            "natural expiry creates the collapse effect");
        Require(second.CurrentHealth == collapseTargetHealth - Mathf.RoundToInt(
                player.GetComponent<PlayerStats>().AttackDamage * augments[5].Value),
            "natural collapse deals the authored attack-scaled damage");
        Require(GetResonanceFields(combat).Count == 1 && ContainsReference(GetResonanceFields(combat), thirdField),
            "natural expiry removes only its own field");
    }

    private static IEnumerator TestCollapseCancellation(string cause)
    {
        GameObject player = CreateWizardPlayer("WizardCollapse" + cause, new Vector3(30f, 0f, 40f));
        PlayerAugments playerAugments = player.GetComponent<PlayerAugments>();
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        WizardCombat combat = player.GetComponent<WizardCombat>();
        WizardSkillSO fieldSkill = skills.SkillE as WizardSkillSO;
        Require(playerAugments.TryAcquireLoadedSkill(augments[5], combatAugmentPrefab) == SkillAcquireStatus.Instantiated,
            "collapse augment acquired for " + cause);
        CreateGround("CollapseGround" + cause, new Vector3(30f, -0.1f, 44f), new Vector3(30f, 0.2f, 30f));
        Camera camera = CreateAimCamera("CollapseCamera" + cause, new Vector3(30f, 8f, 44f),
            new Vector3(30f, 0f, 44f));
        CreateEnemy("CollapseCancelTarget" + cause, 1000, new Vector3(30f, 0.5f, 44f));
        Physics.SyncTransforms();
        yield return null;
        Require(CastE(skills, fieldSkill), "field placed for " + cause + " cancellation");
        object field = GetResonanceFields(combat)[0];
        yield return null;

        if (cause == "death")
            player.GetComponent<PlayerStateManager>().ChangeState(PlayerState.Dead);
        else if (cause == "disable")
            combat.enabled = false;
        else if (cause == "revocation")
        {
            Require(RemoveAugment(playerAugments, augments[5]), "collapse augment revoked while field is active");
            SetField(field, "EndTime", Time.time + 0.1f);
            SetField(field, "NextTickTime", Time.time + 1f);
        }
        else
            throw new InvalidOperationException("unknown collapse cancellation: " + cause);

        yield return WaitSeconds(0.25f);
        Require(GameObject.Find("WizardFieldCollapse") == null,
            "" + cause + " removal does not trigger a collapse");
        Require(GetResonanceFields(combat).Count == 0 || cause == "disable" || cause == "death",
            "" + cause + " leaves no expiring field state");
        Object.Destroy(camera.gameObject);
    }

    private static IEnumerator TestGravityRadiusAndResidualSlow()
    {
        BakeNavMesh();
        GameObject baselinePlayer = CreateWizardPlayer("GravityRadiusBaseline", new Vector3(0f, 0f, 0f));
        PlayerSkillController baselineSkills = baselinePlayer.GetComponent<PlayerSkillController>();
        EnemyHealth baselineStop = CreateNavEnemy("GravityRadiusBaselineStop", 1000, new Vector3(0f, 0f, 4f));
        EnemyHealth baselineEdge = CreateNavEnemy("GravityRadiusBaselineEdge", 1000, new Vector3(4.1f, 0f, 4f));
        BoxCollider baselineEdgeCollider = baselineEdge.GetComponent<BoxCollider>();
        baselineEdgeCollider.size = new Vector3(0.2f, 2.2f, 0.2f);
        NavMeshAgent baselineEdgeAgent = baselineEdge.GetComponent<NavMeshAgent>();
        yield return null;
        Require(baselineEdgeAgent.isOnNavMesh, "baseline edge target is on the test NavMesh");
        Require(InvokeTryCast(baselineSkills, baselineSkills.SkillQ, false), "baseline gravity core cast");
        yield return WaitSeconds(0.55f);
        Require(baselineStop.CurrentHealth == baselineStop.MaxHealth && !baselineEdgeAgent.isStopped,
            "unaugmented gravity does not pull a target outside its base radius");

        ClearFixtures();
        yield return null;
        GameObject augmentedPlayer = CreateWizardPlayer("GravityRadiusAugmented", new Vector3(20f, 0f, 0f));
        PlayerSkillController augmentedSkills = augmentedPlayer.GetComponent<PlayerSkillController>();
        PlayerAugments augmentedPlayerAugments = augmentedPlayer.GetComponent<PlayerAugments>();
        EnemyHealth augmentedStop = CreateNavEnemy("GravityRadiusAugmentedStop", 1000, new Vector3(20f, 0f, 4f));
        EnemyHealth augmentedEdge = CreateNavEnemy("GravityRadiusAugmentedEdge", 1000, new Vector3(24.1f, 0f, 4f));
        BoxCollider augmentedEdgeCollider = augmentedEdge.GetComponent<BoxCollider>();
        augmentedEdgeCollider.size = new Vector3(0.2f, 2.2f, 0.2f);
        NavMeshAgent augmentedEdgeAgent = augmentedEdge.GetComponent<NavMeshAgent>();
        Require(augmentedPlayerAugments.TryAcquireLoadedSkill(augments[2], combatAugmentPrefab) ==
                SkillAcquireStatus.Instantiated, "gravity-radius augment acquired");
        yield return null;
        Require(augmentedEdgeAgent.isOnNavMesh, "augmented edge target is on the test NavMesh");
        Require(InvokeTryCast(augmentedSkills, augmentedSkills.SkillQ, false), "augmented gravity core cast");
        yield return WaitForCondition(() => augmentedEdgeAgent.isStopped,
            "25-percent gravity radius pulls the edge target", 120);
        Require(augmentedStop.CurrentHealth == augmentedStop.MaxHealth,
            "gravity radius increases pull reach without requiring a blast hit");

        ClearFixtures();
        yield return null;
        GameObject residualPlayer = CreateWizardPlayer("ResidualGravityNatural", new Vector3(40f, 0f, 0f));
        PlayerSkillController residualSkills = residualPlayer.GetComponent<PlayerSkillController>();
        PlayerAugments residualAugments = residualPlayer.GetComponent<PlayerAugments>();
        WizardCombat residualCombat = residualPlayer.GetComponent<WizardCombat>();
        EnemyHealth slowedEnemy = CreateNavEnemy("ResidualGravityNaturalTarget", 1000, new Vector3(40f, 0f, 4f));
        NavMeshAgent slowedAgent = slowedEnemy.GetComponent<NavMeshAgent>();
        slowedAgent.speed = 4f;
        float baseSpeed = slowedAgent.speed;
        Require(residualAugments.TryAcquireLoadedSkill(augments[3], combatAugmentPrefab) ==
                SkillAcquireStatus.Instantiated, "residual-gravity augment acquired");
        yield return null;
        Require(InvokeTryCast(residualSkills, residualSkills.SkillQ, false), "normal Q cast starts residual gravity");
        yield return WaitForCondition(() => slowedEnemy.CurrentHealth < slowedEnemy.MaxHealth &&
                                            slowedAgent.speed < baseSpeed * 0.9f,
            "normal Q completion applies a 30-percent residual slow", 300);
        Require(Mathf.Abs(slowedAgent.speed - baseSpeed * 0.7f) < 0.05f,
            "residual gravity applies the authored slow fraction");
        float slowStartedAt = Time.time;
        yield return WaitSeconds(1.6f);
        Require(slowedAgent != null && slowedAgent.speed < baseSpeed * 0.9f,
            "residual slow remains active before its two-second expiry");
        yield return WaitSeconds(Mathf.Max(0f, 2.15f - (Time.time - slowStartedAt)));
        Require(slowedAgent != null && Mathf.Abs(slowedAgent.speed - baseSpeed) < 0.05f,
            "residual gravity restores the original NavMesh speed after two seconds");
        Require(GetField<IList>(residualCombat, "residualGravityFields").Count == 0,
            "expired residual field is removed");
    }

    private static IEnumerator TestResidualSlowRevocation()
    {
        GameObject player = CreateWizardPlayer("ResidualGravityRevocation", new Vector3(60f, 0f, 0f));
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        PlayerAugments playerAugments = player.GetComponent<PlayerAugments>();
        EnemyHealth enemy = CreateNavEnemy("ResidualGravityRevocationTarget", 1000, new Vector3(60f, 0f, 4f));
        NavMeshAgent agent = enemy.GetComponent<NavMeshAgent>();
        agent.speed = 4f;
        float baseSpeed = agent.speed;
        Require(playerAugments.TryAcquireLoadedSkill(augments[3], combatAugmentPrefab) ==
                SkillAcquireStatus.Instantiated, "residual-gravity augment acquired for revocation test");
        yield return null;
        Require(InvokeTryCast(skills, skills.SkillQ, false), "Q cast for residual revocation");
        yield return WaitForCondition(() => agent.speed < baseSpeed * 0.9f,
            "residual slow begins before revocation", 300);
        Require(RemoveAugment(playerAugments, augments[3]), "residual-gravity augment removed during slow");
        yield return null;
        Require(Mathf.Abs(agent.speed - baseSpeed) < 0.05f,
            "revoking residual gravity immediately restores the target's original speed");
        Require(GetField<IList>(player.GetComponent<WizardCombat>(), "residualGravityFields").Count == 0,
            "revocation removes residual-field ownership");
    }

    private static IEnumerator TestResidualSlowCancellation()
    {
        GameObject player = CreateWizardPlayer("ResidualGravityCancellation", new Vector3(80f, 0f, 0f));
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        WizardCombat combat = player.GetComponent<WizardCombat>();
        PlayerAugments playerAugments = player.GetComponent<PlayerAugments>();
        EnemyHealth enemy = CreateNavEnemy("ResidualGravityCancellationTarget", 1000, new Vector3(80f, 0f, 4f));
        NavMeshAgent agent = enemy.GetComponent<NavMeshAgent>();
        agent.speed = 4f;
        float baseSpeed = agent.speed;
        Require(playerAugments.TryAcquireLoadedSkill(augments[3], combatAugmentPrefab) ==
                SkillAcquireStatus.Instantiated, "residual-gravity augment acquired for cancellation test");
        yield return null;
        Require(InvokeTryCast(skills, skills.SkillQ, false), "Q cast for cancellation test");
        combat.enabled = false;
        yield return WaitSeconds(0.25f);
        Require(Mathf.Abs(agent.speed - baseSpeed) < 0.05f &&
                GetField<IList>(combat, "residualGravityFields").Count == 0,
            "cancelling Q before normal completion never leaves a residual slow");
    }

    private static IEnumerator TestOvercharge()
    {
        GameObject player = CreateWizardPlayer("WizardOverchargeValidation", new Vector3(100f, 0f, 0f));
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        PlayerAugments playerAugments = player.GetComponent<PlayerAugments>();
        PlayerDamageDealer ownerDealer = player.GetComponent<PlayerDamageDealer>();
        WizardCombat combat = player.GetComponent<WizardCombat>();
        FillGauge(player.GetComponent<PlayerUltimateGauge>());
        Require(skills.TryUseUltimate() && combat.IsOverdriveActive, "R starts before overcharge acquisition");
        object state = GetField<object>(combat, "overdrive");
        float baseEndTime = GetField<float>(state, "BaseEndTime");
        float initialEndTime = GetField<float>(state, "EndTime");

        GameObject otherDealerObject = NewGameObject("WizardOtherDamageDealer", new Vector3(200f, 0f, 0f));
        PlayerDamageDealer otherDealer = otherDealerObject.AddComponent<PlayerDamageDealer>();
        EnemyHealth otherKill = CreateEnemy("UnattributedOverchargeKill", 10, new Vector3(210f, 0.5f, 0f));
        otherDealer.DealDamage(otherKill, 10, PlayerDamageType.Skill);
        Require(Mathf.Abs(GetField<float>(state, "EndTime") - initialEndTime) < 0.01f,
            "another damage dealer's kill does not extend R");

        EnemyHealth earlyKill = CreateEnemy("PreAugmentOverchargeKill", 10, new Vector3(211f, 0.5f, 0f));
        ownerDealer.DealDamage(earlyKill, 10, PlayerDamageType.BasicAttack);
        Require(Mathf.Abs(GetField<float>(state, "EndTime") - initialEndTime) < 0.01f,
            "a kill before acquiring overcharge is not credited retroactively");
        Require(playerAugments.TryAcquireLoadedSkill(augments[6], combatAugmentPrefab) == SkillAcquireStatus.Instantiated &&
                playerAugments.TryAcquireLoadedSkill(augments[6], combatAugmentPrefab) == SkillAcquireStatus.Stacked,
            "overcharge stacks acquired during the active R");

        EnemyHealth nonlethal = CreateEnemy("NonlethalOverchargeHit", 10, new Vector3(212f, 0.5f, 0f));
        ownerDealer.DealDamage(nonlethal, 1, PlayerDamageType.BasicAttack);
        yield return null;
        Require(Mathf.Abs(GetField<float>(state, "EndTime") - initialEndTime) < 0.01f,
            "nonlethal damage does not extend R");

        for (int i = 0; i < 4; i++)
        {
            EnemyHealth kill = CreateEnemy("CreditedOverchargeKill" + i, 10,
                new Vector3(213f + i, 0.5f, 0f));
            ownerDealer.DealDamage(kill, 10, PlayerDamageType.BasicAttack);
            float expected = baseEndTime + Mathf.Min(3f, (i + 1) * 1f);
            Require(Mathf.Abs(GetField<float>(state, "EndTime") - expected) < 0.03f,
                "owner-attributable kill adds one second at two stacks, capped at three seconds (" + (i + 1) + ")");
        }

        Require(RemoveOneStack(playerAugments, augments[6]), "one overcharge stack revoked during R");
        yield return null;
        Require(Mathf.Abs(GetField<float>(state, "EndTime") - (baseEndTime + 2f)) < 0.03f,
            "stack removal recalculates previously credited kills at the one-stack rate");
        Require(RemoveAugment(playerAugments, augments[6]), "final overcharge stack revoked during R");
        yield return null;
        Require(Mathf.Abs(GetField<float>(state, "EndTime") - baseEndTime) < 0.03f,
            "removing overcharge returns R to its unextended base duration");
        EnemyHealth revokedKill = CreateEnemy("RevokedOverchargeKill", 10, new Vector3(218f, 0.5f, 0f));
        ownerDealer.DealDamage(revokedKill, 10, PlayerDamageType.BasicAttack);
        Require(Mathf.Abs(GetField<float>(state, "EndTime") - baseEndTime) < 0.03f,
            "kills after revocation cannot restore removed extension");
    }

    private static IEnumerator TestNoAugmentBaselineAndPause()
    {
        GameObject player = CreateWizardPlayer("WizardNoAugmentPause", new Vector3(0f, 0f, 0f));
        PlayerAugments playerAugments = player.GetComponent<PlayerAugments>();
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        WizardCombat combat = player.GetComponent<WizardCombat>();
        foreach (PlayerCombatAugmentSO augment in augments)
        {
            Require(Mathf.Approximately(playerAugments.GetCombatAugmentValue(Effect(augment), augment.TargetSkill), 0f),
                "no-augment baseline value is zero: " + augment.name);
        }
        Require(combat.BasicAdditionalPierces == 0 && Mathf.Approximately(combat.BasicDamageMultiplier, 1f),
            "no-augment baseline has ordinary basic attacks");

        CreateGround("PauseGround", new Vector3(0f, -0.1f, 4f), new Vector3(20f, 0.2f, 20f));
        EnemyHealth waveTarget = CreateEnemy("PauseWaveTarget", 1000, new Vector3(0f, 0.5f, 1f));
        Physics.SyncTransforms();
        FillGauge(player.GetComponent<PlayerUltimateGauge>());
        Require(skills.TryUseUltimate() && combat.IsOverdriveActive,
            "baseline R activates without any augment");
        Require(combat.BasicAdditionalPierces == 1,
            "unaugmented R still applies its own extra pierce");
        yield return WaitForCondition(() => waveTarget.CurrentHealth < waveTarget.MaxHealth,
            "unaugmented R begins its ordinary wave damage");
        int pausedHealth = waveTarget.CurrentHealth;
        object state = GetField<object>(combat, "overdrive");
        float pausedEndTime = GetField<float>(state, "EndTime");
        Time.timeScale = 0f;
        yield return WaitSecondsUnscaled(0.25f);
        Require(waveTarget.CurrentHealth == pausedHealth &&
                Mathf.Abs(GetField<float>(state, "EndTime") - pausedEndTime) < 0.01f && combat.IsOverdriveActive,
            "pause freezes R waves, timer, and baseline active state");
        Time.timeScale = 1f;
        yield return WaitForCondition(() => waveTarget.CurrentHealth < pausedHealth,
            "unpaused R resumes its wave");
    }

    private static PlayerCombatAugmentEffect Effect(PlayerCombatAugmentSO augment)
    {
        return (PlayerCombatAugmentEffect)Enum.Parse(typeof(PlayerCombatAugmentEffect), augment.Effect.ToString());
    }

    private static GameObject CreateWizardPlayer(string name, Vector3 position)
    {
        GameObject player = NewGameObject(name, position);
        player.tag = "Player";
        player.AddComponent<PlayerCharacter>().SetCharacterId(PlayerCharacterId.Wizard);
        PlayerStats stats = player.AddComponent<PlayerStats>();
        stats.baseAttackDamage = 10f;
        player.AddComponent<PlayerHealth>();
        player.AddComponent<PlayerStateManager>();
        player.AddComponent<PlayerDamageDealer>();
        player.AddComponent<PlayerAugments>();
        player.AddComponent<PlayerUltimateGauge>();
        PlayerSkillController skills = player.AddComponent<PlayerSkillController>();
        Transform origin = new GameObject("SkillOrigin").transform;
        origin.SetParent(player.transform, false);
        origin.localPosition = new Vector3(0f, 1.1f, 0.8f);
        SetField(skills, "skillOrigin", origin);
        SetField(skills, "skillQ", AssetDatabase.LoadAssetAtPath<WizardSkillSO>(
            "Assets/ScriptableObjects/Skills/Wizard/WizardSkillQ.asset"));
        SetField(skills, "skillE", AssetDatabase.LoadAssetAtPath<WizardSkillSO>(
            "Assets/ScriptableObjects/Skills/Wizard/WizardSkillE.asset"));
        SetField(skills, "ultimate", AssetDatabase.LoadAssetAtPath<WizardSkillSO>(
            "Assets/ScriptableObjects/Skills/Wizard/WizardUltimate.asset"));
        WizardCombat combat = player.AddComponent<WizardCombat>();
        CopyPackReferences(combat);
        ProjectilePlayerAttack attack = player.AddComponent<ProjectilePlayerAttack>();
        SetField(attack, "stats", stats);
        SetField(attack, "projectileOrigin", origin);
        SetField(attack, "projectilePrefab", AssetDatabase.LoadAssetAtPath<DamageProjectile>(
            "Assets/Prefabs/Projectile/WizardGreenProjectile.prefab"));
        CapsuleCollider capsule = player.AddComponent<CapsuleCollider>();
        capsule.height = 2f;
        capsule.radius = 0.4f;
        return player;
    }

    private static readonly string[] PackFields =
    {
        "gravityCorePrefab", "gravityVortexPrefab", "starHitPrefab", "resonanceFieldPrefab",
        "overdriveAuraPrefab", "overdriveWavePrefab", "overdriveFinalPrefab", "resonanceHitPrefab"
    };
    private static readonly string[] PackPaths =
    {
        "GravityCore.prefab", "GravityVortex.prefab",
        "GravityImpact.prefab", "ResonanceField.prefab",
        "OverdriveAura.prefab", "OverdriveWave.prefab",
        "OverdriveFinal.prefab", "ResonanceImpact.prefab"
    };

    private static void CopyPackReferences(WizardCombat combat)
    {
        for (int i = 0; i < PackFields.Length; i++)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/SFX_VFX/Wizard/" + PackPaths[i]);
            Require(prefab != null, "visual prefab loads: " + PackPaths[i]);
            SetField(combat, PackFields[i], prefab);
        }
    }

    private static bool CastE(PlayerSkillController skills, WizardSkillSO fieldSkill)
    {
        return InvokeTryCast(skills, fieldSkill, true);
    }

    private static bool InvokeTryCast(PlayerSkillController skills, PlayerSkillSO skill, bool isE)
    {
        float qBefore = skills.QRemaining;
        int eChargesBefore = skills.ECharges;
        MethodInfo method = typeof(PlayerSkillController).GetMethod("TryCast",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Require(method != null, "player skill cast entrypoint exists");
        method.Invoke(skills, new object[] { skill, isE });
        if (isE)
            return skills.ECharges < eChargesBefore;
        return skills.QRemaining > qBefore;
    }

    private static void SetECastReady(PlayerSkillController skills)
    {
        SetField(skills, "eCharges", skills.EMaxCharges);
        SetField(skills, "ePendingRechargeCharges", 0);
        SetField(skills, "eNextRechargeTime", 0f);
    }

    private static void FillGauge(PlayerUltimateGauge gauge)
    {
        MethodInfo fill = typeof(PlayerUltimateGauge).GetMethod("FillForTest",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Require(fill != null, "ultimate gauge test fill entrypoint exists");
        fill.Invoke(gauge, null);
    }

    private static DamageProjectile CreateProjectilePrefab(string name)
    {
        GameObject projectile = NewGameObject(name, new Vector3(-100f, -100f, 0f));
        Rigidbody body = projectile.AddComponent<Rigidbody>();
        body.useGravity = false;
        body.isKinematic = true;
        CapsuleCollider collider = projectile.AddComponent<CapsuleCollider>();
        collider.isTrigger = true;
        collider.direction = 2;
        collider.radius = 0.15f;
        collider.height = 0.3f;
        return projectile.AddComponent<DamageProjectile>();
    }

    private static DamageProjectile FindLaunchedProjectile(DamageProjectile prefab)
    {
        DamageProjectile latest = null;
        foreach (DamageProjectile projectile in Object.FindObjectsOfType<DamageProjectile>())
        {
            if (projectile == prefab || !projectile.name.StartsWith(prefab.name))
                continue;
            if (latest == null || projectile.GetInstanceID() > latest.GetInstanceID())
                latest = projectile;
        }
        return latest;
    }

    private static EnemyHealth CreateEnemy(string name, int health, Vector3 position)
    {
        GameObject enemyObject = new GameObject(name);
        enemyObject.SetActive(false);
        enemyObject.transform.SetParent(fixtureRoot.transform, true);
        enemyObject.transform.position = position;
        BoxCollider hitbox = enemyObject.AddComponent<BoxCollider>();
        hitbox.size = new Vector3(1f, 2.2f, 1f);
        hitbox.center = new Vector3(0f, 0.6f, 0f);
        EnemyHealth enemy = enemyObject.AddComponent<EnemyHealth>();
        SetField(enemy, "maxHealth", health);
        SetField(enemy, "expReward", 0);
        SetField(enemy, "destroyDelay", 60f);
        enemyObject.SetActive(true);
        return enemy;
    }

    private static EnemyHealth CreateNavEnemy(string name, int health, Vector3 position)
    {
        EnemyHealth enemy = CreateEnemy(name, health, position);
        NavMeshAgent agent = enemy.gameObject.AddComponent<NavMeshAgent>();
        agent.height = 2f;
        agent.radius = 0.4f;
        agent.speed = 4f;
        return enemy;
    }

    private static void CreateGround(string name, Vector3 position, Vector3 size)
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = name;
        ground.transform.SetParent(fixtureRoot.transform, true);
        ground.transform.position = position;
        ground.transform.localScale = size;
    }

    private static Camera CreateAimCamera(string name, Vector3 position, Vector3 lookAt)
    {
        Camera existing = Camera.main;
        if (existing != null)
            existing.tag = "Untagged";
        GameObject cameraObject = NewGameObject(name, position);
        cameraObject.tag = "MainCamera";
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.transform.LookAt(lookAt);
        return camera;
    }

    private static void AimAt(Camera camera, float x, float z)
    {
        camera.transform.position = new Vector3(x, 8f, z);
        camera.transform.LookAt(new Vector3(x, 0f, z));
    }

    private static void BakeNavMesh()
    {
        var sources = new List<NavMeshBuildSource>
        {
            new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                size = new Vector3(160f, 0.2f, 80f),
                transform = Matrix4x4.TRS(new Vector3(50f, -0.1f, 20f), Quaternion.identity, Vector3.one),
                area = 0
            }
        };
        NavMeshData data = NavMeshBuilder.BuildNavMeshData(NavMesh.GetSettingsByIndex(0), sources,
            new Bounds(new Vector3(50f, 0f, 20f), new Vector3(180f, 10f, 100f)), Vector3.zero, Quaternion.identity);
        Require(data != null, "test NavMesh builds");
        NavMesh.AddNavMeshData(data);
    }

    private static IList GetResonanceFields(WizardCombat combat)
    {
        return GetField<IList>(combat, "resonanceFields");
    }

    private static object FindFieldByCenterX(IList fields, float x)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            object field = fields[i];
            if (Mathf.Abs(GetField<Vector3>(field, "Center").x - x) < 0.1f)
                return field;
        }
        return null;
    }

    private static bool ContainsReference(IList items, object target)
    {
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], target))
                return true;
        return false;
    }

    private static bool RemoveOneStack(PlayerAugments playerAugments, SkillAugmentSO target)
    {
        Dictionary<SkillAugmentSO, AugmentSkill> owned = GetOwnedAugments(playerAugments);
        if (!owned.TryGetValue(target, out AugmentSkill skill) || skill == null)
            return false;
        if (skill.StackCount <= 1)
            return RemoveAugment(playerAugments, target);
        bool removed = skill.TryRemoveStack();
        if (removed)
            playerAugments.onSkillsChanged?.Invoke();
        return removed;
    }

    private static bool RemoveAugment(PlayerAugments playerAugments, SkillAugmentSO target)
    {
        Dictionary<SkillAugmentSO, AugmentSkill> owned = GetOwnedAugments(playerAugments);
        if (!owned.TryGetValue(target, out AugmentSkill skill))
            return false;
        if (skill != null)
        {
            skill.Release();
            Object.Destroy(skill.gameObject);
        }
        owned.Remove(target);
        playerAugments.onSkillsChanged?.Invoke();
        return true;
    }

    private static Dictionary<SkillAugmentSO, AugmentSkill> GetOwnedAugments(PlayerAugments playerAugments)
    {
        return GetField<Dictionary<SkillAugmentSO, AugmentSkill>>(playerAugments, "ownedSkills");
    }

    private static void ClearFixtures()
    {
        if (fixtureRoot == null)
            return;
        for (int i = fixtureRoot.transform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(fixtureRoot.transform.GetChild(i).gameObject);
    }

    private static GameObject NewGameObject(string name, Vector3 position)
    {
        GameObject gameObject = new GameObject(name);
        gameObject.transform.SetParent(fixtureRoot.transform, true);
        gameObject.transform.position = position;
        return gameObject;
    }

    private static IEnumerator WaitForCondition(Func<bool> condition, string description, int maxFrames = 180)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            if (condition()) yield break;
            yield return new WaitForFixedUpdate();
            yield return null;
        }
        Require(condition(), "timed out: " + description);
    }

    private static IEnumerator WaitSeconds(float seconds)
    {
        float end = Time.time + seconds;
        while (Time.time < end)
            yield return null;
    }

    private static IEnumerator WaitSecondsUnscaled(float seconds)
    {
        float end = Time.unscaledTime + seconds;
        while (Time.unscaledTime < end)
            yield return null;
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = FindField(target.GetType(), name);
        Require(field != null, "reflection fixture field exists: " + target.GetType().Name + "." + name);
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string name)
    {
        FieldInfo field = FindField(target.GetType(), name);
        Require(field != null, "reflection field exists: " + target.GetType().Name + "." + name);
        return (T)field.GetValue(target);
    }

    private static FieldInfo FindField(Type type, string name)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("WIZARD_AUGMENT_FAIL: " + message);
        Debug.Log("WIZARD_AUGMENT_CHECK: " + message);
    }
}

internal sealed class WizardAugmentValidationRunner : MonoBehaviour
{
}
