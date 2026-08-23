using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Two ends of a wire the guard CAN string - across a doorway, the top of the stairs, a hallway pinch point.
//
// Replaces the old single-point TrapPoint for wires specifically. A wire is a LINE, and a line needs two ends; the
// old type could only say "somewhere around here", which is why the wire used to appear as a dot floating at one
// marker. Floor traps never used TrapPoint anyway - GuardPatrol samples those straight onto the NavMesh - so this is
// the only placement type the level actually has to author by hand now.
//
// AUTHORING: drop two empties where the wire should run, put this on ONE of them, and drag the other into otherEnd.
// One object owns the pair, so the hierarchy can't drift into a pile of unpaired anchors nobody can match up. The
// gizmo draws the real line, so the level view shows every wire at a glance.
public class TripwireSpan : MonoBehaviour
{
    public static readonly List<TripwireSpan> All = new List<TripwireSpan>();

    [SerializeField] private Transform otherEnd;             //the far anchor. without it this span is unusable, and SetupValidator says so
    [SerializeField] private float occupiedRadius = 1f;      //a wire already this close means the span is taken
    [SerializeField] private LayerMask blockingMask = ~0;    //what counts as "something is in the way of the wire"

    public Vector3 AnchorA => transform.position;
    public Vector3 AnchorB => otherEnd != null ? otherEnd.position : transform.position;
    public Vector3 Midpoint => (AnchorA + AnchorB) * 0.5f;
    public bool HasFarEnd => otherEnd != null;

    private void OnEnable() => All.Add(this);
    private void OnDisable() => All.Remove(this);

    //Is the straight line between the two anchors actually clear? A span with a wall, a bookcase or a bannister
    //through the middle of it can't hold a wire, and the guard walking over to string one there would look broken.
    //Checked once when he picks a span rather than every tick - the geometry doesn't move.
    public bool IsLineClear()
    {
        if (otherEnd == null)
        {
            return false;
        }
        //QueryTriggerInteraction.Ignore so a trigger volume sitting in a doorway - an interact zone, a room bounds
        //marker - doesn't read as a solid obstruction. Only real collision should block a wire.
        return !Physics.Linecast(AnchorA, AnchorB, blockingMask, QueryTriggerInteraction.Ignore);
    }

    //Why this span is unusable, or null if it's fine. SetupValidator reads this so authoring mistakes surface when you
    //open the scene rather than as a guard who mysteriously refuses to set traps three minutes into a playtest.
    public string DescribeProblem()
    {
        if (otherEnd == null)
        {
            return $"'{name}' has no Other End assigned, so no wire can ever be strung there.";
        }
        if (Vector3.Distance(AnchorA, AnchorB) < 0.25f)
        {
            return $"'{name}' has both ends in almost the same place - that's a dot, not a wire.";
        }
        if (!IsLineClear())
        {
            return $"'{name}' has something solid between its two ends, so the wire can't be strung.";
        }
        return null;
    }

    //Nearest usable, unwired span to where he lost you, or null if there's nothing worth walking to.
    public static TripwireSpan FindNearestFree(Vector3 position, float maxRange)
    {
        TripwireSpan nearest = null;
        float nearestDistance = maxRange;
        Scene activeScene = SceneManager.GetActiveScene();

        foreach (TripwireSpan span in All)
        {
            //spans from the scene we just left linger in this static list for a frame or two holding stale
            //coordinates - the same trap SpawnPoint and the old TrapPoint both hit. ignore anything out of scene.
            if (span.gameObject.scene != activeScene) continue;
            if (!span.HasFarEnd || !span.IsLineClear()) continue; //a broken span is skipped silently here; the validator is what complains about it

            //measured to the MIDPOINT, not to whichever anchor happens to be nearer - otherwise a long span pointing
            //away from him wins over a short one right next to him purely because one of its ends is close.
            float distance = Vector3.Distance(span.Midpoint, position);
            if (distance >= nearestDistance) continue;
            if (GuardTrap.AnyTrapNear(span.Midpoint, span.occupiedRadius)) continue; //already wired

            nearestDistance = distance;
            nearest = span;
        }
        return nearest;
    }

    private void OnDrawGizmos() //so you can see every wire run while dressing the level
    {
        bool usable = HasFarEnd && IsLineClear();
        Gizmos.color = usable ? new Color(1f, 0.35f, 0.35f, 0.9f) : new Color(1f, 0.9f, 0.1f, 0.9f); //yellow shouts that this one is broken

        Gizmos.DrawWireSphere(AnchorA, 0.15f);
        if (!HasFarEnd)
        {
            Gizmos.DrawLine(AnchorA, AnchorA + Vector3.up * 1.2f); //lone anchor - stands up so it's findable in a busy scene
            return;
        }
        Gizmos.DrawWireSphere(AnchorB, 0.15f);
        Gizmos.DrawLine(AnchorA, AnchorB);
    }
}
