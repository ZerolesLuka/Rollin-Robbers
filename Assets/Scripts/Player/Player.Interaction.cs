using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// Player - the E key. One press runs down a priority list: free a trapped teammate, pick up a world item,
// use an exit door OR swing a door (whichever is closer), start the getaway van, sit at the computer, sell at
// the pawn shop, or enter/exit a hiding spot. First match wins and returns.
public partial class Player
{
    private Safe NearestCrackableSafe()
    {
        Safe nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (Safe safe in Safe.AllSafes)
        {
            if (safe.IsOpen)
            {
                continue; //already cracked
            }
            float distance = Vector3.Distance(transform.position, safe.transform.position);
            if (distance <= safe.CrackRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = safe;
            }
        }
        return nearest;
    }

    private void HandleCracking(bool interactHeld)
    {
        //E does two different things at a safe. TAP it and the keypad comes up - type the code off the note and it
        //pops instantly and silently. HOLD it and you brute-force the dial instead: slow, loud, and a genuinely bad
        //idea with the guard awake. That's the whole risk/reward - go find the note, or gamble that he's far enough away.
        Safe nearbySafe = NearestCrackableSafe();

        if (interactHeld && nearbySafe != null)
        {
            safeInteractHoldTime += Runner.DeltaTime;
        }
        else if (!interactHeld)
        {
            //released. a SHORT press near a safe was a tap, so bring the keypad up instead of cracking.
            if (safeInteractHoldTime > 0f && safeInteractHoldTime < safeHoldToCrackTime && nearbySafe != null)
            {
                OpenSafeKeypad(nearbySafe);
            }
            safeInteractHoldTime = 0f;
        }

        //only publish that we're cracking once the hold passes the threshold, so a tap never nudges the meter.
        //the safe reads every player's CrackingSafeId in its own FixedUpdateNetwork and advances itself; let go or
        //walk out of range and this clears, which PAUSES the meter rather than resetting it.
        bool actuallyCracking = interactHeld && nearbySafe != null && safeInteractHoldTime >= safeHoldToCrackTime;
        SetCrackingSafe(actuallyCracking ? nearbySafe.SafeId : Safe.NoSafe);
    }

    private bool IsLootWithinReach() //loot we could take if our hands were empty. only used to explain a full bag, never to act
    {
        foreach (WorldItem item in WorldItem.AllItems)
        {
            if (item.pendingRemoval) continue;
            if (item.LockedInSafe) continue; //we can't take it yet anyway, so don't blame our full hands for it
            if (Vector3.Distance(transform.position, item.transform.position) <= pickupRange) return true;
        }
        return false;
    }

    //What E would act on right now. HandleInteract performs it and the HUD prompt describes it, both off THIS one
    //scan - so the prompt can never promise something different from what the key actually does. Add an interactable
    //here once and both halves pick it up.
    //NOTE: there's no PlaceWedge here. E already opens doors, and a second E action at the same door would either
    //steal the open or need a hold, which collides with the safe's tap-vs-hold. Wedging is on G instead - the key
    //that already means "put down what you're carrying". See HandleDrop.
    private enum InteractKind { None, Rescue, Disarm, TakeWedge, PullWedge, WedgeStuck, Pickup, ReadNote, SwingDoor, ExitDoor, Van, Computer, Shop, Keeper, Hide }

