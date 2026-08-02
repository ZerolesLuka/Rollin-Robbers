using Fusion;
using UnityEngine;

// Player - the loadout you carry into a house.
//
// Tools are bought with the SHARED wallet but owned PER PLAYER, which is the interesting shape: the crew decides
// together who gets the crowbar, and that person is now the one who has to go to the safe. One player carrying
// everything isn't possible, because there are only two slots each.
//
// LOST ON CATCH, per the locked economy decision. Getting grabbed already costs you the loot you were holding; this
// makes it cost the kit as well, which is what stops a rich crew from treating capture as a minor inconvenience.
//
// Two explicit slots rather than a NetworkArray on purpose - it's two ints, it's readable, and it can't be got subtly
// wrong. Add a third by adding a field and extending HasTool/GrantTool; nothing else looks at the slots directly.
public partial class Player
{
    [Networked] public ToolType ToolSlotA { get; private set; }
    [Networked] public ToolType ToolSlotB { get; private set; }

    public bool HasTool(ToolType tool)
    {
        if (tool == ToolType.None) return false;
        return ToolSlotA == tool || ToolSlotB == tool;
    }

    public bool HasFreeToolSlot => ToolSlotA == ToolType.None || ToolSlotB == ToolType.None;

    public ToolType ToolInSlot(int index) => index == 0 ? ToolSlotA : ToolSlotB;

    //Called on the buyer's own machine once RunManager has confirmed the shared wallet could afford it. Returns false
    //if there was nowhere to put it, so the money can be handed back rather than vanishing.
    public bool GrantTool(ToolType tool)
    {
        if (tool == ToolType.None) return false;
        if (HasTool(tool)) return false; //no point owning two of the same thing - the effects don't stack

        if (ToolSlotA == ToolType.None) { ToolSlotA = tool; return true; }
        if (ToolSlotB == ToolType.None) { ToolSlotB = tool; return true; }
        return false; //both slots full - drop something first
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.InputAuthority)] //RunManager took the money; this lands on the buyer and equips it
    public void RPC_GrantTool(int toolTypeValue)
    {
        GrantTool((ToolType)toolTypeValue);
    }

    //Ask to buy. The answer comes back as RPC_GrantTool if the shared wallet covered it - deliberately never decided
    //here, or two players buying on the same tick would each pass their own affordability check against one balance.
    public void RequestBuyTool(ToolType tool)
    {
        if (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid) return;
        if (tool == ToolType.None || HasTool(tool) || !HasFreeToolSlot) return; //cheap local rejections, so obvious no-ops never leave the machine
        RunManager.Instance.RPC_BuyTool((int)tool, Object.InputAuthority);
    }

    public bool CanAfford(ToolType tool)
    {
        if (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid) return false;
        return RunManager.Instance.Money >= ToolTable.CostOf(tool);
    }

    public void DropTool(int slotIndex) //make room without buying over the top of something you wanted
    {
        if (slotIndex == 0) ToolSlotA = ToolType.None;
        else ToolSlotB = ToolType.None;
    }

    private void LoseTools() //caught. the kit goes with the loot - see the economy decision this implements
    {
        ToolSlotA = ToolType.None;
        ToolSlotB = ToolType.None;
    }

    //EFFECTS. Each of these is read by a system that already existed, so a tool never needs its own update loop.

    public float MovementNoiseMultiplier => HasTool(ToolType.PaddedBoots) ? ToolTable.PaddedBootsNoiseMultiplier : 1f;

    public float SafeCrackMultiplier => HasTool(ToolType.Crowbar) ? ToolTable.CrowbarCrackMultiplier : 1f;

    public bool CanDisarmQuietly => HasTool(ToolType.WireCutters);

    public int ToolInventoryBonus => HasTool(ToolType.DuffelBag) ? ToolTable.DuffelBagExtraSlots : 0;

    //The hum. Folded into NoiseLevel as just another source, so it competes with walking rather than adding to it -
    //which is why it only actually matters when you're stood still.
    public float ToolNoiseFloor => HasTool(ToolType.SignalJammer) ? ToolTable.JammerNoise : 0f;

    //Is this spot inside somebody's jamming bubble? Static because a CAMERA needs to ask, and a camera has no idea
    //which players exist - it just knows where it's pointing. Covers teammates too, on purpose: the bubble is a
    //reason for the crew to move as a group rather than a personal invisibility cloak.
    public static bool IsPositionJammed(Vector3 position)
    {
        foreach (Player player in ActivePlayers)
        {
            if (player == null || !player.HasTool(ToolType.SignalJammer)) continue;
            if (player.IsEliminated) continue; //his kit went with him when he was caught
            if (Vector3.Distance(player.transform.position, position) <= ToolTable.JammerRadius) return true;
        }
        return false;
    }

    //Hand out the wedges a WedgeKit promises. Called when a fresh run starts rather than when the tool is bought, so
    //the kit refills every heist instead of being a one-off purchase of two wedges.
    public void RefillToolConsumables()
    {
        if (!HasTool(ToolType.WedgeKit)) return;
        for (int i = 0; i < ToolTable.WedgeKitWedges; i++)
        {
            if (!CanCarryAnotherWedge) break;
            AddWedge(); //direct, not the RPC - we're already on the machine that owns this player
        }
    }
}
