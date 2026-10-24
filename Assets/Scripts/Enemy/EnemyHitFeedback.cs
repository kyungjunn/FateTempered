using System.Collections;
using UnityEngine;

// 적이 피격됐을 때 짧게 색을 바꿔 타격감을 보여줌
public class EnemyHitFeedback : MonoBehaviour
{
    [SerializeField] private Color flashColor = new Color(1f, 0.15f, 0.08f, 1f);
    [SerializeField] private float flashDuration = 0.12f;

    // URP Lit은 _BaseColor, 일부 기본 셰이더는 _Color를 사용
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private Renderer[] renderers;
    private MaterialPropertyBlock propertyBlock;
    private Coroutine flashRoutine;

    private void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>();
        propertyBlock = new MaterialPropertyBlock();
    }

    public void Play()
    {
        if (renderers == null || renderers.Length == 0)
        {
            return;
        }

        if (flashRoutine != null)
        {
            StopCoroutine(flashRoutine);
        }

        flashRoutine = StartCoroutine(FlashRoutine());
    }

    // 이미 깜빡이는 중이면 이전 연출을 끊고 새 피격 연출을 시작
    private IEnumerator FlashRoutine()
    {
        SetFlashColor(flashColor);
        yield return new WaitForSeconds(flashDuration);
        ClearFlashColor();
        flashRoutine = null;
    }

    private void SetFlashColor(Color color)
    {
        foreach (Renderer targetRenderer in renderers)
        {
            if (targetRenderer == null)
            {
                continue;
            }

            // 공유 머티리얼을 직접 수정하지 않도록 PropertyBlock 사용
            targetRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(BaseColorId, color);
            propertyBlock.SetColor(ColorId, color);
            targetRenderer.SetPropertyBlock(propertyBlock);
        }
    }

    private void ClearFlashColor()
    {
        foreach (Renderer targetRenderer in renderers)
        {
            if (targetRenderer != null)
            {
                targetRenderer.SetPropertyBlock(null);
            }
        }
    }

    private void OnDisable()
    {
        if (flashRoutine != null)
        {
            StopCoroutine(flashRoutine);
            flashRoutine = null;
        }

        ClearFlashColor();
    }

    private void OnValidate()
    {
        flashDuration = Mathf.Max(0.01f, flashDuration);
    }
}
