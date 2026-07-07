using System.Collections.Generic;
using UnityEngine;

// Attach to a floor plank with a Box Collider set to Is Trigger.
// Squeaks when a player walks through it - same cheap client-side audio trick as SqueakyToy.
public class SqueakyFloorboard : MonoBehaviour
{
    [SerializeField] private float cooldown = 2f;
    [SerializeField] private AudioSource squeakSource;

    private float cooldownTimer;
    private Dictionary<Player, Vector3> lastPositions = new Dictionary<Player, Vector3>();

    private void OnTriggerStay(Collider other)
    {
        if (cooldownTimer > 0f) return;

        Player player = other.GetComponent<Player>();
        if (player == null || player.IsEliminated || player.IsHiding) return;

        if (lastPositions.TryGetValue(player, out Vector3 lastPosition))
        {
            bool isMoving = Vector3.Distance(lastPosition, player.transform.position) > 0.01f;
            lastPositions[player] = player.transform.position;
            if (!isMoving) return;
        }
        else
        {
            lastPositions[player] = player.transform.position;
            return;
        }

        cooldownTimer = cooldown;
        if (squeakSource != null) squeakSource.Play();
        if (GuardPatrol.Instance != null) GuardPatrol.Instance.AlertTo(transform.position);
    }

    private void Update()
    {
        cooldownTimer -= Time.deltaTime;
    }
}
