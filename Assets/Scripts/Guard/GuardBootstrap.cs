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
    }
}
