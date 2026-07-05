using System.Collections.Generic;
using UnityEngine;

// Attach to any lootable prop in the scene. No NetworkObject needed: IsLooted reads the
// master-authoritative bitmask on RunManager which replicates to every client automatically.
// Give each instance a UNIQUE lootId (0-63) in the Inspector - duplicates will silently merge.
public class Lootable : MonoBehaviour
{
    public static readonly List<Lootable> AllLootables = new List<Lootable>();

    [SerializeField] public int lootId;             // unique per scene (0-63)
    [SerializeField] public string itemName = "Item";
    [SerializeField] public int value = 100;
    [SerializeField] private GameObject[] hideOnLooted; // drag in the mesh renderers to hide on pickup

    public bool IsLooted => RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid && RunManager.Instance.IsLooted(lootId);

    private bool wasLooted;

    private void OnEnable() => AllLootables.Add(this);
    private void OnDisable() => AllLootables.Remove(this);

    private void Update()
    {
        bool looted = IsLooted;
        if (looted == wasLooted) return;

        wasLooted = looted;
        foreach (GameObject obj in hideOnLooted)
        {
            if (obj != null)
            {
                obj.SetActive(!looted);
            }
        }
    }
}
