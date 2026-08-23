using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// Player - scene-load spawning. TeleportTo/ApplyPendingTeleport are the CharacterController-safe teleport pipeline
// (the fields hasPendingTeleport / teleportSettleTicks that drive it live in the core FixedUpdateNetwork). The
// coroutine finds the right SpawnPoint after a scene change, and FindMyVanSeat picks this player's van seat.
public partial class Player
{
    //WHERE WE WERE STOOD IN THE VAN, in the VAN'S OWN local space. Kept so a scene change can put us back in the same
    //spot rather than stacking the whole crew on one point.
    //
    //Local space, not a world offset: the destination van may be parked facing a completely different direction, and
    //a world-space delta would drop us outside it. Local space means "left of the wheel arch" stays left of the wheel
    //arch whichever way the van is pointing.
    //
    //Plain private fields, NOT networked. Only our own machine cares where we personally were, and the Player object
    //survives scene loads, so these ride across intact.
    private Vector3 vanLocalPosition;
    private bool hasVanLocalPosition;   //are we in the van THIS FRAME - cleared as soon as we step out
    private bool departedFromTheVan;    //were we in it at the moment the scene changed - latched in OnSceneChanged, read by the coroutine
    private const float VanRememberRadius = 6f; //only note our spot while we're actually AT the van - otherwise we'd record a position from halfway across the garden and restore that into the next van, i.e. inside a wall

    //Called every frame from Update on the local player only. Continuous capture rather than a snapshot taken as we
    //leave, because there's no clean hook for the moment of departure - activeSceneChanged fires AFTER the load, by
    //which point the old van is already destroyed.
    private void RememberVanPosition()
    {
        Van van = FindVanInThisScene();
        bool insideTheVan = van != null && Vector3.Distance(transform.position, van.transform.position) <= VanRememberRadius;

        //CLEARS ITSELF the moment we walk away. The flag therefore means "we are in the van RIGHT NOW", not "we were
        //once", and that distinction is the whole fix: leaving the house through a door has to land on the door's
        //SpawnPoint, but Outdoor also contains a van - so a stale flag hijacked every door transition and dumped us
        //back in the van instead of on the doorstep.
        if (!insideTheVan)
        {
            hasVanLocalPosition = false;
            return;
        }

        vanLocalPosition = van.transform.InverseTransformPoint(transform.position);
        hasVanLocalPosition = true;
    }

