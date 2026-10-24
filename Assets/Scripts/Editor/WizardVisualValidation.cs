using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using static UnityEngine.Object;

// Runs the existing gameplay fixtures without the batch runners closing the editor.
public static class WizardVisualValidation
{
    public const string ResultKey = "WizardVisualValidation.Result";

    [MenuItem("Tools/Validation/Wizard E Preview Live Review")]
    public static void ReviewPreview()
    {
        if (!EditorApplication.isPlaying)
            throw new InvalidOperationException("Run in Play Mode in WizardTest.");
        WizardCombat combat = FindFirstObjectByType<WizardCombat>();
        PlayerSkillController skills = combat.GetComponent<PlayerSkillController>();
        Vector3 target = combat.transform.position + Vector3.forward * 2f;
        target.y = 0f;
        var brain = Camera.main.GetComponent<Unity.Cinemachine.CinemachineBrain>();
        if (brain != null) brain.enabled = false;
        Camera.main.transform.position = target + new Vector3(0f, 8f, -9f);
        Camera.main.transform.LookAt(target);
        Require(combat.TryBeginResonancePreview((WizardSkillSO)skills.SkillE),
            "live preview begins");
        Require(combat.ResonancePreviewHasGroundTarget && combat.ResonancePreviewIsValid,
            "live preview targets valid ground");
        MeshRenderer fill = GameObject.Find("WizardResonancePreviewFill").GetComponent<MeshRenderer>();
        Require(fill.enabled, "live preview fill is visible");
        float worldRadius = fill.transform.TransformVector(Vector3.right).magnitude;
        Require(Mathf.Abs(worldRadius - combat.ResonancePreviewRadius) < 0.001f,
            "live preview fill matches gameplay radius");
        EditorApplication.isPaused = true;
        Debug.Log("WIZARD_E_PREVIEW_LIVE_PASS");
    }

