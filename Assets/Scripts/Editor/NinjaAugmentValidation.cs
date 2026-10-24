using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class NinjaAugmentValidation
{
    private const string Pending = "NinjaAugmentValidation.Pending";
    private static NinjaAugmentValidationRunner runner;

    static NinjaAugmentValidation()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Batch mode required.");
        try
        {
            foreach (string name in new[]
                     {
                         "NinjaHwaDunDamage", "NinjaHwaDunRange", "NinjaDashDamage",
                         "NinjaDashMoveSpeed", "NinjaDashCharges", "NinjaStormRange", "NinjaEndBurst"
                     })
            {
                var augment = Resources.Load<PlayerCombatAugmentSO>("Augments/Data/" + name + "_Data");
                Require(augment != null && augment.icon != null, "resource and icon: " + name);
                string iconPath = AssetDatabase.GetAssetPath(augment.icon);
                Require(iconPath.StartsWith("Assets/Images/AugmentIcons/"), "authored icon folder: " + name);
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Pending, true);
            EditorApplication.EnterPlaymode();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
        SessionState.SetBool(Pending, false);
        var host = new GameObject("NinjaAugmentValidationRunner");
        Object.DontDestroyOnLoad(host);
        runner = host.AddComponent<NinjaAugmentValidationRunner>();
        runner.StartCoroutine(runner.Run());
    }

    internal static IEnumerator ValidateRoutine()
    {
        IEnumerator routine = Validate();
        while (true)
        {
            object current;
            try
            {
                if (!routine.MoveNext())
                    break;
                current = routine.Current;
            }
            catch (Exception exception)
            {
                Fail(exception);
                yield break;
            }

            yield return current;
        }

        Debug.Log("NINJA_AUGMENT_VALIDATION_PASS");
        EditorApplication.Exit(0);
    }

    internal static void Fail(Exception exception)
    {
        Debug.LogException(exception);
        EditorApplication.Exit(1);
    }

    private static IEnumerator Validate()
    {
        Time.timeScale = 1f;
        PlayerCombatAugmentSO heat = Load("NinjaHwaDunDamage");
        PlayerCombatAugmentSO spread = Load("NinjaHwaDunRange");
        PlayerCombatAugmentSO dashDamage = Load("NinjaDashDamage");
        PlayerCombatAugmentSO afterimage = Load("NinjaDashMoveSpeed");
        PlayerCombatAugmentSO charges = Load("NinjaDashCharges");
        PlayerCombatAugmentSO corridor = Load("NinjaStormRange");
        PlayerCombatAugmentSO burst = Load("NinjaEndBurst");
        GameObject combatPrefab = Resources.Load<GameObject>("Augments/Skills/PlayerCombatAugment");
        Require(combatPrefab != null && combatPrefab.GetComponent<PlayerCombatAugment>() != null, "shared combat prefab");

        Require(heat.maxStacks == 3 && Approximately(heat.Value, 0.2f) && heat.Effect == PlayerCombatAugmentEffect.SkillDamage, "고열 data");
        Require(spread.maxStacks == 3 && Approximately(spread.Value, 0.15f) && spread.Effect == PlayerCombatAugmentEffect.SkillRange, "확산 화염 data");
        Require(dashDamage.maxStacks == 3 && Approximately(dashDamage.Value, 0.25f) && dashDamage.Effect == PlayerCombatAugmentEffect.SkillDamage, "예리한 돌진 data");
        Require(afterimage.maxStacks == 1 && Approximately(afterimage.Value, 0.2f) && afterimage.Effect == PlayerCombatAugmentEffect.DashMoveSpeed, "잔영 보법 data");
        Require(charges.maxStacks == 1 && Approximately(charges.Value, 1f) && charges.Effect == PlayerCombatAugmentEffect.DashExtraCharge, "연속 보법 data");
        Require(corridor.maxStacks == 2 && Approximately(corridor.Value, 0.2f) && corridor.Effect == PlayerCombatAugmentEffect.UltimateStormRange, "폭풍 회랑 data");
        Require(burst.maxStacks == 1 && Approximately(burst.Value, 2f) && burst.Effect == PlayerCombatAugmentEffect.UltimateEndBurst, "낙뢰의 끝 data");

        GameObject ninja = CreateNinja();
        GameObject sword = CreateSword();
        yield return null;

        PlayerAugments ninjaAugments = ninja.GetComponent<PlayerAugments>();
        PlayerSkillController ninjaSkills = ninja.GetComponent<PlayerSkillController>();
        PlayerStats ninjaStats = ninja.GetComponent<PlayerStats>();
        NinjaDashSkillSO dashSkill = (NinjaDashSkillSO)ninjaSkills.SkillE;
        NinjaHwaDunSkillSO fireSkill = (NinjaHwaDunSkillSO)ninjaSkills.SkillQ;
        Require(dashSkill != null && fireSkill != null, "ninja skill assets assigned");

        Require(heat.IsAvailableFor(ninja) && !heat.IsAvailableFor(sword), "고열 ninja-only");
        Require(dashDamage.IsAvailableFor(ninja) && !dashDamage.IsAvailableFor(sword), "예리한 돌진 ninja-only");
        Require(corridor.IsAvailableFor(ninja) && !corridor.IsAvailableFor(sword), "폭풍 회랑 ninja-only");
        Require(burst.IsAvailableFor(ninja) && !burst.IsAvailableFor(sword), "낙뢰의 끝 ninja-only");

        Require(ninjaAugments.TryAcquireLoadedSkill(heat, combatPrefab) == SkillAcquireStatus.Instantiated, "고열 acquire");
        Require(Approximately(ninjaAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillDamage, fireSkill), 0.2f), "고열 one stack");
        Require(ninjaAugments.TryAcquireLoadedSkill(heat, combatPrefab) == SkillAcquireStatus.Stacked, "고열 stack");
        Require(ninjaAugments.TryAcquireLoadedSkill(heat, combatPrefab) == SkillAcquireStatus.Stacked, "고열 third");
        Require(ninjaAugments.TryAcquireLoadedSkill(heat, combatPrefab) == SkillAcquireStatus.AlreadyMax, "고열 cap");
        Require(Approximately(ninjaAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillDamage, fireSkill), 0.6f), "고열 three stacks");
        Require(ninjaAugments.TryAcquireLoadedSkill(spread, combatPrefab) == SkillAcquireStatus.Instantiated, "확산 화염 acquire");
        Require(Approximately(ninjaAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillRange, fireSkill), 0.15f), "확산 화염 value");
        Require(ninjaAugments.TryAcquireLoadedSkill(dashDamage, combatPrefab) == SkillAcquireStatus.Instantiated, "예리한 돌진 acquire");
        Require(Approximately(ninjaAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.SkillDamage, dashSkill), 0.25f), "예리한 돌진 value");

        EnemyHealth fireDummy = CreateEnemy("FireDummy", 1000, ninja.transform.position + ninja.transform.forward * 2f);
        yield return null;
        Require(InvokeTryCast(ninjaSkills, fireSkill, false), "cast 화둔");
        float fireDeadline = Time.time + 0.5f;
        while (Time.time < fireDeadline && fireDummy.CurrentHealth == fireDummy.MaxHealth)
            yield return null;
        int firstTickLost = fireDummy.MaxHealth - fireDummy.CurrentHealth;
        Require(firstTickLost == 16, "고열 tick 16 from 10 * 1.6, actual " + firstTickLost);
        NinjaHwaDunChannel channel = ninja.GetComponentInChildren<NinjaHwaDunChannel>(true);
        Require(channel != null, "화둔 channel exists");
        Require(Approximately(channel.transform.localScale.x, 1.15f), "확산 화염 scale");
        Object.Destroy(channel.gameObject);
        Object.Destroy(fireDummy.gameObject);

        Require(ninjaAugments.TryAcquireLoadedSkill(afterimage, combatPrefab) == SkillAcquireStatus.Instantiated, "잔영 보법 acquire");
        Require(ninjaAugments.TryAcquireLoadedSkill(charges, combatPrefab) == SkillAcquireStatus.Instantiated, "연속 보법 acquire");
        Require(ninjaSkills.EMaxCharges == 2, "연속 보법 capacity 2");
        Require(ninjaSkills.ECharges == 1, "new extra charge starts empty");
        Require(ninjaSkills.ERechargeRemaining > 0f, "new extra charge queued");

        float baseSpeed = ninjaStats.MoveSpeed;
        ninjaSkills.NotifyDashCompleted(dashSkill);
        Require(ninjaStats.MoveSpeed > baseSpeed + 0.01f, "잔영 보법 speed after dash");
        Require(Approximately(ninjaStats.MoveSpeed, baseSpeed * 1.2f), "잔영 보법 20 percent");
        Require(RemoveSkill(ninjaAugments, afterimage), "잔영 보법 remove");
        InvokeSkillsChanged(ninjaSkills);
        yield return null;
        Require(Approximately(ninjaStats.MoveSpeed, baseSpeed), "잔영 보법 removed immediately");
        Require(ninjaAugments.TryAcquireLoadedSkill(afterimage, combatPrefab) == SkillAcquireStatus.Instantiated, "잔영 보법 reacquire");
        ninjaSkills.NotifyDashCompleted(dashSkill);
        Require(Approximately(ninjaStats.MoveSpeed, baseSpeed * 1.2f), "잔영 보법 refresh not stacked");
        ninja.GetComponent<PlayerStateManager>().ChangeState(PlayerState.Dead);
        yield return null;
        Require(Approximately(ninjaStats.MoveSpeed, baseSpeed), "잔영 보법 cleared on death");

        GameObject livingNinja = CreateNinja();
        yield return null;
        PlayerAugments livingAugments = livingNinja.GetComponent<PlayerAugments>();
        PlayerSkillController livingSkills = livingNinja.GetComponent<PlayerSkillController>();
        NinjaDashSkillSO livingDash = (NinjaDashSkillSO)livingSkills.SkillE;
        Require(livingAugments.TryAcquireLoadedSkill(charges, combatPrefab) == SkillAcquireStatus.Instantiated, "charge acquire on living ninja");
        Require(livingSkills.ECharges == 1 && livingSkills.EMaxCharges == 2, "living extra charge empty");
        float remaining = livingSkills.ERechargeRemaining;
        Require(remaining > 3.5f, "charge cooldown near 4s");
        Require(RemoveSkill(livingAugments, charges), "charge removal");
        InvokeSkillsChanged(livingSkills);
        yield return null;
        Require(livingSkills.EMaxCharges == 1, "charge removal clamps capacity");
        Require(livingSkills.ECharges == 1, "base charge preserved");
        Require(livingAugments.TryAcquireLoadedSkill(charges, combatPrefab) == SkillAcquireStatus.Instantiated, "charge reacquire");
        Require(livingSkills.ECharges == 1, "reacquire does not refill");
        Require(livingSkills.ERechargeRemaining <= remaining + 0.05f, "reacquire does not reset timer");
        int chargesBeforeUse = livingSkills.ECharges;
        InvokeTryCast(livingSkills, livingDash, true);
        Require(livingSkills.ECharges == chargesBeforeUse - 1, "spent last ready charge");
        InvokeTryCast(livingSkills, livingDash, true);
        Require(livingSkills.ECharges == 0, "empty charges cannot recast");

        GameObject ultimateNinja = CreateNinja();
        yield return null;
        PlayerAugments ultimateAugments = ultimateNinja.GetComponent<PlayerAugments>();
        NinjaUltimate ultimate = ultimateNinja.GetComponent<NinjaUltimate>();
        Require(ultimateAugments.TryAcquireLoadedSkill(corridor, combatPrefab) == SkillAcquireStatus.Instantiated, "폭풍 회랑 acquire");
        Require(ultimateAugments.TryAcquireLoadedSkill(corridor, combatPrefab) == SkillAcquireStatus.Stacked, "폭풍 회랑 stack");
        Require(ultimateAugments.TryAcquireLoadedSkill(burst, combatPrefab) == SkillAcquireStatus.Instantiated, "낙뢰의 끝 acquire");
        Require(Approximately(InvokeFloat(ultimate, "GetAugmentedStormRadius"), 2.8f), "폭풍 회랑 radius 2 * 1.4");
        Require(RemoveSkill(ultimateAugments, corridor), "remove one 폭풍 회랑");
        Require(Approximately(InvokeFloat(ultimate, "GetAugmentedStormRadius"), 2.4f), "live radius after removal");

        Require(InvokeInt(ultimate, "CalculateBurstDamage", 10f, 2f) == 20, "낙뢰의 끝 200 percent");
        Require(RemoveSkill(ultimateAugments, burst), "remove 낙뢰의 끝");
        Require(Approximately(ultimateAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.UltimateEndBurst), 0f), "낙뢰의 끝 cancelled after removal");

        PlayerUltimateGauge gauge = ultimateNinja.GetComponent<PlayerUltimateGauge>();
        gauge.Add(7f);
        Require(Approximately(gauge.Current, 7f), "failed unconfigured activation does not lock charging");
        Require(!ultimate.TryActivate(), "unconfigured ultimate activation fails");
        Require(GetBodyAura(ultimate) == null, "failed activation creates no body aura");
        gauge.Add(3f);
        Require(Approximately(gauge.Current, 10f), "charging remains available after failed activation");
        GameObject ninjaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/NinjaPlayer.prefab");
        Animator sourceAnimator = ninjaPrefab.GetComponentInChildren<Animator>(true);
        Animator testAnimator = ultimateNinja.AddComponent<Animator>();
        testAnimator.runtimeAnimatorController = sourceAnimator.runtimeAnimatorController;
        SetField(ultimate, "animator", testAnimator);
        Transform testHand = new GameObject("UltimateValidationHand").transform;
        testHand.SetParent(ultimateNinja.transform, false);
        testHand.localPosition = new Vector3(2f, 2f, 0f);
        SetField(ultimate, "handAnchor", testHand);
        SetField(ultimate, "lightningPrefab", AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/VFX/NinjaLightning.prefab"));
        GameObject auraPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Magic effects pack/Prefabs/Character auras/Lightning aura.prefab");
        Require(auraPrefab != null && new SerializedObject(ninjaPrefab.GetComponent<NinjaUltimate>())
            .FindProperty("readyBodyAuraPrefab").objectReferenceValue == auraPrefab, "ninja prefab wires imported body aura independently");
        SetField(ultimate, "readyBodyAuraPrefab", auraPrefab);
        SetField(ultimate, "readyDuration", 0.15f);
        SetField(ultimate, "dashDuration", 0.15f);
        SetField(ultimate, "stormDuration", 0.3f);
        ultimateNinja.transform.position = new Vector3(100f, 0f, 100f);
        yield return null;
        Require(ultimate.TryActivate() && ultimate.IsUltimateActive, "ready ultimate starts real lifecycle");
        GameObject bodyAura = GetBodyAura(ultimate);
        Require(bodyAura != null && bodyAura.transform.parent == ultimateNinja.transform &&
            bodyAura.transform.parent != testHand, "first activation attaches aura to body, not hand");
        Vector3 auraLocalPosition = bodyAura.transform.localPosition;
        ultimateNinja.transform.position += Vector3.right * 3f;
        testHand.localPosition += Vector3.up;
        Require(Vector3.Distance(bodyAura.transform.position, ultimateNinja.transform.TransformPoint(auraLocalPosition)) < 0.001f,
            "body aura follows moving ninja independently of hand");
        foreach (ParticleSystem particle in bodyAura.GetComponentsInChildren<ParticleSystem>(true))
            Require(particle.main.loop && particle.main.simulationSpace == ParticleSystemSimulationSpace.Local,
                "ready aura particles loop and follow body");
        int readyChildCount = ultimateNinja.transform.childCount;
        Require(!ultimate.TryActivate() && !ultimate.TryActivate() && GetBodyAura(ultimate) == bodyAura &&
            ultimateNinja.transform.childCount == readyChildCount, "failed repeated activation creates no duplicate aura");
        ultimateNinja.GetComponent<PlayerSkillController>().SetMovementLocked(true);
        Require(!ultimate.TryRecast() && GetBodyAura(ultimate) == bodyAura &&
            ultimateNinja.transform.childCount == readyChildCount, "failed recast preserves single ready aura");
        ultimateNinja.GetComponent<PlayerSkillController>().SetMovementLocked(false);
        gauge.Add(20f);
        Require(Approximately(gauge.Current, 10f), "ready buff blocks gauge without erasing existing charge");
        float readyDeadline = Time.time + 1f;
        while (ultimate.IsUltimateActive && Time.time < readyDeadline) yield return null;
        Require(!ultimate.IsUltimateActive, "ready expires naturally");
        yield return null;
        Require(GetBodyAura(ultimate) == null && bodyAura == null, "ready expiry destroys body aura");
        gauge.Add(5f);
        Require(Approximately(gauge.Current, 15f), "ready expiry resumes gauge");
        Require(ultimate.TryActivate(), "ready activation before recast");
        bodyAura = GetBodyAura(ultimate);
        Require(ultimate.TryRecast() && ultimate.IsDashing && GetBodyAura(ultimate) == null, "ultimate recast starts dash and removes ready aura");
        gauge.Add(20f);
        Require(Approximately(gauge.Current, 15f), "dash blocks gauge");
        EnemyHealth stormTarget = CreateEnemy("GaugeStormTarget", 1000, ultimateNinja.transform.position + Vector3.forward * 3f);
        Physics.SyncTransforms();
        float dashDeadline = Time.time + 1f;
        while (ultimate.IsDashing && Time.time < dashDeadline) yield return null;
        Require(bodyAura == null, "recast destroys body aura without storm replacement");
        Require(!ultimate.IsDashing && ultimate.IsUltimateActive && stormTarget.CurrentHealth < stormTarget.MaxHealth,
            "remaining storm is active and deals real damage after dash");
        gauge.Add(20f);
        Require(Approximately(gauge.Current, 15f), "storm damage and direct gains remain blocked");
        float stormDeadline = Time.time + 1f;
        while (ultimate.IsUltimateActive && Time.time < stormDeadline) yield return null;
        Require(!ultimate.IsUltimateActive, "storm expires naturally");
        gauge.Add(5f);
        Require(Approximately(gauge.Current, 20f), "storm expiry resumes gauge");
        Require(ultimate.TryActivate(), "activate for explicit cancellation");
        bodyAura = GetBodyAura(ultimate);
        ultimate.Cancel();
        yield return null;
        Require(GetBodyAura(ultimate) == null && bodyAura == null, "cancel destroys body aura");
        gauge.Add(5f);
        Require(!ultimate.IsUltimateActive && Approximately(gauge.Current, 25f), "cancel resumes gauge");
        Require(ultimate.TryActivate(), "activate for disable cancellation");
        bodyAura = GetBodyAura(ultimate);
        ultimate.enabled = false;
        yield return null;
        Require(GetBodyAura(ultimate) == null && bodyAura == null, "disable destroys body aura");
        gauge.Add(5f);
        Require(!ultimate.IsUltimateActive && Approximately(gauge.Current, 30f), "disable resumes gauge");
        ultimate.enabled = true;
        Require(ultimate.TryActivate(), "activate for death cancellation");
        bodyAura = GetBodyAura(ultimate);
        ultimateNinja.GetComponent<PlayerStateManager>().ChangeState(PlayerState.Dead);
        yield return null;
        yield return null;
        Require(GetBodyAura(ultimate) == null && bodyAura == null, "death destroys body aura");
        Object.Destroy(stormTarget.gameObject);
        Object.Destroy(ninja);
        Object.Destroy(sword);
        Object.Destroy(livingNinja);
        Object.Destroy(ultimateNinja);
        yield return null;
    }

    private static GameObject GetBodyAura(NinjaUltimate ultimate)
    {
        return (GameObject)typeof(NinjaUltimate).GetField("readyBodyAuraEffect",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ultimate);
    }

    private static PlayerCombatAugmentSO Load(string name)
    {
        var augment = Resources.Load<PlayerCombatAugmentSO>("Augments/Data/" + name + "_Data");
        Require(augment != null, "load " + name);
        return augment;
    }

    private static GameObject CreateNinja()
    {
        GameObject player = new GameObject("NinjaValidationPlayer");
        player.tag = "Player";
        player.AddComponent<PlayerCharacter>().SetCharacterId(PlayerCharacterId.Ninja);
        player.AddComponent<PlayerStats>();
        player.AddComponent<PlayerHealth>();
        player.AddComponent<PlayerStateManager>();
        player.AddComponent<PlayerDamageDealer>();
        player.AddComponent<PlayerAugments>();
        player.AddComponent<PlayerUltimateGauge>();
        player.AddComponent<NinjaUltimate>();
        PlayerSkillController skills = player.AddComponent<PlayerSkillController>();
        SetField(skills, "skillQ", AssetDatabase.LoadAssetAtPath<NinjaHwaDunSkillSO>("Assets/ScriptableObjects/Skills/NinjaHwaDunSkill.asset"));
        SetField(skills, "skillE", AssetDatabase.LoadAssetAtPath<NinjaDashSkillSO>("Assets/ScriptableObjects/Skills/NinjaDashSkill.asset"));
        Transform origin = new GameObject("SkillOrigin").transform;
        origin.SetParent(player.transform, false);
        origin.localPosition = Vector3.forward;
        SetField(skills, "skillOrigin", origin);
        CapsuleCollider capsule = player.AddComponent<CapsuleCollider>();
        capsule.height = 2f;
        capsule.radius = 0.4f;
        return player;
    }

    private static GameObject CreateSword()
    {
        GameObject player = new GameObject("SwordValidationPlayer");
        player.AddComponent<PlayerCharacter>().SetCharacterId(PlayerCharacterId.Sword);
        player.AddComponent<PlayerStats>();
        player.AddComponent<PlayerAugments>();
        player.AddComponent<PlayerSkillController>();
        return player;
    }

    private static EnemyHealth CreateEnemy(string name, int health, Vector3 position)
    {
        GameObject enemy = new GameObject(name);
        enemy.transform.position = position;
        enemy.AddComponent<BoxCollider>().size = Vector3.one;
        EnemyHealth enemyHealth = enemy.AddComponent<EnemyHealth>();
        SetField(enemyHealth, "maxHealth", health);
        SetField(enemyHealth, "expReward", 0);
        SetProperty(enemyHealth, "CurrentHealth", health);
        SetProperty(enemyHealth, "IsDead", false);
        return enemyHealth;
    }

    private static bool InvokeTryCast(PlayerSkillController skills, PlayerSkillSO skill, bool isE)
    {
        MethodInfo method = typeof(PlayerSkillController).GetMethod("TryCast", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(skills, new object[] { skill, isE });
        if (!isE) return skills.QChanneling;
        return skills.IsMovementLocked || skills.ECharges < skills.EMaxCharges || skills.ERemaining > 0f;
    }

    private static void InvokeBurst(NinjaUltimate ultimate, Vector3 endpoint, float attack)
    {
        MethodInfo method = typeof(NinjaUltimate).GetMethod("ApplyEndpointBurst", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(ultimate, new object[] { endpoint, attack, new HashSet<EnemyHealth>() });
    }

    private static float InvokeFloat(object target, string name)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return (float)method.Invoke(target, null);
    }
    private static int InvokeInt(object target, string name, params object[] args)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return (int)method.Invoke(target, args);
    }

    private static bool RemoveSkill(PlayerAugments augments, SkillAugmentSO target)
    {
        FieldInfo ownedField = typeof(PlayerAugments).GetField("ownedSkills", BindingFlags.Instance | BindingFlags.NonPublic);
        var owned = (Dictionary<SkillAugmentSO, AugmentSkill>)ownedField.GetValue(augments);
        if (owned == null || !owned.TryGetValue(target, out AugmentSkill skill)) return false;
        if (skill != null && skill.TryRemoveStack())
        {
            return true;
        }

        owned.Remove(target);
        if (skill != null)
        {
            skill.Release();
            Object.Destroy(skill.gameObject);
        }

        return true;
    }

    private static void InvokeSkillsChanged(PlayerSkillController skills)
    {
        MethodInfo method = typeof(PlayerSkillController).GetMethod("HandlePlayerSkillsChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(skills, null);
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Require(field != null, "field " + name);
        field.SetValue(target, value);
    }

    private static void SetProperty(object target, string name, object value)
    {
        PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Require(property != null, "property " + name);
        property.SetValue(target, value);
    }

    private static bool Approximately(float actual, float expected)
    {
        return Mathf.Abs(actual - expected) <= 0.02f;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("NINJA_AUGMENT_FAIL: " + message);
        Debug.Log("NINJA_AUGMENT_PASS: " + message);
    }
}

internal sealed class NinjaAugmentValidationRunner : MonoBehaviour
{
    public IEnumerator Run()
    {
        IEnumerator routine = NinjaAugmentValidation.ValidateRoutine();
        while (true)
        {
            object current;
            try
            {
                if (!routine.MoveNext())
                    yield break;
                current = routine.Current;
            }
            catch (Exception exception)
            {
                NinjaAugmentValidation.Fail(exception);
                yield break;
            }

            yield return current;
        }
    }
}
