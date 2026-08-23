// One thing in your bag. Loot and TOOLS are both this - a tool is just an item that happens to do something while
// you're holding it, which is what makes it droppable, sellable-adjacent and visible in the loot wheel for free.
//
// Tools used to live in their own pair of networked slots, entirely outside the inventory. That meant two carry
// systems, two drop paths, and G literally could not see a tool to drop it. Folding them in deletes the seam.
public struct InventoryItem
{
    public string name;
    public int value;
    public ToolType tool; //ToolType.None for ordinary loot. anything else and the item is a tool doing its job from the bag

    public bool IsTool => tool != ToolType.None;

    public InventoryItem(string name, int value)
    {
        this.name = name;
        this.value = value;
        this.tool = ToolType.None;
    }

    public InventoryItem(ToolType tool)
    {
        this.tool = tool;
        this.name = ToolTable.NameOf(tool);
        this.value = 0; //the fence pays nothing for your own kit - it's yours, not swag
    }
}
