using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// Player - inventory drop. The carried list, CarriedValue and the pickup itself live elsewhere (core + interaction);
// this is just the G key spawning the held item back into the world so it falls to the floor.
public partial class Player
{
    private void LoseCarriedLoot() //the guard grabbed you - you go home empty-handed. called the moment you're caught (eliminated) or hauled off to the closet (jailed), so getting caught actually costs the haul. the loot's already counted toward the house clear-% (reported at pickup); this just stops you banking it at the pawn shop
    {
        inventory.Clear();
    }

    //Kick a wedge under the nearest shut, un-wedged house door. Returns whether it actually wedged something, so the
    //caller knows whether the press was spent. Only real Doors qualify, not every cupboard with a hinge on it.
    private bool TryWedgeNearestDoor()
    {
        Door nearest = null;
        float nearestDistance = wedgePlaceRange;
        foreach (Door door in Door.AllDoors)
        {
            if (door.IsOpen || door.IsWedged) continue; //can't wedge a door that's standing open, and one wedge is enough
            float distance = Vector3.Distance(transform.position, door.transform.position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = door;
            }
        }

        if (nearest == null) return false;
        PlaceWedgeIn(nearest);
        return true;
    }

    private void HandleDrop(bool dropPressed)
    {
        bool pressed = dropPressed && !dropHeldLastTick; //rising edge only - one drop per press
        dropHeldLastTick = dropPressed;
        if (!pressed) return;

        //G at a shut door kicks a wedge under it. wedging lives on G rather than E because E already opens the door,
        //and G already means "put down the thing you're carrying" - which is exactly what this is.
        if (WedgesCarried > 0 && TryWedgeNearestDoor())
        {
            return; //wedged something, that was this press
        }

        if (inventory.Count == 0 || worldItemPrefab == null) return;

        int slot = ResolveDropSlot(); //whatever the loot wheel is pointing at, clamped - the list shrinks under a stale index
        if (slot < 0) return;

        InventoryItem dropped = inventory[slot];
        inventory.RemoveAt(slot);

        Vector3 dropPosition = transform.position + transform.forward * dropForwardOffset + Vector3.up; //spawn it a bit ahead and up so it falls to the floor
        Runner.Spawn(worldItemPrefab, dropPosition, UnityEngine.Random.rotation, Object.InputAuthority, //random tilt so it tumbles and lands on a face, not balanced on a point
            (runner, spawnedObject) =>
            {
                WorldItem item = spawnedObject.GetComponent<WorldItem>();
                if (item != null) //carry the name AND value back onto the dropped item so it's worth the same when re-picked
                {
                    item.ItemName = dropped.name;
                    item.Value = dropped.value;
                    item.SpawnPoint = dropPosition;  //same networked-position safeguard as placed loot, in case a drop ever gets deferred too
                    item.UseSpawnPoint = true;
                    item.CountedAsStolen = true;     //this loot was already counted against the house when it was FIRST lifted - picking it back up must not count it again
                }
            });
    }
}
