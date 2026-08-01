using System.Collections;
using Fusion;
using UnityEngine;

// Spawns crackable safes at runtime, one per child transform, the same way ItemSpawner handles loot. Scene-placed
// NetworkObjects don't enrol reliably in Fusion Shared Mode, so the master spawns the safes and they replicate to
// everyone. Each CHILD of this object is a "safe goes here" marker - its position AND rotation are used, so face
// the marker's blue arrow the way you want the safe door to point. Loot/crack settings live on the safe prefab.
public class SafeSpawner : MonoBehaviour
{
    [SerializeField] private NetworkObject safePrefab;

    //STATIC on purpose. a per-spawner counter restarted at 1 in every spawner, so a second SafeSpawner in the same
    //house would hand out ids that already existed - and since a safe advances whenever ANY player's CrackingSafeId
    //matches its own, cracking one safe would quietly crack its twin across the map. ids only need to be unique, not
    //stable, because a player's CrackingSafeId is recomputed from proximity every tick.
    private static int nextSafeId = 1; //starts at 1 so a player's default CrackingSafeId of 0 (Safe.NoSafe) never matches a real safe

    private IEnumerator Start()
    {
        //wait until RunManager is spawned + valid - proves the sim's spawn system is fully up (same guard ItemSpawner uses)
        while (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid)
        {
            yield return null;
        }

        NetworkRunner runner = RunManager.Instance.Runner;
        if (runner == null || !runner.IsSharedModeMasterClient)
        {
            yield break; //only the master spawns; the safes replicate to everyone
        }

        foreach (Transform marker in transform)
        {
            int safeId = nextSafeId; //captured per-iteration for the closure below
            nextSafeId++;
            Vector3 markerPosition = marker.position;
            Quaternion markerRotation = marker.rotation;

            runner.Spawn(safePrefab, markerPosition, markerRotation, PlayerRef.None, (spawnRunner, spawnedObject) =>
            {
                Safe safe = spawnedObject.GetComponent<Safe>();
                if (safe != null)
                {
                    safe.SafeId = safeId;
                    safe.Code = Random.Range(1000, 10000); //4 digits, never leading-zero so it always reads as four characters. master rolls it and it replicates, so the note and the keypad can't disagree
                    Debug.Log($"[SAFE] id {safeId} code {safe.Code} at {markerPosition}"); //TEMP - there's no note in the world yet, so this is the only way to learn the code. delete once the note prop exists
                    safe.SpawnPoint = markerPosition; //networked-position safeguard, exactly like the loot fix - deferred spawns drop the position arg
                    safe.UseSpawnPoint = true;
                }
            });
        }
    }
}
