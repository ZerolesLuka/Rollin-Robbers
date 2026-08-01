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
    private bool wasSeen; //was a player visible last frame - so the sound fires once when you're first seen, not every frame

    private void Start()
    {
        initialEuler = transform.eulerAngles;
        initialYaw = initialEuler.y;
    }

    private void Update()
    {
        cooldownTimer -= Time.deltaTime;

        Player spotted = GetVisiblePlayer();

        bool isSeen = spotted != null;
        if (isSeen && !wasSeen) //rising edge - the camera just caught a player, so the sound plays ONLY when you're actually seen (not on awake, not on a timer)
        {
            if (alertSource != null)
            {
                alertSource.Play();
            }
        }
        else if (!isSeen && wasSeen) //dropped out of view - cut the sound so audio means "the camera can see me right now"
        {
            if (alertSource != null)
            {
                alertSource.Stop();
            }
        }
        wasSeen = isSeen;

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
            if (player.IsEliminated || player.IsLockedUp || player.IsHiding) continue;

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

        if (GuardPatrol.Instance != null) GuardPatrol.Instance.AlertTo(position); //master only - summons the guard. he never forwards it to the dog, so the camera still can't reach the dog (a dog doesn't watch monitors). the spotted sound already played on sight (see Update)
    }

    private void OnDrawGizmos()
    {
        float centerYaw = Application.isPlaying ? initialYaw : transform.eulerAngles.y;
        Vector3 euler = Application.isPlaying ? initialEuler : transform.eulerAngles;
        float halfFov = fovAngle * 0.5f;
        int ringSegments = 24;

        //detection cone in yellow - a ring of rays around transform.forward at the half-angle, plus lines from the tip back to origin
        Gizmos.color = new Color(1f, 0.9f, 0f, 0.6f);
        Vector3 prevRingPoint = Vector3.zero;
        for (int i = 0; i <= ringSegments; i++)
        {
            float ringAngle = (i / (float)ringSegments) * 360f;
            //rotate a vector that sits on the cone edge (halfFov away from forward) around the forward axis
            Vector3 coneEdge = Quaternion.AngleAxis(ringAngle, transform.forward) * (Quaternion.AngleAxis(halfFov, transform.up) * transform.forward);
            Vector3 ringPoint = transform.position + coneEdge * sightRange;
            Gizmos.DrawLine(transform.position, ringPoint); //spoke from camera to ring
            if (i > 0) Gizmos.DrawLine(prevRingPoint, ringPoint); //ring edge
            prevRingPoint = ringPoint;
        }

        //patrol arc limits in cyan
        Gizmos.color = new Color(0f, 1f, 1f, 0.5f);
        float halfArc = patrolArcDegrees * 0.5f;
        Vector3 leftDir = Quaternion.Euler(euler.x, centerYaw - halfArc, euler.z) * Vector3.forward;
        Vector3 rightDir = Quaternion.Euler(euler.x, centerYaw + halfArc, euler.z) * Vector3.forward;
        Gizmos.DrawRay(transform.position, leftDir * sightRange);
        Gizmos.DrawRay(transform.position, rightDir * sightRange);
    }
}
