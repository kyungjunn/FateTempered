using UnityEngine;

// 한 판 동안의 개인 기록. 시뮬레이션 인스턴스(오프라인·서버)에서만 누적한다.
// 플레이어 오브젝트는 판마다 새로 생성되므로 인스턴스 수명이 곧 한 판이다.
[RequireComponent(typeof(PlayerDamageDealer))]
[RequireComponent(typeof(PlayerHealth))]
public sealed class PlayerRunStats : MonoBehaviour
{
    private PlayerDamageDealer damageDealer;
    private PlayerHealth health;

    public long DamageDealt { get; private set; }
    public int Kills { get; private set; }
    // 살아 있던 시간만 센다 (쓰러져 있던 시간 제외).
    public float AliveSeconds { get; private set; }

    private void Awake()
    {
        damageDealer = GetComponent<PlayerDamageDealer>();
        health = GetComponent<PlayerHealth>();
    }

    private void OnEnable()
    {
        damageDealer.OnDamageDealt += HandleDamageDealt;
    }

    private void OnDisable()
    {
        damageDealer.OnDamageDealt -= HandleDamageDealt;
    }

    private void Update()
    {
        if (!NetworkAuthority.CanSimulate(gameObject) || health.IsDead) return;
        GameManager session = GameManager.Instance;
        if (session == null || !session.IsActiveSession || session.IsPaused) return;
        AliveSeconds += Time.deltaTime;
    }

    // EnemyHealth가 마지막 타격자에게 한 번 호출한다.
    public void AddKill()
    {
        if (NetworkAuthority.CanSimulate(gameObject)) Kills++;
    }

    private void HandleDamageDealt(int damage, PlayerDamageType type)
    {
        if (damage > 0) DamageDealt += damage;
    }
}
