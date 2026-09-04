using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

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
    //WHICH TOOLS WE'RE CARRYING, as a bitmask, republished whenever the bag changes.
    //
    //The tools themselves now live as ordinary items in `inventory` - but that list is LOCAL, and plenty of things ask
    //about our tools from another machine: Safe checks the cracking player's crowbar on the authority, RunManager vets
    //purchases on the master. Reading the list from over there answers "no tools" with total confidence. So the list
    //is the truth and this is the replica, exactly like CarriedCount.
    [Networked] public int ToolMask { get; private set; }

    public bool HasTool(ToolType tool)
    {
        if (tool == ToolType.None) return false;
        return (ToolMask & (1 << (int)tool)) != 0;
    }

    //Kept so the shop UI and debug panel keep working. Derived from the bag now rather than being storage of their
    //own - "slot A" just means the first tool you happen to be carrying.
    public ToolType ToolSlotA => ToolInSlot(0);
    public ToolType ToolSlotB => ToolInSlot(1);

    public ToolType ToolInSlot(int index)
    {
        int seen = 0;
        foreach (InventoryItem item in inventory)
        {
            if (!item.IsTool) continue;
            if (seen == index) return item.tool;
            seen++;
        }
        return ToolType.None;
    }

    public bool HasFreeToolSlot => CarriedCount < MaxInventorySlots; //no separate kit any more - a tool needs a bag slot like everything else

    //A tool takes a REAL slot now, so this is just "is there room in the bag". The Duffel Bag is exempt from its own
    //check because it hands back more room than it occupies, so buying it with a full bag is always a net win.
    public bool HasRoomForTool(ToolType tool)
    {
        if (tool == ToolType.DuffelBag) return true;

        //CarriedCount, not inventory.Count. RunManager calls this on the MASTER to vet a purchase, and over there a
        //remote player's local list is permanently empty - so reading the list always answered "bag's empty, sure",
        //and the check only ever worked for whoever happened to be hosting.
        return CarriedCount < MaxInventorySlots;
    }

    //Called on the buyer's own machine once RunManager has confirmed the shared wallet could afford it. Returns false
    //if there was nowhere to put it, so the money can be handed back rather than vanishing.
    public bool GrantTool(ToolType tool)
    {
        if (tool == ToolType.None) return false;
        if (HasTool(tool) && !ToolTable.Stacks(tool)) return false; //no point owning two of the same tool - the effects don't stack. wedges are the exception, being a consumable
        if (inventory.Count >= MaxInventorySlots) return false; //bag's full. drop something first - and now that CAN be a tool

        if (tool == ToolType.SignalJammer)
        {
            JammerChargesLeft = ToolTable.JammerCharges; //a fresh unit comes with a full set. buying a second one is blocked above, so this can't top up a spent one
        }

        inventory.Add(new InventoryItem(tool));
        PublishCarriedCount(); //also republishes ToolMask, which is what makes the effects live for everyone else
        return true;
    }

    //RpcSources.All, NOT StateAuthority - see RPC_GrantWedge. RunManager sends this from the MASTER, which is not the
    //state authority of the buyer's Player object, so the stricter source silently dropped it: money gone, no tool.
    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] //RunManager took the money; this lands on the buyer and equips it
    public void RPC_GrantTool(int toolTypeValue)
    {
        GrantTool((ToolType)toolTypeValue);
    }

    //Ask to buy. The answer comes back as RPC_GrantTool if the shared wallet covered it - deliberately never decided
    //here, or two players buying on the same tick would each pass their own affordability check against one balance.
    public void RequestBuyTool(ToolType tool)
    {
        if (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid) return;
        if (tool == ToolType.None || HasTool(tool) || !HasFreeToolSlot || !HasRoomForTool(tool)) return; //cheap local rejections, so obvious no-ops never leave the machine
        RunManager.Instance.RPC_BuyTool((int)tool, Object.InputAuthority);
    }

    public bool CanAfford(ToolType tool)
    {
        if (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid) return false;
        return RunManager.Instance.Money >= ToolTable.CostOf(tool);
    }

    //Ditch a tool from the shop UI. G does the same thing out in the world, because a tool is just an item now.
    public void DropTool(int slotIndex)
    {
        RemoveToolFromBag(ToolInSlot(slotIndex));
    }

    //Take a tool out of the bag WITHOUT dropping anything into the world - used when a tool is spent rather than
    //discarded, like the jammer being deployed.
    private void RemoveToolFromBag(ToolType tool)
    {
        if (tool == ToolType.None) return;

        for (int i = 0; i < inventory.Count; i++)
        {
            if (inventory[i].tool != tool) continue;
            inventory.RemoveAt(i);
            PublishCarriedCount();
            return;
        }
    }

    //Caught. Nothing to do here any more - the kit lives in the bag, and the bag is already emptied on capture, so
    //"lose your tools too" now falls out of the existing rule instead of being a second thing to remember.
    private void LoseTools()
    {
    }

    //EFFECTS. Each of these is read by a system that already existed, so a tool never needs its own update loop.

    //Movement noise is speed x this. Keep normal walking near the old 7-ish guard-heard loudness while the movement
    //slider changes how far the body travels. Padded Boots still multiplies on top, so the tool's discount is unchanged.
    private const float TargetWalkingNoise = 7f;
    public float MovementNoiseMultiplier
    {
        get
        {
            float speedCompensation = EffectiveMoveSpeed > 0f ? TargetWalkingNoise / EffectiveMoveSpeed : 0f;
            return speedCompensation * (HasTool(ToolType.PaddedBoots) ? ToolTable.PaddedBootsNoiseMultiplier : 1f);
        }
    }

    public float SafeCrackMultiplier => HasTool(ToolType.Crowbar) ? ToolTable.CrowbarCrackMultiplier : 1f;

    public bool CanDisarmQuietly => HasTool(ToolType.WireCutters);

    public int ToolInventoryBonus => HasTool(ToolType.DuffelBag) ? ToolTable.DuffelBagExtraSlots : 0;

    //TOOLS COST CARRYING SPACE. Every tool in your kit is one less thing you can take home, which is what makes a
    //loadout a decision rather than a shopping list - and it's why the Duffel Bag reads as buying back the room your
    //other tool cost rather than as free capacity.
    public int ToolsCarried
    {
        get
        {
            int count = 0;
            foreach (InventoryItem item in inventory)
            {
                if (item.IsTool) count++;
            }
            return count;
        }
    }

    //NO DEPLOY KEY. Q used to spend the jammer and place it; it's an ordinary carried item now - scroll to it, right
    //click to switch it on, G to put it down like anything else in the bag. One less binding to teach.

    //Put a jammer on the floor, carrying over whatever it has left. Called by the normal G drop, so dropping it
    //mid-burst leaves the bubble sitting where it landed and dropping it cold leaves an inert unit someone can pick
    //up later.
    public void DropJammerToFloor()
    {
        if (jammerDevicePrefab == null || !HasTool(ToolType.SignalJammer)) return;

        Vector3 dropAt = transform.position + transform.forward * 0.6f + Vector3.up * 0.1f;
        float secondsStillRunning = JammerActiveSecondsLeft;

        //spend it FIRST. spawning is deferred, so waiting for the callback to remove it leaves a window where a second
        //press would place a second unit off one tool.
        RemoveToolFromBag(ToolType.SignalJammer);
        JammerActiveSecondsLeft = 0f;      //the bubble goes with the object, not with us
        JammerCooldownSecondsLeft = 0f;    //and so does the recharge - we aren't holding it any more

        Runner.Spawn(jammerDevicePrefab, dropAt, Quaternion.identity, PlayerRef.None, (runner, spawnedObject) =>
        {
            JammerDevice device = spawnedObject.GetComponent<JammerDevice>();
            if (device == null) return;
            device.SecondsLeft = secondsStillRunning; //0 if it was off - an inert box on the floor, which is allowed
            device.SpawnPoint = dropAt;   //networked-position safeguard - a deferred spawn drops the position argument
            device.UseSpawnPoint = true;
        });
    }

    //Cameras ask this. Reads BOTH deployed units and players carrying a switched-on one.
    public static bool IsPositionJammed(Vector3 position) => JammerDevice.CoversPosition(position);

    //Nothing refills any more. Wedges are bought individually and spent individually, so what you carry into a house
    //is exactly what you paid for - no tool quietly topping you up between runs. Kept as an empty hook because the
    //run-start path calls it, and a future consumable will want somewhere to go.
    public void RefillToolConsumables()
    {
    }
}
