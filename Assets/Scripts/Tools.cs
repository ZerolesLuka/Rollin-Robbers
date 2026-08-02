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
public enum ToolType
{
    None = 0,
    SoftSoles,   //quieter on your feet
    Crowbar,     //force a safe faster
    WireCutters, //disarm the guard's traps without announcing it
    DuffelBag,   //carry more loot
    WedgeKit,    //start the run with door wedges
    SignalJammer,//blinds cameras near you, but hums
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
    public const float SoftSolesNoiseMultiplier = 0.55f; //movement noise only - it does nothing about your mouth
    public const float CrowbarCrackMultiplier = 0.6f;    //fraction of the normal time to force a safe
    public const int DuffelBagExtraSlots = 2;
    public const int WedgeKitWedges = 2;

    //The jammer trades one way of being caught for another. It blinds cameras in a bubble around whoever carries it -
    //teammates included, so the crew has a reason to move together - but the thing HUMS.
    //
    //6f is chosen against GuardHearing's threshold of 5, not picked for feel: it sits just above, so the jammer is
    //faintly audible on its own. That does nothing while you're walking (movement noise is 7 and NoiseLevel takes the
    //loudest, not the sum) - it only bites when you STAND STILL, which is exactly when you'd otherwise be silent.
    //Carrying it means you can never go quiet, only quieter.
    public const float JammerRadius = 6f;
    public const float JammerNoise = 6f;

    private static readonly ToolDefinition[] all = new ToolDefinition[]
    {
        new ToolDefinition { type = ToolType.SoftSoles,   name = "Soft Soles",   cost = 450,  description = "Your footsteps carry about half as far. Says nothing about your voice." },
        new ToolDefinition { type = ToolType.Crowbar,     name = "Crowbar",      cost = 600,  description = "Forcing a safe takes noticeably less time. Still just as loud." },
        new ToolDefinition { type = ToolType.WireCutters, name = "Wire Cutters", cost = 500,  description = "Disarm his traps quietly. Without them it can be done, but he'll hear it." },
        new ToolDefinition { type = ToolType.DuffelBag,   name = "Duffel Bag",   cost = 750,  description = "Two more slots for loot. Nothing else." },
        new ToolDefinition { type = ToolType.WedgeKit,    name = "Wedge Kit",    cost = 300,  description = "Start each run carrying two door wedges." },
        new ToolDefinition { type = ToolType.SignalJammer,name = "Signal Jammer",cost = 800,  description = "Cameras go blind near whoever's carrying it, teammates included. It hums, so you can never be truly still." },
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
