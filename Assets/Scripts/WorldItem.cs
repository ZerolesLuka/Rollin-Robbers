using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

// A physical pickup item. Pick up with E (into your inventory), drop with G (spawns one at your feet that falls).
// One generic prefab for now - the name string is what distinguishes items. Networked so pickups/drops sync to
// everyone: give the prefab a NetworkObject + NetworkRigidbody3D + Collider so a dropped one falls and replicates.
public class WorldItem : NetworkBehaviour
{
    public static readonly List<WorldItem> AllItems = new List<WorldItem>();

    [SerializeField] private string startingName = "Item"; // for items placed in the scene; the spawner/drop set these on spawn instead
    [SerializeField] private int startingValue = 100;       // fallback value; the spawner/drop set the real value on spawn
    [SerializeField] private Light glowLight;               // optional child light - tinted by rarity so pricey loot glows. leave empty for no glow
    [Networked] public NetworkString<_32> ItemName { get; set; }
    [Networked] public int Value { get; set; }              // what it sells for at the pawn shop
    [Networked] private NetworkBool claimed { get; set; }   // stops two players grabbing the same item on the same tick
    [Networked] public Vector3 SpawnPoint { get; set; }       // where this item should be. sent as networked data because a deferred spawn (prefab still loading) silently drops the position argument and dumps the item at origin
    [Networked] public NetworkBool UseSpawnPoint { get; set; } // true = runtime-spawned loot, re-apply SpawnPoint in Spawned. false = an item placed directly in the scene, which keeps its own transform
    [Networked] public NetworkBool CountedAsStolen { get; set; } // true once this item's value has been added to RunManager.GatheredLootValue. a G-dropped item spawns with this ALREADY true, so re-picking it can't count the same loot toward the house twice (which used to push clear-% over 100% and poison BestClearPercent)

    [Networked] public NetworkBool IsBait { get; set; } // a plant. looks worth taking, pays nothing, and screams when you lift it

    //Loot that lives inside a shut safe. It EXISTS from the moment the safe does - the door is just in the way - so
    //without this you could stand next to a locked safe and pull its contents straight through the door, since pickup
    //is a proximity check and doesn't care about geometry. Cleared by Safe.Open when the door actually swings.
    [Networked] public NetworkBool LockedInSafe { get; set; }
    [Networked] public int InSafeId { get; set; } // which safe is holding it, so that safe knows what to release

    [Header("Bait")]
    [SerializeField] private AudioClip baitAlarmClip;          // what it does when you fall for it. 3D, so the guard isn't the only one who learns where you are
    [SerializeField, Range(0f, 1f)] private float baitAlarmVolume = 0.9f;
    [SerializeField] private float baitFlickerSpeed = 4.5f;    // THE TELL. bait breathes; real loot sits dead still. subtle enough to miss when you're panicking, obvious once you know
    [SerializeField] private float baitFlickerAmount = 0.18f;  // how deep that breath is, as a fraction of the light's authored intensity. bigger = easier to spot = kinder
    private float glowBaseIntensity;                           // whatever the light was authored at, so the flicker is relative rather than absolute
    private bool glowAuthoredEnabled;                          // whether the prefab wanted a glow at all - a safe hides it, and this is what it's restored to
    private AudioSource baitAudio;

    [HideInInspector] public bool pendingRemoval; // set locally the instant we grab it, so our own pickup scan can't re-grab it during the despawn lag

    public override void Spawned()
    {
        AllItems.Add(this);

        if (glowLight != null)
        {
            glowBaseIntensity = glowLight.intensity;      //remember the authored brightness so the bait flicker rides on top of it instead of replacing it
            glowAuthoredEnabled = glowLight.enabled;      //and whether it was meant to be lit at all, so hiding it in a safe can be undone without inventing a glow
        }

        //3D on purpose: a bait going off across the house should be a distant "oh no", one in your hands should be a
        //jolt. built in code so a bait prefab needs nothing wired but the clip.
        baitAudio = gameObject.AddComponent<AudioSource>();
        baitAudio.playOnAwake = false;
        baitAudio.loop = false;
        baitAudio.spatialBlend = 1f;
        baitAudio.rolloffMode = AudioRolloffMode.Linear;
        baitAudio.minDistance = 2f;
        baitAudio.maxDistance = 30f;
        AudioOcclusion.Attach(baitAudio); //so the crew can tell which room someone just got greedy in

        //DON'T place the item here. On a deferred spawn (prefab still loading) the networked SpawnPoint hasn't
        //replicated yet - it reads (0,0,0) in Spawned - and a dynamic rigidbody dropped at origin, overlapping the
        //other loot, explodes before the real position ever arrives. So FREEZE it, and let a coroutine place it a
        //tick later once SpawnPoint has actually arrived. Same "delay the tick so network + physics stop arguing
        //over where the object goes" fix the player scene-transition uses.
        if (UseSpawnPoint)
        {
            Rigidbody body = GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = true; //frozen: no gravity, no collision blast, while we wait for the real position
            }
            StartCoroutine(PlaceOnceSpawnPointArrives());
        }

