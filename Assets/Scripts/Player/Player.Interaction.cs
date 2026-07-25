using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// Player - the E key. One press runs down a priority list: free a trapped teammate, pick up a world item,
// open/close a door, use an exit door, start the getaway van, sit at the computer, sell at the pawn shop,
// or enter/exit a hiding spot. First match wins and returns.
public partial class Player
{
    private void HandleCracking(bool interactHeld)
    {
        //cracking is a HOLD, not a tap. we don't fill the meter here - we just publish WHICH un-opened safe we're
        //standing on via networked CrackingSafeId (same one-source-of-truth idea as the hiding spots). the safe
        //itself reads every player's CrackingSafeId in its FixedUpdateNetwork and advances its own meter. stop
        //holding, or walk out of range, and CrackingSafeId clears - the meter just pauses, it never resets.
        Safe target = null;
        if (interactHeld)
        {
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
                    target = safe;
                }
            }
        }
        SetCrackingSafe(target != null ? target.SafeId : Safe.NoSafe);
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
                    inventory.Add(new InventoryItem(item.ItemName.ToString(), item.Value));
                    if (!item.CountedAsStolen && RunManager.Instance != null) RunManager.Instance.RPC_ReportLootTaken(item.Value, item.transform.position); //FIRST lift only - the house is now missing this, which feeds the guard's suspicion and tells him WHERE it went. re-picking something a teammate dropped isn't a fresh theft
                    item.pendingRemoval = true; //so we don't re-grab it before it despawns
                    item.RPC_PickUp();
                    return; //picked up, done for this press
                }
            }
        }

        //openable doors: swing on E. doesn't need RunManager, and the toggle is broadcast so every client's copy matches
        foreach (Door door in Door.AllDoors)
        {
            if (Vector3.Distance(transform.position, door.transform.position) <= door.interactRange)
            {
                RPC_ToggleDoor(door.transform.position);
                return; //toggled a door, done for this press
            }
        }

        //everything below this point needs the RunManager
        if (RunManager.Instance == null) return;

        //exit door: only runs if no rescue or pickup happened
        foreach (ExitDoor door in ExitDoor.AllDoors)
        {
            if (Vector3.Distance(transform.position, door.transform.position) <= door.interactRange)
            {
                RunManager.Instance.RPC_LoadScene(door.targetSceneBuildIndex, door.spawnPointId);
                return;
            }
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
