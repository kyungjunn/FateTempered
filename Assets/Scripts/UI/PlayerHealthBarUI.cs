using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 체력바. 스포너 또는 부모 플레이어.
public sealed class PlayerHealthBarUI : MonoBehaviour
{
    [SerializeField] private Slider slider;
    [SerializeField] private TMP_Text valueText;
    [SerializeField] private bool bindSpawnedPlayer;
    [SerializeField] private bool faceCamera;

    private PlayerHealth playerHealth;
    private Camera cachedCamera;

    private void Awake()
    {
        if (slider != null)
        {
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;
            slider.interactable = false;
        }
    }

    private void OnEnable()
    {
        if (bindSpawnedPlayer)
        {
            // 이벤트 + 현재 스냅샷
            SpawnManager.OnPlayerSpawned += HandlePlayerSpawned;
            HandlePlayerSpawned(SpawnManager.Instance?.SpawnedPlayer);
        }
        else
        {
            BindHealth(GetComponentInParent<PlayerHealth>());
        }
    }

    private void Start()
    {
        // PlayerHealth initializes its current value in Awake, which may run after this UI binds.
        if (bindSpawnedPlayer)
        {
            HandlePlayerSpawned(SpawnManager.Instance?.SpawnedPlayer);
        }
        else
        {
            BindHealth(GetComponentInParent<PlayerHealth>());
        }

        RefreshHealth();
    }

    private void OnDisable()
    {
        if (bindSpawnedPlayer)
        {
            SpawnManager.OnPlayerSpawned -= HandlePlayerSpawned;
        }

        BindHealth(null);
    }

    private void LateUpdate()
    {
        if (!faceCamera)
        {
            return;
        }

        if (cachedCamera == null)
        {
            cachedCamera = Camera.main;
        }

        if (cachedCamera == null)
        {
            return;
        }

        transform.rotation = cachedCamera.transform.rotation;
    }

    private void HandlePlayerSpawned(GameObject playerObject)
    {
        PlayerHealth health = playerObject != null ? playerObject.GetComponent<PlayerHealth>() : null;
        BindHealth(health);
    }

    private void BindHealth(PlayerHealth health)
    {
        if (playerHealth == health)
        {
            RefreshHealth();
            return;
        }

        if (playerHealth != null)
        {
            playerHealth.onHealthChanged -= RefreshHealth;
        }

        playerHealth = health;

        if (playerHealth != null)
        {
            playerHealth.onHealthChanged += RefreshHealth;
        }

        RefreshHealth();
    }

    private void RefreshHealth()
    {
        if (playerHealth == null)
        {
            if (slider != null)
            {
                slider.SetValueWithoutNotify(0f);
            }

            if (valueText != null)
            {
                valueText.text = string.Empty;
            }

            return;
        }

        int currentHealth = playerHealth.CurrentHealth;
        int maxHealth = playerHealth.MaxHealth;
        float normalizedHealth = maxHealth > 0 ? Mathf.Clamp01((float)currentHealth / maxHealth) : 0f;

        if (slider != null)
        {
            slider.SetValueWithoutNotify(normalizedHealth);
        }

        if (valueText != null)
        {
            valueText.text = $"{currentHealth} / {maxHealth}";
        }
    }
}
