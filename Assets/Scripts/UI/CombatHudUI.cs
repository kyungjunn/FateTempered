using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 전투 HUD. 수치 계산은 플레이어 측.
public sealed class CombatHudUI : MonoBehaviour
{
    [SerializeField] private Image basicAttackIcon;
    [SerializeField] private SkillSlotUI skillQ;
    [SerializeField] private SkillSlotUI skillE;
    [SerializeField] private Image ultimateFill;
    [SerializeField] private Image ultimateIcon;
    [SerializeField] private TextMeshProUGUI ultimatePercent;
    [SerializeField] private TextMeshProUGUI ultimateKey;
    [SerializeField] private GameObject statsPanel;
    private PlayerSkillController skills;
    private PlayerUltimateGauge gauge;
    private static readonly Color Charging = new Color(0.28f, 0.82f, 1f);
    private static readonly Color Ready = new Color(1f, 0.75f, 0.25f);

    private void Awake()
    {
        statsPanel.SetActive(false);
    }

    private void OnEnable()
    {
        // 이벤트 + 현재 스냅샷
        GameManager.OnLocalPlayerChanged += Bind;
        Bind(GameManager.Instance?.CurrentPlayer);
    }

    private void OnDisable()
    {
        GameManager.OnLocalPlayerChanged -= Bind;
        Unbind();
        statsPanel.SetActive(false);
    }

    private void Unbind()
    {
        // 이전 플레이어의 게이지 이벤트 구독 해제.
        if (gauge != null) gauge.Changed -= RefreshGauge;
        gauge = null;
        skills = null;
    }

    public void Bind(GameObject player)
    {
        Unbind();
        // 플레이어 컴포넌트 참조와 슬롯 아이콘 갱신.
        if (player != null)
        {
            skills = player.GetComponent<PlayerSkillController>();
            gauge = player.GetComponent<PlayerUltimateGauge>();
        }
        basicAttackIcon.sprite = skills != null ? skills.BasicAttackIcon : null;
        basicAttackIcon.enabled = basicAttackIcon.sprite != null;
        skillQ.SetIcon(skills != null && skills.SkillQ != null ? skills.SkillQ.Icon : null);
        skillE.SetIcon(skills != null && skills.SkillE != null ? skills.SkillE.Icon : null);
        ultimateIcon.sprite = skills != null ? skills.UltimateIcon : null;
        ultimateIcon.enabled = ultimateIcon.sprite != null;
        if (gauge != null) gauge.Changed += RefreshGauge;
        // 현재 게이지와 쿨타임 즉시 반영.
        RefreshGauge(gauge != null ? gauge.Normalized : 0f);
        RefreshSkills();
    }

    private void Update()
    {
        // 결과 화면에서는 Tab이 능력치 패널을 다시 열지 않도록 닫아 둔다.
        NetworkGameSession session = NetworkGameSession.Instance;
        // 네트워크 종료 또는 결과 데이터 있으면 능력치 패널 잠금.
        bool matchEnded = (session != null && session.State == NetworkGameSessionState.Ended) ||
                          GameResult.Data.HasValue;
        if (matchEnded)
            statsPanel.SetActive(false);
        else if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
            statsPanel.SetActive(!statsPanel.activeSelf);

        // 쿨타임: 매 프레임 표시 갱신.
        RefreshSkills();
        bool recast = skills != null && skills.UltimateRecastReady;
        // 궁극기 충전 완료 또는 재시전 가능 시 강조색 적용.
        ultimateKey.color = recast || (gauge != null && gauge.IsFull) ? Ready : Color.white;
        ultimateFill.color = recast || (gauge != null && gauge.IsFull) ? Ready : Charging;
    }

    private void RefreshSkills()
    {
        // 컨트롤러의 남은 시간/채널링 상태를 각 스킬 슬롯에 전달.
        skillQ.Refresh(skills != null ? skills.QRemaining : 0f,
            skills != null && skills.SkillQ != null ? skills.SkillQ.Cooldown : 0f,
            skills != null && skills.QChanneling, 1, 1);
        skillE.Refresh(skills != null ? skills.ERemaining : 0f,
            skills != null && skills.SkillE != null ? skills.SkillE.Cooldown : 0f,
            skills != null && skills.EChanneling,
            skills != null ? skills.ECharges : 1,
            skills != null ? skills.EMaxCharges : 1);
    }

    private void RefreshGauge(float value)
    {
        // 정규화된 게이지(0~1)를 채움 비율과 정수 퍼센트로 변환.
        value = Mathf.Clamp01(value);
        ultimateFill.fillAmount = value;
        ultimatePercent.SetText("{0}%", Mathf.FloorToInt(value * 100f));
    }
}
