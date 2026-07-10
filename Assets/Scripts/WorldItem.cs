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

    [HideInInspector] public bool pendingRemoval; // set locally the instant we grab it, so our own pickup scan can't re-grab it during the despawn lag

    public override void Spawned()
    {
        AllItems.Add(this);

        //every client falls the item with its own local physics from the same synced spawn position - no NetworkTransform
        //resampling the motion, so the fall is smooth everywhere and they land in the same spot (gravity is deterministic enough)

        if (HasStateAuthority && string.IsNullOrEmpty(ItemName.ToString())) // a scene item the spawner/drop didn't name
        {
            ItemName = startingName;
            Value = startingValue;
        }
    }

    public override void Render() //every frame on all clients - keeps the glow matched to the networked Value even as it replicates in after spawn
    {
        if (glowLight != null)
        {
            glowLight.color = LootRarityTable.ColorFor(Value);
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        AllItems.Remove(this);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_PickUp() // routed to whoever owns this item (scene master, or the player who dropped it); they despawn it
    {
        if (claimed) return;
        claimed = true;
        Runner.Despawn(Object);
    }
}
