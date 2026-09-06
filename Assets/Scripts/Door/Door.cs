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

    //WHICH LOCAL AXIS POINTS THROUGH THE DOORWAY when the door is shut. Everything about wedges is decided by which
    //side of the door you're stood on, and that's this axis. If wedges behave back to front on a door, flip this to
    //Back (or Right/Left, depending on how the mesh was authored) rather than moving the door.
    public enum ThroughAxis { Forward, Back, Right, Left }
    [SerializeField] private ThroughAxis throughDoorwayAxis = ThroughAxis.Forward;

    private SwingingHinge hinge;
    private Quaternion closedWorldRotation; //captured while shut, so the side test doesn't swing about as the door opens

    private void Awake()
    {
        hinge = GetComponent<SwingingHinge>();
        closedWorldRotation = transform.rotation; //doors start closed on scene load, so this IS the closed pose
    }

    //The direction pointing through the doorway, frozen at the closed pose. Taking this live off transform.forward
    //would rotate with the door, so "which side am I on" would flip halfway through it swinging open.
    public Vector3 ThroughDoorway
    {
        get
        {
            switch (throughDoorwayAxis)
            {
                case ThroughAxis.Back:  return closedWorldRotation * Vector3.back;
                case ThroughAxis.Right: return closedWorldRotation * Vector3.right;
                case ThroughAxis.Left:  return closedWorldRotation * Vector3.left;
                default:                return closedWorldRotation * Vector3.forward;
            }
        }
    }

    //+1 or -1 for which side of the doorway a point is on. Two things on the same side agree; through the door they don't.
    public int SideOf(Vector3 worldPosition)
    {
        return Vector3.Dot(worldPosition - transform.position, ThroughDoorway) >= 0f ? 1 : -1;
    }

    //Identify a door by WHERE it is. Doors aren't NetworkObjects, so anything networked (the open/close RPC, a wedge)
    //has to refer to one by position. Static scene geometry sits at identical coordinates on every client, so this
    //resolves to the same door everywhere.
    public static Door FindNearest(Vector3 position, float maxDistance)
    {
        Door nearest = null;
        float nearestDistance = maxDistance;
        foreach (Door door in AllDoors)
        {
            float distance = Vector3.Distance(door.transform.position, position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = door;
            }
        }
        return nearest;
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

    //Wide enough for a person to walk through, which is NOT the same as "not shut". A door sitting at 20% is open by
    //any reasonable reading and still a wall as far as walking is concerned.
    public bool IsWideEnoughToPass => hinge != null && hinge.IsWideEnoughToPass;

    //The wedge jamming this door, or null. Derived from the wedges themselves rather than stored here: a Door isn't a
    //NetworkObject, so it can't hold networked state - but DoorWedge is one, and every client can read it.
    public DoorWedge Wedge => DoorWedge.WedgeOn(this);
    public bool IsWedged => Wedge != null;

    public void SetOpen(bool open) //run on EVERY client by RunManager.RPC_SetDoorOpen, so all copies swing together
    {
        if (open && IsWedged)
        {
            return; //jammed. the wedge has to come out first, by hand or by the guard breaking it
        }
        if (hinge != null)
        {
            hinge.SetOpen(open);
        }
    }

    //Shove it open away from whoever's pushing - the guard's version, so the leaf never swings through him. Same
    //wedge rule as above: a jammed door doesn't care which direction you'd have liked it to go.
    public void SetOpenAwayFrom(Vector3 openerPosition)
    {
        if (IsWedged)
        {
            return;
        }
        if (hinge != null)
        {
            hinge.SetOpenAwayFrom(openerPosition);
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

    //Nearest shut door that is actually ON the walker's route - the one they're about to walk through, rather than
    //one they happen to be facing. Kept separate from the plain distance search because the guard needs to know where
    //he's going and the dog doesn't.
    //
    //Is a closed door actually ON his route? The old question here was angular - "is this door roughly in front of
    //me" - and it was wrong in both directions at once. The Door sits on the HINGE, at the EDGE of a doorway, so a
    //door he is walking straight through reads almost side-on; meanwhile a door clear across the room reads dead
    //ahead. Widening the cone to stop missing the first made the second far worse, and he ended up flinging open
    //every door he happened to be facing - two at once, without taking a step, on waking up suspicious.
    //
    //Distance to the PATH SEGMENT answers both properly. A doorway he walks through puts its hinge roughly half a
    //metre off the line he travels; a door across the room is metres off it however squarely he faces it. Same
    //point-to-segment maths TripwireSpan uses, for the same reason - a thing with width is not a dot.
    public static Door FindClosedDoorOnPath(Vector3 pathStart, Vector3 pathEnd, float corridorRadius)
    {
        Vector3 alongPath = pathEnd - pathStart;
        alongPath.y = 0f;
        float pathLengthSquared = alongPath.sqrMagnitude;

        Door best = null;
        float bestDistance = float.MaxValue;
        foreach (Door door in AllDoors)
        {
            //IsWideEnoughToPass, NOT IsOpen. This is the AI asking "is this door in my way", and a door a player left
            //ajar at 20% is very much in his way even though it reads as open. Using IsOpen had him walk straight
            //through a gap far too narrow for a person rather than shoving it wider.
            if (door.IsWideEnoughToPass)
            {
                continue;
            }

            Vector3 towardDoor = door.transform.position - pathStart;
            towardDoor.y = 0f;

            //CLAMPED to the segment, so a door behind him or past the end of this leg is measured from the nearer
            //END of the route rather than from an imaginary extension of the line through the wall.
            float alongFraction = pathLengthSquared > 0.0001f
                ? Mathf.Clamp01(Vector3.Dot(towardDoor, alongPath) / pathLengthSquared)
                : 0f;
            if (Vector3.Distance(towardDoor, alongPath * alongFraction) > corridorRadius)
            {
                continue; //off his route - he may well be facing it, but he isn't going through it
            }

            float distanceFromHim = towardDoor.magnitude;
            if (distanceFromHim < bestDistance)
            {
                bestDistance = distanceFromHim;
                best = door;
            }
        }
        return best;
    }
}
