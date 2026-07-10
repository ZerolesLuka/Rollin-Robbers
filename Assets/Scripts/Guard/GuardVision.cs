using UnityEngine;

// Reusable sight for any guard archetype (homeowner, dog, ...). Pure geometry, no networked state:
// the brain (GuardPatrol / future DogAI) owns the FSM and just asks "can you see this target?".
// Config lives here so each archetype tweaks its own values in its own inspector.
public class GuardVision : MonoBehaviour
{
    [SerializeField] private float sightRange = 10f;  //how far he can see
    [SerializeField] private float fovAngle = 120f;   //width of the view cone in degrees
    [SerializeField] private float eyeHeight = 1.6f;  //where he sees from - MUST stay exposed: the dog tunes this down to ~0.84 (sees from lower), shared component
    [SerializeField] private LayerMask obstacleMask;  //what blocks line of sight
    private float flashlightSightBonus = 6f; //a player with their flashlight ON is lit up - visible from this much farther away (hidden for now)

    public bool CanSee(Transform target)
    {
        Player targetPlayer = target.GetComponent<Player>();
        if (targetPlayer != null && targetPlayer.IsHiding) // hidden players are invisible to all sensors
        {
            return false;
        }

        Vector3 eyePos = transform.position + Vector3.up * eyeHeight;  //where the guard sees from
        Vector3 targetPos = target.position + Vector3.up * 1f;         //aim at the target's body
        Vector3 toTarget = targetPos - eyePos;
        float distance = toTarget.magnitude;

        //a lit flashlight gives the holder away - the guard picks them out from farther in the dark
        float effectiveSightRange = sightRange;
        if (targetPlayer != null && targetPlayer.IsFlashlightOn)
        {
            effectiveSightRange += flashlightSightBonus;
        }

        if (distance > effectiveSightRange) //out of range
        {
            return false;
        }

        Vector3 dir = toTarget.normalized;

        // horizontal-only cone so going up/down stairs doesn't break line of sight
        Vector3 flatToTarget = Vector3.ProjectOnPlane(toTarget, Vector3.up);
        Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (Vector3.Angle(flatForward, flatToTarget) > fovAngle * 0.5f) //outside the cone
        {
            return false;
        }

        if (Physics.Raycast(eyePos, dir, distance, obstacleMask)) //something's blocking the view
        {
            return false;
        }

        return true;
    }

    private void OnDrawGizmos() //draws the view cone in the editor
    {
        Vector3 eye = transform.position + Vector3.up * eyeHeight;

        // red while actually seeing a player, yellow otherwise
        bool sees = false;
        if (Application.isPlaying)
        {
            foreach (Player pl in FindObjectsByType<Player>(FindObjectsSortMode.None))
            {
                if (CanSee(pl.transform))
                {
                    sees = true;
                    break;
                }
            }
        }
        Gizmos.color = sees ? new Color(1f, 0.2f, 0.2f) : new Color(1f, 0.9f, 0.2f);

        int rays = 10;
        Vector3 prev = Vector3.zero;
        for (int i = 0; i <= rays; i++)
        {
            float ang = Mathf.Lerp(-fovAngle * 0.5f, fovAngle * 0.5f, (float)i / rays);
            Vector3 end = eye + (Quaternion.Euler(0, ang, 0) * transform.forward) * sightRange;
            Gizmos.DrawLine(eye, end);              // ray from the eye to the edge of range
            if (i > 0)
            {
                Gizmos.DrawLine(prev, end);         // connect the tips into the far arc
            }
            prev = end;
        }
    }
}