    private InteractKind FindInteraction(out Component target)
    {
        target = null;

        //shut inside a wardrobe: climbing out is the ONLY thing E may do. without this the normal priority order
        //still applies, so a hiding spot placed near a door traps you - E swings the door instead of letting you out.
        if (IsHiding)
        {
            foreach (HidingSpot occupied in HidingSpot.AllHidingSpots)
            {
                if (occupied.IsOccupiedByLocalPlayer)
                {
                    target = occupied;
                    return InteractKind.Hide;
                }
            }
            return InteractKind.None;
        }

        //rescue takes priority: free the nearest trapped teammate. you can NEVER free yourself (locked player returns before this runs, and we skip self below)
        foreach (Player other in ActivePlayers)
        {
            if (other == this) continue;
            if (!other.IsLockedUp && !other.IsBearTrapped) continue; //jailed in a closet OR pinned by the ankle - both need a friend
            if (Vector3.Distance(transform.position, other.transform.position) <= rescueRange)
            {
                target = other;
                return InteractKind.Rescue;
            }
        }

        //a guard's tripwire outranks loot on purpose: if you're stood on one AND there's something shiny next to it,
        //you want E to make you safe rather than make you richer. doesn't need RunManager either.
        GuardTrap armedTrap = GuardTrap.FindDisarmableNear(transform.position);
        if (armedTrap != null)
        {
            target = armedTrap;
            return InteractKind.Disarm;
        }

        //a loose wedge on the floor, above loot because it's small, easy to miss, and you usually want it in a hurry
        DoorWedge looseWedge = DoorWedge.LooseWedgeNear(transform.position);
        if (looseWedge != null && CanCarryAnotherWedge)
        {
            target = looseWedge;
            return InteractKind.TakeWedge;
        }

        //world item pickup: into the inventory (separate from the loot-value system). doesn't need RunManager, so it runs before that check.
        //a FULL bag deliberately falls straight through this block rather than blocking - otherwise loot lying by the
        //exit door would make the door unopenable exactly when you're loaded up and trying to leave.
        if (inventory.Count < MaxInventorySlots)
        {
            foreach (WorldItem item in WorldItem.AllItems)
            {
                if (item.pendingRemoval) continue; //already grabbed locally, waiting on the despawn
                if (item.LockedInSafe) continue;   //sat behind a shut safe door. pickup is a proximity check, so without this you'd reach straight through it
                if (Vector3.Distance(transform.position, item.transform.position) <= pickupRange)
                {
                    target = item;
                    return InteractKind.Pickup;
                }
            }
        }

        //reading a safe-code note. doesn't need RunManager, and it's above the doors/van so a note lying on a desk
        //next to something else still wins - it's a tiny target and the most annoying thing to fail to pick up.
        //NOTE: only REPORTS the note here. this scan runs every render frame to feed the prompt, so actually reading
        //it in this method would re-read the code continuously without anyone ever pressing E.
        foreach (SafeNote note in SafeNote.AllNotes)
        {
            if (Vector3.Distance(transform.position, note.transform.position) <= note.ReadRange)
            {
                target = note;
                return InteractKind.ReadNote;
            }
        }

        //everything below this point needs the RunManager
        if (RunManager.Instance == null) return InteractKind.None;

        //a scene-changing ExitDoor and a swinging Door often sit right on top of each other (the front threshold
        //has both), so don't pick by a fixed order - use whichever you're actually CLOSEST to, the same way the van
        //and the van computer resolve. a fixed order would make the loser permanently unreachable.
        ExitDoor nearestExit = null;
        float nearestExitDistance = float.MaxValue;
        foreach (ExitDoor exitDoor in ExitDoor.AllDoors)
        {
            float distanceToExit = Vector3.Distance(transform.position, exitDoor.transform.position);
            if (distanceToExit <= exitDoor.interactRange && distanceToExit < nearestExitDistance)
            {
                nearestExit = exitDoor;
                nearestExitDistance = distanceToExit;
            }
        }

        //ANY openable, not just doors. searching SwingingHinge instead of Door means a cupboard, a drawer or a
        //jewellery box is interactive with that one component on it - no Door script bolted on to props that aren't
        //doors. house doors still turn up here because every door owns a hinge.
        SwingingHinge nearestSwingDoor = SwingingHinge.FindNearest(transform.position);
        float nearestSwingDistance = nearestSwingDoor != null
            ? Vector3.Distance(transform.position, nearestSwingDoor.transform.position)
            : float.MaxValue;

        if (nearestExit != null || nearestSwingDoor != null)
        {
            //a tie goes to the swinging door on purpose - it's harmless and reversible, whereas an ExitDoor yanks
            //the whole crew into another scene. accidentally opening a door beats accidentally ending the burgle.
            bool useSwingDoor = nearestSwingDoor != null && (nearestExit == null || nearestSwingDistance <= nearestExitDistance);
            if (useSwingDoor)
            {
                //a wedge under this door outranks opening it, because the door isn't going to budge until it's out.
                //only from the side it was kicked in from though - from the far side all you can do is look at it.
                Door houseDoor = nearestSwingDoor.GetComponent<Door>();
                DoorWedge jammedWedge = houseDoor != null ? houseDoor.Wedge : null;
                if (jammedWedge != null)
                {
                    target = jammedWedge;
                    return jammedWedge.CanBeRemovedFrom(transform.position) && CanCarryAnotherWedge
                        ? InteractKind.PullWedge
                        : InteractKind.WedgeStuck;
                }

                target = nearestSwingDoor;
                return InteractKind.SwingDoor;
            }
            target = nearestExit;
            return InteractKind.ExitDoor;
        }

        //the getaway van (driver's seat, ends the run for everyone) and the van computer (routing) sit right next to
        //each other, so don't pick by a fixed order - pick whichever you're actually CLOSEST to. that way standing at
        //the seat starts the van and standing at the screen opens the computer, no accidental run-endings.
        Van nearestVan = null;
        float nearestVanDistance = float.MaxValue;
        foreach (Van van in Van.AllVans)
        {
            Transform seat = van.driverSeat != null ? van.driverSeat : van.transform;
            float distanceToSeat = Vector3.Distance(transform.position, seat.position);
            if (distanceToSeat <= van.interactRange && distanceToSeat < nearestVanDistance)
            {
                nearestVan = van;
                nearestVanDistance = distanceToSeat;
            }
        }

        ComputerTerminal nearestTerminal = null;
        float nearestTerminalDistance = float.MaxValue;
        foreach (ComputerTerminal terminal in ComputerTerminal.AllTerminals)
        {
            float distanceToTerminal = Vector3.Distance(transform.position, terminal.transform.position);
            if (distanceToTerminal <= terminal.interactRange && distanceToTerminal < nearestTerminalDistance)
            {
                nearestTerminal = terminal;
                nearestTerminalDistance = distanceToTerminal;
            }
        }

        if (nearestVan != null || nearestTerminal != null)
        {
            //both in reach? use the closer one. a tie goes to the computer on purpose - it's harmless, whereas the van ends the whole run
            bool useComputer = nearestTerminal != null && (nearestVan == null || nearestTerminalDistance <= nearestVanDistance);
            if (useComputer)
            {
                target = nearestTerminal;
                return InteractKind.Computer;
            }
            target = nearestVan;
            return InteractKind.Van;
        }

        //the fence behind the desk. selling goes through HIM now, not the counter - see Player.Haggle
        Shopkeeper keeper = Shopkeeper.NearestTo(transform.position);
        if (keeper != null)
        {
            target = keeper;
            return InteractKind.Keeper;
        }

        //the tool shop. above the sell counter because they sit side by side and buying is the more deliberate act -
        //walking up to sell and accidentally opening the shop is harmless, the reverse loses your haul in one press.
        ToolShop shop = ToolShop.NearestTo(transform.position);
        if (shop != null)
        {
            target = shop;
            return InteractKind.Shop;
        }

        //NO instant-sell counter any more. It dumped the whole bag at full sticker price, which made the fence
        //pointless the moment he opened at 60% - you'd never talk to him again. Selling goes through him, one item at
        //a time, or it doesn't happen. SellCounter is now just the desk he stands behind.

        foreach (HidingSpot hidingSpot in HidingSpot.AllHidingSpots)
        {
            if (Vector3.Distance(transform.position, hidingSpot.transform.position) <= hidingSpot.interactRange)
            {
                target = hidingSpot;
                return InteractKind.Hide;
            }
        }

        return InteractKind.None;
    }

