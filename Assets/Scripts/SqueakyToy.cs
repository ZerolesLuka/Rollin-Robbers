using UnityEngine;

// Attach to ANY toy in the scene - no NetworkObject needed. When a live player is within range,
// the toy squeaks and (on the master only) pings the guard to come investigate this spot.
// The squeak is "networked" the cheap way: EVERY client runs this and plays its own copy, keyed off
// the already-replicated player positions - so everyone hears it, spatially, with no RPC. Reusable.
public class SqueakyToy : MonoBehaviour
{
    [SerializeField] private float triggerRange = 4f;  //how close a player must get to set it off
    [SerializeField] private float cooldown = 2.5f;    //seconds before it can trigger again
    [SerializeField] private AudioSource squeakSource; //the squeak (set Spatial Blend to 3D so it's directional)

    private float cooldownTimer;

    private void Update()
    {
        cooldownTimer -= Time.deltaTime;
        if (cooldownTimer > 0f) return;

        if (!AnyPlayerInRange()) return;

        cooldownTimer = cooldown;

        //sound: every client plays its own copy, all keyed off the same replicated positions, so everyone hears it
        if (squeakSource != null)
        {
            squeakSource.Play();
        }

        //guard alert: only the master has the guard, so only it sends him over
        if (GuardPatrol.Instance != null)
        {
            GuardPatrol.Instance.AlertTo(transform.position);
        }
    }

    private bool AnyPlayerInRange()
    {
        foreach (Player player in Player.ActivePlayers)
        {
            if (player.IsEliminated) continue; //ignore players who are already out
            if (Vector3.Distance(transform.position, player.transform.position) <= triggerRange)
            {
                return true;
            }
        }
        return false;
    }
}
