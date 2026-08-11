using UnityEngine;

// What you can buy and take into a house. This is the answer to "why run House 1 again" - the house doesn't change,
// so the variety has to come from what you brought.
//
// Every tool here CHANGES A NUMBER SOMETHING ELSE ALREADY READS rather than adding a new mechanic of its own. That's
// deliberate: a tool that bolts on new behaviour is a second system to balance and debug, whereas one that scales an
// existing dial is understood the moment you own it, and interacts with everything already built for free.
//
// Deliberately NOT ScriptableObjects. Those would mean an asset per tool to create and wire before any of this runs,
// and the table below is the same data with none of that. Add a tool by adding an enum entry and a row.
//NUMBERED EXPLICITLY. These values are serialized - in the Player prefab's held-prop rows, in the networked ToolMask,
//in every dropped WorldItem's ToolKind - so they can never be allowed to shift. 5 is a hole where WedgeKit used to be
//and it stays a hole; renumbering to close it would silently turn every saved SignalJammer into something else.
public enum ToolType
{
    None = 0,
    PaddedBoots = 1, //quieter on your feet
    Crowbar = 2,     //force a safe faster
    WireCutters = 3, //disarm the guard's traps without announcing it
    DuffelBag = 4,   //carry more loot
    //5 was WedgeKit - removed. It bought you two wedges per run while itself occupying a bag slot, so once wedges
    //became real items you paid for the same thing twice and walked into the house with three of four slots full.
    //Wedges are sold directly now, which is what a consumable should be.
    SignalJammer = 6,//blinds cameras near you, but hums
    DoorWedge = 7,   //a single wedge. bought by the handful, spent one per door, and the only tool that STACKS
}

public struct ToolDefinition
{
    public ToolType type;
    public string name;
    public string description;
    public int cost;
}

public static class ToolTable
{
    public const int SlotCount = 2; //TWO on purpose. the interesting part of a loadout is what you LEAVE behind, and three slots is enough to bring one of everything

    //Balance lives here and nowhere else, so tuning is one file rather than a hunt through five systems.
    public const float PaddedBootsNoiseMultiplier = 0.8f;
    //0.8, NOT lower, and the reason is a threshold rather than a feel. GuardHearing ignores anything under 5.
    //Walking is moveSpeed 7, so 0.55 gave 3.85 and made walking COMPLETELY inaudible - identical to crouching,
    //which quietly deleted the crouch decision for 450. 0.8 gives 5.6: still clearly quieter, still heard.
    public const float CrowbarCrackMultiplier = 0.6f;    //fraction of the normal time to force a safe
    public const int DuffelBagExtraSlots = 2;

    //THE ONLY STACKING ENTRY. Every other tool is a thing you either own or don't, and owning two does nothing -
    //which is why GrantTool refuses duplicates. A wedge is a consumable, so that rule has to bend for exactly this.
    public static bool Stacks(ToolType type) => type == ToolType.DoorWedge;

    //The jammer is PLACED, not merely owned. Deploying spends it: the device sits where you dropped it, blinds every
    //camera inside JammerRadius, and dies when the battery does. Nobody gets it back.
    //
    //It was a passive effect at first and that was its whole problem - it worked invisibly, on a prop that might only
    //appear once in a house, so you could carry it a full run and never see it do anything. Placing it makes every
    //part legible: you chose the spot, it's sat there, and you know exactly when it stops.
    public const float JammerRadius = 7f;
    public const float JammerSeconds = 45f;

    private static readonly ToolDefinition[] all = new ToolDefinition[]
    {
        new ToolDefinition { type = ToolType.PaddedBoots, name = "Padded Boots", cost = 450,  description = "Your footsteps carry noticeably less far. Says nothing about your voice." },
        new ToolDefinition { type = ToolType.Crowbar,     name = "Crowbar",      cost = 600,  description = "Forcing a safe takes noticeably less time. Still just as loud." },
        new ToolDefinition { type = ToolType.WireCutters, name = "Wire Cutters", cost = 500,  description = "Disarm his traps quietly. Without them it can be done, but he'll hear it." },
        new ToolDefinition { type = ToolType.DuffelBag,   name = "Duffel Bag",   cost = 750,  description = "Two more slots for loot. Nothing else." },
        new ToolDefinition { type = ToolType.DoorWedge,   name = "Door Wedge",   cost = 150,  description = "Jams one door shut from the side you kicked it in. Buy as many as you'll carry - each one takes a slot." },
        new ToolDefinition { type = ToolType.SignalJammer,name = "Signal Jammer",cost = 550,  description = "Press Q to set it down. Blinds every camera around it for about a minute, then the battery dies and it's gone." },
    };

    public static ToolDefinition[] All => all;

    public static ToolDefinition Get(ToolType type)
    {
        foreach (ToolDefinition definition in all)
        {
            if (definition.type == type) return definition;
        }
        return new ToolDefinition { type = ToolType.None, name = "Empty", description = "", cost = 0 };
    }

    public static int CostOf(ToolType type) => Get(type).cost;
    public static string NameOf(ToolType type) => type == ToolType.None ? "Empty" : Get(type).name;
}
