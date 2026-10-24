using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class LoadingPanelUI : MonoBehaviour
{
    [Header("UI Components")]
    [SerializeField] private Slider progressBar;
    [SerializeField] private TextMeshProUGUI tipText;

    [Header("Settings")]
    [SerializeField] private float fillSpeed = 0.5f;

    private float targetProgress = 0f;
    private bool isFillsDone = false;

    // 랜덤 팁 문구 리스트
    private readonly string[] loadingTips = new string[]
    {
        "레벨을 올리고 증강을 선택하세요 ! 플레이 스타일에 맞춰 강력한 능력을 조합해 보세요 !",
        "Let's go Pray !",
        "꿈을 펼쳐보세요 !"
    };

    public void ResetLoadingUI()
    {
        progressBar.value = 0f;
        targetProgress = 0f;
        isFillsDone = false;

        // 팁 문구 랜덤
        if (loadingTips.Length > 0)
        {
            int randomIndex = Random.Range(0, loadingTips.Length);
            tipText.text = loadingTips[randomIndex];
        }
    }

    public void SetTargetProgress(float progress)
    {
        targetProgress = progress;
    }

    // 게이지가 다 찼는지 확인할 프로퍼티
    public bool IsLoadingVisualDone => isFillsDone;

    private void Update()
    {
        if (progressBar == null || isFillsDone)
        {
            return;
        }

        // UI와 같은 건 unscaled를 사용
        progressBar.value = Mathf.MoveTowards(progressBar.value, targetProgress, fillSpeed * Time.unscaledDeltaTime);

        if (progressBar.value >= 1f )
        {
            isFillsDone = true;
        }
    }
}
