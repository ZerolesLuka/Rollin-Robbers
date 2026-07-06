using UnityEngine;

// Mount on a camera housing prop. Sweeps a detection cone back and forth across a constrained arc.
// A player visible in the cone for dwellTime seconds sends the guard to investigate.
// Plain MonoBehaviour - runs on every client. AlertTo only fires on the master (GuardPatrol.Instance
// is null on non-master clients), so the guard is only driven once. Alert sound plays locally on
// every machine from the camera's 3D position, same as the squeaky toy.
public class SecurityCamera : MonoBehaviour
{
    [SerializeField] private float fovAngle = 60f;
    [SerializeField] private float sightRange = 12f;
    [SerializeField] private float dwellTime = 1.5f;
    [SerializeField] private LayerMask obstacleMask;
    [SerializeField] private float patrolArcDegrees = 90f;
    [SerializeField] private float patrolSpeed = 25f;
    [SerializeField] private float trackSpeed = 80f;
    [SerializeField] private float alertCooldown = 8f;
    [SerializeField] private AudioSource alertSource;

    private Vector3 initialEuler;   // full euler at placement - X/Z preserved so a tilted camera stays tilted
    private float initialYaw;       // Y component of initialEuler, used as the center of the patrol arc
    private float patrolOffset;     // current angle offset from initialYaw (negative = left, positive = right)
    private int patrolDir = 1;      // sweep direction: 1 = sweeping right, -1 = sweeping left
    private float dwellTimer;
    private float cooldownTimer;

    private void Start()
    {
        initialEuler = transform.eulerAngles;
        initialYaw = initialEuler.y;
    }

    private void Update()
    {
        cooldownTimer -= Time.deltaTime;

        Player spotted = GetVisiblePlayer();

        if (spotted != null && cooldownTimer <= 0f)
        {
            TrackPlayer(spotted.transform.position);
            dwellTimer += Time.deltaTime;
            if (dwellTimer >= dwellTime)
            {
                TriggerAlert(spotted.transform.position);
            }
        }
        else
        {
            dwellTimer = 0f;
            Patrol();
        }
    }

    private Player GetVisiblePlayer()
    {
        foreach (Player player in Player.ActivePlayers)
        {
            if (player.Object == null || !player.Object.IsValid) continue;
            if (player.IsEliminated || player.IsLockedUp) continue;

            Vector3 toPlayer = player.transform.position - transform.position;
            float distance = toPlayer.magnitude;

            if (distance > sightRange) continue;
            if (Vector3.Angle(transform.forward, toPlayer) > fovAngle * 0.5f) continue; // outside cone
            if (Physics.Raycast(transform.position, toPlayer.normalized, distance, obstacleMask)) continue; // wall in the way

            return player;
        }
        return null;
    }

    private void TrackPlayer(Vector3 targetPosition)
    {
        //only rotate on Y - flatten the direction so the camera doesn't tilt up/down chasing the player
        Vector3 toTarget = targetPosition - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.001f) return;

        float targetYaw = Quaternion.LookRotation(toTarget).eulerAngles.y;
        float delta = Mathf.DeltaAngle(initialYaw, targetYaw);
        float halfArc = patrolArcDegrees * 0.5f;
        float clampedDelta = Mathf.Clamp(delta, -halfArc, halfArc); //can't track past the patrol arc limits
        float clampedYaw = initialYaw + clampedDelta;

        float newYaw = Mathf.MoveTowardsAngle(transform.eulerAngles.y, clampedYaw, trackSpeed * Time.deltaTime);
        transform.rotation = Quaternion.Euler(initialEuler.x, newYaw, initialEuler.z);
        patrolOffset = Mathf.DeltaAngle(initialYaw, newYaw); //keep patrol in sync so it resumes from the tracking position
    }

    private void Patrol()
    {
        float halfArc = patrolArcDegrees * 0.5f;
        patrolOffset += patrolDir * patrolSpeed * Time.deltaTime;

        if (patrolOffset >= halfArc) { patrolOffset = halfArc; patrolDir = -1; }
        else if (patrolOffset <= -halfArc) { patrolOffset = -halfArc; patrolDir = 1; }

        float targetYaw = initialYaw + patrolOffset;
        transform.rotation = Quaternion.Euler(initialEuler.x, targetYaw, initialEuler.z);
    }

    private void TriggerAlert(Vector3 position)
    {
        dwellTimer = 0f;
        cooldownTimer = alertCooldown;

        if (alertSource != null) alertSource.Play();
        if (GuardPatrol.Instance != null) GuardPatrol.Instance.AlertTo(position); //master only
    }

    private void OnDrawGizmos()
    {
        float centerYaw = Application.isPlaying ? initialYaw : transform.eulerAngles.y;
        Vector3 euler = Application.isPlaying ? initialEuler : transform.eulerAngles;
        float halfFov = fovAngle * 0.5f;
        int segments = 20;

        //detection cone in yellow - lines from camera to each edge of the cone + arc across the tip
        Gizmos.color = new Color(1f, 0.9f, 0f, 0.6f);
        Vector3 prevEdge = Vector3.zero;
        for (int i = 0; i <= segments; i++)
        {
            float angle = Mathf.Lerp(-halfFov, halfFov, i / (float)segments);
            Vector3 dir = Quaternion.Euler(euler.x, transform.eulerAngles.y + angle, euler.z) * Vector3.forward;
            Vector3 edge = transform.position + dir * sightRange;
            Gizmos.DrawLine(transform.position, edge);
            if (i > 0) Gizmos.DrawLine(transform.position + prevEdge, edge); // tip arc
            prevEdge = dir * sightRange;
        }

        //patrol arc limits in cyan - shows how far left and right the camera sweeps
        Gizmos.color = new Color(0f, 1f, 1f, 0.5f);
        float halfArc = patrolArcDegrees * 0.5f;
        Vector3 leftDir = Quaternion.Euler(euler.x, centerYaw - halfArc, euler.z) * Vector3.forward;
        Vector3 rightDir = Quaternion.Euler(euler.x, centerYaw + halfArc, euler.z) * Vector3.forward;
        Gizmos.DrawRay(transform.position, leftDir * sightRange);
        Gizmos.DrawRay(transform.position, rightDir * sightRange);
    }
}
