using System.Collections.Generic;
using Fusion;
using UnityEngine;

// A deployed signal jammer. You put it DOWN somewhere while it's running and it blinds every camera within its radius
// until that burst runs out, then drops back to an ordinary pickup with whatever charges it had left.
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
    [SerializeField] private NetworkObject pickupPrefab; //the WorldItem this turns back into when the burst ends. plain WorldItem works; a WorldItem_Jammer looks right

    [Networked] public float SecondsLeft { get; set; }   //ticks down on the authority, replicated so any HUD can show it
    [Networked] public int ChargesLeft { get; set; }     //charges the unit still had when it was put down, handed to whoever picks it back up
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

    [Networked] private NetworkBool dead { get; set; } //latched. without it the block below re-fires every tick of the despawn delay

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority || dead) return;

        SecondsLeft -= Runner.DeltaTime;
        if (SecondsLeft <= 0f)
        {
            //ONCE. this runs at 32Hz and the despawn is deliberately delayed to let the sound finish, so without the
            //latch it would fire the death chirp about twenty times and start twenty despawn coroutines.
            dead = true;
            SecondsLeft = 0f; //stop it drifting negative - anything reading it for a HUD bar would go past empty
            RPC_BatteryDied();
            LeavePickupBehind();
            StartCoroutine(DespawnAfterSound());
        }
    }

    //THE BURST ENDING DOESN'T DESTROY THE UNIT. This used to despawn for good the moment its time ran out - left over from
    //when the jammer was a one-shot battery - so leaving a running jammer covering a corridor quietly threw away a 550
    //tool and every charge still on it. Now it drops back to an ordinary pickup, charges and all.
    private void LeavePickupBehind()
    {
        if (pickupPrefab == null)
        {
            Debug.LogError($"[JammerDevice] '{name}' has no Pickup Prefab, so this jammer is destroyed when its burst ends. Assign WorldItem (or WorldItem_Jammer) on the JammerDevice prefab.", this);
            return;
        }

        Vector3 dropAt = transform.position + Vector3.up * 0.2f;
        int charges = ChargesLeft; //read now, not inside the deferred callback
        Runner.Spawn(pickupPrefab, dropAt, Quaternion.identity, PlayerRef.None, (runner, spawnedObject) =>
        {
            WorldItem item = spawnedObject.GetComponent<WorldItem>();
            if (item == null)
            {
                return;
            }
            item.ItemName = ToolTable.NameOf(ToolType.SignalJammer);
            item.Value = 0;
            item.ToolKind = (int)ToolType.SignalJammer;
            item.ToolCharges = charges;
            item.SpawnPoint = dropAt;         //networked-position safeguard - a deferred spawn drops the position argument
            item.UseSpawnPoint = true;
            item.CountedAsStolen = true;      //your own kit, never house loot
            item.WasDroppedByPlayer = true;   //a real object on the floor, not scenery
        });
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
    //
    //TWO SOURCES now: units sitting on the floor, and players carrying one they've switched on with right-click. A
    //camera shouldn't have to care which - it only wants to know whether it can see.
    public static bool CoversPosition(Vector3 position)
    {
        foreach (JammerDevice jammer in AllJammers)
        {
            if (jammer.SecondsLeft <= 0f) continue; //flat battery, still finishing its death sound
            if (Vector3.Distance(jammer.transform.position, position) <= ToolTable.JammerRadius) return true;
        }
        return Player.AnyCarriedJammerCovers(position);
    }
}
