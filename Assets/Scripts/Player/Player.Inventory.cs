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
    //Push the local list's size onto the wire. Called after EVERY change to the list, because the count is the one
    //part of the inventory other machines make decisions from - and a machine that doesn't own this player has an
    //empty list, so anything reading the list itself from over there gets a confident, wrong answer.
    //WHAT YOU'RE CARRYING, SHOWN ON YOUR BODY. Runs on EVERY client, not just the owner - the whole point is that your
    //crew can see you're lugging something, so it has to be driven by replicated state rather than the local list.
    //
    //CarriedCount is already networked (the master reads it to vet tool purchases), so this needs no new networking:
    //carrying anything at all shows the prop, an empty bag hides it.
    //
    //ONE generic prop for now, because there is one generic WorldItem prefab - a vase and a crowbar look identical in
    //hand. Swapping this for a per-item mesh later means changing which object gets enabled here and nothing else.
    private void UpdateHeldItemVisual()
    {
        //hidden for the same reasons the body is: inside a wardrobe your arms aren't visible, and a vase floating
        //outside a closet door would be a spectacular tell. eliminated and jailed players show nothing either.
        bool handsFree = IsHiding || IsEliminated || IsLockedUp;

        //HeldKind, not SelectedSlot. Selection is local, so reading it here would leave every REMOTE copy of us
        //holding whatever their own scroll wheel happened to be pointing at. -1 is empty-handed.
        int held = handsFree ? -1 : HeldKind;

        if (heldProps == null)
        {
            return; //nothing wired in the inspector - the feature simply doesn't exist rather than throwing every frame
        }

        //Pick the winner FIRST, then do a single pass enabling it and disabling everything else. Deciding and applying
        //in one loop would let two props end up on at once if a tool were ever listed twice.
        GameObject wanted = null;
        if (held >= 0)
        {
            foreach (HeldProp mapping in heldProps)
            {
                if (mapping.prop == null) continue;
                if ((int)mapping.tool == held)
                {
                    wanted = mapping.prop; //exact match for this tool
                    break;
                }
                if (mapping.tool == ToolType.None && wanted == null)
                {
                    wanted = mapping.prop; //remember the fallback, but keep looking for something better
                }
            }
        }

        foreach (HeldProp mapping in heldProps)
        {
            SetPropActive(mapping.prop, mapping.prop == wanted);
        }
    }

    private static void SetPropActive(GameObject prop, bool active)
    {
        if (prop == null || prop.activeSelf == active) return; //null-tolerant so a half-filled mapping list is harmless, and no needless SetActive churn
        prop.SetActive(active);
    }

    private void PublishCarriedCount()
    {
        if (!HasStateAuthority) return; //only the owner may write it; a remote copy asking would be dropped anyway
        CarriedCount = inventory.Count;

        //Republish WHICH TOOLS too, for the same reason and in the same breath. Tools live in the bag now, so every
        //mutation of the list is potentially a change of kit - and Safe, RunManager and anything else asking about our
        //tools from another machine reads the mask, never the list.
        int mask = 0;
        foreach (InventoryItem item in inventory)
        {
            if (item.IsTool) mask |= 1 << (int)item.tool;
        }
        ToolMask = mask;
    }

    private void LoseCarriedLoot() //the guard grabbed you - you go home empty-handed. called the moment you're caught (eliminated) or hauled off to the closet (jailed), so getting caught actually costs the haul. the loot's already counted toward the house clear-% (reported at pickup); this just stops you banking it at the pawn shop
    {
        inventory.Clear();
        PublishCarriedCount();
    }

    //Kick a wedge under the nearest shut, un-wedged house door. Returns whether it actually wedged something, so the
    //caller knows whether the press was spent. Only real Doors qualify, not every cupboard with a hinge on it.
    private bool TryWedgeNearestDoor()
    {
        Door nearest = null;
        float nearestDistance = wedgePlaceRange;
        foreach (Door door in Door.AllDoors)
        {
            //ONLY "already wedged" disqualifies a door now. This used to skip anything where IsOpen was true, which
            //made sense when a door was strictly open or shut - but doors rest at any angle since they became
            //hand-pushed, and IsOpen is a threshold, so a door you'd nudged and released looked shut to you and read
            //as open to this. G then fell through and dropped a vase at your feet instead. Jamming a door that's ajar
            //is a perfectly good thing to want anyway - it holds the gap exactly where it is.
            if (door.IsWedged) continue; //one is enough, and two would just fight over who owns the door
            float distance = Vector3.Distance(transform.position, door.transform.position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = door;
            }
        }

        if (nearest == null) return false;
        return PlaceWedgeIn(nearest); //report what ACTUALLY happened. this used to return true unconditionally, so a wedge that never went down still ate the G press and nothing at all occurred
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
        PublishCarriedCount();

        Vector3 dropPosition = transform.position + transform.forward * dropForwardOffset + Vector3.up; //spawn it a bit ahead and up so it falls to the floor
        Runner.Spawn(worldItemPrefab, dropPosition, UnityEngine.Random.rotation, Object.InputAuthority, //random tilt so it tumbles and lands on a face, not balanced on a point
            (runner, spawnedObject) =>
            {
                WorldItem item = spawnedObject.GetComponent<WorldItem>();
                if (item != null) //carry the name AND value back onto the dropped item so it's worth the same when re-picked
                {
                    item.ItemName = dropped.name;
                    item.Value = dropped.value;
                    item.ToolKind = (int)dropped.tool; //a dropped crowbar has to still be a crowbar when it's picked back up

                    item.SpawnPoint = dropPosition;  //same networked-position safeguard as placed loot, in case a drop ever gets deferred too
                    item.UseSpawnPoint = true;
                    item.CountedAsStolen = true;     //this loot was already counted against the house when it was FIRST lifted - picking it back up must not count it again
                }
            });
    }
}
