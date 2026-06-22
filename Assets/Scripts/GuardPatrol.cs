using UnityEngine;
using Fusion;
using UnityEngine.AI;

public class GuardPatrol : NetworkBehaviour
{
    public enum GuardState { Asleep, Relaxed, Suspicious, Searching, Chasing, Caught }

    [Networked] public GuardState State { get; private set; } //the guard's current state

    [SerializeField] private float suspiciousDuration = 3f;
    [SerializeField] private float noiseRange = 4f; //how close a noise registers
    [SerializeField] private int noiseThreshold;
    [SerializeField] private float searchDuration = 8f; //how long the guard will search before giving up and change states
    [SerializeField] private float catchRange = 1.5f;
    [SerializeField] private float chaseSenseRange = 5f;
    [SerializeField] private float noiseSpeedThreshold = 5f;
    private int noiseCounter; //how many times the guard tolerat
    private float noiseTickTimer; //tracks how long weve heard a noise
    private float suspicionTimer; //how long the guard is sus after in suspicion state
    private float searchTimer;//how long guard searches
    private Player chaseTarget;//last seen player
    private Vector3 lastKnownPos; //playerpos

    [SerializeField] private NavMeshAgent agent; //guard
    [SerializeField] private Transform[] waypoints; //guard patrol
    [SerializeField] private float reachDistance = 0.5f; //distance of which the guard consider it has reached waypoint

    [SerializeField] private float sightRange = 10f; //howfarsee
    [SerializeField] private float fovAngle = 120f; //field of view angle for the guard to see the player
    [SerializeField] private float eyeHeight = 1.6f; //where the guard sees
    [SerializeField] private LayerMask obstacleMask; //to check if there are obstacles between the guard and the player

    private int currentWaypoint = 0; //used in modulo

    public override void Spawned()
    {
        if (!HasStateAuthority) 
        {
            agent.enabled = false;
            return;
        }
        State = GuardState.Asleep; //guard starts asleep
        noiseThreshold = Random.Range(2, 5); //guard is triggered randomly
        agent.Warp(transform.position);
    }

    public override void FixedUpdateNetwork()
    {
        if(!HasStateAuthority) return; //only run for the state authority, which is the host in this case, so only the host will control the guard's movement

        switch(State)
        {
            case GuardState.Asleep:
                if(HearsNoise())
                {
                    noiseTickTimer += Runner.DeltaTime; //if we heard noise, start the timer
                    if (noiseTickTimer >= .3f) //if we heard noise for x seconds, increase the noise counter
                    {
                        noiseTickTimer = 0f; //reset timer
                        noiseCounter++;
                        if(noiseCounter >= noiseThreshold) //if we heard enough noise, wake up
                        {
                            Debug.Log("Guard woke up!");
                            State = GuardState.Suspicious;
                        }
                    }

                }
            break;
            case GuardState.Suspicious:
                suspicionTimer += Runner.DeltaTime; //start the suspicion timer
                if (HearsNoise())
                {
                    Debug.Log("Guard: Who's There?");
                    State = GuardState.Searching;
                    suspicionTimer = 0;
                }
                else if(suspicionTimer >= suspiciousDuration) //if we were suspicious for too long, go back to sleep
                {
                    Debug.Log("Guard: False Alarm.");
                    State = GuardState.Asleep;
                    noiseCounter = 0; //reset noise counter when going back to sleep
                }
            break;
            case GuardState.Searching:
                //Move
                if (!agent.pathPending && agent.remainingDistance <= reachDistance) //if theres no path pending and we got to our destination
                {
                    currentWaypoint = (currentWaypoint + 1) % waypoints.Length; //add to the waypoint % means dont go over the lenght of waypoints, will reset to 0
                    agent.SetDestination(waypoints[currentWaypoint].position); //agents destination is the current waypoints position
                }
                foreach (Player p in FindObjectsByType<Player>(FindObjectsSortMode.None)) //for each player, if we can see them, log we see them
                    if (CanSeePlayer(p))
                    {
                        Debug.Log($"Guard sees {p.name}!");
                        chaseTarget = p;                 // ← HERE, where p exists
                        State = GuardState.Chasing;
                        break;
                    }
                searchTimer += Runner.DeltaTime; //start the search timer
                if(searchTimer >= searchDuration) //if we searched for too long, go back to sleep
                {
                    Debug.Log("Guard: Must have been nothing...");
                    State = GuardState.Asleep; //Should switch to relaxed instead of asleep, for now this
                    searchTimer = 0f; //reset search timer
                }


                break;
            case GuardState.Chasing:
                float dist = Vector3.Distance(transform.position, chaseTarget.transform.position);

                if (CanSeePlayer(chaseTarget) || dist < chaseSenseRange)   // sees them OR they're right on him
                    lastKnownPos = chaseTarget.transform.position;
                agent.SetDestination(lastKnownPos);

                if (dist < catchRange)
                {
                    Debug.Log("CAUGHT!");
                    State = GuardState.Caught;
                }
                else if (!CanSeePlayer(chaseTarget) && dist > chaseSenseRange && !agent.pathPending && agent.remainingDistance <= reachDistance)
                {
                    Debug.Log("Guard: lost 'em...");
                    State = GuardState.Searching;
                }
                break;
        }
    }
    public void SetWaypoints(Transform[] points)
    {
        waypoints = points; //set the waypoints from the spawner, since we cant set them in the inspector for the guard prefab
    }

