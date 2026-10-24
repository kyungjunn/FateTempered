using UnityEngine;

public enum PlayerCharacterId
{
    Sword,
    Ninja,
    Wizard
}

// 플레이어 프리팹이 어떤 캐릭터인지 명시한다.
public sealed class PlayerCharacter : MonoBehaviour
{
    [SerializeField] private PlayerCharacterId characterId = PlayerCharacterId.Sword;

    public PlayerCharacterId CharacterId => characterId;

    public bool Is(PlayerCharacterId expected)
    {
        return characterId == expected;
    }

    // 에디터 빌더와 테스트에서 프리팹을 구성할 때 사용한다.
    public void SetCharacterId(PlayerCharacterId value)
    {
        characterId = value;
    }
}
