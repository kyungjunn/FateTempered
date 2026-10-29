using UnityEngine;

public abstract class ItemBase : MonoBehaviour, IInteractable
{
    [Header("ItemData")]
    [SerializeField] protected ItemData itemData;

    public ItemData ItemData => itemData;

    public virtual void Interact() 
    {
        // 네트워크 전투: 오프라인 아이템 효과 미적용.
        if (NetworkGameSession.IsNetworkSessionActive) return;
        ApplyEffect(null);
    }

    public void ApplyTo(GameObject player)
    {
        // 네트워크 전투: 플레이어 직접 적용 금지.
        if (NetworkGameSession.IsNetworkSessionActive) return;
        ApplyEffect(player);
    }

    public void ApplyTo(GameObject player, ItemData data)
    {
        // 네트워크 전투: 데이터 교체·효과 적용 금지.
        if (NetworkGameSession.IsNetworkSessionActive) return;
        itemData = data;
        ApplyEffect(player);
    }

    public virtual string GetInteractText()
    {
        return $"{itemData.ItemName} 획득";
    }

    protected abstract void ApplyEffect(GameObject player);
}
