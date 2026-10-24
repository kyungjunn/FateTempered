using System;
using System.Collections;
using UnityEngine;

// 공용 돌진 이동. 타격/이펙트는 각 스킬의 onStep에서 처리.
public static class SkillDashMovement
{
    public static IEnumerator Move(
        Transform actor, Vector3 direction, float distance, float duration,
        Action<Vector3, Vector3, float> onStep,
        Func<bool> isPaused = null, Func<bool> shouldStop = null)
    {
        // 높이 성분 제거 후 수평 이동 방향 확정.
        direction.y = 0f;
        if (direction.sqrMagnitude <= Mathf.Epsilon)
            direction = Vector3.forward;
        else
            direction.Normalize();

        Vector3 origin = actor.position;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            // 중단 우선, 일시정지 중에는 돌진 시간도 멈춤.
            if (shouldStop != null && shouldStop())
                yield break;

            if (isPaused != null && isPaused())
            {
                yield return null;
                continue;
            }

            float step = Mathf.Min(Time.deltaTime, duration - elapsed);
            if (step <= 0f)
            {
                yield return null;
                continue;
            }

            Vector3 start = actor.position;
            // 누적 시간 비율로 목표 지점 계산: 프레임 오차 누적 방지.
            elapsed += step;
            float traveled = distance * Mathf.Clamp01(elapsed / duration);
            Vector3 end = origin + direction * traveled;
            actor.position = end;
            // 이번 이동 구간을 스킬별 타격/이펙트 처리에 전달.
            onStep?.Invoke(start, end, traveled);
            yield return null;
        }
    }
}
