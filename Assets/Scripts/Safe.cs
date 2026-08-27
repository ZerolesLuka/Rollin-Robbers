using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

// A crackable safe - the heist centrepiece. You don't tap it open: a player HOLDS interact next to it and a shared
// meter (CrackProgress) fills over crackSeconds, then it pops and spits its loot onto the floor. The whole point is
// tension - you're pinned in the open next to it while it fills, exposed to the guard's line of sight. Everything
// meaningful is [Networked] so the meter agrees on every screen, TWO players can tag-team one safe, and walking
// away just PAUSES it (progress persists) instead of resetting. Scene-placed NetworkObjects don't enrol reliably
// in Fusion Shared Mode, so safes are runtime-spawned by SafeSpawner, same as loot.
[RequireComponent(typeof(NetworkObject))]
public class Safe : NetworkBehaviour
{
    public const int NoSafe = 0; // Player.CrackingSafeId when a player isn't cracking anything. real ids start at 1 so a fresh player's default 0 never matches a real safe

    public static readonly List<Safe> AllSafes = new List<Safe>();

    [SerializeField] private float crackSeconds = 5f;  // how long a single cracker takes to open it. two crackers don't stack - it's presence, not headcount
    [SerializeField] private float crackRange = 2.2f;  // how close a player must stay to keep the meter moving
    [SerializeField] private SwingingHinge doorHinge;  // the safe's door. leave EMPTY and it finds a SwingingHinge on any child by itself - just add that component to the door mesh. only assign this by hand if the safe has more than one hinged part

    //THE LOOT IS ALREADY IN THERE. It spawns inside the safe the moment the safe does, sat behind a shut door -
    //cracking doesn't CREATE anything, it just reveals what was always there. That matters for two reasons: the
    //contents count toward the house total from the start (so an uncracked safe is the difference between a good run
    //and a perfect one, rather than quietly not existing), and the payoff reads as opening a box rather than as items
    //materialising out of nothing.
    [Header("What's inside")]
    [SerializeField] private NetworkObject worldItemPrefab; // the SAME WorldItem prefab everything else uses
    [SerializeField] private Transform lootAnchor;          // where the contents sit. leave empty and they stack just above the safe's own origin
    [SerializeField] private string lootName = "Jewellery";
    [SerializeField] private int lootValueMin = 1500;
    [SerializeField] private int lootValueMax = 4000;
    [SerializeField] private int lootItemCount = 3;
    [SerializeField] private float lootScatter = 0.12f;     // tiny - they're in a box, not thrown across a room

    [Networked] public int SafeId { get; set; }                 // unique per safe, assigned by the spawner. Player.CrackingSafeId points at this
    [Networked] public int Code { get; set; }                   // 4-digit combination, rolled by the spawner. networked so the note, the keypad and the safe all agree on one number
    [Networked] public float CrackProgress { get; private set; } // 0..1 - drive a progress bar off this
    [Networked] public NetworkBool IsOpen { get; private set; }
    [Networked] public Vector3 SpawnPoint { get; set; }          // same deferred-spawn safeguard as WorldItem - a deferred spawn drops the position arg
    [Networked] public NetworkBool UseSpawnPoint { get; set; }

    public float CrackRange => crackRange; // the player's hold-to-crack check reads this

    public static Safe FindById(int safeId) // the HUD turns a player's networked CrackingSafeId back into the safe itself, to read its meter
    {
        if (safeId == NoSafe) return null;
        foreach (Safe safe in AllSafes)
        {
            if (safe.SafeId == safeId) return safe;
        }
        return null;
    }

    public override void Spawned()
    {
        AllSafes.Add(this);

        //the inspector's object picker only lists things that ALREADY have a SwingingHinge, which makes wiring this
        //by hand a chicken-and-egg annoyance. so: if it's empty, just go find one on a child. drop SwingingHinge on
        //the door mesh and the safe sorts itself out.
        if (doorHinge == null)
        {
            doorHinge = GetComponentInChildren<SwingingHinge>();
        }
        if (doorHinge != null)
        {
            //THE SAFE OWNS THIS DOOR. Without this the hinge is just another openable in SwingingHinge.AllHinges, so
            //E at a safe swung it open like a kitchen cupboard - no code, no note, no meter, the entire mechanic
            //walked past. Only Open() moves it now.
            doorHinge.PlayerOperable = false;
        }
        if (UseSpawnPoint)
        {
            StartCoroutine(PlaceOnceSpawnPointArrives()); //stocks the contents once it lands - see below for why it can't happen here
        }
        else if (HasStateAuthority)
        {
            StockContents(); //already where it belongs, so fill it now
        }
        ApplyVisual();
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        AllSafes.Remove(this);
    }

