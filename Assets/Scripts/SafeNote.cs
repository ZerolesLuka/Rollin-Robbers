using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

// A scrap of paper with a safe's combination on it. Press E to read it - the number goes onto YOUR hud only, which
// is the whole point: the person who finds it is rarely the person stood at the safe, so the code has to travel by
// voice. The note stays in the world after reading so a teammate can go and check it themselves.
//
// It stores the safe's ID rather than a copy of the code, and looks the live number up when read. That way it does
// not matter whether the note or the safe spawned first - by the time a player is reading it, both exist.
[RequireComponent(typeof(NetworkObject))]
public class SafeNote : NetworkBehaviour
{
    public static readonly List<SafeNote> AllNotes = new List<SafeNote>();

    [SerializeField] private float readRange = 2f; // how close you have to be to press E on it

    [Networked] public int SafeId { get; set; }              // which safe this note is the code for
    [Networked] public Vector3 SpawnPoint { get; set; }      // same deferred-spawn safeguard as WorldItem and Safe - a deferred spawn silently drops the position argument
    [Networked] public NetworkBool UseSpawnPoint { get; set; }

    public float ReadRange => readRange;

    public override void Spawned()
    {
        AllNotes.Add(this);
        if (UseSpawnPoint)
        {
            StartCoroutine(PlaceOnceSpawnPointArrives());
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        AllNotes.Remove(this);
    }

    private IEnumerator PlaceOnceSpawnPointArrives()
    {
        //(0,0,0) doubles as the "hasn't replicated yet" sentinel, so cap the wait - a note that genuinely belongs at
        //the world origin would otherwise spin here forever, invisible as a bug.
        int framesWaited = 0;
        while (SpawnPoint == Vector3.zero && framesWaited < 120)
        {
            framesWaited++;
            yield return null;
        }
        transform.position = SpawnPoint;
    }

    public int ReadCode() //the live code off the safe this note belongs to, or 0 if that safe is gone
    {
        foreach (Safe safe in Safe.AllSafes)
        {
            if (safe.SafeId == SafeId)
            {
                return safe.Code;
            }
        }
        return 0;
    }
}