    private Van FindVanInThisScene()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        foreach (Van candidate in Van.AllVans)
        {
            //vans from the scene we just left linger in the static list for a frame or two holding stale coordinates -
            //the SpawnPoint search below guards against exactly the same thing
            if (candidate == null || candidate.gameObject.scene != activeScene)
            {
                continue;
            }
            return candidate;
        }
        return null;
    }

    public void TeleportTo(Vector3 position) //called after a scene load to reposition the local player
    {
        if (!HasInputAuthority) return; //only move our own player; Fusion syncs the position to everyone else
        pendingTeleportPosition = position;
        hasPendingTeleport = true; //applied in FixedUpdateNetwork - see the block at the top of it
    }

    private void ApplyPendingTeleport()
    {
        hasPendingTeleport = false;
        verticalVelocity = 0f; //reset fall speed so the player doesn't phase through the floor on arrival
        NoiseLevel = 0f; //clear any stale walking noise from before the transition so a freshly-spawned guard doesn't wake to a footstep that already happened
        characterController.enabled = false; //stays off for teleportSettleTicks so the disable is processed before the re-enable
        transform.position = pendingTeleportPosition;

        NetworkTransform networkTransform = GetComponent<NetworkTransform>();
        if (networkTransform != null)
        {
            networkTransform.Teleport(pendingTeleportPosition); //update the networked position + clear interpolation so Fusion doesn't lerp us back to the old spot
        }

        teleportSettleTicks = 2;
    }

    private void OnSceneChanged(Scene previous, Scene next)
    {
        Cursor.lockState = CursorLockMode.Locked;

        //SAFETY NET for every "something in the scene we just left is still holding us" state. All three freeze
        //movement, and all three are held by an object that died with the old scene, so none of them can be undone
        //the normal way once we've arrived. A teammate opening the exit door is enough to trigger any of them.
        if (isUsingComputer)
        {
            //the terminal we were sat at is a destroyed object now, and E can't rescue us because exiting needs
            //currentTerminal. the route buttons stand us up properly, but any OTHER scene change would strand us.
            ExitComputer();
        }

        if (IsHiding)
        {
            //the exact softlock the run-end van ride already guards against, which a mid-run scene change could still
            //reach: IsHiding freezes movement AND hides playerVisuals, and the only way out is pressing E beside a
            //hiding spot - which does not exist in the van or the pawn shop. Invisible, immobile, for the rest of the
            //session. Clearing the id too, or that wardrobe reads as occupied for the whole next run.
            SetHiding(false, HidingSpot.NoSpot);
        }

        if (IsBearTrapped)
        {
            //the trap despawns 0.6s after it springs (GuardTrap.DespawnAfterSound), so being pinned is pure player
            //state with no object behind it - carrying it into the next scene leaves you held by nothing at all.
            //bearTrapSelfEscapeSeconds would eventually free you, but that timer is documented as safe to set to 0
            //for teammate-only rescues, and at 0 this is a permanent freeze. Same shape as the drag fix in July.
            IsBearTrapped = false;
        }

        //LATCH IT HERE, before the coroutine runs. activeSceneChanged fires before any Update in the new scene, so
        //this still reflects whether we were in the van as we left. Reading hasVanLocalPosition inside the coroutine
        //instead would race: the coroutine yields, Update runs first, finds we're nowhere near the NEW scene's van,
        //and clears the flag before the coroutine ever gets to look at it.
        departedFromTheVan = hasVanLocalPosition;

        StartCoroutine(TeleportAfterLoad());
    }

    private System.Collections.IEnumerator TeleportAfterLoad()
    {
        //if the run already ended, the van ride is handled in FixedUpdateNetwork - don't also do a normal door-spawn teleport here
        bool runEnded = RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid
            && RunManager.Instance.State != RunManager.RunState.InProgress;
        if (runEnded)
        {
            yield break;
        }

        SpawnPoint spawnPoint = null;
        float timeout = 5f;
        while (spawnPoint == null && timeout > 0f)
        {
            //re-read the id every frame - the networked value may not have replicated on the frame the scene loaded
            int targetId = (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
                ? RunManager.Instance.EntrySpawnPointId
                : 0;

            Scene activeScene = SceneManager.GetActiveScene();
            foreach (SpawnPoint candidate in SpawnPoint.All)
            {
                //ignore spawn points from the scene we just left - they linger in the static list for a frame or two and hold stale coordinates
                if (candidate.gameObject.scene != activeScene) continue;
                if (candidate.spawnId == targetId)
                {
                    spawnPoint = candidate;
                    break;
                }
            }
            timeout -= Time.deltaTime;
            yield return null;
        }

        //fall back to any spawn point IN THIS SCENE rather than leaving the player floating in the void at their old coordinates
        if (spawnPoint == null)
        {
            Scene activeScene = SceneManager.GetActiveScene();
            foreach (SpawnPoint candidate in SpawnPoint.All)
            {
                if (candidate.gameObject.scene != activeScene) continue;
                spawnPoint = candidate;
                Debug.LogWarning($"[Player] Matching SpawnPoint not found in time - falling back to '{spawnPoint.name}'.");
                break;
            }
        }

        //BACK TO WHERE WE WERE STANDING, if the place we've arrived at has a van and we noted a spot inside the last
        //one. This outranks the SpawnPoint because a SpawnPoint is a single fixed marker - route four players through
        //it and they arrive inside each other, since CharacterControllers don't push each other apart.
        //
        //Deliberately checked AFTER the loop above rather than waiting on its own: the van and the SpawnPoints are
        //scene objects that register together, so if a SpawnPoint has turned up the van has too. Polling separately
        //would just add a stall to every trip into the house, which has no van at all.
        Van arrivalVan = departedFromTheVan ? FindVanInThisScene() : null;
        if (arrivalVan != null)
        {
            TeleportTo(arrivalVan.transform.TransformPoint(vanLocalPosition));
            yield break;
        }

        if (spawnPoint != null)
        {
            TeleportTo(spawnPoint.transform.position);
        }
        else
        {
            Debug.LogWarning($"[Player] No SpawnPoints exist in scene '{SceneManager.GetActiveScene().name}'.");
        }
    }

    private VanSeat FindMyVanSeat() //my assigned seat if it exists in the loaded scene, else any seat, else null (van scene not loaded yet)
    {
        Scene activeScene = SceneManager.GetActiveScene();
        int seatIndex = Object.InputAuthority.PlayerId % 4;

        VanSeat fallback = null;
        foreach (VanSeat candidate in VanSeat.AllSeats)
        {
            if (candidate.gameObject.scene != activeScene) continue; //ignore seats lingering from a scene we just left
            if (candidate.seatIndex == seatIndex) return candidate; //my exact seat
            fallback = candidate; //at least land in the van somewhere
        }
        return fallback;
    }
}
