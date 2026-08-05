using Fusion;
using UnityEngine;

// Attach to a GameObject in the indoor scene. GameBootstrap calls TriggerSpawn from
// OnSceneLoadDone so Fusion is fully settled before we try to spawn anything.
public class GuardBootstrap : MonoBehaviour
{
    [SerializeField] private NetworkObject guardPrefab;
    [SerializeField] private Transform guardSpawn;   //also the centre of his patrol - he wanders a radius around this, no waypoint list needed
    [SerializeField] private Transform closetSpot;

    [SerializeField] private NetworkObject dogPrefab;     //leave null to skip the dog entirely - not every house needs one
    [SerializeField] private Transform dogSpawn;
    [SerializeField] private Transform[] dogWaypoints;    //optional - DogAI wanders near its spawn if this is empty

    public void TriggerSpawn(NetworkRunner runner)
    {
        if (!runner.IsSharedModeMasterClient) return;

        //The OPTIONAL dog below is carefully null-checked; the mandatory guard was not, and that asymmetry was the
        //dangerous way round. An empty guardSpawn slot threw on .position here, and because the exception aborts this
        //whole method it took the dog spawn down with it - one unassigned field in the Indoor scene and the house
        //contained no animals at all, with nothing in the log naming the field.
        if (guardPrefab == null || guardSpawn == null)
        {
            Debug.LogError("[GuardBootstrap] guardPrefab or guardSpawn is unassigned, so no guard will exist in this house at all. The dog (if any) still spawns.", this);
        }
        else
        {
            runner.Spawn(guardPrefab, guardSpawn.position, Quaternion.identity, PlayerRef.None,
                (spawnRunner, obj) =>
                {
                    GuardPatrol guard = obj.GetComponent<GuardPatrol>();
                    if (guard == null)
                    {
                        Debug.LogError("[GuardBootstrap] The guard prefab has no GuardPatrol on it.", this);
                        return;
                    }
                    guard.SetCloset(closetSpot); //may legitimately be null - GuardPatrol handles that and says so
                });
        }

        if (dogPrefab != null && dogSpawn != null)
        {
            runner.Spawn(dogPrefab, dogSpawn.position, Quaternion.identity, PlayerRef.None,
                (spawnRunner, obj) =>
                {
                    DogAI dog = obj.GetComponent<DogAI>();
                    dog.SetWaypoints(dogWaypoints);
                });
        }
    }
}
