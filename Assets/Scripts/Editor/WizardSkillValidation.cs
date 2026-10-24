using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class WizardSkillValidation
{
    private const string Pending = "WizardSkillValidation.Pending";
    private static GameObject fixtureRoot;
    private static bool finished;

    static WizardSkillValidation()
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
            var host = new GameObject("WizardSkillValidationRunner");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<WizardSkillValidationRunner>().StartCoroutine(ValidateRoutine());
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
        Debug.Log("WIZARD_SKILL_VALIDATION_PASS");
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
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                unexpectedLogs.Add(type + ": " + condition);
        };
        Application.logMessageReceived += logHandler;
        fixtureRoot = new GameObject("WizardSkillValidationFixtures");

        try
        {
            yield return null;
            TestSkillAreaQuery();
            ClearFixtures();
            yield return TestDamageProjectiles();
            ClearFixtures();
            yield return TestWizardSkills();

            Object.DestroyImmediate(fixtureRoot);
            fixtureRoot = null;
            yield return null;
            Require(unexpectedLogs.Count == 0,
                "unexpected Unity errors/exceptions: " + string.Join(" | ", unexpectedLogs));
        }
        finally
        {
            if (fixtureRoot != null)
            {
                Object.DestroyImmediate(fixtureRoot);
                fixtureRoot = null;
            }
            Application.logMessageReceived -= logHandler;
            Time.timeScale = 1f;
        }
    }

    private static void TestSkillAreaQuery()
    {
        SkillAreaQuery query = new SkillAreaQuery();
        EnemyHealth compound = CreateEnemy("AreaCompound", 100, new Vector3(100f, 0f, 0f), 2);
        EnemyHealth neighbor = CreateEnemy("AreaNeighbor", 100, new Vector3(101f, 0f, 0f));
        Physics.SyncTransforms();

        IReadOnlyList<EnemyHealth> collected = query.Collect(new Vector3(100.5f, 0f, 0f), 2f);
        Require(collected.Count == 2 && Contains(collected, compound) && Contains(collected, neighbor),
            "area query deduplicates compound hitboxes");

        EnemyHealth[] dense = new EnemyHealth[40];
        for (int i = 0; i < dense.Length; i++)
        {
            float x = (i % 8) * 0.3f;
            float z = (i / 8) * 0.3f;
            dense[i] = CreateEnemy("DenseArea" + i, 100, new Vector3(x, 0f, z));
        }
        Physics.SyncTransforms();
        collected = query.Collect(new Vector3(1.05f, 0f, 0.6f), 3f);
        Require(collected.Count == dense.Length,
            "area query returns every target beyond the initial 32-collider buffer (" + collected.Count + ")");
        for (int i = 0; i < dense.Length; i++)
            Require(Contains(collected, dense[i]), "dense area target " + i + " is retained");

        EnemyHealth lethal = CreateEnemy("AreaLethal", 13, new Vector3(100f, 0f, 10f), 2);
        PlayerDamageDealer dealer = CreateDealer("AreaDamageDealer", new Vector3(110f, 0f, 10f));
        int callbackCount = 0;
        int capturedDamage = 0;
        Vector3 capturedPosition = Vector3.zero;
        PlayerDamageType capturedType = PlayerDamageType.Skill;
        dealer.OnTargetDamaged += (enemy, position, actualDamage, damageType) =>
        {
            if (enemy != lethal) return;
            callbackCount++;
            capturedPosition = position;
            capturedDamage = actualDamage;
            capturedType = damageType;
        };
        Vector3 source = new Vector3(104f, 2f, 8f);
        query.DealDamage(dealer, lethal.transform.position, 2f, 50, PlayerDamageType.Skill, source);
        Require(lethal.IsDead && lethal.CurrentHealth == 0, "area damage goes through PlayerDamageDealer and kills target");
        Require(callbackCount == 1 && capturedDamage == 13 && capturedType == PlayerDamageType.Skill,
            "lethal damage callback reports once with health-clamped actual damage");
        Require((capturedPosition - new Vector3(100f, 0f, 10f)).sqrMagnitude < 0.001f,
            "lethal target callback captures hit position before death cleanup");
    }

    private static IEnumerator TestDamageProjectiles()
    {
        PlayerDamageDealer dealer = CreateDealer("ProjectileDealer", new Vector3(-20f, 0f, 0f));

        EnemyHealth first = CreateEnemy("DefaultProjectileFirst", 100, new Vector3(0f, 0f, 4f));
        EnemyHealth second = CreateEnemy("DefaultProjectileSecond", 100, new Vector3(0f, 0f, 7f));
        DamageProjectile ordinary = CreateProjectile("DefaultProjectile", new Vector3(0f, 0f, 0f));
        ordinary.Initialize(dealer, PlayerDamageType.Skill, 19, 20f, 12f, Vector3.forward);
        Physics.SyncTransforms();
        float startZ = ordinary.transform.position.z;
        yield return new WaitForFixedUpdate();
        yield return WaitForCondition(
            () => ordinary == null || ordinary.transform.position.z > startZ + 0.1f,
            "projectile advances through FixedUpdate physics before impact");
        yield return WaitForCondition(() => first.CurrentHealth < first.MaxHealth || ordinary == null,
            "default projectile reaches its first target");
        Require(first.CurrentHealth == first.MaxHealth - 19 && second.CurrentHealth == second.MaxHealth,
            "default projectile damages only its first target and does not pierce");

        EnemyHealth piercingFirst = CreateEnemy("PiercingProjectileFirst", 100, new Vector3(20f, 0f, 2f), 2);
        EnemyHealth piercingSecond = CreateEnemy("PiercingProjectileSecond", 100, new Vector3(20f, 0f, 4.5f), 2);
        EnemyHealth piercingThird = CreateEnemy("PiercingProjectileThird", 100, new Vector3(20f, 0f, 7f));
        PlayerDamageDealer piercingDealer = CreateDealer("PiercingProjectileDealer", new Vector3(40f, 0f, 0f));
        Dictionary<EnemyHealth, int> piercingHits = new Dictionary<EnemyHealth, int>();
        piercingDealer.OnTargetDamaged += (enemy, position, damage, type) =>
        {
            piercingHits.TryGetValue(enemy, out int count);
            piercingHits[enemy] = count + 1;
        };
        DamageProjectile piercing = CreateProjectile("PiercingProjectile", new Vector3(20f, 0f, 0f));
        piercing.Initialize(piercingDealer, PlayerDamageType.BasicAttack, 23, 20f, 12f,
            Vector3.forward, additionalPierces: 1);
        yield return WaitForCondition(() => piercing == null, "one-pierce projectile finishes at second target");
        Require(piercingFirst.CurrentHealth == 77 && piercingSecond.CurrentHealth == 77 &&
                piercingThird.CurrentHealth == 100,
            "one additional pierce hits exactly two distinct targets");
        Require(piercingHits.Count == 2 && piercingHits[piercingFirst] == 1 && piercingHits[piercingSecond] == 1,
            "piercing projectile hits compound colliders once per target");

        EnemyHealth blastDirect = CreateEnemy("BlastDirect", 100, new Vector3(40f, 0f, 2f), 2);
        EnemyHealth blastNearby = CreateEnemy("BlastNearby", 100, new Vector3(40f, 0f, 2.8f));
        EnemyHealth blastOutside = CreateEnemy("BlastOutside", 100, new Vector3(40f, 0f, 6f));
        PlayerDamageDealer blastDealer = CreateDealer("BlastProjectileDealer", new Vector3(60f, 0f, 0f));
        Dictionary<EnemyHealth, int> blastHits = new Dictionary<EnemyHealth, int>();
        blastDealer.OnTargetDamaged += (enemy, position, damage, type) =>
        {
            blastHits.TryGetValue(enemy, out int count);
            blastHits[enemy] = count + 1;
        };
        DamageProjectile blast = CreateProjectile("BlastProjectile", new Vector3(40f, 0f, 0f));
        blast.Initialize(blastDealer, PlayerDamageType.Skill, 17, 20f, 12f,
            Vector3.forward, blastRadius: 1.5f);
        yield return WaitForCondition(() => blast == null, "explosive projectile completes its flight");
        Require(blastDirect.CurrentHealth == 83 && blastNearby.CurrentHealth == 83 && blastOutside.CurrentHealth == 100,
            "existing projectile explosion damages direct and nearby targets only");
        Require(blastHits.Count == 2 && blastHits[blastDirect] == 1 && blastHits[blastNearby] == 1,
            "explosion deduplicates direct target and nearby compound colliders");
    }

    private static IEnumerator TestWizardSkills()
    {
        TestAssignedAssets();
        yield return TestBasicAttackCooldown();
        ClearFixtures();
        yield return TestGravityCore();
        ClearFixtures();
        yield return TestResonanceTargeting();
        ClearFixtures();
        yield return TestResonanceField();
        ClearFixtures();
        yield return TestOverdrive();
    }

    private static void TestAssignedAssets()
    {
        GameObject wizardPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Prefabs/Player/WizardPlayer.prefab");
        GameObject swordPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Prefabs/Player/SwordPlayer.prefab");
        GameObject ninjaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Prefabs/Player/NinjaPlayer.prefab");
        Require(wizardPrefab != null && swordPrefab != null && ninjaPrefab != null, "character prefabs exist");

        PlayerSkillController wizardSkills = wizardPrefab.GetComponent<PlayerSkillController>();
        PlayerSkillController swordSkills = swordPrefab.GetComponent<PlayerSkillController>();
        PlayerSkillController ninjaSkills = ninjaPrefab.GetComponent<PlayerSkillController>();
        Require(wizardSkills != null && swordSkills != null && ninjaSkills != null, "character skill controllers exist");

        WizardSkillSO wizardQ = wizardSkills.SkillQ as WizardSkillSO;
        WizardSkillSO wizardE = wizardSkills.SkillE as WizardSkillSO;
        WizardSkillSO wizardR = FindSerializedSkill(wizardSkills, "ultimate") as WizardSkillSO;
        Require(wizardQ != null && wizardQ.Kind == WizardSkillKind.GravityCore, "wizard Q is gravity core");
        Require(Mathf.Approximately(wizardQ.GravityCorePullDuration, 1.5f),
            "wizard Q has a configurable 1.5-second base pull duration");
        Require(wizardE != null && wizardE.Kind == WizardSkillKind.ResonanceField, "wizard E is resonance field");
        Require(wizardR != null && wizardR.Kind == WizardSkillKind.Overdrive, "wizard R is overdrive");
        PlayerStats wizardStats = wizardPrefab.GetComponent<PlayerStats>();
        Require(wizardStats != null && Mathf.Approximately(wizardStats.baseAttackSpeed, 1f),
            "wizard prefab base attack speed is 1");
        TestWizardAttackAnimation(wizardPrefab);
        Require(wizardPrefab.GetComponent<WizardCombat>() != null, "wizard prefab has WizardCombat");
        ValidatePackReferences(wizardPrefab.GetComponent<WizardCombat>());
        Require(FindSerializedObject(wizardPrefab.GetComponent<ProjectilePlayerAttack>(), "projectilePrefab") != null,
            "wizard basic attack still uses a projectile prefab");

        Require(swordSkills.SkillE is ProjectileSkillSO, "sword E remains ProjectileSkillSO");
        Require(!(ninjaSkills.SkillE is WizardSkillSO) && !(ninjaSkills.SkillQ is WizardSkillSO),
            "ninja skills are unchanged");
    }

    private static readonly string[] PackFields = {
        "gravityCorePrefab", "gravityVortexPrefab", "starHitPrefab", "resonanceFieldPrefab",
        "overdriveAuraPrefab", "overdriveWavePrefab", "overdriveFinalPrefab", "resonanceHitPrefab"
    };
    private static readonly string[] PackPaths = {
        "GravityCore.prefab", "GravityVortex.prefab",
        "GravityImpact.prefab", "ResonanceField.prefab",
        "OverdriveAura.prefab", "OverdriveWave.prefab",
        "OverdriveFinal.prefab", "ResonanceImpact.prefab"
    };

    private static void CopyPackReferences(WizardCombat combat)
    {
        for (int i = 0; i < PackFields.Length; i++)
            SetField(combat, PackFields[i], AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/SFX_VFX/Wizard/" + PackPaths[i]));
    }

    private static void ValidatePackReferences(WizardCombat combat)
    {
        for (int i = 0; i < PackFields.Length; i++)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/SFX_VFX/Wizard/" + PackPaths[i]);
            Require(prefab != null && FindSerializedObject(combat, PackFields[i]) == prefab,
                "wizard uses selected pack prefab: " + PackPaths[i]);
            GameObject instance = Object.Instantiate(prefab);
            try
            {
                Dictionary<Material, Color> authoredColors = new Dictionary<Material, Color>();
                foreach (ParticleSystemRenderer renderer in instance.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    Material material = renderer.sharedMaterial;
                    if (material != null && material.HasProperty("_BaseColor"))
                        authoredColors[material] = material.GetColor("_BaseColor");
                }
                bool continuous = i == 0 || i == 1 || i == 3 || i == 4;
                var firstParticle = instance.GetComponentInChildren<ParticleSystem>();
                var authoredStartColor = firstParticle.main.startColor;
                float authoredLifetime = firstParticle.main.startLifetimeMultiplier;
                float authoredEmission = firstParticle.emission.rateOverTimeMultiplier;
                instance.GetComponent<WizardSkillVisual>().Initialize(continuous, i == 4);
                Require(firstParticle.main.startColor.mode == authoredStartColor.mode &&
                        firstParticle.main.startColor.Evaluate(0f) == authoredStartColor.Evaluate(0f) &&
                        firstParticle.main.startColor.Evaluate(0.5f) == authoredStartColor.Evaluate(0.5f) &&
                        firstParticle.main.startColor.Evaluate(1f) == authoredStartColor.Evaluate(1f) &&
                        firstParticle.main.startLifetimeMultiplier == authoredLifetime &&
                        firstParticle.emission.rateOverTimeMultiplier == authoredEmission,
                    "runtime playback preserves inspector-authored particle appearance");
                foreach (ParticleSystemRenderer renderer in instance.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    Material material = renderer.sharedMaterial;
                    if (material == null || !authoredColors.TryGetValue(material, out Color authored))
                        continue;
                    MaterialPropertyBlock block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block);
                    Require(!block.HasProperty("_BaseColor") && !block.HasProperty("_Color"),
                        "runtime playback does not override inspector-authored material values");
                    Require(material.GetColor("_BaseColor") == authored,
                        "pack recoloring does not mutate shared materials");
                }
                ParticleSystem[] particles = instance.GetComponentsInChildren<ParticleSystem>(true);
                Require(particles.Length > 0, "pack effect contains particles: " + PackPaths[i]);
                foreach (ParticleSystem particle in particles)
                {
                    Require(particle.main.loop == continuous,
                        "all child particle modes match effect lifetime: " + PackPaths[i]);
                    Require(particle.main.scalingMode == ParticleSystemScalingMode.Hierarchy,
                        "pack effect respects gameplay radius scaling");
                    if (i == 4)
                        Require(particle.main.simulationSpace == ParticleSystemSimulationSpace.Local,
                            "R aura particles follow their owner locally");
                    if (i == 1 && particle.transform != instance.transform)
                        Require(particle.velocityOverLifetime.enabled &&
                                particle.velocityOverLifetime.radial.constant < 0f,
                            "gravity vortex wisps flow inward while root smoke stays faint");
                }
            }
            finally { Object.DestroyImmediate(instance); }
        }
    }

    private static void TestWizardAttackAnimation(GameObject wizardPrefab)
    {
        Animator animator = wizardPrefab.GetComponentInChildren<Animator>(true);
        AnimatorController controller = animator != null
            ? animator.runtimeAnimatorController as AnimatorController
            : null;
        Require(controller != null && controller.layers.Length > 0,
            "wizard prefab uses its AnimatorController asset");

        AnimatorControllerParameter attackSpeed = null;
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == "AttackSpeed")
            {
                attackSpeed = parameter;
                break;
            }
        }
        Require(attackSpeed != null && attackSpeed.type == AnimatorControllerParameterType.Float &&
                Mathf.Approximately(attackSpeed.defaultFloat, 1f),
            "wizard AttackSpeed animator parameter is a float defaulting to 1");

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        AnimatorState attackState = null;
        foreach (ChildAnimatorState childState in stateMachine.states)
        {
            if (childState.state.name == "Attack")
            {
                attackState = childState.state;
                break;
            }
        }
        Require(attackState != null && attackState.speedParameterActive &&
                attackState.speedParameter == "AttackSpeed" && Mathf.Approximately(attackState.speed, 1f),
            "wizard Attack state maps AttackSpeed to its playback rate");

        AnimationClip attackClip = attackState.motion as AnimationClip;
        Require(attackClip != null && attackClip.name == "Attack01" && attackClip.length > 0f,
            "wizard Attack state uses the imported Attack01 clip");

        AnimatorStateTransition attackEntry = null;
        foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
        {
            if (transition.destinationState != attackState)
                continue;

            foreach (AnimatorCondition condition in transition.conditions)
            {
                if (condition.parameter == "Attack" && condition.mode == AnimatorConditionMode.If)
                {
                    attackEntry = transition;
                    break;
                }
            }
        }
        Require(attackEntry != null && !attackEntry.hasExitTime && attackEntry.hasFixedDuration &&
                Mathf.Approximately(attackEntry.duration, 0.05f),
            "wizard attack enters on its trigger with a fixed 0.05-second blend");

        AnimatorStateTransition attackExit = null;
        foreach (AnimatorStateTransition transition in attackState.transitions)
        {
            if (transition.hasExitTime)
            {
                attackExit = transition;
                break;
            }
        }
        Require(attackExit != null && !attackExit.hasFixedDuration &&
                Mathf.Approximately(attackExit.exitTime, 0.95f) &&
                Mathf.Approximately(attackExit.duration, 0.05f),
            "wizard attack unlocks after 0.95 normalized time plus a 0.05 normalized exit blend");
        Debug.Log("WIZARD_ATTACK_ANIMATION_AUDIT: clip=" + attackClip.name +
                  ", length=" + attackClip.length.ToString("F3") + "s, frameRate=" +
                  attackClip.frameRate.ToString("F1") + ", speedParameter=" + attackState.speedParameter +
                  ", entryBlend=" + attackEntry.duration.ToString("F2") + "s, exit=" +
                  attackExit.exitTime.ToString("F2") + " normalized + " +
                  attackExit.duration.ToString("F2") + " normalized");
    }

    private static IEnumerator TestBasicAttackCooldown()
    {
        yield return TestBasicAttackCooldownAtSpeed(1f);
        ClearFixtures();
        yield return TestBasicAttackCooldownAtSpeed(2f);
    }

    private static IEnumerator TestBasicAttackCooldownAtSpeed(float attackSpeed)
    {
        GameObject player = CreateWizardPlayer("BasicAttackSpeed" + attackSpeed, Vector3.zero);
        PlayerStats stats = player.GetComponent<PlayerStats>();
        stats.baseAttackSpeed = attackSpeed;
        ProjectilePlayerAttack attack = player.GetComponent<ProjectilePlayerAttack>();
        SetField(attack, "maxDistance", 0.1f);
        float cooldown = 1f / attackSpeed;

        Require(attack.TryAttack(), "basic attack fires at attack speed " + attackSpeed);
        Require(!attack.TryAttack(), "basic attack cooldown blocks an immediate repeat at attack speed " + attackSpeed);
        yield return WaitSeconds(cooldown * 0.6f);
        Require(!attack.TryAttack(), "basic attack cooldown remains active before " + cooldown + " seconds");
        yield return WaitSeconds(cooldown * 0.5f);
        Require(attack.TryAttack(), "basic attack cooldown expires at attack speed " + attackSpeed);
        Require(!attack.TryAttack(), "basic attack restarts its cooldown at attack speed " + attackSpeed);
    }

    private static IEnumerator TestGravityCore()
    {
        BakeNavMesh();
        GameObject player = CreateWizardPlayer("GravityWizard", Vector3.zero);
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        WizardSkillSO gravity = skills.SkillQ as WizardSkillSO;
        Require(gravity != null, "gravity skill assigned");

        EnemyHealth firstStop = CreateNavEnemy("GravityFirstStop", 400, new Vector3(0f, 0f, 4f));
        EnemyHealth pulled = CreateNavEnemy("GravityPulled", 400, new Vector3(2.2f, 0f, 4f));
        yield return null;
        Require(InvokeTryCast(skills, gravity, false), "cast gravity at first enemy");
        yield return WaitForCondition(() => firstStop.CurrentHealth < firstStop.MaxHealth, "gravity blast hits first enemy", 240);
        Require(pulled.transform.position.x < 1.6f, "gravity pulls nearby ordinary enemy");
        Require(skills.QRemaining > 0f, "successful Q consumes cooldown");
        yield return TestGravityCorePullSmoothness(gravity);

        GameObject triggerPlayer = CreateWizardPlayer("GravityTriggerWizard", new Vector3(10f, 0f, 0f));
        PlayerSkillController triggerSkills = triggerPlayer.GetComponent<PlayerSkillController>();
        GameObject decoy = GameObject.CreatePrimitive(PrimitiveType.Cube);
        decoy.name = "GravityTriggerIgnore";
        decoy.transform.SetParent(fixtureRoot.transform, true);
        decoy.transform.position = new Vector3(10f, 1.1f, 2.2f);
        decoy.transform.localScale = new Vector3(1.2f, 1.2f, 0.3f);
        decoy.GetComponent<Collider>().isTrigger = true;
        EnemyHealth behindTrigger = CreateNavEnemy("GravityBehindTrigger", 400, new Vector3(10f, 0f, 4.5f));
        yield return null;
        Require(InvokeTryCast(triggerSkills, gravity, false), "cast gravity through a non-enemy trigger");
        yield return WaitForCondition(() => behindTrigger.CurrentHealth < behindTrigger.MaxHealth,
            "gravity ignores non-enemy triggers and still hits the enemy behind them", 240);

        GameObject wallPlayer = CreateWizardPlayer("GravityWallWizard", new Vector3(20f, 0f, 0f));
        PlayerSkillController wallSkills = wallPlayer.GetComponent<PlayerSkillController>();
        CreateSolidBox("GravityWall", new Vector3(20f, 1f, 3f), new Vector3(4f, 2f, 0.4f));
        EnemyHealth behindWall = CreateNavEnemy("GravityBehindWall", 400, new Vector3(20f, 0f, 6f));
        EnemyHealth distantWallEnemy = CreateNavEnemy("GravityDistantWall", 400, new Vector3(20f, 0f, 9f));
        int behindHealth = distantWallEnemy.CurrentHealth;
        yield return null;
        Require(InvokeTryCast(wallSkills, gravity, false), "cast gravity into a wall");
        yield return WaitSeconds(gravity.GravityCorePullDuration + 0.5f);
        Require(distantWallEnemy.CurrentHealth == behindHealth, "gravity stopped by wall does not blast a distant enemy");
        Require(behindWall.transform.position.z >= 3.6f,
            "gravity does not pull an enemy through a wall");

        GameObject maxPlayer = CreateWizardPlayer("GravityMaxWizard", new Vector3(40f, 0f, 0f));
        PlayerSkillController maxSkills = maxPlayer.GetComponent<PlayerSkillController>();
        EnemyHealth farEnemy = CreateNavEnemy("GravityFar", 400, new Vector3(40f, 0f,
            gravity.GravityCoreRange + gravity.GravityCorePullRadius + 5f));
        int farHealth = farEnemy.CurrentHealth;
        yield return null;
        Require(InvokeTryCast(maxSkills, gravity, false), "cast gravity at empty max range");
        yield return WaitSeconds(gravity.GravityCoreRange / gravity.GravityCoreSpeed +
                                  gravity.GravityCorePullDuration + 0.5f);
        Require(farEnemy.CurrentHealth == farHealth, "max-range gravity does not hit an out-of-range enemy");

        GameObject closePlayer = CreateWizardPlayer("GravityCloseWizard", new Vector3(60f, 0f, 0f));
        PlayerSkillController closeSkills = closePlayer.GetComponent<PlayerSkillController>();
        CreateSolidBox("GravityCloseWall", new Vector3(60f, 1.1f, 0.8f), new Vector3(2f, 2.2f, 0.4f));
        EnemyHealth closeBehind = CreateNavEnemy("GravityCloseBehind", 400, new Vector3(60f, 0f, 9f));
        int closeHealth = closeBehind.CurrentHealth;
        yield return null;
        Require(InvokeTryCast(closeSkills, gravity, false), "cast gravity into an immediately overlapping wall");
        yield return WaitSeconds(gravity.GravityCorePullDuration + 0.5f);
        Require(closeBehind.CurrentHealth == closeHealth, "overlapping wall stops the core before it travels");

        GameObject lowPlayer = CreateWizardPlayer("GravityLowWizard", new Vector3(80f, 0f, 0f));
        PlayerSkillController lowSkills = lowPlayer.GetComponent<PlayerSkillController>();
        EnemyHealth lowStop = CreateNavEnemy("GravityLowStop", 400, new Vector3(80f, 0f, 3f));
        CreateSolidBox("GravityLowObstacle", new Vector3(80f, 0.2f, 4.5f), new Vector3(2f, 0.4f, 0.6f));
        EnemyHealth lowEnemy = CreateNavEnemy("GravityLowEnemy", 400, new Vector3(80f, 0f, 6.5f), 2f, 0.5f);
        Vector3 lowStart = lowEnemy.transform.position;
        yield return null;
        Require(InvokeTryCast(lowSkills, gravity, false), "cast gravity that would pull over a low obstacle");
        yield return WaitForCondition(() => lowStop.CurrentHealth < lowStop.MaxHealth,
            "gravity stops on the near enemy before the low obstacle", 240);
        Require(lowEnemy.transform.position.z < lowStart.z - 0.1f,
            "low-obstacle target is exposed to the pull");
        Require(lowEnemy.transform.position.z >= 5.15f,
            "full-height capsule sweep blocks a low obstacle");

        GameObject bossPlayer = CreateWizardPlayer("GravityBossWizard", new Vector3(100f, 0f, 0f));
        PlayerSkillController bossSkills = bossPlayer.GetComponent<PlayerSkillController>();
        EnemyHealth boss = CreateNavEnemy("GravityBoss", 400, new Vector3(100f, 0f, 4f));
        boss.gameObject.AddComponent<BossEnemyAttack>();
        Vector3 bossStart = boss.transform.position;
        yield return null;
        Require(InvokeTryCast(bossSkills, gravity, false), "cast gravity near a boss");
        yield return WaitForCondition(() => boss.CurrentHealth < boss.MaxHealth, "gravity still damages a boss", 240);
        Require((boss.transform.position - bossStart).sqrMagnitude < 0.04f, "bosses are not pulled");
    }

    private static IEnumerator TestGravityCorePullSmoothness(WizardSkillSO gravity)
    {
        GameObject player = CreateWizardPlayer("GravitySmoothWizard", new Vector3(110f, 0f, 0f));
        WizardCombat combat = player.GetComponent<WizardCombat>();
        WizardSkillSO adjustableSkill = Object.Instantiate(gravity);
        SetField(adjustableSkill, "gravityCorePullDuration", 2f);
        Require(Mathf.Approximately(adjustableSkill.GravityCorePullDuration, 2f),
            "gravity pull duration can be adjusted through the existing skill asset");

        CreateNavEnemy("GravitySmoothStop", 400, new Vector3(110f, 0f, 4f));
        EnemyHealth pulled = CreateNavEnemy("GravitySmoothPulled", 400, new Vector3(113.5f, 0f, 4f));
        NavMeshAgent pulledAgent = pulled.GetComponent<NavMeshAgent>();
        EnemyHealth incoming = CreateNavEnemy("GravityLateEntrant", 400, new Vector3(105f, 0f, 4f));
        NavMeshAgent incomingAgent = incoming.GetComponent<NavMeshAgent>();
        yield return null;

        Require(combat.TryCast(adjustableSkill), "cast gravity with an adjusted pull duration");
        yield return WaitForCondition(() => pulledAgent.isStopped, "gravity acquires the initial pull target");
        float pullStartedAt = Time.time;
        yield return ValidateSmoothPull(pulledAgent, adjustableSkill.GravityCorePullSpeed, 0.25f,
            "initial target");

        while (Time.time < pullStartedAt + 1.2f)
            yield return null;
        Require(Vector3.Distance(incoming.transform.position, new Vector3(110f, 0f, 2.5f)) >
                adjustableSkill.GravityCorePullRadius,
            "late entrant begins outside the gravity pull radius");
        Require(incomingAgent.Warp(new Vector3(107.5f, 0f, 4f)),
            "late entrant crosses into the gravity pull radius on the NavMesh");
        Physics.SyncTransforms();
        yield return WaitForCondition(() => incomingAgent.isStopped,
            "gravity continuously acquires targets that enter its radius");
        yield return ValidateSmoothPull(incomingAgent, adjustableSkill.GravityCorePullSpeed, 0.15f,
            "late entrant");

        while (Time.time < pullStartedAt + 1.6f)
            yield return null;
        Require(pulledAgent.isStopped && incomingAgent.isStopped,
            "adjusted 2-second pull continues beyond the 1.5-second base duration");
        while (Time.time < pullStartedAt + 2.3f)
            yield return null;
        Require(!pulledAgent.isStopped && !incomingAgent.isStopped,
            "gravity releases both original and late targets after its configured duration");
        yield return WaitSeconds(0.3f);
        Object.Destroy(adjustableSkill);
    }

    private static IEnumerator ValidateSmoothPull(NavMeshAgent agent, float pullSpeed, float duration,
        string targetDescription)
    {
        Vector3 previousPosition = agent.transform.position;
        float elapsed = 0f;
        float totalMovement = 0f;
        bool frameBounded = true;
        while (elapsed < duration)
        {
            yield return null;
            float deltaTime = Time.deltaTime;
            Vector3 position = agent.transform.position;
            float movement = Vector3.Distance(position, previousPosition);
            frameBounded &= movement <= pullSpeed * deltaTime + 0.05f;
            totalMovement += movement;
            previousPosition = position;
            elapsed += deltaTime;
        }

        Require(frameBounded,
            "gravity pull moves the " + targetDescription + " without a frame-sized teleport");
        Require(totalMovement > 0.1f,
            "gravity smoothly pulls the " + targetDescription + " over multiple frames");
    }

    private static IEnumerator TestResonanceTargeting()
    {
        GameObject player = CreateWizardPlayer("PreviewWizard", new Vector3(0f, 0f, 40f));
        PlayerInputHandler input = player.AddComponent<PlayerInputHandler>();
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        WizardCombat combat = player.GetComponent<WizardCombat>();
        WizardSkillSO skill = (WizardSkillSO)skills.SkillE;
        Camera camera = CreateAimCamera("PreviewCamera", new Vector3(0f, 8f, 44f),
            new Vector3(0f, 0f, 44f));
        CreateGround("PreviewGround", new Vector3(0f, 0f, 46f), new Vector3(40f, 0.2f, 40f));
        EnemyHealth enemy = CreateEnemy("PreviewEnemy", 400, new Vector3(0f, 0.5f, 44f));
        Physics.SyncTransforms();
        yield return null;

        int charges = skills.ECharges;
        Require(skills.TryBeginWizardResonancePreview(), "first E starts targeting without casting");
        RefreshPreview(combat);
        Require(combat.ResonancePreviewIsValid, "ground inside E range is a valid preview");
        Require(skills.ECharges == charges && skills.ERemaining == 0f,
            "beginning E preview consumes neither charge nor cooldown");
        Require(!skills.IsMovementLocked && skills.ShouldSuppressBasicAttack,
            "preview allows movement while blocking basic attack");
        LineRenderer range = GameObject.Find("WizardResonancePreviewRange").GetComponent<LineRenderer>();
        LineRenderer field = GameObject.Find("WizardResonancePreviewField").GetComponent<LineRenderer>();
        Require(range != null && field != null, "E preview shows maximum range and placement radius");
        Vector3 rangePoint = PreviewWorldPoint(range, 0);
        Vector3 rangeOffset = rangePoint - player.transform.position;
        rangeOffset.y = 0f;
        Require(Mathf.Abs(rangeOffset.magnitude - skill.ResonanceFieldRange) < 0.1f,
            "maximum E ring matches configured cast range");
        bool aboveGround = true;
        for (int i = 0; i < range.positionCount; i++)
            aboveGround &= PreviewWorldPoint(range, i).y > 0.1f;
        Require(aboveGround, "maximum range ring is drawn above the ground rather than buried");
        CreateSolidBox("PreviewOverhead", new Vector3(rangePoint.x, 5f, rangePoint.z),
            new Vector3(2f, 0.2f, 2f));
        Physics.SyncTransforms();
        RefreshPreview(combat);
        Require(PreviewWorldPoint(range, 0).y < 0.2f,
            "range preview stays on nearby ground instead of jumping onto an overhead roof");
        yield return WaitSeconds(0.15f);
        Require(enemy.CurrentHealth == enemy.MaxHealth && GameObject.Find("WizardResonanceField") == null,
            "preview never starts field ticks or damages enemies");
        camera.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
        RefreshPreview(combat);
        Require(!combat.ResonancePreviewIsValid && !skills.TryConfirmWizardResonancePreview() &&
            skills.ECharges == charges, "aiming into the sky cannot place or consume E");

        player.transform.position += Vector3.right;
        camera.transform.position = new Vector3(3f, 8f, 44f);
        camera.transform.LookAt(new Vector3(3f, 0f, 44f));
        Physics.SyncTransforms();
        RefreshPreview(combat);
        Vector3 markerCenter = PreviewRingCenter(field);
        Require(Mathf.Abs(markerCenter.x - 3f) < 0.1f && Mathf.Abs(markerCenter.z - 44f) < 0.1f,
            "aiming moves E placement marker to the intended ground");
        Vector3 fieldOffset = PreviewWorldPoint(field, 0) - markerCenter;
        fieldOffset.y = 0f;
        Require(Mathf.Abs(fieldOffset.magnitude - skill.ResonanceFieldRadius) < 0.1f,
            "placement ring matches actual E field radius");
        Vector3 movedRange = PreviewWorldPoint(range, 0) - player.transform.position;
        movedRange.y = 0f;
        Require(Mathf.Abs(movedRange.magnitude - skill.ResonanceFieldRange) < 0.1f,
            "maximum range ring follows player movement");

        camera.transform.position = new Vector3(0f, 8f, 58f);
        camera.transform.LookAt(new Vector3(0f, 0f, 58f));
        RefreshPreview(combat);
        Require(!combat.ResonancePreviewIsValid, "out-of-range ground is an invalid preview");
        Require(field.startColor.r > field.startColor.g && field.startColor.r > field.startColor.b,
            "invalid placement is shown in red");
        Require(!skills.TryConfirmWizardResonancePreview() && skills.ECharges == charges,
            "invalid confirmation does not spend E charge");
        camera.transform.position = new Vector3(3f, 8f, 44f);
        camera.transform.LookAt(new Vector3(3f, 0f, 44f));
        RefreshPreview(combat);
        markerCenter = PreviewRingCenter(field);
        yield return null;
        input.AttackTriggered = true;
        Require(skills.TryConfirmWizardResonancePreview(), "valid preview confirmation places E");
        Require(skills.ECharges == charges - 1 && skills.ERechargeRemaining > 0f,
            "only successful preview confirmation consumes E charge");
        Require(!input.AttackTriggered, "confirmation clears queued basic attack input");
        input.AttackTriggered = true;
        Require(skills.ShouldSuppressBasicAttack, "confirmation blocks late same-frame attack input");
        input.AttackTriggered = false;
        GameObject placed = GameObject.Find("WizardResonanceField");
        Require(placed != null && Mathf.Abs(placed.transform.position.x - markerCenter.x) < 0.1f &&
            Mathf.Abs(placed.transform.position.z - markerCenter.z) < 0.1f,
            "confirmed field is placed at the displayed position");
        yield return null;
        Require(range == null && field == null, "confirmation removes both preview rings");
        Require(!skills.TryBeginWizardResonancePreview(), "empty E charge cannot open targeting");

        SetField(skills, "eCharges", skills.EMaxCharges);
        SetField(skills, "ePendingRechargeCharges", 0);
        SetField(skills, "eNextRechargeTime", 0f);
        Require(skills.TryBeginWizardResonancePreview(), "charged E can preview a replacement");
        camera.transform.position = new Vector3(0f, 8f, 58f);
        camera.transform.LookAt(new Vector3(0f, 0f, 58f));
        RefreshPreview(combat);
        Require(!skills.TryConfirmWizardResonancePreview() && placed != null &&
            GameObject.Find("WizardResonanceField") == placed,
            "invalid replacement leaves the previous E field intact");
        skills.CancelWizardResonancePreview();
        camera.transform.position = new Vector3(3f, 8f, 44f);
        camera.transform.LookAt(new Vector3(3f, 0f, 44f));
        Require(skills.ECharges == skills.EMaxCharges && placed != null,
            "cancel preserves charge and the already placed field");
        input.AttackTriggered = true;
        Require(skills.ShouldSuppressBasicAttack, "cancel blocks same-frame attack leakage");
        input.AttackTriggered = false;
        yield return null;
        Require(GameObject.Find("WizardResonancePreviewRange") == null,
            "cancel removes range preview");

        Require(skills.TryBeginWizardResonancePreview(), "start preview before pause");
        Time.timeScale = 0f;
        yield return null;
        Time.timeScale = 1f;
        yield return null;
        Require(GameObject.Find("WizardResonancePreviewRange") == null,
            "pause cancels targeting without spending E");
        Require(skills.ECharges == skills.EMaxCharges, "paused targeting preserves E charge");
        Require(skills.TryBeginWizardResonancePreview(), "start preview before charge loss");
        SetField(skills, "eCharges", 0);
        RefreshPreview(combat);
        Require(!skills.IsWizardResonancePreviewActive, "losing E charge cancels targeting");
        SetField(skills, "eCharges", skills.EMaxCharges);
        Require(skills.TryBeginWizardResonancePreview(), "start preview before skill replacement");
        SetField(skills, "skillE", null);
        RefreshPreview(combat);
        Require(!combat.IsResonancePreviewActive, "replacing E skill cancels its stale preview");
        SetField(skills, "skillE", skill);
        Require(skills.TryBeginWizardResonancePreview(), "start preview before R input");
        skills.TryUseUltimate();
        Require(!combat.IsResonancePreviewActive && skills.ECharges == skills.EMaxCharges,
            "R input cancels E preview even when ultimate is unavailable");
        Require(skills.TryBeginWizardResonancePreview(), "start preview before disable");
        combat.enabled = false;
        yield return null;
        Require(GameObject.Find("WizardResonancePreviewRange") == null &&
            GameObject.Find("WizardResonancePreviewField") == null, "disable cleans preview rings");
        combat.enabled = true;
        yield return null;
        Require(skills.TryBeginWizardResonancePreview(), "start preview before death");
        player.GetComponent<PlayerStateManager>().ChangeState(PlayerState.Dead);
        yield return null;
        Require(GameObject.Find("WizardResonancePreviewRange") == null,
            "death cancels targeting without a lingering range ring");
    }

    private static void RefreshPreview(WizardCombat combat)
    {
        typeof(WizardCombat).GetMethod("UpdateResonancePreview",
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(combat, null);
    }

    private static Vector3 PreviewWorldPoint(LineRenderer renderer, int index)
    {
        Vector3 point = renderer.GetPosition(index);
        return renderer.useWorldSpace ? point : renderer.transform.TransformPoint(point);
    }

    private static Vector3 PreviewRingCenter(LineRenderer renderer)
    {
        Vector3 center = Vector3.zero;
        for (int i = 0; i < renderer.positionCount; i++)
            center += PreviewWorldPoint(renderer, i);
        return center / renderer.positionCount;
    }

    private static IEnumerator TestResonanceField()
    {
        GameObject player = CreateWizardPlayer("ResonanceWizard", new Vector3(0f, 0f, 40f));
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        PlayerDamageDealer dealer = player.GetComponent<PlayerDamageDealer>();
        WizardSkillSO fieldSkill = skills.SkillE as WizardSkillSO;
        Require(fieldSkill != null, "resonance skill assigned");
        Camera aimCamera = CreateAimCamera("ResonanceCamera", player.transform.position + new Vector3(0f, 8f, 4f),
            new Vector3(0f, 0f, 44f));

        CreateGround("ResonanceGround", new Vector3(0f, 0f, 44f), new Vector3(20f, 0.2f, 20f));
        EnemyHealth inside = CreateEnemy("ResonanceInside", 400, new Vector3(0f, 0.5f, 44f));
        EnemyHealth outside = CreateEnemy("ResonanceOutside", 400, new Vector3(8f, 0.5f, 44f));
        Physics.SyncTransforms();
        yield return null;
        Require(InvokeTryCast(skills, fieldSkill, true), "place a resonance field on valid ground");
        Require(skills.ECharges == 0, "successful E consumes a charge");
        yield return WaitForCondition(() => inside.CurrentHealth < inside.MaxHealth, "field tick damages an inside enemy");
        Require(outside.CurrentHealth == outside.MaxHealth, "field tick does not damage an out-of-radius enemy");

        int afterTick = inside.CurrentHealth;
        dealer.DealDamage(inside, 5, PlayerDamageType.BasicAttack, inside.transform.position);
        Require(inside.CurrentHealth == afterTick - 5 - 6, "basic hit in the field adds resonance splash");
        int afterResonance = inside.CurrentHealth;
        dealer.DealDamage(inside, 5, PlayerDamageType.BasicAttack, inside.transform.position);
        Require(inside.CurrentHealth == afterResonance - 5, "shared resonance cooldown blocks extra splashes");
        afterResonance = inside.CurrentHealth;
        dealer.DealDamage(inside, 5, PlayerDamageType.Skill, inside.transform.position);
        Require(inside.CurrentHealth == afterResonance - 5, "skill hits do not trigger resonance");
        yield return WaitSeconds(0.4f);

        EnemyHealth lethal = CreateEnemy("ResonanceLethal", 3, new Vector3(0.4f, 0.5f, 44f));
        bool pausedDuringHit = false;
        Action<int, PlayerDamageType> pauseOnBasicHit = (damage, type) =>
        {
            if (type != PlayerDamageType.BasicAttack) return;
            Time.timeScale = 0f;
            pausedDuringHit = true;
        };
        dealer.OnDamageDealt += pauseOnBasicHit;
        dealer.DealDamage(lethal, 3, PlayerDamageType.BasicAttack, lethal.transform.position);
        dealer.OnDamageDealt -= pauseOnBasicHit;
        Require(lethal.IsDead, "lethal basic hit still occurs");
        Require(pausedDuringHit, "level-up pause can happen during a lethal basic hit");
        Require(GameObject.Find("WizardResonanceBurst") != null,
            "lethal basic hit still produces a resonance effect");
        Time.timeScale = 1f;

        EnemyHealth highEnemy = CreateEnemy("ResonanceHigh", 400, new Vector3(0f, 8f, 44f));
        int highHealth = highEnemy.CurrentHealth;
        int insideBeforeHigh = inside.CurrentHealth;
        dealer.DealDamage(highEnemy, 5, PlayerDamageType.BasicAttack, highEnemy.transform.position);
        Require(highEnemy.CurrentHealth == highHealth - 5 && inside.CurrentHealth == insideBeforeHigh,
            "vertical hits outside the field radius do not resonate");

        GameObject firstField = GameObject.Find("WizardResonanceField");
        Require(firstField != null, "resonance field visual exists after placement");
        RequireParticleMode(firstField, true);
        Require(InvokeTryCast(skills, fieldSkill, true) == false, "spent E charge cannot replace the field yet");
        SetField(skills, "eCharges", skills.EMaxCharges);
        SetField(skills, "ePendingRechargeCharges", 0);
        SetField(skills, "eNextRechargeTime", 0f);
        aimCamera.transform.position = new Vector3(4f, 8f, 44f);
        aimCamera.transform.LookAt(new Vector3(4f, 0f, 44f));
        Physics.SyncTransforms();
        yield return null;
        Require(InvokeTryCast(skills, fieldSkill, true), "recast replaces the previous field");
        yield return null;
        Require(firstField == null, "previous resonance field visual is destroyed");
        Require(GameObject.Find("WizardResonanceField") != null, "replacement field visual exists");

        GameObject missPlayer = CreateWizardPlayer("ResonanceMissWizard", new Vector3(-16f, 0f, 40f));
        PlayerSkillController missSkills = missPlayer.GetComponent<PlayerSkillController>();
        Camera missCamera = CreateAimCamera("ResonanceMissCamera",
            missPlayer.transform.position + new Vector3(0f, 8f, 30f),
            missPlayer.transform.position + Vector3.forward * 40f);
        int missCharges = missSkills.ECharges;
        float missRemaining = missSkills.ERemaining;
        Physics.SyncTransforms();
        yield return null;
        Require(!InvokeTryCast(missSkills, fieldSkill, true), "invalid aim does not place a field");
        Require(missSkills.ECharges == missCharges && Mathf.Abs(missSkills.ERemaining - missRemaining) < 0.05f,
            "failed E placement does not consume cooldown");

        Object.Destroy(aimCamera.gameObject);
        Object.Destroy(missCamera.gameObject);
    }

    private static IEnumerator TestOverdrive()
    {
        GameObject player = CreateWizardPlayer("OverdriveWizard", new Vector3(0f, 0f, 80f));
        PlayerSkillController skills = player.GetComponent<PlayerSkillController>();
        PlayerUltimateGauge gauge = player.GetComponent<PlayerUltimateGauge>();
        WizardCombat combat = player.GetComponent<WizardCombat>();
        ProjectilePlayerAttack attack = player.GetComponent<ProjectilePlayerAttack>();
        WizardSkillSO overdrive = FindSerializedSkill(skills, "ultimate") as WizardSkillSO;
        Require(overdrive != null, "overdrive skill assigned");
        CreateGround("OverdriveGround", new Vector3(0f, 0f, 80f), new Vector3(20f, 0.2f, 20f));
        EnemyHealth waveTarget = CreateEnemy("OverdriveWave", 400, new Vector3(0f, 0.5f, 81f));
        EnemyHealth pierceFirst = CreateEnemy("OverdrivePierceFirst", 400, new Vector3(0f, 0.5f, 82f), 2);
        EnemyHealth pierceSecond = CreateEnemy("OverdrivePierceSecond", 400, new Vector3(0f, 0.5f, 84f));
        Physics.SyncTransforms();

        Require(!skills.TryUseUltimate(), "empty gauge cannot activate overdrive");
        FillGauge(gauge);
        Require(skills.TryUseUltimate() && combat.IsOverdriveActive, "full gauge activates overdrive once");
        Transform aura = player.transform.Find("WizardOverdriveAura");
        Require(aura != null, "R aura is attached to the player");
        RequireParticleMode(aura.gameObject, true);
        Vector3 auraBefore = aura.position;
        player.transform.position += Vector3.right;
        Require(Vector3.Distance(aura.position, auraBefore + Vector3.right) < 0.001f,
            "R aura follows player movement");
        player.transform.position -= Vector3.right;
        GameObject firstWave = GameObject.Find("WizardOverdriveWave");
        Require(firstWave != null, "R creates a pack wave");
        RequireParticleMode(firstWave, false);
        Require(!gauge.IsFull, "successful R consumes the gauge");
        FillGauge(gauge);
        Require(!skills.TryUseUltimate() && combat.IsOverdriveActive, "overdrive cannot be recast while active");
        Require(gauge.IsFull && gauge.TryConsume(), "forced test fill still works during overdrive");
        gauge.Add(10f);
        Require(gauge.Current == 0f, "active overdrive blocks direct gauge gain");
        Require(combat.BasicAdditionalPierces == 1 && combat.BasicDamageMultiplier > 1.01f,
            "overdrive strengthens basic attacks");

        Require(attack.TryAttack(), "overdrive basic attack fires");
        yield return WaitForCondition(
            () => pierceFirst.CurrentHealth < pierceFirst.MaxHealth &&
                  pierceSecond.CurrentHealth < pierceSecond.MaxHealth,
            "overdrive basic attack pierces one extra enemy");
        Require(gauge.Current == 0f, "overdrive basic damage cannot recharge gauge");

        int pausedHealth = waveTarget.CurrentHealth;
        Time.timeScale = 0f;
        yield return WaitSecondsUnscaled(0.4f);
        Require(waveTarget.CurrentHealth == pausedHealth, "pause freezes overdrive waves");
        Time.timeScale = 1f;
        yield return WaitForCondition(() => waveTarget.CurrentHealth < pausedHealth, "overdrive waves deal area damage");
        Require(gauge.Current == 0f, "overdrive wave damage cannot recharge gauge");

        GameObject blastPlayer = CreateWizardPlayer("OverdriveBlastWizard", new Vector3(0f, 0f, 110f));
        WizardCombat blastCombat = blastPlayer.GetComponent<WizardCombat>();
        PlayerUltimateGauge blastGauge = blastPlayer.GetComponent<PlayerUltimateGauge>();
        FillGauge(blastGauge);
        EnemyHealth waveOnly = CreateEnemy("OverdriveNaturalWave", 400, new Vector3(0f, 0.5f, 111f));
        EnemyHealth finalOnly = CreateEnemy("OverdriveNaturalFinal", 400, new Vector3(0f, 0.5f, 116f));
        bool finalDamageObserved = false;
        bool finalDamageLocked = false;
        blastPlayer.GetComponent<PlayerDamageDealer>().OnTargetDamaged += (enemy, position, damage, type) =>
        {
            if (enemy != finalOnly) return;
            finalDamageObserved = true;
            blastGauge.Add(10f);
            finalDamageLocked = blastCombat.IsUltimateActive && blastGauge.Current == 0f;
        };
        Require(blastPlayer.GetComponent<PlayerSkillController>().TryUseUltimate(),
            "activate overdrive for natural expiry");
        yield return WaitForCondition(() => waveOnly.CurrentHealth < waveOnly.MaxHealth,
            "overdrive waves damage nearby enemies before expiry");
        Require(finalOnly.CurrentHealth == finalOnly.MaxHealth, "final blast has not fired during overdrive");
        yield return WaitSeconds(overdrive.OverdriveDuration + 0.1f);
        yield return WaitForCondition(() => !blastCombat.IsOverdriveActive &&
                                            finalOnly.CurrentHealth < finalOnly.MaxHealth,
            "overdrive expiry fires a single final blast");
        int afterFinal = finalOnly.CurrentHealth;
        Require(finalDamageObserved && finalDamageLocked && blastGauge.Current == 0f,
            "final blast callbacks and damage cannot recharge gauge at natural expiry");
        blastGauge.Add(7f);
        Require(!blastCombat.IsUltimateActive && blastGauge.Current == 7f,
            "natural overdrive expiry resumes gauge while blast visual may linger");
        yield return WaitSeconds(0.5f);
        Require(finalOnly.CurrentHealth == afterFinal, "final blast does not repeat");

        GameObject deathPlayer = CreateWizardPlayer("OverdriveDeathWizard", new Vector3(30f, 0f, 80f));
        PlayerSkillController deathSkills = deathPlayer.GetComponent<PlayerSkillController>();
        WizardCombat deathCombat = deathPlayer.GetComponent<WizardCombat>();
        FillGauge(deathPlayer.GetComponent<PlayerUltimateGauge>());
        EnemyHealth deathNearby = CreateEnemy("OverdriveDeathNearby", 400, new Vector3(30f, 0.5f, 81f));
        Require(deathSkills.TryUseUltimate(), "activate overdrive for death cancellation");
        int nearbyHealth = deathNearby.CurrentHealth;
        deathPlayer.GetComponent<PlayerStateManager>().ChangeState(PlayerState.Dead);
        yield return null;
        Require(!deathCombat.IsOverdriveActive, "death cancels overdrive");
        Require(deathPlayer.transform.Find("WizardOverdriveAura") == null &&
            ((HashSet<GameObject>)FindField(typeof(WizardCombat), "activeVisuals").GetValue(deathCombat)).Count == 0,
            "death cleans all owned pack effects");
        PlayerUltimateGauge deathGauge = deathPlayer.GetComponent<PlayerUltimateGauge>();
        deathGauge.Add(7f);
        Require(!deathCombat.IsUltimateActive && deathGauge.Current == 7f, "death cancellation releases gauge lock");
        yield return WaitSeconds(0.4f);
        Require(deathNearby.CurrentHealth == nearbyHealth, "cancelled overdrive does not explode");

        GameObject disablePlayer = CreateWizardPlayer("OverdriveDisableWizard", new Vector3(50f, 0f, 80f));
        WizardCombat disableCombat = disablePlayer.GetComponent<WizardCombat>();
        FillGauge(disablePlayer.GetComponent<PlayerUltimateGauge>());
        EnemyHealth disableNearby = CreateEnemy("OverdriveDisableNearby", 400, new Vector3(50f, 0.5f, 81f));
        Require(disablePlayer.GetComponent<PlayerSkillController>().TryUseUltimate(),
            "activate overdrive for disable cancellation");
        int disableHealth = disableNearby.CurrentHealth;
        disableCombat.enabled = false;
        yield return null;
        Require(!disableCombat.IsOverdriveActive, "disable cancels overdrive");
        Require(disablePlayer.transform.Find("WizardOverdriveAura") == null &&
            ((HashSet<GameObject>)FindField(typeof(WizardCombat), "activeVisuals").GetValue(disableCombat)).Count == 0,
            "disable cleans all owned pack effects");
        PlayerUltimateGauge disableGauge = disablePlayer.GetComponent<PlayerUltimateGauge>();
        disableGauge.Add(7f);
        Require(!disableCombat.IsUltimateActive && disableGauge.Current == 7f, "disable cancellation resumes gauge");
        yield return WaitSeconds(0.4f);
        Require(disableNearby.CurrentHealth == disableHealth, "disabled overdrive does not explode");
    }

    private static void RequireParticleMode(GameObject visual, bool continuous)
    {
        ParticleSystem[] systems = visual.GetComponentsInChildren<ParticleSystem>(true);
        Require(systems.Length > 0, "runtime visual uses pack particles: " + visual.name);
        foreach (ParticleSystem system in systems)
            Require(system.main.loop == continuous, "runtime particle mode: " + visual.name);
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

    private static EnemyHealth CreateNavEnemy(string name, int health, Vector3 position,
        float height = 2f, float radius = 0.5f)
    {
        EnemyHealth enemy = CreateEnemy(name, health, position);
        NavMeshAgent agent = enemy.gameObject.AddComponent<NavMeshAgent>();
        agent.height = height;
        agent.radius = radius;
        agent.enabled = true;
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

    private static void CreateSolidBox(string name, Vector3 position, Vector3 size)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(fixtureRoot.transform, true);
        box.transform.position = position;
        box.transform.localScale = size;
    }

    private static Camera CreateAimCamera(string name, Vector3 position, Vector3 lookAt)
    {
        Camera existing = Camera.main;
        if (existing != null)
            existing.tag = "Untagged";

        GameObject cameraObject = NewGameObject(name, position);
        cameraObject.tag = "MainCamera";
        Camera camera = cameraObject.AddComponent<Camera>();
        cameraObject.transform.LookAt(lookAt);
        return camera;
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
        NavMesh.AddNavMeshData(data);
    }

    private static PlayerSkillSO FindSerializedSkill(PlayerSkillController skills, string fieldName)
    {
        return FindField(typeof(PlayerSkillController), fieldName).GetValue(skills) as PlayerSkillSO;
    }

    private static UnityEngine.Object FindSerializedObject(Component component, string fieldName)
    {
        if (component == null) return null;
        FieldInfo field = FindField(component.GetType(), fieldName);
        return field != null ? field.GetValue(component) as UnityEngine.Object : null;
    }

    private static bool InvokeTryCast(PlayerSkillController skills, PlayerSkillSO skill, bool isE)
    {
        float qBefore = skills.QRemaining;
        int eChargesBefore = skills.ECharges;
        MethodInfo method = typeof(PlayerSkillController).GetMethod("TryCast",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(skills, new object[] { skill, isE });
        if (isE)
            return skills.ECharges < eChargesBefore;
        return skills.QRemaining > qBefore;
    }

    private static void FillGauge(PlayerUltimateGauge gauge)
    {
        MethodInfo fill = typeof(PlayerUltimateGauge).GetMethod("FillForTest",
            BindingFlags.Instance | BindingFlags.NonPublic);
        fill.Invoke(gauge, null);
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

    private static DamageProjectile CreateProjectile(string name, Vector3 position)
    {
        GameObject projectile = NewGameObject(name, position);
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

    private static PlayerDamageDealer CreateDealer(string name, Vector3 position)
    {
        return NewGameObject(name, position).AddComponent<PlayerDamageDealer>();
    }

    private static EnemyHealth CreateEnemy(string name, int health, Vector3 position, int extraColliders = 0)
    {
        GameObject enemyObject = new GameObject(name);
        enemyObject.SetActive(false);
        enemyObject.transform.SetParent(fixtureRoot.transform, true);
        enemyObject.transform.position = position;
        BoxCollider hitbox = enemyObject.AddComponent<BoxCollider>();
        hitbox.size = new Vector3(1f, 2.2f, 1f);
        hitbox.center = new Vector3(0f, 0.6f, 0f);
        for (int i = 0; i < extraColliders; i++)
        {
            GameObject child = new GameObject("Hitbox" + i);
            child.transform.SetParent(enemyObject.transform, false);
            child.transform.localPosition = new Vector3(0.2f * (i + 1), 0f, 0f);
            child.AddComponent<BoxCollider>().size = Vector3.one * 0.8f;
        }

        EnemyHealth enemy = enemyObject.AddComponent<EnemyHealth>();
        SetField(enemy, "maxHealth", health);
        SetField(enemy, "expReward", 0);
        SetField(enemy, "destroyDelay", 60f);
        enemyObject.SetActive(true);
        return enemy;
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

    private static bool Contains(IReadOnlyList<EnemyHealth> enemies, EnemyHealth target)
    {
        for (int i = 0; i < enemies.Count; i++)
            if (enemies[i] == target)
                return true;
        return false;
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = FindField(target.GetType(), name);
        Require(field != null, "fixture field exists: " + target.GetType().Name + "." + name);
        field.SetValue(target, value);
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
        if (!condition) throw new InvalidOperationException("WIZARD_SKILL_VALIDATION_FAIL: " + message);
        Debug.Log("WIZARD_SKILL_VALIDATION_CHECK: " + message);
    }
}

internal sealed class WizardSkillValidationRunner : MonoBehaviour
{
}
