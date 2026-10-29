// R 궁극기 게이지. 피해량으로 충전, 시전 시 소모.
using System;
using UnityEngine;

public sealed class PlayerUltimateGauge : MonoBehaviour
{
    // 최대 게이지
    [SerializeField, Min(1f)] private float maxGauge = 100f;
    // 현재 게이지
    [SerializeField, Min(0f)] private float currentGauge;
    // 피해당 충전량
    [SerializeField, Min(0f)] private float gaugePerDamage = 1f;

    private PlayerDamageDealer damageDealer;
    private NinjaUltimate ninjaUltimate;
    private WizardCombat wizardCombat;

    public float Current => currentGauge;
    public float Max => maxGauge;
    public float Normalized => currentGauge / maxGauge;
    public bool IsFull => currentGauge >= maxGauge;
    public event Action<float> Changed;

    private void Awake()
    {
        damageDealer = GetComponent<PlayerDamageDealer>();
        if (damageDealer != null)
            damageDealer.OnDamageDealt += HandleDamageDealt;
    }

    private void OnDestroy()
    {
        if (damageDealer != null)
            damageDealer.OnDamageDealt -= HandleDamageDealt;
    }

    // 충전
    public void Add(float amount)
    {
        // 서버·오프라인 시뮬레이션 권한 및 유효 수치 검사
        if (!NetworkAuthority.CanSimulate(gameObject) || float.IsNaN(amount) || float.IsInfinity(amount))
            return;
        // 게이지 이후 전투 컴포넌트 추가를 지원하는 지연 참조
        if (ninjaUltimate == null)
            ninjaUltimate = GetComponent<NinjaUltimate>();
        if (wizardCombat == null)
            wizardCombat = GetComponent<WizardCombat>();
        if ((ninjaUltimate != null && ninjaUltimate.IsUltimateActive) ||
            (wizardCombat != null && wizardCombat.IsUltimateActive))
            return;

        currentGauge = Mathf.Clamp(currentGauge + Mathf.Max(0f, amount), 0f, maxGauge);
        Changed?.Invoke(Normalized);
    }

    // 풀일 때만 소모
    public bool TryConsume()
    {
        // 시뮬레이션 권한 없는 클라이언트의 원본 게이지 소모 차단
        if (!NetworkAuthority.CanSimulate(gameObject) || !IsFull)
            return false;

        currentGauge = 0f;
        Changed?.Invoke(0f);
        return true;
    }

    // 실제 피해량만큼 충전
    private void HandleDamageDealt(int damage, PlayerDamageType damageType)
    {
        Add(damage * gaugePerDamage);
    }

    // 에디터 테스트 충전
    [ContextMenu("Fill Gauge For Test")]
    private void FillForTest()
    {
        // 테스트 충전에도 동일 시뮬레이션 권한 적용
        if (!NetworkAuthority.CanSimulate(gameObject)) return;
        currentGauge = maxGauge;
        Changed?.Invoke(1f);
    }

    // 클라이언트 복제 게이지 적용: 최대치 1 이상·현재치 범위 제한·변경 알림
    internal void ApplyNetworkReplica(float current, float maximum)
    {
        // 원본 게이지는 서버가 유지; 복제본만 반영.
        if (NetworkAuthority.CanSimulate(gameObject)) return;
        float nextMax = Mathf.Max(1f, maximum);
        float nextCurrent = Mathf.Clamp(current, 0f, nextMax);
        if (currentGauge == nextCurrent && maxGauge == nextMax) return;
        maxGauge = nextMax;
        currentGauge = nextCurrent;
        Changed?.Invoke(Normalized);
    }
}
