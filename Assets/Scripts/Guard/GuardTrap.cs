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

    [Header("Tripwire visual - assign on the wire prefab only")]
    //Dragged in rather than found by name: a transform.Find("PostA") breaks silently the day someone renames a child,
    //and the failure looks like a physics bug rather than a typo. Left empty on floor traps, which have nothing to lay
    //out - LayOutWire simply returns.
    [SerializeField] private Transform wirePostA;
    [SerializeField] private Transform wirePostB;
    [SerializeField] private Transform wireBar;        //the bit that stretches. its local +Z is treated as its length
    [SerializeField] private float barLengthAtScaleOne = 1f; //how long the bar mesh is with scale 1. a default Unity Cube is exactly 1

    [Header("Tripwire tangle")]
    [SerializeField] private float tangleSeconds = 3.5f;        //how long a wire hobbles you. deliberately NOT a full stop - see RPC_TangledInTripwire on Player
    [SerializeField] private float wireTriggerRadius = 0.75f;   //how close to the LINE, measured HORIZONTALLY, counts as walking into it
    [SerializeField] private float wireHeightTolerance = 1.6f;  //how far above/below the wire still counts. generous on purpose - see PlayerIsOnTheTrap

    [Networked] public Vector3 SpawnPoint { get; set; }      //same deferred-spawn safeguard as Safe/SafeNote/WorldItem - a deferred spawn silently drops the position argument
    [Networked] public NetworkBool UseSpawnPoint { get; set; }
    [Networked] private NetworkBool sprung { get; set; }     //stops a second player setting it off in the tick before it despawns

    //A wire is a LINE, not a dot. Both ends are networked because detection runs on the authority against replicated
    //positions, and every client needs them to draw the thing in the right place. Zero-length means "not a span" and
    //the trap falls back to the plain radius check, so a wire prefab dropped by anything other than the span-planting
    //path still behaves like it always did rather than becoming untriggerable.
    [Networked] public Vector3 WireEndA { get; set; }
    [Networked] public Vector3 WireEndB { get; set; }

    private bool IsStrungWire => kind == TrapKind.Tripwire && (WireEndB - WireEndA).sqrMagnitude > 0.0625f; //0.25m apart or it's a dot

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
        else
        {
            LayOutWire(); //nothing to wait for - whatever the ends are now is what they'll be
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
        LayOutWire(); //WireEndA/B ride the same replication as SpawnPoint, so by here they've landed too
    }

    //Move the posts onto the two anchors and stretch the bar between them, so the mesh matches the line detection is
    //already using. Runs on EVERY client, because this is purely cosmetic - the authority's maths reads WireEndA/B
    //directly and doesn't care where the mesh ended up.
    //
    //The posts are MOVED but never scaled, and only the bar is stretched. Scaling the whole prefab to cover the gap
    //would fatten the posts as the doorway got wider, which is the obvious approach and looks wrong immediately.
    private void LayOutWire()
    {
        if (!IsStrungWire) return;  //floor trap, or a wire spawned without endpoints - leave the mesh exactly as authored
        if (wirePostA == null || wirePostB == null || wireBar == null) return; //prefab isn't wired up; the trap still FUNCTIONS, it just looks like whatever it looks like

        wirePostA.position = WireEndA;
        wirePostB.position = WireEndB;

        Vector3 alongWire = WireEndB - WireEndA;
        float span = alongWire.magnitude;
        if (span < 0.01f || barLengthAtScaleOne <= 0f) return; //degenerate span or a mis-set mesh length - stretching by this would divide by ~zero

        wireBar.position = (WireEndA + WireEndB) * 0.5f;
        wireBar.rotation = Quaternion.LookRotation(alongWire / span, Vector3.up); //local +Z now runs along the wire, which is the axis we scale

        Vector3 barScale = wireBar.localScale;
        barScale.z = span / barLengthAtScaleOne;
        wireBar.localScale = barScale;
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
            if (!PlayerIsOnTheTrap(player.transform.position))
            {
                continue;
            }

            Spring(player);
            return;
        }
    }

    //Did this player just walk into the trap?
    //
    //Deliberately maths against replicated positions rather than a collider, for the same reason the rest of this
    //class does: trigger events never fire for remote players, because they're moved by NetworkTransform rather than
    //CharacterController.Move. A collider stretched along the wire works perfectly solo and then ignores everyone else.
    //
    //THE HEIGHT TRAP, which cost a whole debugging session: a full 3D distance from the wire to the player's
    //transform.position never fires. That origin sits at the player's FEET while their body is two metres tall, and
    //the wire sits at whatever height the level author put the anchors. Comparing those two points directly measures
    //a gap that has nothing to do with whether anyone walked through anything. So: HORIZONTAL distance answers "did
    //they cross the line", and a generous vertical band answers "were they on this floor at all".
    private bool PlayerIsOnTheTrap(Vector3 playerPosition)
    {
        if (!IsStrungWire)
        {
            return Vector3.Distance(transform.position, playerPosition) <= triggerRadius;
        }

        Vector3 flatWire = WireEndB - WireEndA;
        flatWire.y = 0f;
        Vector3 flatToPlayer = playerPosition - WireEndA;
        flatToPlayer.y = 0f;

        //closest point along the span, clamped to the ends so walking PAST a wire doesn't trip it
        float alongWire = flatWire.sqrMagnitude > 0.0001f
            ? Mathf.Clamp01(Vector3.Dot(flatToPlayer, flatWire) / flatWire.sqrMagnitude)
            : 0f;
        float horizontalDistance = Vector3.Distance(flatToPlayer, flatWire * alongWire);
        if (horizontalDistance > wireTriggerRadius)
        {
            return false;
        }

        //same floor check. wide enough that it doesn't care whether the player's origin is their feet or their middle,
        //tight enough that someone directly above or below on another storey doesn't trip it.
        float wireHeightHere = Mathf.Lerp(WireEndA.y, WireEndB.y, alongWire);
        return Mathf.Abs(playerPosition.y - wireHeightHere) <= wireHeightTolerance;
    }

    private void Spring(Player victim)
    {
        sprung = true;
        RPC_Spring(); //everyone hears it, wherever they are in the house

        //a tripwire is a snitch, not a threat - it wakes HIM and nobody else. the alarm is the loud one and drags the
        //dog in too, which is what makes it worth the guard's remaining trap slots.
        //
        //both are pinged DIRECTLY and separately, never guard-tells-dog. the two AIs were deliberately decoupled so
        //they hunt independently, and routing this through the guard would quietly put that back - one noise would
        //once again summon the whole house. a trap going off is a world event they both happen to hear.
        bool pullTheDogIn = kind != TrapKind.Tripwire;
        if (GuardPatrol.Instance != null)
        {
            GuardPatrol.Instance.AlertTo(transform.position);
        }
        if (pullTheDogIn && DogAI.Instance != null)
        {
            DogAI.Instance.AlertTo(transform.position);
        }

        if (kind == TrapKind.BearTrap)
        {
            victim.RPC_CaughtInBearTrap(); //pinned where they stand, loud about it, and stuck until a teammate arrives
        }
        else if (kind == TrapKind.Tripwire)
        {
            //A HOBBLE, NOT A HOLD. The bear trap already owns "you are stuck and need your crew" - if the wire also
            //froze you the two would be the same trap with different meshes, and the bear trap would stop being the
            //scary one. More importantly, standing still while a guard walks at you isn't tension, it's watching
            //yourself lose: the player has nothing left to do. Slowed, you can still stagger for the stairs and
            //sometimes make it, so when you don't you lost it rather than had it taken.
            victim.RPC_TangledInTripwire(tangleSeconds);
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
            GuardPatrol.Instance.AlertTo(transform.position); //him only - AlertTo never passes anything to the dog, so a snipped wire stays between you and the guard
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
