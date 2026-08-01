using System.Collections.Generic;
using UnityEngine;

// Marks a SwingingHinge as a HOUSE DOOR, which matters only to the AI.
//
// This script does NOT make something openable - SwingingHinge does that on its own, and props (cupboards, drawers,
// jewellery boxes) need nothing but the hinge. Door exists purely so the guard and dog can ask "where's the nearest
// shut door in my way" without also being handed every kitchen drawer in the house. Add it ONLY to real doors.
//
// The swinging, wobble and sound all live in SwingingHinge, which the safe and every prop use too. It's NOT a
// NetworkObject - every change goes through RunManager.RPC_SetDoorOpen and each client swings its OWN copy, so
// scene-placed doors sidestep Fusion's scene-object enrolment problem the loot ran into. That RPC identifies a
// door by its POSITION, so there's nothing to number or wire per door. Doors start closed on scene load.
//
// The hinge MUST sit at the door's pivot - if a door's pivot is at its centre it'll spin in place, so parent the
// mesh under an empty placed at the hinge. Put the door on the guard's obstacle layer (Enviorment) so a CLOSED door
// blocks his line of sight.
[RequireComponent(typeof(SwingingHinge))]
public class Door : MonoBehaviour
{
    public static readonly List<Door> AllDoors = new List<Door>();

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

    public static Door FindClosedDoorNear(Vector3 position, float range) //nearest shut door, any direction. the dog uses this to sniff at a door someone slammed
    {
        Door nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (Door door in AllDoors)
        {
            if (door.IsOpen)
            {
                continue;
            }
            float distance = Vector3.Distance(door.transform.position, position);
            if (distance <= range && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = door;
            }
        }
        return nearest;
    }

    //Nearest shut door that's also roughly in FRONT of the walker - the one they're about to walk into, rather than
    //one they're strolling past. Kept separate from the plain distance search because the guard needs the direction
    //test and the dog doesn't.
    //
    //minFacingDot has to be forgiving, and here's why: this script sits on the HINGE, not the middle of the doorway.
    //Walk straight at a door and its pivot is off to one side, so the angle from your forward to the pivot is wide -
    //nearly 90 degrees by the time you're close. A tight cone therefore rejects every door you're actually heading
    //through, which is exactly how the guard ended up ignoring all of them.
    public static Door FindClosedDoorAhead(Vector3 position, Vector3 facing, float range, float minFacingDot)
    {
        Door best = null;
        float bestDistance = float.MaxValue;
        foreach (Door door in AllDoors)
        {
            if (door.IsOpen)
            {
                continue;
            }

            Vector3 towardDoor = door.transform.position - position;
            towardDoor.y = 0f;
            float distance = towardDoor.magnitude;
            if (distance > range)
            {
                continue;
            }
            if (distance > 0.01f && Vector3.Dot(facing, towardDoor / distance) < minFacingDot)
            {
                continue; //behind him, or well off to the side - not one he's walking into
            }
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = door;
            }
        }
        return best;
    }
}
