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

        //safety net: any terminal we were sat at died with the scene we just left, and isUsingComputer freezes
        //movement with no way to clear it once currentTerminal is a destroyed object. the route buttons stand us up
        //properly now, but anything else that changes scene mid-session would otherwise strand us frozen here.
        if (isUsingComputer)
        {
            ExitComputer();
        }

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
