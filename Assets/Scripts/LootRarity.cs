using UnityEngine;

// Loot rarity is derived straight from an item's value - no need to network a separate field, since every client
// classifies the same value the same way. Used to colour inventory slots (and later item glows) so a big score
// reads at a glance. The thresholds ARE the design here - tune them as loot values settle in.
public enum LootRarity
{
    Common,
    Uncommon,
    Rare,
    Legendary
}

public static class LootRarityTable
{
    private const int UncommonAtValue = 150;   // value >= this is Uncommon
    private const int RareAtValue = 400;        // value >= this is Rare
    private const int LegendaryAtValue = 900;   // value >= this is Legendary

    public static LootRarity Classify(int value)
    {
        if (value >= LegendaryAtValue)
        {
            return LootRarity.Legendary;
        }
        if (value >= RareAtValue)
        {
            return LootRarity.Rare;
        }
        if (value >= UncommonAtValue)
        {
            return LootRarity.Uncommon;
        }
        return LootRarity.Common;
    }

    public static Color ColorFor(LootRarity rarity)
    {
        switch (rarity)
        {
            case LootRarity.Legendary:
                return new Color(1f, 0.6f, 0.1f);    // orange
            case LootRarity.Rare:
                return new Color(0.3f, 0.55f, 1f);   // blue
            case LootRarity.Uncommon:
                return new Color(0.35f, 0.85f, 0.4f);// green
            default:
                return new Color(0.82f, 0.82f, 0.82f);// off-white
        }
    }

    public static Color ColorFor(int value)
    {
        return ColorFor(Classify(value));
    }

    public static string DisplayName(LootRarity rarity)
    {
        return rarity.ToString();
    }
}
