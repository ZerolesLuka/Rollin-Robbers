using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// A spot in the house where a tripwire CAN be strung - a doorway, the top of the stairs, a hallway pinch point.
// Drop an empty GameObject wherever a wire would make sense and put this on it. Same idea as SpawnPoint: the level
// decides where these are, the code just picks between them.
//
// The guard doesn't wander looking for one. When he gives up a hunt he takes the point NEAREST to where he last saw
// someone, so he's covering the way he thinks you went. That keeps his choices readable - players learn the wire
// spots and start checking them - while still being driven by where you actually were.
public class TrapPoint : MonoBehaviour
{
    public static readonly List<TrapPoint> All = new List<TrapPoint>();

    [SerializeField] private float occupiedRadius = 1f; //a trap already sitting this close means this point is taken

    private void OnEnable() => All.Add(this);
    private void OnDisable() => All.Remove(this);

    //Nearest free point to where he lost you, or null if there's nothing usable in range.
    public static TrapPoint FindNearestFree(Vector3 position, float maxRange)
    {
        TrapPoint nearest = null;
        float nearestDistance = maxRange;
        Scene activeScene = SceneManager.GetActiveScene();

        foreach (TrapPoint point in All)
        {
            //points from the scene we just left linger in this static list for a frame or two holding stale
            //coordinates - the same trap SpawnPoint hits. ignore anything that isn't in the scene we're actually in.
            if (point.gameObject.scene != activeScene) continue;

            float distance = Vector3.Distance(point.transform.position, position);
            if (distance >= nearestDistance) continue;
            if (GuardTrap.AnyTrapNear(point.transform.position, point.occupiedRadius)) continue; //already wired

            nearestDistance = distance;
            nearest = point;
        }
        return nearest;
    }

    private void OnDrawGizmos() //so you can see where the wires can go while dressing the level
    {
        Gizmos.color = new Color(1f, 0.35f, 0.35f, 0.9f);
        Gizmos.DrawWireSphere(transform.position, 0.35f);
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.2f);
    }
}