    private void HandleInteract(bool interacting)
    {
        bool pressed = interacting && !interactHeldLastTick; //rising edge only - one action per press
        interactHeldLastTick = interacting;
        if (!pressed) return;

        InteractKind kind = FindInteraction(out Component target);
        switch (kind)
        {
            case InteractKind.Rescue:
                ((Player)target).RPC_Rescue();
                break;

            case InteractKind.Disarm:
                ((GuardTrap)target).RPC_Disarm(CanDisarmQuietly); //wire cutters make it silent; bare hands make it a noise he investigates
                break;

            case InteractKind.Pickup:
                //ASK, don't take. the item's owner decides who actually gets it and reports the theft once,
                //then sends it back to the winner (RPC_GrantPickup). doing it locally let two players grabbing
                //the same item on the same tick both keep it and both sell it - a straight money dupe.
                WorldItem item = (WorldItem)target;
                item.pendingRemoval = true; //local guard so our own scan doesn't fire a second request while this one's in flight
                item.RPC_RequestPickUp(Object.InputAuthority);
                break;

            case InteractKind.ReadNote:
                //onto OUR hud only - the whole point is reading it out loud to whoever's stood at the safe. the note
                //stays in the world so a teammate can come and check it themselves.
                SafeNote note = (SafeNote)target;
                LearnSafeCode(note.SafeId, note.ReadCode()); //keyed by safe, so a second note can't overwrite the first
                break;

            case InteractKind.TakeWedge:
            case InteractKind.PullWedge:
                //both are the same act: the wedge object goes away and we're carrying one. pulling one out of a door
                //unjams it as a side effect, because Door.IsWedged is derived from the wedges that exist.
                //ASK, don't take - the wedge's owner picks a single winner and grants it back (RPC_GrantWedge), the
                //same way loot pickup resolves. counting it locally would let two players share one wedge.
                ((DoorWedge)target).RPC_TakeWedge(Object.InputAuthority);
                break;

            case InteractKind.WedgeStuck:
                break; //it's on the far side, or our hands are full. the prompt already said so

            case InteractKind.SwingDoor:
                Door door = (Door)target;
                RunManager.Instance.RPC_SetDoorOpen(door.transform.position, !door.IsOpen);
                break;

            case InteractKind.ExitDoor:
                ExitDoor exitDoor = (ExitDoor)target;
                RunManager.Instance.RPC_LoadScene(exitDoor.targetSceneBuildIndex, exitDoor.spawnPointId);
                break;

            case InteractKind.Computer:
                if (RunManager.Instance.IsComputerFree) //networked lock - we claim it and enter once granted (see UpdateComputerClaim)
                {
                    pendingTerminal = (ComputerTerminal)target;
                    RunManager.Instance.RPC_ClaimComputer(Object.InputAuthority);
                }
                break;

            case InteractKind.Van:
                RunManager.Instance.RPC_StartGetaway(); //start the van - the run ends successfully for everyone
                break;

            case InteractKind.Keeper:
                EnterKeeper((Shopkeeper)target);
                break;

            case InteractKind.Shop:
                EnterShop((ToolShop)target);
                break;

            case InteractKind.Hide:
                HidingSpot spot = (HidingSpot)target;
                if (spot.IsOccupiedByLocalPlayer)
                {
                    spot.OnSpotExit(); //we're the one inside - climb out
                }
                else if (!spot.IsOccupied)
                {
                    spot.OnSpotEnter(); //free spot - get in
                }
                //someone ELSE is in there: do nothing. we still consumed the press rather than falling through to another spot
                break;
        }
    }