    private bool CanSeePlayer(Player target)//bool with the parameter of a player, passed in by whoever calling
    {
        Vector3 eyePos = transform.position + Vector3.up * eyeHeight; //where the guard sees from
        Vector3 targetPos = target.transform.position + Vector3.up * 1f; //where the player is, we can adjust this if we want the guard to see the player's head or body
        Vector3 toTarget = targetPos - eyePos; //direction from guard to player
        float distance = toTarget.magnitude; //length of the vector

        if(distance > sightRange) return false; //In sight?
        Vector3 dir = toTarget.normalized; //direction from guard to player normalized
        if(Vector3.Angle(transform.forward, dir) > fovAngle * 0.5f) return false; //in the cone?
        if (Physics.Raycast(eyePos, dir, distance, obstacleMask)) return false; //if there's an obstacle between the guard and the player, return false

        return true; //if we passed all the checks, we can see the player
    }
    private bool HearsNoise()
    {
        foreach (Player player in FindObjectsByType<Player>(FindObjectsSortMode.None))
        {
            float distanceToPlayer = Vector3.Distance(transform.position, player.transform.position);
            if (distanceToPlayer <= noiseRange)   // only log when actually near, cuts the spam
            {
                bool loudEnough = player.NoiseLevel >= noiseSpeedThreshold;
                Debug.Log($"HEARS — distance {distanceToPlayer:F1}/{noiseRange}, noise {player.NoiseLevel:F1}/{noiseSpeedThreshold}, loud? {loudEnough}");
                if (loudEnough) return true;
            }
        }
        return false;
    }

    private void OnDrawGizmos() //used to draw lines fro the guards view, honestly had no idea how to do this so watched some videos and used AI
    { //NO IDEA HOW THIS WORKS
        Vector3 eye = transform.position + Vector3.up * eyeHeight;

        // red while actually seeing a player, yellow otherwise
        bool sees = false;
        if (Application.isPlaying)
            foreach (Player pl in FindObjectsByType<Player>(FindObjectsSortMode.None))
                if (CanSeePlayer(pl)) { sees = true; break; }
        Gizmos.color = sees ? new Color(1f, 0.2f, 0.2f) : new Color(1f, 0.9f, 0.2f);

        int rays = 10;
        Vector3 prev = Vector3.zero;
        for (int i = 0; i <= rays; i++)
        {
            float ang = Mathf.Lerp(-fovAngle * 0.5f, fovAngle * 0.5f, (float)i / rays);
            Vector3 end = eye + (Quaternion.Euler(0, ang, 0) * transform.forward) * sightRange;
            Gizmos.DrawLine(eye, end);              // ray from the eye to the edge of range
            if (i > 0) Gizmos.DrawLine(prev, end);  // connect tips → forms the far arc
            prev = end;
        }
    }

}