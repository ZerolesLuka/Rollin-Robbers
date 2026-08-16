using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

// Player - the carried Signal Jammer.
//
// It used to be a one-shot: press Q, it goes on the floor, the battery drains, it's gone for good. Now it's a
// re-usable unit with a fixed number of charges. Right-click with it in your bag to burn one and blind every camera
// around YOU for a few seconds; the bubble follows you while it's running. Press Q and it goes on the floor instead,
// covering a fixed spot so the crew can leave it watching a corridor and walk off.
//
// WHY CHARGES AND A COOLDOWN, rather than one or the other: charges keep every activation a decision - there are only
// ever three - and the cooldown stops it being kept permanently on by re-triggering the instant the last one lapses.
// With duration alone it would just be "cameras are off now", which deletes the prop rather than playing against it.
//
// The three counters live on Player as [Networked] because jamming has to be true for EVERYONE - a camera decides
// whether it can see you on the master, not on the machine of whoever pressed the button.
public partial class Player
{
    //Whatever the scroll wheel is currently pointing at, or None if that slot is loot or empty. Local-only, same as
    //SelectedSlot itself - this decides what OUR right click does, and nothing else reads it.
    private ToolType SelectedTool()
    {
        if (SelectedSlot < 0 || SelectedSlot >= inventory.Count) return ToolType.None;
        InventoryItem selected = inventory[SelectedSlot];
        return selected.IsTool ? selected.tool : ToolType.None;
    }

    //Called from Update on the local player only - this is a raw mouse read, same as the door drag and the loot wheel.
    private void UpdateJammerInput()
    {
        if (Mouse.current == null) return;

        bool held = Mouse.current.rightButton.isPressed;
        bool pressedThisFrame = held && !jammerHeldLastFrame; //rising edge, or holding the button burns all three charges in three frames
        jammerHeldLastFrame = held;

        if (!pressedThisFrame) return;
        if (SelectedTool() != ToolType.SignalJammer) return; //has to be the thing you've scrolled to, not just something in the bag
        if (KeyboardIsCaptured || IsLootWheelOpen) return;   //haggling, typing a safe code, or picking an item - right click isn't for this
        if (JammerChargesLeft <= 0) return;               //spent
        if (JammerCooldownSecondsLeft > 0f) return;       //still recharging
        if (IsJammerActive) return;                       //already running - don't let a second press waste a charge
        if (IsHiding || IsEliminated || IsLockedUp || IsBearTrapped) return; //hands aren't free

        JammerChargesLeft--;
        JammerActiveSecondsLeft = ToolTable.JammerActiveSeconds;
        JammerCooldownSecondsLeft = ToolTable.JammerActiveSeconds + ToolTable.JammerCooldownSeconds; //recharge starts when it SWITCHES OFF, not when it comes on
    }

    //Ticked on the network tick rather than the render frame so the duration is the same length for everyone
    //regardless of framerate. Called from HandleMovement, which already runs once per tick for the owning client.
    private void TickJammer()
    {
        if (JammerActiveSecondsLeft > 0f)
        {
            JammerActiveSecondsLeft = Mathf.Max(0f, JammerActiveSecondsLeft - Runner.DeltaTime);
        }
        if (JammerCooldownSecondsLeft > 0f)
        {
            JammerCooldownSecondsLeft = Mathf.Max(0f, JammerCooldownSecondsLeft - Runner.DeltaTime);
        }
    }

    //Is this spot inside the bubble of a player carrying a live jammer? JammerDevice.CoversPosition asks this so a
    //camera doesn't need to know whether the thing blinding it is on the floor or in somebody's bag.
    public static bool AnyCarriedJammerCovers(Vector3 position)
    {
        foreach (Player player in ActivePlayers)
        {
            if (!player.IsJammerActive) continue;
            if (player.IsEliminated || player.IsLockedUp) continue; //out of the run, and so is their kit
            if (Vector3.Distance(player.transform.position, position) <= ToolTable.JammerRadius) return true;
        }
        return false;
    }
}