    private IEnumerator PlaceOnceSpawnPointArrives()
    {
        //the deferred spawn replicates SpawnPoint a tick or two after Spawned; until it lands it reads (0,0,0).
        //a safe is static furniture (no rigidbody), so unlike WorldItem there's nothing to freeze - just wait, then
        //snap. capped like WorldItem's: (0,0,0) doubles as the "not arrived" sentinel, so a safe genuinely placed at
        //the world origin would otherwise spin here forever.
        int framesWaited = 0;
        while (SpawnPoint == Vector3.zero && framesWaited < 120)
        {
            framesWaited++;
            yield return null;
        }
        transform.position = SpawnPoint;

        //ONLY NOW. the contents are placed relative to this safe, and on a deferred spawn the safe is still sat at the
        //origin until the line above runs - stocking any earlier would leave a pile of jewellery in the middle of the
        //world with a safe somewhere else entirely.
        if (HasStateAuthority)
        {
            StockContents();
        }
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority || IsOpen)
        {
            return; //only the authority advances the meter, and an open safe is finished
        }

        float speedMultiplier = BestCrackMultiplier();
        if (speedMultiplier > 0f)
        {
            //multiplier scales the TIME, so a Crowbar's 0.6 means the meter fills in 60% of crackSeconds
            CrackProgress = Mathf.Min(1f, CrackProgress + Runner.DeltaTime / (crackSeconds * speedMultiplier));
            if (CrackProgress >= 1f)
            {
                Open();
            }
        }
    }

    //0 if nobody's working on it, otherwise the BEST multiplier among everyone who is. Two crackers still don't stack
    //- it's presence, not headcount - but the one with the crowbar sets the pace for the pair, so handing the tool to
    //whoever's going to the safe actually matters.
    private float BestCrackMultiplier()
    {
        float best = 0f;
        foreach (Player player in Player.ActivePlayers)
        {
            if (player.CrackingSafeId != SafeId)
            {
                continue; //not working on this safe
            }
            if (player.IsEliminated || player.IsLockedUp || player.IsHiding)
            {
                continue; //out of action - can't be cracking
            }
            if (Vector3.Distance(player.transform.position, transform.position) > crackRange)
            {
                continue;
            }

            float mine = player.SafeCrackMultiplier;
            if (best == 0f || mine < best) best = mine; //lower is faster
        }
        return best;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_TryCode(int attempt, PlayerRef sender) //keypad entry - the RIGHT number skips the whole crack instantly and silently
    {
        if (IsOpen) return;

        if (attempt == Code)
        {
            CrackProgress = 1f; //jump the meter so anything reading it (a future progress bar) shows a completed safe
            Open();
            return;
        }

        //wrong code just fails. no penalty beyond the time you wasted standing there in the open, which in a stealth
        //game is penalty enough - and it keeps brute-forcing the keypad strictly worse than brute-forcing the dial.
        RPC_CodeRejected(sender);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_CodeRejected(PlayerRef sender) //tell just the person who typed it that it was wrong
    {
        if (Player.LocalPlayer != null && Player.LocalPlayer.Object != null && Player.LocalPlayer.Object.InputAuthority == sender)
        {
            Player.LocalPlayer.OnSafeCodeRejected();
        }
    }

    private void Open() //runs on the state authority only (gated in FixedUpdateNetwork and RPC_TryCode)
    {
        IsOpen = true; //networked - every client's Render swings their own door off the back of this

        //NOTHING IS CREATED HERE. The contents were spawned with the safe and have been sat behind the door the whole
        //time; opening it just stops them being locked, so they can be seen and picked up like any other loot.
        foreach (WorldItem item in WorldItem.AllItems)
        {
            if (item.LockedInSafe && item.InSafeId == SafeId)
            {
                item.LockedInSafe = false;
            }
        }
    }

    //Fill the safe the moment it exists. Master only, same as every other spawner, and the total goes into
    //HouseLootTotal right away - so an uncracked safe is the gap between a good run and a perfect one rather than
    //loot that quietly never existed.
    private void StockContents()
    {
        if (worldItemPrefab == null || lootItemCount <= 0) return;

        Vector3 anchor = lootAnchor != null ? lootAnchor.position : transform.position + Vector3.up * 0.5f;
        int totalValue = 0;

        for (int i = 0; i < lootItemCount; i++)
        {
            int value = Random.Range(lootValueMin, lootValueMax + 1); //declared in the loop so each closure captures its own
            totalValue += value;

            Vector3 spawnAt = anchor + Random.insideUnitSphere * lootScatter; //barely scattered - they're in a box, not strewn across a room
            int mySafeId = SafeId;
            Runner.Spawn(worldItemPrefab, spawnAt, Random.rotation, PlayerRef.None, (runner, spawnedObject) =>
            {
                WorldItem item = spawnedObject.GetComponent<WorldItem>();
                if (item == null) return;
                item.ItemName = lootName;
                item.Value = value;
                item.LockedInSafe = true;  //behind a shut door until this safe opens
                item.InSafeId = mySafeId;
                item.SpawnPoint = spawnAt; //networked-position safeguard - a deferred spawn drops the position argument
                item.UseSpawnPoint = true;
            });
        }

        if (RunManager.Instance != null)
        {
            RunManager.Instance.ReportHouseLoot(totalValue); //counts from the start, cracked or not
        }
    }

    public override void Render()
    {
        ApplyVisual(); //keep the door matched to the networked IsOpen on every client, even as it replicates in
    }

    private void ApplyVisual()
    {
        if (doorHinge != null)
        {
            doorHinge.SetOpen(IsOpen); //SwingingHinge ignores a repeat of the state it's already in, so calling this every frame is free
        }
    }
}
