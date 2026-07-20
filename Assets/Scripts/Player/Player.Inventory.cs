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

    private void HandleDrop(bool dropPressed)
    {
        bool pressed = dropPressed && !dropHeldLastTick; //rising edge only - one drop per press
        dropHeldLastTick = dropPressed;
        if (!pressed || inventory.Count == 0 || worldItemPrefab == null) return;

        InventoryItem dropped = inventory[inventory.Count - 1]; //drop the most recently picked up (the one you're "holding")
        inventory.RemoveAt(inventory.Count - 1);

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
