using System.Collections.Generic;
using UnityEngine;

// A house door. Players open it with E; the guard and dog shove it open when they walk into one. It's NOT a
// NetworkObject - every change goes through RunManager.RPC_SetDoorOpen and each client swings its OWN copy, so
// scene-placed doors sidestep Fusion's scene-object enrolment problem the loot ran into. That RPC identifies a
// door by its POSITION, so there's nothing to number or wire per door. Doors start closed on scene load.
//
// The actual swinging, wobble and sound all live in SwingingHinge, which the safe uses too. This script is just the
// door-specific part: the registry the AIs search, and the interact range. The hinge MUST sit at the door's pivot -
// if a door's pivot is at its centre it'll spin in place, so parent the mesh under an empty placed at the hinge.
// Put the door on the guard's obstacle layer (Enviorment) so a CLOSED door blocks his line of sight.
[RequireComponent(typeof(SwingingHinge))]
public class Door : MonoBehaviour
{
    public static readonly List<Door> AllDoors = new List<Door>();

    [SerializeField] public float interactRange = 2f;

    private SwingingHinge hinge;

    private void Awake()
    {
        hinge = GetComponent<SwingingHinge>();
    }

    private void OnEnable()
    {
        AllDoors.Add(this);
    }

    private void OnDisable()
    {
        AllDoors.Remove(this);
    }

    public bool IsOpen => hinge != null && hinge.IsOpen;

    public void SetOpen(bool open) //run on EVERY client by RunManager.RPC_SetDoorOpen, so all copies swing together
    {
        if (hinge != null)
        {
            hinge.SetOpen(open);
        }
    }

    public static Door FindClosedDoorNear(Vector3 position, float range) //used by the AIs to spot a shut door in their way
    {
        foreach (Door door in AllDoors)
        {
            if (door.IsOpen)
            {
                continue;
            }
            if (Vector3.Distance(door.transform.position, position) <= range)
            {
                return door;
            }
        }
        return null;
    }
}