    [MenuItem("Tools/Validation/Wizard VFX Visual Invariants")]
    public static void RunVisualChecks()
    {
        if (EditorApplication.isPlaying)
            throw new InvalidOperationException("Run visual invariants outside Play Mode.");

        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        try
        {
            GameObject wizard = Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Player/WizardPlayer.prefab"));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(wizard, scene);
            WizardCombat combat = wizard.GetComponent<WizardCombat>();
            Type previewType = typeof(WizardCombat).GetNestedType("ResonancePreview",
                BindingFlags.NonPublic);
            object preview = Activator.CreateInstance(previewType, true);
            typeof(WizardCombat).GetMethod("CreateResonancePreviewVisual",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(combat, new[] { preview });
            GameObject previewRoot = (GameObject)previewType.GetField("VisualRoot").GetValue(preview);
            Mesh fillMesh = (Mesh)previewType.GetField("FillMesh").GetValue(preview);
            Material previewMaterial = (Material)previewType.GetField("LineMaterial").GetValue(preview);
            try
            {
                Require(previewRoot != null && fillMesh != null, "E preview creates filled geometry");
                Require(fillMesh.vertexCount == 65 && fillMesh.triangles.Length == 192,
                    "E preview has a complete 64-segment disk");
                Vector3[] vertices = fillMesh.vertices;
                int[] triangles = fillMesh.triangles;
                for (int i = 1; i < vertices.Length; i++)
                    Require(Mathf.Abs(vertices[i].magnitude - 1f) < 0.001f,
                        "E preview perimeter matches unit skill radius");
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 a = vertices[triangles[i + 1]] - vertices[triangles[i]];
                    Vector3 b = vertices[triangles[i + 2]] - vertices[triangles[i]];
                    Require(Vector3.Cross(a, b).y > 0f, "E preview fill faces camera above ground");
                }
                Require(previewRoot.GetComponentsInChildren<Collider>().Length == 0,
                    "E preview does not interfere with ground targeting");
                Require(!previewRoot.GetComponentInChildren<MeshRenderer>().enabled,
                    "E preview fill starts hidden until a ground target is available");
            }
            finally
            {
                DestroyImmediate(previewRoot);
                DestroyImmediate(fillMesh);
                DestroyImmediate(previewMaterial);
            }
            string[] fields = { "gravityCorePrefab", "gravityVortexPrefab", "starHitPrefab",
                "resonanceFieldPrefab", "resonanceHitPrefab", "overdriveAuraPrefab",
                "overdriveWavePrefab", "overdriveFinalPrefab" };
            foreach (WizardSkillVisualRole role in Enum.GetValues(typeof(WizardSkillVisualRole)))
            {
                GameObject prefab = (GameObject)typeof(WizardCombat).GetField(fields[(int)role],
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(combat);
                GameObject instance = Instantiate(prefab);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(instance, scene);
                try
                {
                    bool continuous = role == WizardSkillVisualRole.GravityCore ||
                        role == WizardSkillVisualRole.GravityVortex ||
                        role == WizardSkillVisualRole.ResonanceField ||
                        role == WizardSkillVisualRole.OverdriveAura;
                    bool follows = role == WizardSkillVisualRole.OverdriveAura;
                    WizardSkillVisual visual = instance.GetComponent<WizardSkillVisual>();
                    visual.Initialize(continuous, follows);
                    int liveParticles = 0;
                    foreach (ParticleSystem system in instance.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                        system.useAutoRandomSeed = false;
                        system.randomSeed = 123;
                        system.Simulate(0.4f, false, true, true);
                        liveParticles += system.particleCount;
                        Require(system.main.loop == continuous, role + ": loop ownership");
                        Require(!follows || system.main.simulationSpace == ParticleSystemSimulationSpace.Local,
                            role + ": follows owner");
                    }
                    Require(liveParticles > 0, role + ": visible particles after simulation");
                    if (!continuous)
                    {
                        int remainingParticles = 0;
                        foreach (ParticleSystem system in instance.GetComponentsInChildren<ParticleSystem>(true))
                        {
                            system.Simulate(12f, false, true, true);
                            remainingParticles += system.particleCount;
                        }
                        // Edit-mode Simulate pauses systems; IsAlive includes that pending playback state.
                        Require(remainingParticles == 0, role + ": burst particles expire within cleanup bound");
                    }
                    if (role == WizardSkillVisualRole.GravityVortex || role == WizardSkillVisualRole.ResonanceField)
                    {
                        instance.transform.localScale = Vector3.one * 1.75f;
                        instance.transform.rotation = Quaternion.Euler(12f, 0f, 8f);
                        typeof(WizardCombat).GetMethod("AddGroundBoundary", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(combat, new object[] { visual, 5.25f, Color.cyan, 0.075f });
                        LineRenderer line = instance.GetComponentInChildren<LineRenderer>();
                        Require(line != null && line.loop, role + ": closed range boundary");
                        for (int i = 0; i < line.positionCount; i++)
                        {
                            Vector3 local = line.GetPosition(i);
                            Vector3 planar = new Vector3(local.x, 0f, local.z);
                            float radius = instance.transform.TransformVector(planar).magnitude;
                            Require(Mathf.Abs(radius - 5.25f) < 0.001f,
                                role + ": boundary matches augmented radius on slope");
                        }
                    }
                }
                finally { DestroyImmediate(instance); }
            }

            var attack = new SerializedObject(wizard.GetComponent<ProjectilePlayerAttack>());
            DamageProjectile projectilePrefab = (DamageProjectile)attack.FindProperty("projectilePrefab").objectReferenceValue;
            foreach (bool empowered in new[] { false, true })
            {
                DamageProjectile projectile = Instantiate(projectilePrefab);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(projectile.gameObject, scene);
                try
                {
                    CapsuleCollider collider = projectile.GetComponent<CapsuleCollider>();
                    float radius = collider.radius;
                    float height = collider.height;
                    Vector3 scale = projectile.transform.localScale;
                    WizardProjectileVisual visual = projectile.gameObject.AddComponent<WizardProjectileVisual>();
                    visual.Configure(empowered);
                    Require(collider.radius == radius && collider.height == height &&
                        projectile.transform.localScale == scale, "projectile visuals preserve collision geometry");
                    ParticleSystem spark = projectile.transform.Find("WizardProjectileHitSpark").GetComponent<ParticleSystem>();
                    Require(spark.particleCount == 0, "flight does not emit contact sparks");
                    visual.OnSuccessfulHit(projectile.transform.position);
                    Require(spark.particleCount == 5, "accepted hit emits compact spark");
                    visual.OnSuccessfulHit(projectile.transform.position);
                    Require(spark.particleCount == 10, "piercing hit remains visible");
                    projectile.gameObject.SetActive(false);
                    Require(spark.transform.parent == projectile.transform && spark.particleCount == 0,
                        "disabling clears owned hit particles without orphan effects");
                }
                finally { DestroyImmediate(projectile.gameObject); }
            }
            Debug.Log("WIZARD_VFX_VISUAL_INVARIANTS_PASS");
        }
        finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("WIZARD_VFX_VISUAL_INVARIANT_FAIL: " + message);
    }

    [MenuItem("Tools/Validation/Wizard VFX Gameplay Regressions")]
    public static void RunGameplayRegressions()
    {
        if (!EditorApplication.isPlaying)
            throw new InvalidOperationException("Run in Play Mode in an empty test scene.");
        if (FindFirstObjectByType<WizardVisualRegressionRunner>() != null)
            throw new InvalidOperationException("Wizard VFX validation is already running.");

        SessionState.SetString(ResultKey, "RUNNING");
        new GameObject("WizardVisualValidationRunner")
            .AddComponent<WizardVisualRegressionRunner>().StartCoroutine(Run());
    }

    private static IEnumerator Run()
    {
        Type[] suites = { typeof(WizardSkillValidation), typeof(WizardAugmentValidation), typeof(WizardVisualValidation) };
        List<string> failures = new List<string>();
        List<string> passed = new List<string>();
        foreach (Type suite in suites)
        {
            Stack<IEnumerator> routines = new Stack<IEnumerator>();
            Exception failure = null;
            try
            {
                string methodName = suite == typeof(WizardVisualValidation) ? "FocusedWizardGameplay" : "Validate";
                MethodInfo method = suite.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
                if (method == null)
                    throw new MissingMethodException(suite.Name, "Validate");
                routines.Push((IEnumerator)method.Invoke(null, null));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            while (failure == null && routines.Count > 0)
            {
                object current = null;
                bool advanced = false;
                try
                {
                    IEnumerator routine = routines.Peek();
                    advanced = routine.MoveNext();
                    if (advanced)
                        current = routine.Current;
                    else
                        (routines.Pop() as IDisposable)?.Dispose();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                if (failure != null) break;
                if (!advanced) continue;
                if (current is IEnumerator nested)
                    routines.Push(nested);
                else
                    yield return current;
            }

            if (failure != null)
            {
                while (routines.Count > 0)
                    (routines.Pop() as IDisposable)?.Dispose();
                failures.Add(suite.Name + ": " + failure.Message);
                Debug.LogException(failure);
                continue;
            }
            passed.Add(suite.Name);
            Debug.Log("WIZARD_VFX_REGRESSION_PASS: " + suite.Name);
        }
        SessionState.SetString(ResultKey, (failures.Count == 0 ? "PASS" : "FAIL") +
            "\nPassed: " + string.Join(", ", passed) + "\nFailures: " + string.Join("\n", failures));
        DestroyRunner();
    }

    // Keep wizard behavior observable even when unrelated character asset assertions fail.
    // This does not replace or hide the full-suite result above.
    private static IEnumerator FocusedWizardGameplay()
    {
        Type suite = typeof(WizardSkillValidation);
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo root = suite.GetField("fixtureRoot", flags);
        root.SetValue(null, new GameObject("WizardFocusedGameplayFixtures"));
        List<string> errors = new List<string>();
        Application.LogCallback handler = (message, stack, type) =>
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                errors.Add(message);
        };
        Application.logMessageReceived += handler;
        try
        {
            string[] methods = { "TestBasicAttackCooldown", "TestGravityCore", "TestResonanceTargeting",
                "TestResonanceField", "TestOverdrive" };
            foreach (string method in methods)
            {
                yield return (IEnumerator)suite.GetMethod(method, flags).Invoke(null, null);
                suite.GetMethod("ClearFixtures", flags).Invoke(null, null);
            }
            Require(errors.Count == 0, "gameplay runtime errors: " + string.Join(" | ", errors));
        }
        finally
        {
            Application.logMessageReceived -= handler;
            GameObject fixture = (GameObject)root.GetValue(null);
            if (fixture != null) DestroyImmediate(fixture);
            root.SetValue(null, null);
            Time.timeScale = 1f;
        }
    }

    private static void DestroyRunner()
    {
        WizardVisualRegressionRunner runner = FindFirstObjectByType<WizardVisualRegressionRunner>();
        if (runner != null) Destroy(runner.gameObject);
    }
}

internal sealed class WizardVisualRegressionRunner : MonoBehaviour
{
}
