using System.Collections.Generic;
using Fusion;
using UnityEngine;

// A door wedge. Kick it under a door and the door will not open, for anybody, from either side.
//
// The whole mechanic is WHICH SIDE you are on. You can only pull a wedge out from the side it was kicked in from, so
// wedging a door does not just slow the guard down, it decides who is shut in with what. You can absolutely trap a
// teammate in a room with one, and that is deliberate: the wedge is a tool, not a safety device.
//
// The guard has no such rule, he just breaks it, but it costs him time and he has to stand there doing it. Two
// seconds if he is on the wedge's side and can reach it, five if he has to force the door from the far side.
//
// Runtime-spawned like every other networked object here. Doors are not NetworkObjects so a wedge refers to its door
// by POSITION, the same way RunManager's open/close RPC does.
[RequireComponent(typeof(NetworkObject))]
public class DoorWedge : NetworkBehaviour
{
    public static readonly List<DoorWedge> AllWedges = new List<DoorWedge>();

    [SerializeField] private float pickupRange = 2f;      // reach to grab a loose one off the floor
    [SerializeField] private float doorMatchDistance = 1.5f; // how close a wedge must be to a door to count as that door's. tight, so it can't claim one across the hall

    [Networked] public NetworkBool IsPlaced { get; set; }   // false = lying on the floor waiting to be picked up, true = jammed under a door
    [Networked] public Vector3 DoorPosition { get; set; }   // which door it's jamming. doors aren't networked, so they're identified by where they are
    [Networked] public int WedgedSide { get; set; }         // +1 or -1, from Door.SideOf. you can only pull it out from this side
    [Networked] public Vector3 SpawnPoint { get; set; }     // same deferred-spawn safeguard as everything else - a deferred spawn drops the position argument
    [Networked] public NetworkBool UseSpawnPoint { get; set; }

    public float PickupRange => pickupRange;

    public override void Spawned()
    {
        AllWedges.Add(this);
        if (UseSpawnPoint)
        {
            StartCoroutine(PlaceOnceSpawnPointArrives());
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        AllWedges.Remove(this);
    }

    private System.Collections.IEnumerator PlaceOnceSpawnPointArrives()
    {
        //(0,0,0) doubles as the "hasn't replicated yet" sentinel, so cap the wait - a wedge genuinely belonging at the
        //world origin would otherwise spin here forever, invisible as a bug.
        int framesWaited = 0;
        while (SpawnPoint == Vector3.zero && framesWaited < 120)
        {
            framesWaited++;
            yield return null;
        }
        transform.position = SpawnPoint;
    }

    public Door TargetDoor => IsPlaced ? Door.FindNearest(DoorPosition, doorMatchDistance) : null;

    //Can whoever is stood here actually get at it? Only from the side it went in. This is the rule the whole feature
    //hangs on - lose it and a wedge is just a door that opens slower.
    public bool CanBeRemovedFrom(Vector3 worldPosition)
    {
        Door door = TargetDoor;
        if (door == null) return true; //its door vanished (scene change) - don't let it become permanently stuck
        return door.SideOf(worldPosition) == WedgedSide;
    }

    public static DoorWedge WedgeOn(Door door) //the wedge jamming this door, or null
    {
        if (door == null) return null;
        foreach (DoorWedge wedge in AllWedges)
        {
            if (!wedge.IsPlaced) continue;
            if (Vector3.Distance(wedge.DoorPosition, door.transform.position) <= wedge.doorMatchDistance) return wedge;
        }
        return null;
    }

    public static DoorWedge LooseWedgeNear(Vector3 position) //a wedge lying on the floor within arm's reach, or null
    {
        DoorWedge nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (DoorWedge wedge in AllWedges)
        {
            if (wedge.IsPlaced) continue;
            float distance = Vector3.Distance(wedge.transform.position, position);
            if (distance <= wedge.pickupRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = wedge;
            }
        }
        return nearest;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_TakeWedge() //picked up off the floor, or pulled back out of a door. the authority owns the despawn
    {
        if (Object != null && Object.IsValid)
        {
            Runner.Despawn(Object);
        }
    }
}
