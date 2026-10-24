using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

public static class ProjectileHeightAimValidation
{
    public static void RunBatch()
    {
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var aim = new ProjectileHeightAim();
            Vector3 origin = new Vector3(0f, 1.5f, 0f);
            Func<Vector3> resolve = () => aim.Resolve(origin, Vector3.forward, 14f, null, 8f, 35f);
            Require(resolve() == Vector3.forward, "No target retains original direction");
            var enemy = new GameObject("HeightTarget");
            enemy.AddComponent<EnemyHealth>();
            var hitbox = enemy.AddComponent<BoxCollider>();
            hitbox.size = Vector3.one * 0.6f;
            foreach (float height in new[] { 0.3f, 3f })
            {
                enemy.transform.position = new Vector3(0f, height, 5f);
                Physics.SyncTransforms();
                Vector3 direction = resolve();
                Require(Mathf.Sign(direction.y) == Mathf.Sign(height - origin.y), "Correct vertical direction");
                Require(Mathf.Abs(direction.x) < 0.0001f, "Yaw remains unchanged");
                Require(hitbox.Raycast(new Ray(origin, direction), out _, 14f), "Corrected ray hits small/flying target");
            }
            enemy.transform.position = new Vector3(3f, 3f, 5f);
            Physics.SyncTransforms();
            Require(resolve() == Vector3.forward, "Side target excluded");
            enemy.transform.position = new Vector3(0f, 3f, -5f);
            Physics.SyncTransforms();
            Require(resolve() == Vector3.forward, "Behind target excluded");
            enemy.transform.position = new Vector3(0f, 8f, 2f);
            Physics.SyncTransforms();
            Require(resolve() == Vector3.forward, "Excessive pitch excluded");
            enemy.transform.position = new Vector3(0f, 3f, 16f);
            Physics.SyncTransforms();
            Require(resolve() == Vector3.forward, "Out of range excluded");
            enemy.transform.position = new Vector3(0f, 3f, 5f);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = new Vector3(0f, 2f, 2.5f);
            wall.transform.localScale = new Vector3(2f, 5f, 0.5f);
            Physics.SyncTransforms();
            Require(resolve() == Vector3.forward, "Wall blocks target selection");
            Object.DestroyImmediate(wall);
            for (int i = 0; i < 40; i++)
            {
                var clutter = new GameObject("QueryCapacity" + i);
                clutter.transform.position = new Vector3(4f, 0f, 4f);
                clutter.AddComponent<BoxCollider>();
            }
            Physics.SyncTransforms();
            Require(resolve().y > 0f, "Crowded query still finds target");
            var shotObject = new GameObject("Projectile");
            shotObject.transform.position = origin;
            var shot = shotObject.AddComponent<DamageProjectile>();
            shot.Initialize(null, PlayerDamageType.BasicAttack, 1, 14f, 14f, Vector3.forward);
            var directionField = typeof(DamageProjectile).GetField("moveDirection", BindingFlags.Instance | BindingFlags.NonPublic);
            Vector3 initial = (Vector3)directionField.GetValue(shot);
            Require(initial.y > 0f, "Projectile Initialize applies correction");
            enemy.transform.position = new Vector3(0f, -1f, 5f);
            Physics.SyncTransforms();
            typeof(DamageProjectile).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(shot, null);
            Require((Vector3)directionField.GetValue(shot) == initial, "Flight does not retarget");
            Object.DestroyImmediate(shotObject);
            Object.DestroyImmediate(enemy);
            Debug.Log("PROJECTILE_HEIGHT_AIM_VALIDATION_PASS: low/high targets, yaw, limits, walls, crowded query, launch integration, straight flight");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
