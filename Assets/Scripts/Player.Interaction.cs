using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// Player - the E key. One press runs down a priority list: free a trapped teammate, pick up a world item,
// grab legacy loot, use an exit door, start the getaway van, sit at the computer, sell at the pawn shop,
// or enter/exit a hiding spot. First match wins and returns.
public partial class Player
{
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
                    if (RunManager.Instance != null) RunManager.Instance.RPC_ReportLootTaken(item.Value); //the house is now missing this - feeds the guard's suspicion
                    item.pendingRemoval = true; //so we don't re-grab it before it despawns
                    item.RPC_PickUp();
                    return; //picked up, done for this press
                }
            }
        }

        //loot pickup: only runs if no rescue or item pickup happened
        if (RunManager.Instance == null) return;
        foreach (Lootable lootable in Lootable.AllLootables)
        {
            if (lootable.IsLooted) continue;
            if (Vector3.Distance(transform.position, lootable.transform.position) <= lootRange)
            {
                RunManager.Instance.RPC_ClaimLoot(lootable.lootId, lootable.value);
                return; //looted, done for this press
            }
        }

        //exit door: only runs if no rescue or loot happened
        foreach (ExitDoor door in ExitDoor.AllDoors)
        {
            if (Vector3.Distance(transform.position, door.transform.position) <= door.interactRange)
            {
                RunManager.Instance.RPC_LoadScene(door.targetSceneBuildIndex, door.spawnPointId);
                return;
            }
        }

        //getaway van: start it from the driver's seat and the run ends successfully for everyone
        foreach (Van van in Van.AllVans)
        {
            Transform seat = van.driverSeat != null ? van.driverSeat : van.transform;
            if (Vector3.Distance(transform.position, seat.position) <= van.interactRange)
            {
                RunManager.Instance.RPC_StartGetaway();
                return;
            }
        }

        //van computer: press E to sit down at it - only if nobody else is on it (networked lock). we don't enter here; we claim it and enter once granted (see UpdateComputerClaim)
        foreach (ComputerTerminal terminal in ComputerTerminal.AllTerminals)
        {
            if (Vector3.Distance(transform.position, terminal.transform.position) <= terminal.interactRange)
            {
                if (RunManager.Instance.IsComputerFree)
                {
                    pendingTerminal = terminal;
                    RunManager.Instance.RPC_ClaimComputer(Object.InputAuthority);
                }
                return;
            }
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
                if(!hidingSpot.isOccupied)
                {
                    hidingSpot.OnSpotEnter();
                    hidingSpot.isOccupied = true;
                }
                else if(hidingSpot.isOccupied && hidingSpot.isHiding)
                {
                    hidingSpot.OnSpotExit();
                }
                return;
            }
        }
    }
}
