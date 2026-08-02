using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

// Something nasty the guard leaves behind after he loses sight of you. He doesn't wire the exact spot he searched -
// he remembers roughly WHERE he last saw someone and seeds that area, so the parts of the house you keep working
// slowly turn hostile without him ever being clairvoyant about it.
//
// Three kinds, and the kind is baked into the PREFAB rather than networked - every client loads the same prefab, so
// they already agree on what it is with nothing sent over the wire. Give each kind its own prefab, mesh and clip:
//   Tripwire       - strung across a doorway. Alerts him, nothing else. Cheap and everywhere.
//   BearTrap       - on open floor. PINS you where you stand and you make a racket doing it. The genuinely scary one.
//   ProximityAlarm - wide radius, no physical grab, but it screams and brings the dog too.
//
// Runtime-spawned, never scene-placed: Fusion Shared Mode doesn't enrol scene NetworkObjects reliably, same reason
// Safe and the loot are spawned. Detection reads replicated positions on the authority rather than using a trigger
// collider, because trigger events never fire for remote players (they move by NetworkTransform, not
// CharacterController.Move) - the same trick SqueakyToy uses.
[RequireComponent(typeof(NetworkObject))]
public class GuardTrap : NetworkBehaviour
{
    public enum TrapKind { Tripwire, BearTrap, ProximityAlarm }

    public static readonly List<GuardTrap> AllTraps = new List<GuardTrap>();

    [SerializeField] private TrapKind kind = TrapKind.Tripwire; //set per PREFAB. not networked - the prefab is identical on every machine, so they already agree
    [SerializeField] private float triggerRadius = 1.2f;        //how close you have to step. keep it tight for a tripwire, wide for an alarm
    [SerializeField] private float disarmRange = 2f;            //reach to defuse it with E. should be comfortably bigger than triggerRadius or you'd have to stand ON it to disarm it
    [SerializeField] private float armDelay = 1.5f;             //he's stood right next to it as he sets it, and so might you be. don't let it fire the instant it lands

    //NO hold duration here any more. A bear trap holds you until a TEAMMATE pries it off - the only timer left is
    //Player.bearTrapSelfEscapeSeconds, which is a failsafe against being stranded alone rather than the way out. A
    //field here would just be a dial that silently does nothing.

    [Header("Sound")]
    [SerializeField] private AudioClip springClip;              //it going off. 3D, so the room it happened in is the information
    [SerializeField] private AudioClip disarmClip;              //optional - defusing it
    [SerializeField, Range(0f, 1f)] private float volume = 0.85f;
    [SerializeField] private float soundMaxDistance = 25f;

    [Networked] public Vector3 SpawnPoint { get; set; }      //same deferred-spawn safeguard as Safe/SafeNote/WorldItem - a deferred spawn silently drops the position argument
    [Networked] public NetworkBool UseSpawnPoint { get; set; }
    [Networked] private NetworkBool sprung { get; set; }     //stops a second player setting it off in the tick before it despawns

    private float armTimer;
    private AudioSource trapAudio;

    public TrapKind Kind => kind;              //the guard reads this off a prefab to decide which one suits a spot
    public float DisarmRange => disarmRange;   //the player's interaction scan reads this

    public override void Spawned()
    {
        AllTraps.Add(this);
        armTimer = armDelay;

        //built in code so a trap prefab needs nothing wired but its clips. 3D on purpose - hearing WHICH room it went
        //off in is the whole point of the sound.
        trapAudio = gameObject.AddComponent<AudioSource>();
        trapAudio.playOnAwake = false;
        trapAudio.loop = false;
        trapAudio.spatialBlend = 1f;
        trapAudio.rolloffMode = AudioRolloffMode.Linear;
        trapAudio.minDistance = 2f;
        trapAudio.maxDistance = soundMaxDistance;
        AudioOcclusion.Attach(trapAudio); //hearing WHICH room it went off in is the whole point of the sound, and that only works if walls dull it

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
        if (!HasStateAuthority || sprung)
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

            Spring(player);
            return;
        }
    }

    private void Spring(Player victim)
    {
        sprung = true;
        RPC_Spring(); //everyone hears it, wherever they are in the house

        //a tripwire is a snitch, not a threat - it wakes HIM and nobody else. the alarm is the loud one and drags the
        //dog in too, which is what makes it worth the guard's remaining trap slots.
        bool pullTheDogIn = kind != TrapKind.Tripwire;
        if (GuardPatrol.Instance != null)
        {
            GuardPatrol.Instance.AlertTo(transform.position, pullTheDogIn);
        }

        if (kind == TrapKind.BearTrap)
        {
            victim.RPC_CaughtInBearTrap(); //pinned where they stand, loud about it, and stuck until a teammate arrives
        }

        StartCoroutine(DespawnAfterSound());
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
    private void RPC_Spring()
    {
        PlayClip(springClip);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_Disarm(NetworkBool quietly) //a player defused it. the authority owns the despawn, same as every other world object
    {
        if (sprung)
        {
            return; //already went off - too late to be clever
        }
        sprung = true;
        RPC_Disarmed();

        //WITHOUT wire cutters you can still disarm it, you just make a mess of it - the clatter carries and he comes
        //to look. That's what the tool buys: not the ability, the silence. Otherwise Wire Cutters would be a gate on
        //content rather than an upgrade, and a crew without them would simply have to walk around every trap.
        if (!quietly && GuardPatrol.Instance != null)
        {
            GuardPatrol.Instance.AlertTo(transform.position, false); //him only. the dog doesn't need to hear about a snipped wire
        }

        StartCoroutine(DespawnAfterSound());
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_Disarmed()
    {
        PlayClip(disarmClip != null ? disarmClip : springClip);
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
            if (trap.sprung) continue;
            float distance = Vector3.Distance(trap.transform.position, position);
            if (distance <= trap.disarmRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = trap;
            }
        }
        return nearest;
    }

    public static bool AnyTrapNear(Vector3 position, float range) //so he doesn't pile a second trap on top of one he already set
    {
        foreach (GuardTrap trap in AllTraps)
        {
            if (Vector3.Distance(trap.transform.position, position) <= range) return true;
        }
        return false;
    }

    public string DisplayName //what the interaction prompt calls it
    {
        get
        {
            switch (kind)
            {
                case TrapKind.BearTrap: return "bear trap";
                case TrapKind.ProximityAlarm: return "alarm";
                default: return "tripwire";
            }
        }
    }
}
