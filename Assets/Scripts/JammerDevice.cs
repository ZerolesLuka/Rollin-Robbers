using System.Collections.Generic;
using Fusion;
using UnityEngine;

// A deployed signal jammer. You put it DOWN somewhere and it blinds every camera within its radius until the battery
// dies, then it's gone for good.
//
// This started as a passive tool you simply owned, and that was the problem: it worked invisibly, on a prop that
// might only appear once in a house, so you could carry it a whole run and never observe it doing anything. As an
// object you place, every part of it is legible - you chose the spot, you can see it sat there, you can watch the
// battery go, and when it dies you know exactly what you lost.
//
// Runtime-spawned like every other networked object here, and it lives in the world rather than on the player, so
// the crew can leave it covering a hallway and walk away from it.
[RequireComponent(typeof(NetworkObject))]
public class JammerDevice : NetworkBehaviour
{
    public static readonly List<JammerDevice> AllJammers = new List<JammerDevice>();

    [SerializeField] private AudioClip deployClip;   //the thump of it being set down
    [SerializeField] private AudioClip dieClip;      //battery gone. worth hearing, because it's the moment the cameras wake up
    [SerializeField, Range(0f, 1f)] private float volume = 0.8f;

    [Networked] public float SecondsLeft { get; set; }   //ticks down on the authority, replicated so any HUD can show it
    [Networked] public Vector3 SpawnPoint { get; set; }  //same deferred-spawn safeguard as everything else
    [Networked] public NetworkBool UseSpawnPoint { get; set; }

    private AudioSource jammerAudio;

    public override void Spawned()
    {
        AllJammers.Add(this);

        jammerAudio = gameObject.AddComponent<AudioSource>();
        jammerAudio.playOnAwake = false;
        jammerAudio.loop = false;
        jammerAudio.spatialBlend = 1f;
        jammerAudio.rolloffMode = AudioRolloffMode.Linear;
        jammerAudio.minDistance = 2f;
        jammerAudio.maxDistance = 25f;
        AudioOcclusion.Attach(jammerAudio);

        if (UseSpawnPoint)
        {
            StartCoroutine(PlaceOnceSpawnPointArrives());
        }

        PlayClip(deployClip);
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        AllJammers.Remove(this);
    }

    private System.Collections.IEnumerator PlaceOnceSpawnPointArrives()
    {
        //(0,0,0) doubles as the "hasn't replicated yet" sentinel, so cap the wait - one genuinely belonging at the
        //world origin would otherwise spin here forever, invisible as a bug.
        int framesWaited = 0;
        while (SpawnPoint == Vector3.zero && framesWaited < 120)
        {
            framesWaited++;
            yield return null;
        }
        transform.position = SpawnPoint;
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return;

        SecondsLeft -= Runner.DeltaTime;
        if (SecondsLeft <= 0f)
        {
            RPC_BatteryDied();
            StartCoroutine(DespawnAfterSound());
        }
    }

    private System.Collections.IEnumerator DespawnAfterSound() //let the death chirp play before the object carrying the speaker vanishes
    {
        yield return new WaitForSeconds(0.6f);
        if (HasStateAuthority && Object != null && Object.IsValid)
        {
            Runner.Despawn(Object);
        }
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_BatteryDied()
    {
        PlayClip(dieClip != null ? dieClip : deployClip);
    }

    private void PlayClip(AudioClip clip)
    {
        if (clip == null || jammerAudio == null) return;
        jammerAudio.PlayOneShot(clip, volume);
    }

    //Is this spot inside a live jammer's bubble? Asked by cameras, which know where they're pointing but nothing else.
    public static bool CoversPosition(Vector3 position)
    {
        foreach (JammerDevice jammer in AllJammers)
        {
            if (jammer.SecondsLeft <= 0f) continue; //flat battery, still finishing its death sound
            if (Vector3.Distance(jammer.transform.position, position) <= ToolTable.JammerRadius) return true;
        }
        return false;
    }
}
