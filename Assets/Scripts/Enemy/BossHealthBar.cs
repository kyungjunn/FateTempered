using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(EnemyHealth))]
// 보스가 존재하는 동안 화면 상단에 전용 체력 UI를 표시
public class BossHealthBar : MonoBehaviour
{
    [Header("Boss UI")]
    [SerializeField] private string bossName = "TREE MONSTER";
    [SerializeField] private float topOffset = 36f;
    [SerializeField] private float barWidth = 600f;
    [SerializeField] private float barHeight = 30f;
    [SerializeField] private float fillSpeed = 3f;
    [SerializeField] private float damageLagSpeed = 0.45f;
    [SerializeField] private float deathVisibleDuration = 1.25f;

    [Header("Colors")]
    [SerializeField] private Color frameColor = new Color(0.03f, 0.025f, 0.02f, 0.96f);
    [SerializeField] private Color backgroundColor = new Color(0.08f, 0.07f, 0.06f, 0.94f);
    [SerializeField] private Color damageColor = new Color(0.84f, 0.64f, 0.28f, 0.95f);
    [SerializeField] private Color fillColor = new Color(0.48f, 0.045f, 0.035f, 0.98f);

    private const float BarPadding = 3f;

    private EnemyHealth enemyHealth;
    private GameObject hudRoot;
    private RectTransform currentFillRect;
    private RectTransform damageFillRect;
    private TextMeshProUGUI healthText;
    private float targetPercent = 1f;
    private float displayedPercent = 1f;
    private float damagePercent = 1f;
    private float hideAtTime = -1f;

    private float InnerBarWidth => Mathf.Max(1f, barWidth - BarPadding * 2f);

    private void Awake()
    {
        enemyHealth = GetComponent<EnemyHealth>();
        CreateHud();
    }

    private void OnEnable()
    {
        if (enemyHealth == null)
        {
            enemyHealth = GetComponent<EnemyHealth>();
        }

        if (enemyHealth != null)
        {
            enemyHealth.OnHealthChanged += HandleHealthChanged;
        }
    }

    private void Start()
    {
        RefreshTarget();
        ApplyVisuals();
    }

    private void Update()
    {
        float deltaTime = Time.unscaledDeltaTime;
        displayedPercent = Mathf.MoveTowards(displayedPercent, targetPercent, fillSpeed * deltaTime);

        if (damagePercent > targetPercent)
        {
            damagePercent = Mathf.MoveTowards(damagePercent, targetPercent, damageLagSpeed * deltaTime);
        }
        else
        {
            damagePercent = targetPercent;
        }

        ApplyVisuals();

        if (hideAtTime >= 0f && Time.unscaledTime >= hideAtTime && hudRoot != null)
        {
            hudRoot.SetActive(false);
        }
    }

    private void OnDisable()
    {
        if (enemyHealth != null)
        {
            enemyHealth.OnHealthChanged -= HandleHealthChanged;
        }
    }

    private void OnDestroy()
    {
        if (hudRoot != null)
        {
            Destroy(hudRoot);
        }
    }

    private void HandleHealthChanged(int currentHealth, int maxHealth)
    {
        targetPercent = maxHealth > 0 ? Mathf.Clamp01((float)currentHealth / maxHealth) : 0f;

        if (currentHealth <= 0)
        {
            hideAtTime = Time.unscaledTime + deathVisibleDuration;
        }
    }

    private void RefreshTarget()
    {
        if (enemyHealth == null)
        {
            return;
        }

        targetPercent = enemyHealth.HealthPercent;
        displayedPercent = targetPercent;
        damagePercent = targetPercent;
    }

    private void ApplyVisuals()
    {
        if (currentFillRect != null)
        {
            currentFillRect.sizeDelta = new Vector2(InnerBarWidth * displayedPercent, barHeight - BarPadding * 2f);
        }

        if (damageFillRect != null)
        {
            damageFillRect.sizeDelta = new Vector2(InnerBarWidth * damagePercent, barHeight - BarPadding * 2f);
        }

        if (healthText != null && enemyHealth != null)
        {
            healthText.text = $"{enemyHealth.CurrentHealth:N0} / {enemyHealth.MaxHealth:N0}";
        }
    }

    private void CreateHud()
    {
        hudRoot = new GameObject("BossHealthHUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));

        Canvas canvas = hudRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = hudRoot.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        RectTransform container = CreateRect("BossHealthContainer", hudRoot.transform);
        container.anchorMin = new Vector2(0.5f, 1f);
        container.anchorMax = new Vector2(0.5f, 1f);
        container.pivot = new Vector2(0.5f, 1f);
        container.anchoredPosition = new Vector2(0f, -topOffset);
        container.sizeDelta = new Vector2(barWidth, 72f);

        TextMeshProUGUI title = CreateText("BossName", container, bossName, 24f, FontStyles.SmallCaps);
        RectTransform titleRect = title.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.anchoredPosition = Vector2.zero;
        titleRect.sizeDelta = new Vector2(0f, 30f);

        Image frame = CreateImage("Frame", container, frameColor);
        RectTransform frameRect = frame.rectTransform;
        frameRect.anchorMin = new Vector2(0.5f, 1f);
        frameRect.anchorMax = new Vector2(0.5f, 1f);
        frameRect.pivot = new Vector2(0.5f, 1f);
        frameRect.anchoredPosition = new Vector2(0f, -34f);
        frameRect.sizeDelta = new Vector2(barWidth, barHeight);

        Image background = CreateImage("Background", frameRect, backgroundColor);
        StretchToParent(background.rectTransform, BarPadding);

        damageFillRect = CreateFill("DamageFill", frameRect, damageColor);
        currentFillRect = CreateFill("CurrentFill", frameRect, fillColor);

        healthText = CreateText("HealthValue", frameRect, string.Empty, 15f, FontStyles.Bold);
        StretchToParent(healthText.rectTransform, 0f);
    }

    private RectTransform CreateFill(string objectName, Transform parent, Color color)
    {
        Image image = CreateImage(objectName, parent, color);
        RectTransform rect = image.rectTransform;
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = new Vector2(BarPadding, 0f);
        rect.sizeDelta = new Vector2(InnerBarWidth, barHeight - BarPadding * 2f);
        return rect;
    }

    private static RectTransform CreateRect(string objectName, Transform parent)
    {
        GameObject rectObject = new GameObject(objectName, typeof(RectTransform));
        rectObject.transform.SetParent(parent, false);
        return rectObject.GetComponent<RectTransform>();
    }

    private static Image CreateImage(string objectName, Transform parent, Color color)
    {
        GameObject imageObject = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        imageObject.transform.SetParent(parent, false);

        Image image = imageObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static TextMeshProUGUI CreateText(
        string objectName,
        Transform parent,
        string value,
        float fontSize,
        FontStyles fontStyle)
    {
        GameObject textObject = new GameObject(
            objectName,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);

        TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return text;
    }

    private static void StretchToParent(RectTransform rect, float padding)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(padding, padding);
        rect.offsetMax = new Vector2(-padding, -padding);
    }

    private void OnValidate()
    {
        topOffset = Mathf.Max(0f, topOffset);
        barWidth = Mathf.Max(240f, barWidth);
        barHeight = Mathf.Max(18f, barHeight);
        fillSpeed = Mathf.Max(0.1f, fillSpeed);
        damageLagSpeed = Mathf.Max(0.05f, damageLagSpeed);
        deathVisibleDuration = Mathf.Max(0f, deathVisibleDuration);
    }
}