        if (HasStateAuthority && string.IsNullOrEmpty(ItemName.ToString())) // a scene item the spawner/drop didn't name
        {
            ItemName = startingName;
            Value = startingValue;
        }
    }

    private IEnumerator PlaceOnceSpawnPointArrives()
    {
        //the deferred spawn replicates SpawnPoint a tick or two after Spawned; until it lands it reads as zero.
        //the frame cap matters: (0,0,0) is being used as a "hasn't arrived yet" sentinel, so an item that genuinely
        //belongs at the world origin would wait here forever - frozen kinematic and stuck at the origin, invisible
        //as a bug. after the cap we just go with whatever we have rather than hanging.
        int framesWaited = 0;
        while (SpawnPoint == Vector3.zero && framesWaited < 120)
        {
            framesWaited++;
            yield return null;
        }

        transform.position = SpawnPoint;

        Rigidbody body = GetComponent<Rigidbody>();
        if (body != null)
        {
            body.isKinematic = false;   //placed correctly now - release it so it falls and settles like a real object
            body.position = SpawnPoint;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }

    public override void Render() //every frame on all clients - keeps the glow matched to the networked Value even as it replicates in after spawn
    {
        if (glowLight == null)
        {
            return;
        }

        //a glow leaking out of a shut safe would give away exactly what's inside before you've earned it. restore the
        //AUTHORED state rather than forcing it on, or a prefab whose light was deliberately switched off in the
        //inspector would light up the moment this ran - which is what the first version of this did.
        bool wantLit = glowAuthoredEnabled && !LockedInSafe;
        if (glowLight.enabled != wantLit)
        {
            glowLight.enabled = wantLit;
        }
        if (!wantLit)
        {
            return;
        }

        glowLight.color = LootRarityTable.ColorFor(Value);

        //bait deliberately wears the colour of whatever it's pretending to be worth - the value alone must never give
        //it away, or nobody would ever fall for it. the flicker is the only tell, and it's a learnable one: real loot
        //glows steady, a plant breathes. players who slow down and look get to keep their run.
        if (IsBait)
        {
            glowLight.intensity = glowBaseIntensity * (1f + Mathf.Sin(Time.time * baitFlickerSpeed) * baitFlickerAmount);
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        AllItems.Remove(this);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_RequestPickUp(PlayerRef requester) // routed to whoever owns this item (scene master, or the player who dropped it)
    {
        //ONE machine decides who gets this item. Previously each client added the item to its own inventory and
        //reported the theft itself, so two players grabbing the same vase on the same tick BOTH kept it and BOTH
        //reported it - the house read as over-looted and the money was duplicated when they each sold it. `claimed`
        //only ever guarded the despawn, not the payout. Now the loser's request simply arrives second and is dropped.
        if (claimed) return;
        claimed = true;

        //A PLANT. it never entered HouseLootTotal (the guard spawned it, not ItemSpawner), so it must never enter
        //GatheredLootValue either - reporting it would push clear-% above 100% off an item that was never real.
        //nobody gets paid, everybody hears about it, and he comes to the exact spot your hand was.
        if (IsBait)
        {
            RPC_BaitSprung();
            if (GuardPatrol.Instance != null)
            {
                GuardPatrol.Instance.AlertTo(transform.position); //dog too - this is a shout, not a whisper
            }
            StartCoroutine(DespawnAfterAlarm()); //hold on a moment so the alarm isn't cut off by our own despawn
            return;
        }

        //count the theft here too, on the same single machine, so it can't be double-counted either. dropped loot
        //arrives with CountedAsStolen already true, so re-picking a teammate's drop still isn't a fresh theft.
        if (!CountedAsStolen && RunManager.Instance != null)
        {
            RunManager.Instance.RPC_ReportLootTaken(Value, transform.position);
        }

        //hand it to exactly one player - the winner puts it in their bag when this lands on their machine
        foreach (Player player in Player.ActivePlayers)
        {
            if (player != null && player.Object != null && player.Object.InputAuthority == requester)
            {
                player.RPC_GrantPickup(ItemName, Value);
                break;
            }
        }

        Runner.Despawn(Object);
    }

    private IEnumerator DespawnAfterAlarm() //let the alarm actually play before the object carrying the AudioSource disappears
    {
        yield return new WaitForSeconds(0.8f);
        if (HasStateAuthority && Object != null && Object.IsValid)
        {
            Runner.Despawn(Object);
        }
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_BaitSprung() //every client plays it from the item's own position, so the whole crew hears WHERE someone just got greedy
    {
        if (baitAlarmClip != null && baitAudio != null)
        {
            baitAudio.PlayOneShot(baitAlarmClip, baitAlarmVolume);
        }
    }
}
