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

    private void HandleInteract(bool interacting)
    {
        bool pressed = interacting && !interactHeldLastTick; //rising edge only - one action per press
        interactHeldLastTick = interacting;
        if (!pressed) return;

        //rescue takes priority: free the nearest trapped teammate. you can NEVER free yourself (locked player returns before this runs, and we skip self below)
        foreach (Player other in ActivePlayers)
        {
            if (other == this) continue;
            if (!other.IsLockedUp) continue;
            if (Vector3.Distance(transform.position, other.transform.position) <= rescueRange)
            {
                other.RPC_Rescue();
                return; //rescued, done for this press
            }
        }

        //world item pickup: into the inventory (separate from the loot-value system). doesn't need RunManager, so it runs before that check
        if (inventory.Count < maxInventorySlots)
        {
            foreach (WorldItem item in WorldItem.AllItems)
            {
                if (item.pendingRemoval) continue; //already grabbed locally, waiting on the despawn
                if (Vector3.Distance(transform.position, item.transform.position) <= pickupRange)
                {
                    //ASK, don't take. the item's owner decides who actually gets it and reports the theft once,
                    //then sends it back to the winner (RPC_GrantPickup). doing it locally let two players grabbing
                    //the same item on the same tick both keep it and both sell it - a straight money dupe.
                    item.pendingRemoval = true; //local guard so our own scan doesn't fire a second request while this one's in flight
                    item.RPC_RequestPickUp(Object.InputAuthority);
                    return; //requested, done for this press
                }
            }
        }

        //reading a safe-code note. doesn't need RunManager, and it's above the doors/van so a note lying on a desk
        //next to something else still wins - it's a tiny target and the most annoying thing to fail to pick up.
        foreach (SafeNote note in SafeNote.AllNotes)
        {
            if (Vector3.Distance(transform.position, note.transform.position) <= note.ReadRange)
            {
                LearnSafeCode(note.ReadCode()); //onto OUR hud only - the whole point is reading it out to whoever's at the safe
                return;
            }
        }

        //everything below this point needs the RunManager
        if (RunManager.Instance == null) return;

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

        Door nearestSwingDoor = null;
        float nearestSwingDistance = float.MaxValue;
        foreach (Door swingDoor in Door.AllDoors)
        {
            float distanceToDoor = Vector3.Distance(transform.position, swingDoor.transform.position);
            if (distanceToDoor <= swingDoor.interactRange && distanceToDoor < nearestSwingDistance)
            {
                nearestSwingDoor = swingDoor;
                nearestSwingDistance = distanceToDoor;
            }
        }

        if (nearestExit != null || nearestSwingDoor != null)
        {
            //a tie goes to the swinging door on purpose - it's harmless and reversible, whereas an ExitDoor yanks
            //the whole crew into another scene. accidentally opening a door beats accidentally ending the burgle.
            bool useSwingDoor = nearestSwingDoor != null && (nearestExit == null || nearestSwingDistance <= nearestExitDistance);
            if (useSwingDoor)
            {
                RunManager.Instance.RPC_SetDoorOpen(nearestSwingDoor.transform.position, !nearestSwingDoor.IsOpen);
            }
            else
            {
                RunManager.Instance.RPC_LoadScene(nearestExit.targetSceneBuildIndex, nearestExit.spawnPointId);
            }
            return;
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
                if (RunManager.Instance.IsComputerFree) //networked lock - we claim it and enter once granted (see UpdateComputerClaim)
                {
                    pendingTerminal = nearestTerminal;
                    RunManager.Instance.RPC_ClaimComputer(Object.InputAuthority);
                }
            }
            else
            {
                RunManager.Instance.RPC_StartGetaway(); //start the van - the run ends successfully for everyone
            }
            return;
        }

       //pawn shop counter: sell the team's haul for money
        foreach (SellCounter counter in SellCounter.AllCounters)
        {
            if (Vector3.Distance(transform.position, counter.transform.position) <= counter.interactRange)
            {
                if (inventory.Count > 0)
                {
                    RunManager.Instance.RPC_SellItems(CarriedValue); //bank the worth of my haul into the shared money
                    inventory.Clear();                                //handed it all over
                }
                return;
            }
        }

        foreach (HidingSpot hidingSpot in HidingSpot.AllHidingSpots)
        {
            if(Vector3.Distance(transform.position, hidingSpot.transform.position) <= hidingSpot.interactRange)
            {
                if (hidingSpot.IsOccupiedByLocalPlayer)
                {
                    hidingSpot.OnSpotExit(); //we're the one inside - climb out
                }
                else if (!hidingSpot.IsOccupied)
                {
                    hidingSpot.OnSpotEnter(); //free spot - get in
                }
                //someone ELSE is in there: do nothing, and still return so we don't fall through to another spot
                return;
            }
        }
    }
}
