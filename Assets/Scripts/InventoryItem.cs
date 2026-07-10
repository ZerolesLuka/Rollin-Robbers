// One slot's worth of carried loot: what it is and what it sells for. Local to each player's inventory.
public struct InventoryItem
{
    public string name;
    public int value;

    public InventoryItem(string name, int value)
    {
        this.name = name;
        this.value = value;
    }
}
