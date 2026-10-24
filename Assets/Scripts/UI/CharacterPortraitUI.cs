using UnityEngine;
using UnityEngine.UI;

// 초상화. 스포너 스냅샷.
public sealed class CharacterPortraitUI : MonoBehaviour
{
    [SerializeField] private Image portrait;
    [SerializeField] private Sprite swordPortrait;
    [SerializeField] private Sprite wizardPortrait;
    [SerializeField] private Sprite ninjaPortrait;

    private void OnEnable()
    {
        SpawnManager.OnPlayerSpawned += BindPlayer;
        // 이벤트 + 현재 스냅샷
        BindPlayer(SpawnManager.Instance?.SpawnedPlayer);
    }

    private void OnDisable()
    {
        SpawnManager.OnPlayerSpawned -= BindPlayer;
        SetPortrait(null);
    }

    private void BindPlayer(GameObject playerObject)
    {
        Sprite selectedPortrait = null;

        if (playerObject != null && playerObject.TryGetComponent(out PlayerCharacter character))
        {
            switch (character.CharacterId)
            {
                case PlayerCharacterId.Sword:
                    selectedPortrait = swordPortrait;
                    break;
                case PlayerCharacterId.Wizard:
                    selectedPortrait = wizardPortrait;
                    break;
                case PlayerCharacterId.Ninja:
                    selectedPortrait = ninjaPortrait;
                    break;
            }
        }

        SetPortrait(selectedPortrait);
    }

    private void SetPortrait(Sprite sprite)
    {
        if (portrait == null)
        {
            return;
        }

        portrait.preserveAspect = true;
        portrait.sprite = sprite;
        portrait.enabled = sprite != null;
    }
}