    public string InteractPrompt { get; private set; } = ""; //what the HUD shows. local only - each client describes its own player's reach

    public Transform InteractAnchor { get; private set; } //the THING the prompt is about, so the label can be drawn on it in the world rather than floating at the crosshair. null = nothing in reach

    public void UpdateInteractPrompt() //called every render frame from Player.Update for the local player, so the line tracks the crosshair smoothly rather than stepping at the 32Hz tick
    {
        if (isUsingComputer || IsEliminated || IsLockedUp || isBeingDragged || IsBearTrapped)
        {
            InteractPrompt = ""; //no reach while parked, out, jailed, hauled off, or pinned by the ankle
            InteractAnchor = null;
            return;
        }

        InteractKind kind = FindInteraction(out Component target);

        //nothing tappable in reach, so fall back to the two things FindInteraction doesn't cover: a safe (its own
        //tap-vs-hold input path) and loot we can SEE but can't carry. checked in that order and only here, so
        //standing at a safe next to a door doesn't flip the line back and forth between two things that are both true.
        if (kind == InteractKind.None)
        {
            Safe crackable = NearestCrackableSafe();
            if (crackable != null)
            {
                InteractPrompt = "E  Enter the code    (hold to force it open)"; //both halves of the safe, because the tap and the hold do genuinely different things
                InteractAnchor = crackable.transform;
            }
            else if (inventory.Count >= MaxInventorySlots && IsLootWithinReach())
            {
                InteractPrompt = "Hands full - drop something with G"; //the pickup scan skipped this loot entirely, so say why rather than showing nothing
                InteractAnchor = NearestLootInReach();                 //hang it on the thing we can't pick up, so it's obvious WHAT we're being refused
            }
            else
            {
                InteractPrompt = "";
                InteractAnchor = null;
            }
            return;
        }

        InteractPrompt = LabelFor(kind, target);
        InteractAnchor = target != null ? target.transform : null;
    }

