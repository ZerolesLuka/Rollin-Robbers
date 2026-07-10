using System.Collections.Generic;
using UnityEngine;

// A squeaky floorboard. When a player MOVES within its small footprint, it creaks and pings the guard.
// Distance-based (like SqueakyToy), NOT trigger-based - trigger events are unreliable with a CharacterController
// and don't fire at all for remote players (who move via NetworkTransform, not CharacterController.Move). Reading
// replicated positions works for everyone on the host. No collider needed for this to work.
public class SqueakyFloorboard : MonoBehaviour
{
    [SerializeField] private float triggerRange = 1f; // small footprint - the size of a plank you'd step on
    [SerializeField] private float cooldown = 2f;
    [SerializeField] private AudioSource squeakSource;

    private float cooldownTimer;
    private Dictionary<Player, Vector3> lastPositions = new Dictionary<Player, Vector3>();

    private void Update()
    {
        cooldownTimer -= Time.deltaTime;
        if (cooldownTimer > 0f) return;

        if (!AnyPlayerWalkingOn()) return;

        cooldownTimer = cooldown;
        if (squeakSource != null) squeakSource.Play(); //the creak sound plays on EVERY step, even the ones that don't yet alert the guard
        if (GuardPatrol.Instance != null) GuardPatrol.Instance.RegisterFloorboardCreak(transform.position); //takes several creaks before he actually investigates
    }

    private bool AnyPlayerWalkingOn()
    {
        foreach (Player player in Player.ActivePlayers) //live list, so players who joined after this board spawned still count
        {
            if (player.IsEliminated || player.IsHiding) continue;
            if (Vector3.Distance(transform.position, player.transform.position) > triggerRange) continue; //not standing on this board

            if (lastPositions.TryGetValue(player, out Vector3 lastPosition))
            {
                bool isMoving = Vector3.Distance(lastPosition, player.transform.position) > 0.01f; //creaks only when they actually move over it, not when standing still
                lastPositions[player] = player.transform.position;
                if (isMoving) return true;
            }
            else
            {
                lastPositions[player] = player.transform.position; //first frame in range - can't tell movement yet
            }
        }
        return false;
    }
}
