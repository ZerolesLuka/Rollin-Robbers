using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

// Spawns pickup items at runtime, one per child transform of this object. Fusion doesn't enroll scene-placed
// NetworkObjects reliably (especially in the initial scene), so we spawn them properly like the guard and
// floorboards. Only the master spawns; the items replicate to everyone, and because each item's Value is
// [Networked] the master's rolled value carries to every client with no shared seed needed. A new run reloads
// the scene, which re-runs this, so both the loot LAYOUT and the VALUES are freshly rolled every heist.
//
// Name each child by what it should be worth:
//   "Vase"           -> a random value in [defaultMinValue, defaultMaxValue]
//   "Vase:1200"      -> exactly 1200 every run
//   "Vase:800-1500"  -> a random value in [800, 1500], different each run
public class ItemSpawner : MonoBehaviour
{
    [SerializeField] private NetworkObject worldItemPrefab;
    [SerializeField] private int defaultMinValue = 50;   // used when a child's name gives no value
    [SerializeField] private int defaultMaxValue = 250;
    [SerializeField] private int maxItemsPerRun = -1;    // -1 = spawn every spot; otherwise spawn a random subset of this many spots so the loot map changes each run
    [SerializeField, Range(0f, 1f)] private float perSpotSpawnChance = 1f; // independent chance each chosen spot actually spawns - adds a little more layout variety

    private IEnumerator Start()
    {
        //wait until RunManager is spawned + valid. that proves the simulation's spawn system is fully up - spawning any earlier NREs inside Fusion's id allocator
        while (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid)
        {
            yield return null;
        }

        NetworkRunner runner = RunManager.Instance.Runner;
        if (runner == null || !runner.IsSharedModeMasterClient) //only the host spawns; the items replicate to everyone
        {
            yield break;
        }

        //collect the spawn spots, then optionally trim to a random subset so every run has a different loot map
        List<Transform> spawnSpots = new List<Transform>();
        foreach (Transform spawnSpot in transform)
        {
            spawnSpots.Add(spawnSpot);
        }
        Shuffle(spawnSpots);

        int spotsToUse = maxItemsPerRun >= 0 ? Mathf.Min(maxItemsPerRun, spawnSpots.Count) : spawnSpots.Count;
        int spawnedTotalValue = 0;

        for (int spotIndex = 0; spotIndex < spotsToUse; spotIndex++)
        {
            if (perSpotSpawnChance < 1f && Random.value > perSpotSpawnChance) //this spot sits empty this run
            {
                continue;
            }

            Transform spawnSpot = spawnSpots[spotIndex];
            string itemName;
            int itemValue;
            ParseSpot(spawnSpot.name, out itemName, out itemValue);
            spawnedTotalValue += itemValue;

            runner.Spawn(worldItemPrefab, spawnSpot.position, Quaternion.identity, PlayerRef.None, //upright so it drops flat and settles, instead of spawning mid-tumble
                (spawnRunner, spawnedObject) =>
                {
                    WorldItem item = spawnedObject.GetComponent<WorldItem>();
                    if (item != null)
                    {
                        item.ItemName = itemName;
                        item.Value = itemValue;
                        item.SpawnPoint = spawnSpot.position;  //carried as networked data so the deferred spawn can't lose it
                        item.UseSpawnPoint = true;
                    }
                });
        }

        //tell the run how much loot is actually in the house this run, so the success screen can grade the haul against it
        RunManager.Instance.ReportHouseLoot(spawnedTotalValue);
    }

    //child name is "ItemName", "ItemName:Value", or "ItemName:Min-Max"
    private void ParseSpot(string spotName, out string itemName, out int itemValue)
    {
        itemName = spotName;
        itemValue = Random.Range(defaultMinValue, defaultMaxValue + 1); //Range max is exclusive for ints, so +1 to make the top value reachable

        int colonIndex = spotName.IndexOf(':');
        if (colonIndex < 0) //no value given - keep the default random roll
        {
            return;
        }

        itemName = spotName.Substring(0, colonIndex);
        string valuePart = spotName.Substring(colonIndex + 1);

        int dashIndex = valuePart.IndexOf('-');
        if (dashIndex > 0) //a "Min-Max" range - roll inside it so this item is worth something different each run
        {
            if (int.TryParse(valuePart.Substring(0, dashIndex), out int rangeMin) && int.TryParse(valuePart.Substring(dashIndex + 1), out int rangeMax))
            {
                if (rangeMax < rangeMin) //tolerate the range being written backwards
                {
                    int swap = rangeMin;
                    rangeMin = rangeMax;
                    rangeMax = swap;
                }
                itemValue = Random.Range(rangeMin, rangeMax + 1);
            }
        }
        else if (int.TryParse(valuePart, out int fixedValue)) //a single fixed value
        {
            itemValue = fixedValue;
        }
    }

    private void Shuffle(List<Transform> spots) //Fisher-Yates; runs master-only so there's no desync between clients
    {
        for (int index = spots.Count - 1; index > 0; index--)
        {
            int swapIndex = Random.Range(0, index + 1);
            Transform temp = spots[index];
            spots[index] = spots[swapIndex];
            spots[swapIndex] = temp;
        }
    }
}
