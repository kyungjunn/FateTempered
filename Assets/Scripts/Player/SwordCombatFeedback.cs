using System.Collections;
using UnityEngine;
using Unity.Cinemachine;

// 전사 기본 공격 전용 피격 연출.
// 타격 판정 스크립트가 실제 접촉 위치를 전달하므로 이펙트가 정확한 위치에서 발생한다.
public class SwordCombatFeedback : MonoBehaviour
{
    [Header("Hit Stop")]
    [SerializeField, Min(0f)] private float hitStopDuration = 0.035f;
    [SerializeField, Range(0.01f, 1f)] private float hitStopTimeScale = 0.18f;

    [Header("Camera Shake")]
    [SerializeField] private CinemachineImpulseSource impulseSource;
    [SerializeField, Min(0f)] private float shakeForce = 0.10f;
    [SerializeField, Min(0f)] private float feedbackInterval = 0.1f;

    [Header("Hit VFX")]
    [SerializeField] private ParticleSystem hitVfx; // 기존 SwordHitVFX 백업용
    [Tooltip("추천: Sparks explode white / blue. 큰 Shockwave는 평타에는 비추천.")]
    [SerializeField] private GameObject hitVfxPrefab;
    [SerializeField, Min(0.01f)] private float hitVfxScale = 0.38f;
    [SerializeField, Min(0.1f)] private float hitVfxLifetime = 0.9f;
    [SerializeField, Min(0f)] private float hitVfxInterval = 0.05f;

    private float nextFeedbackTime;
    private float nextHitVfxTime;
    private Coroutine hitStopRoutine;
    private float originalTimeScale = 1f;

    private void Awake()
    {
        if (impulseSource == null)
            impulseSource = GetComponent<CinemachineImpulseSource>();
    }

    // MeleePlayerAttack에서 실제로 적중했을 때만 호출.
    public void PlayMeleeImpact(Vector3 contactPoint, bool allowHitStop = true)
    {
        if (!isActiveAndEnabled) return;

        // 적 여러 마리를 동시에 베더라도 카메라와 시간을 지나치게 흔들지 않는다.
        if (Time.unscaledTime >= nextFeedbackTime)
        {
            nextFeedbackTime = Time.unscaledTime + feedbackInterval;
            if (allowHitStop) PlayHitStop();
            if (impulseSource != null)
                impulseSource.GenerateImpulseWithForce(shakeForce);
        }

        if (hitVfxPrefab != null)
        {
            SpawnImpactVfx(contactPoint);
        }
        else if (hitVfx != null && Time.unscaledTime >= nextHitVfxTime)
        {
            nextHitVfxTime = Time.unscaledTime + hitVfxInterval;
            hitVfx.Play(true);
        }

        // EnemyHealth가 이미 피격음과 빨간 피격 플래시를 처리하므로
        // 여기서는 같은 효과음을 중복 재생하지 않는다.
    }

    private void SpawnImpactVfx(Vector3 contactPoint)
    {
        GameObject vfx = Instantiate(
            hitVfxPrefab, contactPoint, hitVfxPrefab.transform.rotation);
        vfx.transform.localScale *= hitVfxScale;

        ParticleSystem[] particles = vfx.GetComponentsInChildren<ParticleSystem>(true);
        foreach (ParticleSystem particle in particles)
        {
            var main = particle.main;
            main.loop = false;
            particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
        foreach (ParticleSystem particle in particles)
            particle.Play(false);

        Destroy(vfx, hitVfxLifetime);
    }

    private void PlayHitStop()
    {
        if (hitStopRoutine != null || hitStopDuration <= 0f || Time.timeScale <= 0f ||
            (GameManager.Instance != null && GameManager.Instance.IsPaused))
            return;

        hitStopRoutine = StartCoroutine(HitStopRoutine());
    }

    private IEnumerator HitStopRoutine()
    {
        originalTimeScale = Time.timeScale;
        Time.timeScale = hitStopTimeScale;
        yield return new WaitForSecondsRealtime(hitStopDuration);
        RestoreTimeScaleIfOwned();
        hitStopRoutine = null;
    }

    private void RestoreTimeScaleIfOwned()
    {
        if ((GameManager.Instance == null || !GameManager.Instance.IsPaused) &&
            Mathf.Approximately(Time.timeScale, hitStopTimeScale))
            Time.timeScale = originalTimeScale;
    }

    private void OnDisable()
    {
        if (hitStopRoutine == null) return;
        StopCoroutine(hitStopRoutine);
        hitStopRoutine = null;
        RestoreTimeScaleIfOwned();
    }

    private void OnValidate()
    {
        hitStopDuration = Mathf.Max(0f, hitStopDuration);
        hitStopTimeScale = Mathf.Clamp(hitStopTimeScale, 0.01f, 1f);
        shakeForce = Mathf.Max(0f, shakeForce);
        feedbackInterval = Mathf.Max(0f, feedbackInterval);
        hitVfxScale = Mathf.Max(0.01f, hitVfxScale);
        hitVfxLifetime = Mathf.Max(0.1f, hitVfxLifetime);
        hitVfxInterval = Mathf.Max(0f, hitVfxInterval);
    }
}
