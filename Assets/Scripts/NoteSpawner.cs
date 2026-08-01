using System.Collections;
using Fusion;
using UnityEngine;

// Drops ONE safe-code note somewhere in the house. Same shape as SafeSpawner and ItemSpawner - the children of this
// object are candidate spots - except it picks a single one at random instead of using them all. Give it plenty of
// children: the more possible hiding places, the more the crew actually has to search rather than beeline.
public class NoteSpawner : MonoBehaviour
{
    [SerializeField] private NetworkObject notePrefab;

    private IEnumerator Start()
    {
        //wait until RunManager is spawned + valid - proves the sim's spawn system is fully up (same guard the other spawners use)
        while (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid)
        {
            yield return null;
        }

        NetworkRunner runner = RunManager.Instance.Runner;
        if (runner == null || !runner.IsSharedModeMasterClient)
        {
            yield break; //only the master spawns; the note replicates to everyone
        }

        if (notePrefab == null || transform.childCount == 0)
        {
            yield break; //nothing to spawn, or nowhere to put it
        }

        //a note is worthless without a safe to unlock, so wait for SafeSpawner to have produced at least one. capped
        //so a house with no safe in it doesn't leave this coroutine spinning for the whole run.
        int framesWaited = 0;
        while (Safe.AllSafes.Count == 0 && framesWaited < 300)
        {
            framesWaited++;
            yield return null;
        }
        if (Safe.AllSafes.Count == 0)
        {
            Debug.LogWarning("[NoteSpawner] No safes exist, so there's no code worth writing down - skipping the note.");
            yield break;
        }

        Safe targetSafe = Safe.AllSafes[Random.Range(0, Safe.AllSafes.Count)]; //one note per house for now; with several safes it picks whose code to leak
        Transform marker = transform.GetChild(Random.Range(0, transform.childCount));
        Vector3 markerPosition = marker.position;
        Quaternion markerRotation = marker.rotation;
        int targetSafeId = targetSafe.SafeId;

        runner.Spawn(notePrefab, markerPosition, markerRotation, PlayerRef.None, (spawnRunner, spawnedObject) =>
        {
            SafeNote note = spawnedObject.GetComponent<SafeNote>();
            if (note != null)
            {
                note.SafeId = targetSafeId;
                note.SpawnPoint = markerPosition; //networked-position safeguard, same as the loot and the safes
                note.UseSpawnPoint = true;
            }
        });

        Debug.Log($"[NOTE] spawned at '{marker.name}' {markerPosition} for safe id {targetSafeId}"); //TEMP - handy while there's no other way to find it
    }
}
