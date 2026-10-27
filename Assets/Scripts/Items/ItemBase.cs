using UnityEngine;

public abstract class ItemBase : MonoBehaviour, IInteractable
{
    [Header("ItemData")]
    [SerializeField] protected ItemData itemData;

    public ItemData ItemData => itemData;

    public virtual void Interact() 
    {
        if (NetworkGameSession.IsNetworkSessionActive) return;
        ApplyEffect(null);
    }

    public void ApplyTo(GameObject player)
    {
        if (NetworkGameSession.IsNetworkSessionActive) return;
        ApplyEffect(player);
    }

    public void ApplyTo(GameObject player, ItemData data)
    {
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
