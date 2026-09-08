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
    //A prop PER THING, matched on what you're actually holding - a tool by its ToolType, loot by its LootKind. Every
    //row is a child of the player model left disabled in the prefab, and this enables exactly one of them. A row whose
    //prop points at a prefab ASSET rather than a child does nothing at all, which is silent and easy to do by accident.
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
        //
        //TWO enums decide this, in order. A row whose tool is anything but None is a TOOL row and matches on that. A
        //row whose tool is None is a LOOT row, and then lootKind picks which loot - that second step is what stopped
        //every trinket in the game sharing one placeholder. LootKind.Generic is the catch-all beneath both.
        GameObject wanted = null;
        GameObject fallback = null;
        if (held >= 0)
        {
            bool holdingLoot = held == (int)ToolType.None;
            foreach (HeldProp mapping in heldProps)
            {
                if (mapping.prop == null)
                {
                    continue;
                }

                if (holdingLoot)
                {
                    if (mapping.tool != ToolType.None)
                    {
                        continue; //a tool row can never describe loot
                    }
                    if ((int)mapping.lootKind == HeldLootKind)
                    {
                        wanted = mapping.prop; //exact match for this loot
                        break;
                    }
                    if (mapping.lootKind == LootKind.Generic)
                    {
                        fallback = mapping.prop; //unmodelled loot still puts something in your hand
                    }
                    continue;
                }

                if ((int)mapping.tool == held)
                {
                    wanted = mapping.prop; //exact match for this tool
                    break;
                }
                if (mapping.tool == ToolType.None && mapping.lootKind == LootKind.Generic)
                {
                    fallback = mapping.prop; //a tool nobody has modelled yet borrows the generic loot prop rather than showing nothing
                }
            }
        }

        if (wanted == null)
        {
            wanted = fallback;
        }

        foreach (HeldProp mapping in heldProps)
        {
            SetPropActive(mapping.prop, mapping.prop == wanted);
        }
    }

    //instance ids we have already complained about, so the warning below fires once instead of once per frame forever
    private static readonly HashSet<int> alreadyWarnedAboutProp = new HashSet<int>();

    private static void SetPropActive(GameObject prop, bool active)
    {
        if (prop == null || prop.activeSelf == active) return; //null-tolerant so a half-filled mapping list is harmless, and no needless SetActive churn

        //REFUSE PREFAB ASSETS. A prop must be a child of the player IN THE SCENE. Drag a prefab from the Project
        //window into this list instead and SetActive writes to the asset ON DISK - Unity saves it, every future spawn
        //of that prefab comes out disabled, and the thing silently stops existing everywhere it is used. That cost an
        //evening: the gold bar was assigned here, got switched off the first frame the player wasn't holding one, and
        //from then on the safe stocked five invisible bars while looking exactly like a spawn failure.
        //A GameObject that lives in an asset rather than a loaded scene has no valid scene, which is the cheap test.
        if (!prop.scene.IsValid())
        {
            //ONCE PER OBJECT, not once per frame. This runs from Update, so a plain LogError here buries the console
            //in thousands of identical lines and hides whatever you were actually trying to read.
            if (alreadyWarnedAboutProp.Add(prop.GetInstanceID()))
            {
                Debug.LogError($"[Player] heldProps is pointing at the PREFAB ASSET '{prop.name}' instead of a child " +
                               "of the player. Refusing to touch it - disabling a prefab on disk breaks it everywhere. " +
                               "Drag the in-hand child object into that row instead.", prop);
            }
            return;
        }

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
        int wedges = 0;
        foreach (InventoryItem item in inventory)
        {
            if (!item.IsTool) continue;
            mask |= 1 << (int)item.tool;
            if (item.tool == ToolType.DoorWedge) wedges++; //a mask is a SET, so it can't count - and you carry several wedges
        }
        ToolMask = mask;
        WedgesCarried = wedges; //derived from the bag now. still networked, because teammates' prompts and the HUD read it
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

        int slot = ResolveDropSlot(); //whatever you're actually holding. -1 means empty hands, and G does nothing
        if (slot < 0) return;

        //THE ITEM IN YOUR HAND DECIDES, not a priority list. G used to try wedging first no matter what you were
        //holding, and when that quietly failed it dropped a vase at your feet instead - a surprising outcome from an
        //unrelated check. Now: holding a wedge at a door wedges it; holding anything else drops that thing.
        //There's only ONE wedge-shaped thing now, so this can't pick the wrong one. Holding a wedge and pressing G at
        //a door means wedge it; nothing else about the press is ambiguous.
        if (inventory[slot].tool == ToolType.DoorWedge)
        {
            //EAT THE PRESS EITHER WAY. If there's no door in reach it stays in your bag rather than being thrown on
            //the floor - dropping the thing you were trying to use is never what you meant, and it's exactly how this
            //whole area kept producing surprises.
            TryWedgeNearestDoor();
            return;
        }

        InventoryItem dropped = inventory[slot];

        //A jammer that's RUNNING goes down as the live device, so you can switch it on and leave it covering a
        //corridor. Switched off it's just an object, and takes the ordinary pickup path below like everything else -
        //which is what makes putting it down reversible rather than a commitment.
        if (dropped.tool == ToolType.SignalJammer && IsJammerActive)
        {
            DropJammerToFloor();
            return;
        }

        //the item's OWN prefab where one is mapped, so a dropped crowbar is a crowbar on the floor rather than a
        //generic box wearing the word "crowbar". Falls back to worldItemPrefab for loot and unmapped tools.
        NetworkObject prefabToDrop = WorldPrefabFor(dropped.tool);
        if (prefabToDrop == null) return;

        inventory.RemoveAt(slot);
        PublishCarriedCount();

        Vector3 dropPosition = transform.position + transform.forward * dropForwardOffset + Vector3.up; //spawn it a bit ahead and up so it falls to the floor
        Runner.Spawn(prefabToDrop, dropPosition, UnityEngine.Random.rotation, Object.InputAuthority, //random tilt so it tumbles and lands on a face, not balanced on a point
            (runner, spawnedObject) =>
            {
                WorldItem item = spawnedObject.GetComponent<WorldItem>();
                if (item != null) //carry the name AND value back onto the dropped item so it's worth the same when re-picked
                {
                    item.ItemName = dropped.name;
                    item.Value = dropped.value;
                    item.ToolKind = (int)dropped.tool; //a dropped crowbar has to still be a crowbar when it's picked back up
                    item.LootKind = (int)dropped.lootKind; //and a dropped gold bar has to still be a gold bar, not a generic trinket

                    item.SpawnPoint = dropPosition;  //same networked-position safeguard as placed loot, in case a drop ever gets deferred too
                    item.UseSpawnPoint = true;
                    item.CountedAsStolen = true;     //this loot was already counted against the house when it was FIRST lifted - picking it back up must not count it again
                    item.WasDroppedByPlayer = true;  //the ONLY place this is ever set - a dropped item is the one kind that gets real physics, everything a spawner placed stays scenery
                }
            });
    }
}
