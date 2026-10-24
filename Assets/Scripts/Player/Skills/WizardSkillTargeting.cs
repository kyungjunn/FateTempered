using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public sealed class WizardSkillTargeting : MonoBehaviour
{
    private sealed class ResonancePreview
    {
        public WizardSkillSO Skill;
        public bool HasGroundTarget;
        public bool IsValid;
        public Vector3 Center;
        public Vector3 Normal;
        public GameObject VisualRoot;
        public Material LineMaterial;
        public LineRenderer RangeRenderer;
        public LineRenderer FieldRenderer;
        public Mesh FillMesh;
        public MeshRenderer FillRenderer;
        public MaterialPropertyBlock FillTint;
    }

    [Header("Resonance Preview")]
    [SerializeField] private Color previewValidColor = new Color(0.72f, 0.38f, 1f, 0.95f);
    [SerializeField] private Color previewInvalidColor = new Color(1f, 0.12f, 0.12f, 0.95f);
    [SerializeField, Range(0f, 1f)] private float previewFillOpacity = 0.18f;

    private PlayerSkillController owner;
    private PlayerSkillInput skillInput;
    private WizardCombat combat;
    private PlayerInputHandler playerInput;
    private ResonancePreview resonancePreview;
    private int resonancePreviewOpenedFrame = -1;
    private int suppressBasicAttackThroughFrame = -1;
    private RaycastHit[] groundHitBuffer = new RaycastHit[16];

    public bool IsPreviewActive => resonancePreview != null;
    public bool ShouldSuppressBasicAttack
    {
        get
        {
            if (IsPreviewActive) return true;
            if (playerInput == null) playerInput = GetComponent<PlayerInputHandler>();
            return Time.frameCount <= suppressBasicAttackThroughFrame &&
                playerInput != null && playerInput.AttackTriggered;
        }
    }
    public bool ResonancePreviewHasGroundTarget =>
        resonancePreview != null && resonancePreview.HasGroundTarget;
    public bool ResonancePreviewIsValid => resonancePreview != null && resonancePreview.IsValid &&
        owner != null && owner.ECharges > 0 && !owner.IsPlayerDead && !IsGamePaused;
    public Vector3 ResonancePreviewPosition =>
        resonancePreview != null && resonancePreview.HasGroundTarget ? resonancePreview.Center : default;
    public Vector3 ResonancePreviewNormal =>
        resonancePreview != null && resonancePreview.HasGroundTarget ? resonancePreview.Normal : Vector3.up;
    public float ResonancePreviewRange =>
        resonancePreview != null && resonancePreview.Skill != null
            ? resonancePreview.Skill.ResonanceFieldRange
            : 0f;
    public float ResonancePreviewRadius =>
        resonancePreview != null && resonancePreview.Skill != null
            ? resonancePreview.Skill.ResonanceFieldRadius
            : 0f;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (resonancePreview != null)
            UpdateResonancePreview();
        if (!IsPreviewActive) return;
        if (!CanReadInput())
        {
            CancelPreview();
            return;
        }
        if ((Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ||
            (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame))
        {
            CancelPreview();
            return;
        }
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame &&
            Time.frameCount > resonancePreviewOpenedFrame && !IsPointerOverUI())
            TryConfirmPreview();
    }

    private void OnDisable()
    {
        CancelPreview();
    }

    public bool TryBeginPreview()
    {
        ResolveReferences();
        if (resonancePreview != null || !isActiveAndEnabled || !CanReadInput() || IsPointerOverUI() ||
            owner == null || !owner.CanBeginSkillTargeting || owner.ECharges <= 0 ||
            !(owner.SkillE is WizardSkillSO skill) || skill.Kind != WizardSkillKind.ResonanceField ||
            combat == null || !combat.isActiveAndEnabled || !gameObject.activeInHierarchy || IsGamePaused)
        {
            return false;
        }

        resonancePreview = new ResonancePreview
        {
            Skill = skill,
            Normal = Vector3.up
        };
        if (!CreateResonancePreviewVisual(resonancePreview))
        {
            resonancePreview = null;
            return false;
        }

        resonancePreviewOpenedFrame = Time.frameCount;
        UpdateResonancePreview();
        SuppressBasicAttackForCurrentFrame();
        return true;
    }

    public bool TryConfirmPreview()
    {
        ResolveReferences();
        if (!IsPreviewActive || !CanReadInput())
            return false;

        SuppressBasicAttackForCurrentFrame();
        if (Time.frameCount <= resonancePreviewOpenedFrame || IsPointerOverUI())
            return false;

        UpdateResonancePreview();
        if (!ResonancePreviewIsValid || owner == null || !owner.CanBeginSkillTargeting ||
            !(owner.SkillE is WizardSkillSO skill) || skill != resonancePreview.Skill)
        {
            return false;
        }

        if (skillInput == null)
            ResolveReferences();
        NetworkPlayerSkills networkSkills = GetComponent<NetworkPlayerSkills>();
        if (networkSkills != null && networkSkills.IsSpawned)
        {
            bool submitted = skillInput != null && skillInput.RequestSkill(
                NetworkSkillSlot.E, resonancePreview.Center, true);
            if (submitted)
                CancelPreview();
            return submitted;
        }

        if (combat == null || !combat.TryPrepareResonanceCast(
                skill, resonancePreview.Center, resonancePreview.Normal))
        {
            return false;
        }

        bool cast = skillInput != null && skillInput.RequestSkill(
            NetworkSkillSlot.E, resonancePreview.Center, true);
        if (!cast)
            combat.CancelPreparedResonanceCast();
        else
            CancelPreview();

        return cast;
    }

    public void CancelPreview()
    {
        resonancePreviewOpenedFrame = -1;
        ResonancePreview preview = resonancePreview;
        if (preview == null)
            return;

        resonancePreview = null;
        combat?.CancelPreparedResonanceCast();
        if (preview.VisualRoot != null)
        {
            preview.VisualRoot.SetActive(false);
            Destroy(preview.VisualRoot);
        }
        if (preview.LineMaterial != null)
            Destroy(preview.LineMaterial);
        if (preview.FillMesh != null)
            Destroy(preview.FillMesh);
        SuppressBasicAttackForCurrentFrame();
    }

    internal bool HandleSkillEInput()
    {
        ResolveReferences();
        if (IsPreviewActive && Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
        {
            CancelPreview();
            return true;
        }
        if (owner == null || !(owner.SkillE is WizardSkillSO skill) ||
            skill.Kind != WizardSkillKind.ResonanceField)
        {
            if (IsPreviewActive) CancelPreview();
            return false;
        }

        if ((Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ||
            (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame))
        {
            CancelPreview();
            return true;
        }
        if (IsPointerOverUI()) return true;
        if (IsPreviewActive)
            TryConfirmPreview();
        else
            TryBeginPreview();
        return true;
    }

    private void UpdateResonancePreview()
    {
        ResolveReferences();
        ResonancePreview preview = resonancePreview;
        if (preview == null)
            return;

        if (owner == null || !owner.isActiveAndEnabled || owner.IsSessionEnded ||
            preview.Skill == null || preview.Skill != owner.SkillE ||
            preview.Skill.Kind != WizardSkillKind.ResonanceField || owner.ECharges <= 0 ||
            owner.IsPlayerDead || IsGamePaused || combat == null || !combat.isActiveAndEnabled ||
            !combat.gameObject.activeInHierarchy)
        {
            CancelPreview();
            return;
        }

        preview.HasGroundTarget = combat.TryFindGroundCandidate(preview.Skill.ResonanceFieldRange,
            out Vector3 position, out Vector3 normal, out bool withinRange);
        if (preview.HasGroundTarget)
        {
            preview.Center = position;
            preview.Normal = normal;
        }

        preview.IsValid = preview.HasGroundTarget && withinRange && owner.ECharges > 0 &&
                          !owner.IsPlayerDead && !IsGamePaused;

        if (preview.RangeRenderer == null)
            return;

        Color rangeColor = preview.IsValid
            ? new Color(0.25f, 0.85f, 1f, 0.95f)
            : new Color(1f, 0.12f, 0.12f, 0.95f);
        Color fieldColor = preview.IsValid ? previewValidColor : previewInvalidColor;
        preview.RangeRenderer.startColor = rangeColor;
        preview.RangeRenderer.endColor = rangeColor;
        SetPreviewRing(preview.RangeRenderer, owner.transform.position, Vector3.up,
            preview.Skill.ResonanceFieldRange, 0.04f, true);

        preview.FieldRenderer.enabled = preview.HasGroundTarget;
        preview.FieldRenderer.startColor = fieldColor;
        preview.FieldRenderer.endColor = fieldColor;
        preview.FillRenderer.enabled = preview.HasGroundTarget;
        if (preview.HasGroundTarget)
        {
            SetPreviewRing(preview.FieldRenderer, preview.Center, preview.Normal,
                preview.Skill.ResonanceFieldRadius, 0.055f, false);
            Transform fill = preview.FillRenderer.transform;
            fill.SetPositionAndRotation(preview.Center + preview.Normal * 0.045f,
                Quaternion.FromToRotation(Vector3.up, preview.Normal));
            fill.localScale = Vector3.one;
            float parentScale = fill.lossyScale.x;
            fill.localScale = Vector3.one * (preview.Skill.ResonanceFieldRadius / parentScale);
            fieldColor.a = previewFillOpacity;
            preview.FillTint.SetColor("_BaseColor", fieldColor);
            preview.FillTint.SetColor("_Color", fieldColor);
            preview.FillRenderer.SetPropertyBlock(preview.FillTint);
        }
    }

    private bool CreateResonancePreviewVisual(ResonancePreview preview)
    {
        ResolveReferences();
        if (combat == null)
            return false;

        preview.LineMaterial = combat.CreateResonanceLineMaterial();
        if (preview.LineMaterial == null)
            return false;

        preview.VisualRoot = new GameObject("WizardResonancePreview");
        preview.VisualRoot.transform.SetParent(transform, false);
        preview.RangeRenderer = CreatePreviewLine(preview.VisualRoot.transform,
            "WizardResonancePreviewRange", preview.LineMaterial, 0.05f);
        preview.FieldRenderer = CreatePreviewLine(preview.VisualRoot.transform,
            "WizardResonancePreviewField", preview.LineMaterial, 0.105f);
        CreatePreviewFill(preview);
        if (preview.RangeRenderer != null && preview.FieldRenderer != null)
            return true;

        preview.VisualRoot.SetActive(false);
        Destroy(preview.VisualRoot);
        Destroy(preview.LineMaterial);
        Destroy(preview.FillMesh);
        preview.VisualRoot = null;
        preview.LineMaterial = null;
        preview.RangeRenderer = null;
        preview.FieldRenderer = null;
        return false;
    }

    private static void CreatePreviewFill(ResonancePreview preview)
    {
        const int segments = 64;
        Vector3[] vertices = new Vector3[segments + 1];
        int[] triangles = new int[segments * 3];
        for (int i = 0; i < segments; i++)
        {
            float angle = Mathf.PI * 2f * i / segments;
            vertices[i + 1] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = (i + 1) % segments + 1;
            triangles[i * 3 + 2] = i + 1;
        }
        preview.FillMesh = new Mesh { name = "WizardResonancePreviewFill" };
        preview.FillMesh.vertices = vertices;
        preview.FillMesh.triangles = triangles;
        preview.FillMesh.RecalculateNormals();
        preview.FillMesh.RecalculateBounds();
        GameObject fill = new GameObject("WizardResonancePreviewFill");
        fill.transform.SetParent(preview.VisualRoot.transform, false);
        fill.AddComponent<MeshFilter>().sharedMesh = preview.FillMesh;
        preview.FillRenderer = fill.AddComponent<MeshRenderer>();
        preview.FillRenderer.sharedMaterial = preview.LineMaterial;
        preview.FillRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        preview.FillRenderer.receiveShadows = false;
        preview.FillRenderer.enabled = false;
        preview.FillTint = new MaterialPropertyBlock();
    }

    private static LineRenderer CreatePreviewLine(Transform parent, string objectName, Material material,
        float width)
    {
        GameObject lineObject = new GameObject(objectName);
        lineObject.transform.SetParent(parent, false);
        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = true;
        line.positionCount = 64;
        line.startWidth = width;
        line.endWidth = width;
        line.sharedMaterial = material;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Tile;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        return line;
    }

    private void SetPreviewRing(LineRenderer line, Vector3 center, Vector3 normal,
        float radius, float surfaceOffset, bool projectToGround)
    {
        normal.Normalize();
        Vector3 tangent = Vector3.Cross(normal, Vector3.right);
        if (tangent.sqrMagnitude < 0.001f)
            tangent = Vector3.Cross(normal, Vector3.forward);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;
        Vector3 ringCenter = center + normal * surfaceOffset;
        for (int i = 0; i < line.positionCount; i++)
        {
            float angle = Mathf.PI * 2f * i / line.positionCount;
            Vector3 offset = tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle);
            Vector3 point = ringCenter + offset * radius;
            if (projectToGround)
                point = ProjectPreviewPointToGround(point, surfaceOffset);
            line.SetPosition(i, point);
        }
    }

    private Vector3 ProjectPreviewPointToGround(Vector3 point, float surfaceOffset)
    {
        const float ProbeHeight = 2f;
        const float ProbeDistance = 20f;
        Ray ray = new Ray(new Vector3(point.x, Mathf.Max(point.y, owner.transform.position.y) + ProbeHeight,
            point.z), Vector3.down);
        int hitCount;
        while (true)
        {
            hitCount = Physics.RaycastNonAlloc(ray, groundHitBuffer, ProbeDistance, ~0,
                QueryTriggerInteraction.Ignore);
            if (hitCount < groundHitBuffer.Length)
                break;

            System.Array.Resize(ref groundHitBuffer, groundHitBuffer.Length * 2);
        }

        float nearestDistance = float.PositiveInfinity;
        RaycastHit nearest = default;
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = groundHitBuffer[i];
            if (hit.collider == null || (combat != null && combat.IsOwnerCollider(hit.collider)) ||
                hit.collider.GetComponentInParent<EnemyHealth>() != null ||
                hit.normal.y < Mathf.Cos(50f * Mathf.Deg2Rad) || hit.distance >= nearestDistance)
            {
                continue;
            }

            nearest = hit;
            nearestDistance = hit.distance;
        }

        if (nearestDistance == float.PositiveInfinity)
            return point;

        return new Vector3(point.x, nearest.point.y + surfaceOffset, point.z);
    }

    private void SuppressBasicAttackForCurrentFrame()
    {
        int suppressThroughFrame = Time.frameCount;
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            suppressThroughFrame++;
        suppressBasicAttackThroughFrame = Mathf.Max(suppressBasicAttackThroughFrame, suppressThroughFrame);
        if (playerInput == null)
            playerInput = GetComponent<PlayerInputHandler>();
        if (playerInput != null)
            playerInput.AttackTriggered = false;
    }

    private bool CanReadInput()
    {
        ResolveReferences();
        return skillInput != null && skillInput.CanReadInput;
    }

    private bool IsPointerOverUI()
    {
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem == null)
            return false;

        if (eventSystem.IsPointerOverGameObject())
            return true;

        Mouse mouse = Mouse.current;
        return mouse != null && eventSystem.IsPointerOverGameObject(mouse.deviceId);
    }

    private void ResolveReferences()
    {
        if (owner == null)
            owner = GetComponent<PlayerSkillController>();
        if (skillInput == null)
            skillInput = GetComponent<PlayerSkillInput>();
        if (combat == null)
            combat = GetComponent<WizardCombat>();
    }

    private static bool IsGamePaused => Time.timeScale <= 0f ||
        (GameManager.Instance != null && GameManager.Instance.IsPaused);
}
