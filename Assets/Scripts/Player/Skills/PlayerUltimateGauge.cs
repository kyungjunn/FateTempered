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
        if (!NetworkHealth.CanWrite(gameObject) || float.IsNaN(amount) || float.IsInfinity(amount))
            return;
        // Lazy lookup also supports runtime factories that add combat after the gauge.
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
        if (!NetworkHealth.CanWrite(gameObject) || !IsFull)
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
        if (!NetworkHealth.CanWrite(gameObject)) return;
        currentGauge = maxGauge;
        Changed?.Invoke(1f);
    }

    internal void ApplyNetworkReplica(float current, float maximum)
    {
        if (NetworkHealth.CanWrite(gameObject)) return;
        float nextMax = Mathf.Max(1f, maximum);
        float nextCurrent = Mathf.Clamp(current, 0f, nextMax);
        if (currentGauge == nextCurrent && maxGauge == nextMax) return;
        maxGauge = nextMax;
        currentGauge = nextCurrent;
        Changed?.Invoke(Normalized);
    }
}
