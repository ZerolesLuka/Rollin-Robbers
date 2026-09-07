// WHAT a piece of loot is, as distinct from what it's worth. Value already varies per spawn and LootRarityTable
// colours the glow by it, but two items worth the same still have to look different in your hand and on the floor.
//
// Kept SEPARATE from ToolType deliberately. A tool is something you bought that does a job while it sits in your bag;
// loot is something you stole that only carries a price. One shared enum would put "Crowbar" and "Gold Bar" in the
// same list and invite the exact confusion where a tool turns out to be sellable or a trinket ends up in your kit.
public enum LootKind
{
    Generic = 0, // anything nobody has modelled yet - falls back to the plain prop, the same role ToolType.None plays for tools
    GoldBar = 1,
    Jewellery = 2,
}
