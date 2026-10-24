using UnityEngine;
using UnityEngine.UI;
using TMPro;

// 경험치 UI. 스포너 스냅샷.
public class InGameExpUI : MonoBehaviour
{
    [Header("UI 컴포넌트")]
    [SerializeField] private Slider expSlider;
    [SerializeField] private TextMeshProUGUI levelText;
    [SerializeField] private TextMeshProUGUI expValueText;

    [Header("게이지 속도")]
    [SerializeField] private float fillSpeed = 5f;

    private PlayerLevelSystem playerLevelSystem;
    private float targetProgress = 0f;
    private int displayedLevel = 1;
    private void Awake()
    {
        ConfigureSlider();
    }

    private void OnEnable()
    {
        SpawnManager.OnPlayerSpawned += BindLevelSystem;
        // 이벤트 + 현재 스냅샷
        BindLevelSystem(SpawnManager.Instance?.SpawnedPlayer);
    }

    private void OnDisable()
    {
        SpawnManager.OnPlayerSpawned -= BindLevelSystem;
        UnbindLevelSystem();
    }


    private void BindLevelSystem(GameObject player)
    {
        UnbindLevelSystem();
        targetProgress = 0f;
        displayedLevel = 1;

        if (expSlider != null)
        {
            expSlider.SetValueWithoutNotify(0f);
        }

        if (levelText != null)
        {
            levelText.text = string.Empty;
        }

        if (expValueText != null)
        {
            expValueText.text = string.Empty;
        }

        if (player == null) return;

        playerLevelSystem = player.GetComponent<PlayerLevelSystem>();
        if (playerLevelSystem != null)
        {
            playerLevelSystem.onExpChanged += RefreshExpUI;
            playerLevelSystem.onLevelUp += HandleLevelUp;

            // 초기 데이터 동기화
            displayedLevel = playerLevelSystem.level;
            targetProgress = playerLevelSystem.GetExpPercent();
            if (expSlider != null)
            {
                expSlider.value = targetProgress;
            }

            UpdateTextUI();
        }
    }

    private void UnbindLevelSystem()
    {
        if (playerLevelSystem != null)
        {
            playerLevelSystem.onExpChanged -= RefreshExpUI;
            playerLevelSystem.onLevelUp -= HandleLevelUp;
        }
        playerLevelSystem = null;
    }

    private void Update()
    {
        if (playerLevelSystem == null || expSlider == null) return;

        expSlider.value = Mathf.MoveTowards(expSlider.value, targetProgress, fillSpeed * Time.deltaTime);
    }

    private void ConfigureSlider()
    {
        if (expSlider == null) return;

        expSlider.minValue = 0f;
        expSlider.maxValue = 1f;
        expSlider.wholeNumbers = false;
        expSlider.interactable = false;

        if (expSlider.fillRect != null && expSlider.fillRect.TryGetComponent(out Image fillImage))
            fillImage.type = Image.Type.Simple;
    }

    // 경험치 변경 시 호출 (단순 수치 증가)
    private void RefreshExpUI()
    {
        if (playerLevelSystem == null) return;

        targetProgress = playerLevelSystem.GetExpPercent();
        UpdateTextUI();
    }

    // 레벨업 시 호출
    private void HandleLevelUp()
    {
        if (playerLevelSystem == null) return;

        // 레벨업하는 순간 직전 레벨의 바를 꽉 채우는 시각적 연출을 주거나, 즉시 0으로 리셋
        expSlider.value = 0f;
        targetProgress = playerLevelSystem.GetExpPercent();

        displayedLevel = playerLevelSystem.level;
        UpdateTextUI();
    }

    // 텍스트 정보 일괄 갱신
    private void UpdateTextUI()
    {
        if (playerLevelSystem == null) return;

        // 레벨 텍스트 갱신 
        if (levelText != null)
        {
            levelText.text = $"LV. {displayedLevel}";
        }

        // 경험치 디테일 텍스트 갱신
        if (expValueText != null)
        {
            float percent = playerLevelSystem.GetExpPercent() * 100f;
            expValueText.text = $"EXP {percent:F0}%";
        }
    }
}
