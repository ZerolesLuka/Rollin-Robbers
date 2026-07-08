using Fusion;
using UnityEngine;

// Attach to a GameObject in the indoor scene. GameBootstrap calls TriggerSpawn from
// OnSceneLoadDone so Fusion is fully settled before we try to spawn anything.
public class GuardBootstrap : MonoBehaviour
{
    [SerializeField] private NetworkObject guardPrefab;
    [SerializeField] private Transform guardSpawn;
    [SerializeField] private Transform[] guardWaypoints;
    [SerializeField] private Transform closetSpot;

    [SerializeField] private NetworkObject dogPrefab;     //leave null to skip the dog entirely - not every house needs one
    [SerializeField] private Transform dogSpawn;
    [SerializeField] private Transform[] dogWaypoints;    //optional - DogAI wanders near its spawn if this is empty

    public void TriggerSpawn(NetworkRunner runner)
    {
        if (!runner.IsSharedModeMasterClient) return;

        runner.Spawn(guardPrefab, guardSpawn.position, Quaternion.identity, PlayerRef.None,
            (spawnRunner, obj) =>
            {
                GuardPatrol guard = obj.GetComponent<GuardPatrol>();
                guard.SetWaypoints(guardWaypoints);
                guard.SetCloset(closetSpot);
            });

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