    private Transform NearestLootInReach() //only used to hang the "hands full" line on something. never acts
    {
        Transform nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (WorldItem item in WorldItem.AllItems)
        {
            if (item.pendingRemoval) continue;
            float distance = Vector3.Distance(transform.position, item.transform.position);
            if (distance <= pickupRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = item.transform;
            }
        }
        return nearest;
    }

    private string LabelFor(InteractKind kind, Component target)
    {
        switch (kind)
        {
            case InteractKind.Rescue:
                Player trapped = target as Player;
                return (trapped != null && trapped.IsBearTrapped) ? "E  Pry the trap off them" : "E  Free your teammate";

            case InteractKind.Disarm:
                GuardTrap trap = target as GuardTrap;
                string trapName = trap != null ? trap.DisplayName : "trap";
                //say the cost out loud. without cutters it's still worth doing, but the player should be choosing to
                //make that noise rather than discovering it afterwards
                return CanDisarmQuietly ? $"E  Cut the {trapName}" : $"E  Disarm the {trapName}   (he'll hear it)";

            case InteractKind.Pickup:
                WorldItem item = target as WorldItem;
                return item != null ? $"E  Take {item.ItemName} (${item.Value})" : "E  Take";

            case InteractKind.ReadNote:
                return "E  Read the note";

            case InteractKind.TakeWedge:
                return "E  Pick up the wedge";

            case InteractKind.PullWedge:
                return "E  Pull the wedge out";

            case InteractKind.WedgeStuck:
                return CanCarryAnotherWedge ? "Wedged from the other side" : "You can't carry any more wedges";

            case InteractKind.SwingDoor:
                Door door = target as Door;
                return (door != null && door.IsOpen) ? "E  Close the door" : "E  Open the door";

            case InteractKind.ExitDoor:
                return "E  Go through";

            case InteractKind.Van:
                return "E  Drive off (ends the run)"; //spelled out: this one is irreversible and ends everyone's heist

            case InteractKind.Computer:
                return (RunManager.Instance != null && !RunManager.Instance.IsComputerFree)
                    ? "Someone else is on the computer"
                    : "E  Use the computer";

            case InteractKind.Keeper:
                return inventory.Count > 0 ? "E  Talk to the fence" : "E  Talk to the fence  (nothing to sell)";

            case InteractKind.Shop:
                return "E  Buy tools";

            case InteractKind.Hide:
                HidingSpot spot = target as HidingSpot;
                if (spot == null) return "";
                if (spot.IsOccupiedByLocalPlayer) return "E  Climb out";
                return spot.IsOccupied ? "Someone's already hiding here" : "E  Hide";

            default:
                return "";
        }
    }
}
