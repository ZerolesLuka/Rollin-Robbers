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
    [Networked] public ToolType ToolSlotA { get; private set; }
    [Networked] public ToolType ToolSlotB { get; private set; }

    public bool HasTool(ToolType tool)
    {
        if (tool == ToolType.None) return false;
        return ToolSlotA == tool || ToolSlotB == tool;
    }

    public bool HasFreeToolSlot => ToolSlotA == ToolType.None || ToolSlotB == ToolType.None;

    //A tool eats a loot slot, so buying one while the bag is already full would leave you carrying MORE than your own
    //capacity - fill up on loot, then buy two tools, and you'd walk into the house with a full bag AND a full kit.
    //The Duffel Bag is exempt from its own check because it hands back more room than it takes.
    public bool HasRoomForTool(ToolType tool)
    {
        if (tool == ToolType.DuffelBag) return true;
        return inventory.Count < MaxInventorySlots; //room to lose one slot without going over
    }

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

    //TOOLS COST CARRYING SPACE. Every tool in your kit is one less thing you can take home, which is what makes a
    //loadout a decision rather than a shopping list - and it's why the Duffel Bag reads as buying back the room your
    //other tool cost rather than as free capacity.
    public int ToolsCarried
    {
        get
        {
            int count = 0;
            if (ToolSlotA != ToolType.None) count++;
            if (ToolSlotB != ToolType.None) count++;
            return count;
        }
    }

    //Q, read straight off the keyboard the way the safe keypad already reads digits, so this needs no new action in
    //the input asset (which would mean regenerating the C# wrapper before anything compiled).
    private void UpdateDeployKey()
    {
        if (Keyboard.current == null) return;
        //anything that has taken your hands or your control also takes this. hiding matters most: your body is inside
        //a wardrobe, so the device would appear through the door as if you'd posted it out.
        if (isUsingComputer || isEnteringSafeCode || IsPaused) return;
        if (IsEliminated || IsHiding || IsLockedUp || IsBearTrapped || isBeingDragged) return;
        if (!Keyboard.current.qKey.wasPressedThisFrame) return;
        TryDeployJammer();
    }

    //Deploying spends the tool and frees its slot.
    public void TryDeployJammer()
    {
        if (jammerDevicePrefab == null || !HasTool(ToolType.SignalJammer)) return;

        Vector3 dropAt = transform.position + transform.forward * 0.6f + Vector3.up * 0.1f;

        //spend it FIRST. spawning is deferred, so waiting for the callback to clear the slot leaves a window where
        //holding Q would place a second one off a single tool.
        if (ToolSlotA == ToolType.SignalJammer) ToolSlotA = ToolType.None;
        else if (ToolSlotB == ToolType.SignalJammer) ToolSlotB = ToolType.None;

        Runner.Spawn(jammerDevicePrefab, dropAt, Quaternion.identity, PlayerRef.None, (runner, spawnedObject) =>
        {
            JammerDevice device = spawnedObject.GetComponent<JammerDevice>();
            if (device == null) return;
            device.SecondsLeft = ToolTable.JammerSeconds;
            device.SpawnPoint = dropAt;   //networked-position safeguard - a deferred spawn drops the position argument
            device.UseSpawnPoint = true;
        });
    }

    //Cameras ask this. It reads DEPLOYED devices, not who's carrying what - a jammer in your pocket does nothing,
    //which is the whole point of making it a thing you place.
    public static bool IsPositionJammed(Vector3 position) => JammerDevice.CoversPosition(position);

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
