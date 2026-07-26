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
    [SerializeField] private GameObject openVisual;    // optional: shown when open (a swung door). leave empty for none
    [SerializeField] private GameObject closedVisual;  // optional: hidden when open (the closed door)

    [Header("Loot it spits out when cracked")]
    [SerializeField] private NetworkObject worldItemPrefab; // the SAME WorldItem prefab the loot system uses
    [SerializeField] private string lootName = "Cash";
    [SerializeField] private int lootValueMin = 1500;
    [SerializeField] private int lootValueMax = 4000;
    [SerializeField] private int lootItemCount = 3;

    [Networked] public int SafeId { get; set; }                 // unique per safe, assigned by the spawner. Player.CrackingSafeId points at this
    [Networked] public float CrackProgress { get; private set; } // 0..1 - drive a progress bar off this
    [Networked] public NetworkBool IsOpen { get; private set; }
    [Networked] public Vector3 SpawnPoint { get; set; }          // same deferred-spawn safeguard as WorldItem - a deferred spawn drops the position arg
    [Networked] public NetworkBool UseSpawnPoint { get; set; }

    public float CrackRange => crackRange; // the player's hold-to-crack check reads this

    public override void Spawned()
    {
        AllSafes.Add(this);
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

    private void Open() //runs on the state authority only (gated in FixedUpdateNetwork)
    {
        IsOpen = true;

        int totalSpawnedValue = 0;
        for (int itemIndex = 0; itemIndex < lootItemCount; itemIndex++)
        {
            int value = Random.Range(lootValueMin, lootValueMax + 1); //declared inside the loop so each closure captures its own value
            totalSpawnedValue += value;

            Vector3 spawnAt = transform.position + transform.forward * 0.5f + Vector3.up * 0.6f + Random.insideUnitSphere * 0.15f; //shove it out the front so the pile doesn't stack in one spot
            Runner.Spawn(worldItemPrefab, spawnAt, Random.rotation, PlayerRef.None, (runner, spawnedObject) =>
            {
                WorldItem item = spawnedObject.GetComponent<WorldItem>();
                if (item != null)
                {
                    item.ItemName = lootName;
                    item.Value = value;
                    item.SpawnPoint = spawnAt;
                    item.UseSpawnPoint = true;
                }
            });
        }

        //fold the safe's contents into the house total the moment they exist, so the success screen's clear-% counts
        //them AND can't run over 100% (an uncracked safe simply never enters the tally). ReportHouseLoot adds within
        //this same scene load, so it sums with whatever ItemSpawner already reported.
        if (RunManager.Instance != null)
        {
            RunManager.Instance.ReportHouseLoot(totalSpawnedValue);
        }
    }

    public override void Render()
    {
        ApplyVisual(); //keep the open/closed look matched to the networked IsOpen on every client, even as it replicates in
    }

    private void ApplyVisual()
    {
        if (openVisual != null)
        {
            openVisual.SetActive(IsOpen);
        }
        if (closedVisual != null)
        {
            closedVisual.SetActive(!IsOpen);
        }
    }
}
