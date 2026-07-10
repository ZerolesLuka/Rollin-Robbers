using System.Collections;
using Fusion;
using UnityEngine;

// Spawns pickup items at runtime, one per child transform of this object. Fusion doesn't enroll scene-placed
// NetworkObjects reliably (especially in the initial scene), so we spawn them properly like the guard and
// floorboards. Name each child what you want the item called - "Knife", "Vase" - and it spawns there with that name.
public class ItemSpawner : MonoBehaviour
{
    [SerializeField] private NetworkObject worldItemPrefab;

    private IEnumerator Start()
    {
        //wait until RunManager is spawned + valid. that proves the simulation's spawn system is fully up - spawning any earlier NREs inside Fusion's id allocator
        while (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid)
        {
            yield return null;
        }

        NetworkRunner runner = RunManager.Instance.Runner;
        if (runner == null || !runner.IsSharedModeMasterClient) yield break; //only the host spawns; the items replicate to everyone

        foreach (Transform spawnPoint in transform)
        {
            string itemName = spawnPoint.name; //the child's name IS the item's name
            runner.Spawn(worldItemPrefab, spawnPoint.position, Random.rotation, PlayerRef.None, //random tilt so it settles on a face instead of balancing on a point; the rotation syncs as part of the spawn
                (spawnRunner, spawnedObject) =>
                {
                    WorldItem item = spawnedObject.GetComponent<WorldItem>();
                    if (item != null) item.ItemName = itemName;
                });
        }
    }
}
