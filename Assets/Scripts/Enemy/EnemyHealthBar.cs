using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(EnemyHealth))]
// 적 머리 위에 표시되는 월드 스페이스 체력바
public class EnemyHealthBar : MonoBehaviour
{
    private const float HorizontalPadding = 0.1f;
    private const float VerticalPadding = 0.06f;

    [SerializeField] private Vector3 worldOffset = new Vector3(0f, 2.4f, 0f);
    [SerializeField] private Vector2 barSize = new Vector2(1.5f, 0.16f);
    [SerializeField] private bool showOnlyWhenDamaged = true;
    [SerializeField] private float visibleDuration = 2f;
    [SerializeField] private Color backgroundColor = new Color(0.08f, 0.08f, 0.08f, 0.75f);
    [SerializeField] private Color fillColor = new Color(0.86f, 0.12f, 0.1f, 0.9f);

    private EnemyHealth enemyHealth;
    private Canvas canvas;
    private RectTransform fillRect;
    private Transform cameraTransform;
    private float hideAtTime;

    private void Awake()
    {
        enemyHealth = GetComponent<EnemyHealth>();
        CreateHealthBar();
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
        Refresh();
        SetVisible(!showOnlyWhenDamaged && enemyHealth != null && !enemyHealth.IsDead);
    }

    private void OnDisable()
    {
        if (enemyHealth != null)
        {
            enemyHealth.OnHealthChanged -= HandleHealthChanged;
        }
    }

    private void LateUpdate()
    {
        if (canvas == null)
        {
            return;
        }

        canvas.transform.position = transform.position + worldOffset;

        // 카메라를 향하도록 회전해서 어느 방향에서도 체력바가 보이게 함
        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }

        if (cameraTransform != null)
        {
            canvas.transform.rotation = cameraTransform.rotation;
        }

        if (showOnlyWhenDamaged && canvas.gameObject.activeSelf && Time.time >= hideAtTime)
        {
            SetVisible(false);
        }
    }

    private void HandleHealthChanged(int currentHealth, int maxHealth)
    {
        Refresh();

        if (enemyHealth == null || enemyHealth.IsDead || currentHealth <= 0)
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);
        hideAtTime = Time.time + visibleDuration;
    }

    // 현재 체력 비율에 맞춰 Fill 오브젝트의 실제 가로 길이를 조정
    private void Refresh()
    {
        if (fillRect == null || enemyHealth == null)
        {
            return;
        }

        float percent = Mathf.Clamp01(enemyHealth.HealthPercent);
        fillRect.sizeDelta = new Vector2(GetFillWidth(percent), GetFillHeight());
    }

    private void CreateHealthBar()
    {
        // 프리팹마다 UI 오브젝트를 따로 만들 필요 없도록 런타임에 생성
        GameObject canvasObject = new GameObject("EnemyHealthBar", typeof(RectTransform), typeof(Canvas));
        canvasObject.transform.SetParent(transform, false);

        canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 20;

        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
        canvasRect.sizeDelta = barSize;

        Image backgroundImage = CreateImage("Background", canvasRect, backgroundColor);
        RectTransform backgroundRect = backgroundImage.rectTransform;
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;

        Image fillImage = CreateImage("Fill", canvasRect, fillColor);

        fillRect = fillImage.rectTransform;
        fillRect.anchorMin = new Vector2(0f, 0.5f);
        fillRect.anchorMax = new Vector2(0f, 0.5f);
        fillRect.pivot = new Vector2(0f, 0.5f);
        fillRect.anchoredPosition = new Vector2(HorizontalPadding * 0.5f, 0f);
        fillRect.sizeDelta = new Vector2(GetFillWidth(1f), GetFillHeight());

        SetVisible(!showOnlyWhenDamaged);
    }

    private Image CreateImage(string objectName, Transform parent, Color color)
    {
        GameObject imageObject = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        imageObject.transform.SetParent(parent, false);

        Image image = imageObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private void SetVisible(bool isVisible)
    {
        if (canvas != null)
        {
            canvas.gameObject.SetActive(isVisible);
        }
    }

    private float GetFillWidth(float percent)
    {
        return Mathf.Max(0f, barSize.x - HorizontalPadding) * Mathf.Clamp01(percent);
    }

    private float GetFillHeight()
    {
        return Mathf.Max(0.01f, barSize.y - VerticalPadding);
    }

    private void OnValidate()
    {
        barSize.x = Mathf.Max(0.1f, barSize.x);
        barSize.y = Mathf.Max(0.03f, barSize.y);
        visibleDuration = Mathf.Max(0.1f, visibleDuration);
    }
}
