using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

// A tripwire the GUARD leaves behind. He drops one at the spot he just finished searching and found nothing - so the
// rooms you keep robbing are the rooms that quietly fill up with these. Walk into one and it snaps, and he comes
// straight back to it. That's the whole loop: the house gets more dangerous in exactly the places you keep working.
//
// Runtime-spawned by GuardPatrol, never scene-placed - Fusion Shared Mode doesn't enrol scene NetworkObjects
// reliably, same reason Safe and the loot are spawned too.
//
// Detection is the SqueakyToy trick rather than a physics trigger: trigger events don't fire for remote players
// (they move by NetworkTransform, not CharacterController.Move), so we read replicated positions on the authority
// instead. Works for everyone, needs no collider.
[RequireComponent(typeof(NetworkObject))]
public class GuardTrap : NetworkBehaviour
{
    public static readonly List<GuardTrap> AllTraps = new List<GuardTrap>();

    [SerializeField] private float triggerRadius = 1.2f;   // how close you have to step. small on purpose - this should feel like bad luck or bad looking, not an aura
    [SerializeField] private float disarmRange = 2f;       // how close you must be to defuse it with E. bigger than the trigger radius would be a joke, so keep it modest
    [SerializeField] private float armDelay = 1.5f;        // he's stood right next to it as he sets it, and so might you be. don't let it fire the instant it lands
    [SerializeField] private AudioClip snapClip;           // the sound of it going off. heard by everyone nearby, 3D
    [SerializeField] private AudioClip disarmClip;         // optional, plays when a player defuses it
    [SerializeField, Range(0f, 1f)] private float volume = 0.85f;
    [SerializeField] private float soundMaxDistance = 25f;

    [Networked] public Vector3 SpawnPoint { get; set; }      // same deferred-spawn safeguard as Safe/SafeNote/WorldItem - a deferred spawn silently drops the position argument
    [Networked] public NetworkBool UseSpawnPoint { get; set; }
    [Networked] private NetworkBool tripped { get; set; }    // stops a second player triggering it in the tick before it despawns

    private float armTimer;
    private AudioSource trapAudio;

    public float DisarmRange => disarmRange; // the player's interaction scan reads this

    public override void Spawned()
    {
        AllTraps.Add(this);
        armTimer = armDelay;

        //built in code so the prefab needs nothing wired but the clips. 3D so you can hear WHICH room it went off in -
        //that's the information the sound is actually carrying.
        trapAudio = gameObject.AddComponent<AudioSource>();
        trapAudio.playOnAwake = false;
        trapAudio.loop = false;
        trapAudio.spatialBlend = 1f;
        trapAudio.rolloffMode = AudioRolloffMode.Linear;
        trapAudio.minDistance = 2f;
        trapAudio.maxDistance = soundMaxDistance;

        if (UseSpawnPoint)
        {
            StartCoroutine(PlaceOnceSpawnPointArrives());
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        AllTraps.Remove(this);
    }

    private IEnumerator PlaceOnceSpawnPointArrives()
    {
        //(0,0,0) doubles as the "hasn't replicated yet" sentinel, so cap the wait - a trap that genuinely belonged at
        //the world origin would otherwise spin here forever, invisible as a bug.
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
        if (!HasStateAuthority || tripped)
        {
            return; //only the master decides it went off, and it only ever goes off once
        }

        if (armTimer > 0f)
        {
            armTimer -= Runner.DeltaTime;
            return;
        }

        foreach (Player player in Player.ActivePlayers)
        {
            if (player.IsEliminated || player.IsLockedUp || player.IsHiding)
            {
                continue; //out of play, or tucked inside a wardrobe with their feet off the floor
            }
            if (Vector3.Distance(transform.position, player.transform.position) > triggerRadius)
            {
                continue;
            }

            tripped = true;
            RPC_Snap();                                              //everyone hears it, wherever they are in the house
            if (GuardPatrol.Instance != null)
            {
                GuardPatrol.Instance.AlertTo(transform.position);    //and he comes straight back to the spot he set it
            }
            StartCoroutine(DespawnAfterSound());
            return;
        }
    }

    private IEnumerator DespawnAfterSound() //let the snap actually play before the object carrying the AudioSource disappears
    {
        yield return new WaitForSeconds(0.6f);
        if (HasStateAuthority && Object != null && Object.IsValid)
        {
            Runner.Despawn(Object);
        }
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_Snap()
    {
        PlayClip(snapClip);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_Disarm() //a player defused it. the authority owns the despawn, same as every other world object
    {
        if (tripped)
        {
            return; //already gone off - too late to be clever
        }
        tripped = true;
        RPC_Disarmed();
        StartCoroutine(DespawnAfterSound());
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_Disarmed()
    {
        PlayClip(disarmClip != null ? disarmClip : snapClip);
    }

    private void PlayClip(AudioClip clip)
    {
        if (clip == null || trapAudio == null)
        {
            return;
        }
        trapAudio.PlayOneShot(clip, volume);
    }

    public static GuardTrap FindDisarmableNear(Vector3 position) //nearest armed trap this player could defuse, or null
    {
        GuardTrap nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (GuardTrap trap in AllTraps)
        {
            if (trap.tripped) continue;
            float distance = Vector3.Distance(trap.transform.position, position);
            if (distance <= trap.disarmRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = trap;
            }
        }
        return nearest;
    }
}
