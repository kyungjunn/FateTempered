using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 박쥐가 발사한 음파를 이동시키고 플레이어 또는 지형과의 충돌을 검사
public sealed class BatSonicProjectile : MonoBehaviour
{
    [SerializeField] private Material waveMaterial;
    [SerializeField, Min(0.1f)] private float speed = 9f;
    [SerializeField, Min(0.05f)] private float hitRadius = 0.32f;
    [SerializeField, Min(0.1f)] private float lifetime = 2f;

    private Transform owner;
    private Vector3 direction;
    private int damage;
    private Mesh waveMesh;
    private bool launched;

    private void Awake()
    {
        if (waveMaterial == null) return;

        GameObject visual = new GameObject("Sonic Wave Visual");
        visual.transform.SetParent(transform, false);
        waveMesh = BuildWaveMesh();
        visual.AddComponent<MeshFilter>().sharedMesh = waveMesh;
        MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = waveMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    public void Launch(Transform source, Vector3 travelDirection, int attackDamage)
    {
        owner = source;
        direction = travelDirection.normalized;
        damage = attackDamage;
        launched = direction.sqrMagnitude > 0f;
        Destroy(gameObject, lifetime);

        if (launched) CheckInitialOverlap();
    }

    private void FixedUpdate()
    {
        if (!launched) return;

        float distance = speed * Time.fixedDeltaTime;
        RaycastHit[] hits = Physics.SphereCastAll(transform.position, hitRadius, direction, distance,
            ~0, QueryTriggerInteraction.Ignore);
        Collider nearest = null;
        float nearestDistance = float.PositiveInfinity;

        foreach (RaycastHit hit in hits)
        {
            if (ShouldIgnore(hit.collider) || hit.distance >= nearestDistance) continue;
            nearest = hit.collider;
            nearestDistance = hit.distance;
        }

        if (nearest != null)
        {
            Hit(nearest);
            return;
        }

        transform.position += direction * distance;
    }

    private void CheckInitialOverlap()
    {
        foreach (Collider hit in Physics.OverlapSphere(transform.position, hitRadius,
                     ~0, QueryTriggerInteraction.Ignore))
        {
            if (ShouldIgnore(hit)) continue;
            Hit(hit);
            return;
        }
    }

    private bool ShouldIgnore(Collider hit)
    {
        return hit == null || (owner != null && hit.transform.IsChildOf(owner))
            || hit.GetComponentInParent<EnemyHealth>() != null;
    }

    private void Hit(Collider hit)
    {
        launched = false;
        PlayerHealth player = hit.GetComponentInParent<PlayerHealth>();
        if (player != null) player.TakeDamage(damage);
        Destroy(gameObject);
    }

    private static Mesh BuildWaveMesh()
    {
        const int segments = 24;
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var triangles = new List<int>();

        // 앞뒤의 얇은 고리를 겹쳐 퍼져 나가는 음파 형태를 만든다.
        for (int wave = 0; wave < 2; wave++)
        {
            int start = vertices.Count;
            float depth = wave == 0 ? 0f : -0.35f;
            float size = wave == 0 ? 1f : 0.7f;
            float opacity = wave == 0 ? 0.85f : 0.4f;
            float[] radii = { 0.3f, 0.46f, 0.64f };
            float[] alpha = { 0f, opacity, 0f };

            for (int ring = 0; ring < radii.Length; ring++)
            {
                for (int i = 0; i < segments; i++)
                {
                    float angle = i * Mathf.PI * 2f / segments;
                    vertices.Add(new Vector3(Mathf.Cos(angle) * radii[ring] * size,
                        Mathf.Sin(angle) * radii[ring] * size, depth));
                    colors.Add(new Color(1f, 1f, 1f, alpha[ring]));
                }
            }

            for (int ring = 0; ring < radii.Length - 1; ring++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int next = (i + 1) % segments;
                    int inner = start + ring * segments + i;
                    int innerNext = start + ring * segments + next;
                    int outer = inner + segments;
                    int outerNext = innerNext + segments;
                    triangles.Add(inner);
                    triangles.Add(outer);
                    triangles.Add(innerNext);
                    triangles.Add(innerNext);
                    triangles.Add(outer);
                    triangles.Add(outerNext);
                }
            }
        }

        var mesh = new Mesh { name = "Bat Sonic Wave" };
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private void OnDestroy()
    {
        if (waveMesh != null) Destroy(waveMesh);
    }

    private void OnValidate()
    {
        speed = Mathf.Max(0.1f, speed);
        hitRadius = Mathf.Max(0.05f, hitRadius);
        lifetime = Mathf.Max(0.1f, lifetime);
    }
}
