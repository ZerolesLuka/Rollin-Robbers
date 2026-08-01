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

    [SerializeField] private float crackSeconds = 8f;  // how long a single cracker takes to open it. two crackers don't stack - it's presence, not headcount
    [SerializeField] private float crackRange = 2.2f;  // how close a player must stay to keep the meter moving
    [SerializeField] private SwingingHinge doorHinge;  // the safe's door. leave EMPTY and it finds a SwingingHinge on any child by itself - just add that component to the door mesh. only assign this by hand if the safe has more than one hinged part

    //LOOT IS DEFERRED ON PURPOSE. A safe spitting its own items meant two systems spawning loot, with two sets of
    //value/rarity settings to keep in step. It'll go through ItemSpawner like everything else instead - the safe just
    //opens, and the spawner decides what's inside. Keeping the fields here (unused) so the wiring is obvious later.
    // [Header("Loot it spits out when cracked")]
    // [SerializeField] private NetworkObject worldItemPrefab;
    // [SerializeField] private string lootName = "Jewellery";
    // [SerializeField] private int lootValueMin = 1500;
    // [SerializeField] private int lootValueMax = 4000;
    // [SerializeField] private int lootItemCount = 3;

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
        if (UseSpawnPoint)
        {
            StartCoroutine(PlaceOnceSpawnPointArrives());
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
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority || IsOpen)
        {
            return; //only the authority advances the meter, and an open safe is finished
        }

        if (AnyoneCracking())
        {
            CrackProgress = Mathf.Min(1f, CrackProgress + Runner.DeltaTime / crackSeconds);
            if (CrackProgress >= 1f)
            {
                Open();
            }
        }
    }

    private bool AnyoneCracking() //is at least one live player holding interact on THIS safe, in range
    {
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
            if (Vector3.Distance(player.transform.position, transform.position) <= crackRange)
            {
                return true;
            }
        }
        return false;
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

    private void Open() //runs on the state authority only (gated in FixedUpdateNetwork)
    {
        IsOpen = true; //networked - every client's Render swings their own door off the back of this

        //loot deliberately not spawned here. it'll come from ItemSpawner so there's ONE loot pipeline instead of two
        //sets of value settings drifting apart. when that lands, tell the spawner the safe opened and let it decide
        //what's inside - and remember to ReportHouseLoot the total, or the safe's contents push clear-% over 100%.
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
