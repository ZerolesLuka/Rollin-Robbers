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

        int nextSafeId = 1; //ids start at 1 so a player's default CrackingSafeId of 0 (NoSafe) never matches a real safe
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
                    safe.SpawnPoint = markerPosition; //networked-position safeguard, exactly like the loot fix - deferred spawns drop the position arg
                    safe.UseSpawnPoint = true;
                }
            });
        }
    }
}
