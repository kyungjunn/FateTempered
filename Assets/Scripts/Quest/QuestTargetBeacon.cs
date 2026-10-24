using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 이벤트 몬스터 위에 따라다니는 반투명 수직 표식
public class QuestTargetBeacon : MonoBehaviour
{
    [SerializeField] private Material beaconMaterial;
    [SerializeField, Min(1f)] private float height = 65f;
    [SerializeField, Min(0.1f)] private float width = 2.4f;
    [SerializeField, Min(0f)] private float groundGlowRadius = 2.5f;

    private Mesh beamMesh;

    private void Awake()
    {
        if (beaconMaterial == null)
        {
            Debug.LogWarning("QuestTargetBeacon에 머티리얼이 지정되지 않았습니다.", this);
            return;
        }

        GameObject visual = new GameObject("Beacon Visual");
        visual.transform.SetParent(transform, false);

        beamMesh = BuildMesh();
        visual.AddComponent<MeshFilter>().sharedMesh = beamMesh;
        MeshRenderer meshRenderer = visual.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = beaconMaterial;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    private Mesh BuildMesh()
    {
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var triangles = new List<int>();
        float[] levels = { 0.05f, 0.8f, height * 0.15f, height * 0.65f, height };
        float[] opacity = { 0.15f, 0.6f, 0.55f, 0.35f, 0f };
        float[] acrossOpacity = { 0f, 1f, 0f };

        // 교차한 두 면의 중앙을 밝게 하여 어느 방향에서도 빔이 보이게 함
        for (int plane = 0; plane < 2; plane++)
        {
            int start = vertices.Count;
            Vector3 axis = plane == 0 ? Vector3.right : Vector3.forward;
            for (int row = 0; row < levels.Length; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    vertices.Add(axis * ((column - 1) * width * 0.5f) + Vector3.up * levels[row]);
                    colors.Add(new Color(1f, 1f, 1f, opacity[row] * acrossOpacity[column]));
                }
            }

            for (int row = 0; row < levels.Length - 1; row++)
            {
                for (int column = 0; column < 2; column++)
                {
                    int a = start + row * 3 + column;
                    triangles.Add(a);
                    triangles.Add(a + 3);
                    triangles.Add(a + 1);
                    triangles.Add(a + 1);
                    triangles.Add(a + 3);
                    triangles.Add(a + 4);
                }
            }
        }

        if (groundGlowRadius > 0f)
        {
            const int segments = 24;
            int center = vertices.Count;
            vertices.Add(Vector3.up * 0.06f);
            colors.Add(new Color(1f, 1f, 1f, 0.4f));
            for (int ring = 0; ring < 2; ring++)
            {
                float radius = ring == 0 ? groundGlowRadius * 0.28f : groundGlowRadius;
                float alpha = ring == 0 ? 0.3f : 0f;
                for (int i = 0; i < segments; i++)
                {
                    float angle = i * Mathf.PI * 2f / segments;
                    vertices.Add(new Vector3(Mathf.Cos(angle) * radius, 0.06f, Mathf.Sin(angle) * radius));
                    colors.Add(new Color(1f, 1f, 1f, alpha));
                }
            }

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                triangles.Add(center);
                triangles.Add(center + 1 + i);
                triangles.Add(center + 1 + next);
                triangles.Add(center + 1 + i);
                triangles.Add(center + 1 + segments + i);
                triangles.Add(center + 1 + segments + next);
                triangles.Add(center + 1 + i);
                triangles.Add(center + 1 + segments + next);
                triangles.Add(center + 1 + next);
            }
        }

        var mesh = new Mesh { name = "Quest Target Beacon" };
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private void OnDestroy()
    {
        if (beamMesh != null) Destroy(beamMesh);
    }

    private void OnValidate()
    {
        height = Mathf.Max(1f, height);
        width = Mathf.Max(0.1f, width);
        groundGlowRadius = Mathf.Max(0f, groundGlowRadius);
    }
}
