using UnityEngine;
using Fusion;
using UnityEngine.AI;

public class GuardPatrol : NetworkBehaviour
{
    public enum GuardState { Asleep, Relaxed, Suspicious, Searching, Chasing, Caught }
    //
    [Networked] public GuardState State { get; private set; } //the guard's current state

    [SerializeField] private float suspiciousDuration = 3f;
    [SerializeField] private float noiseRange = 4f; //how close a noise registers
    [SerializeField] private float noiseThreshold;
    [SerializeField] private float searchDuration = 8f; //how long the guard will search before giving up and change states
    [SerializeField] private float catchRange = 2f;
    private float chaseSenseRange = 5f;
    private float noiseSpeedThreshold = 5f;
    private float noiseDrainRate = 1.5f; //how fast the bucket empties when it's quiet
    private float alertConfirmTime = 1.5f;   // must stay suspicious this long before searching
    private float noiseAccumulator;                 
    private float suspicionTimer; //how long the guard is sus after in suspicion state
    private float searchTimer;//how long guard searches
    private Player chaseTarget;//last seen player
    private Vector3 lastKnownPosition; //playerpos
    private int asleepChances; //how many times the guard relaxes before he perma suspicious
    private int asleepChancesMax = 3;

    private NavMeshAgent agent; //guard
    private Transform[] waypoints; //guard patrol
    [SerializeField] private float reachDistance = 0.5f; //distance of which the guard consider it has reached waypoint

    [SerializeField] private float sightRange = 10f; //howfarsee
    [SerializeField] private float fovAngle = 120f; //field of view angle for the guard to see the player
    [SerializeField] private float eyeHeight = 1.6f; //where the guard sees
    [SerializeField] private LayerMask obstacleMask; //to check if there are obstacles between the guard and the player

 


    private int currentWaypoint = 0; //used in modulo

    public override void Spawned()
    {
        agent = GetComponent<NavMeshAgent>(); //grab it from our own GameObject so it's never null 
        if (!HasStateAuthority)
        {
            agent.enabled = false;
            return;
        }
        State = GuardState.Asleep; //guard starts asleep
        noiseThreshold = Random.Range(3f, 6f); //guard is triggered randomly (float overload, not whole-number ints)
        agent.Warp(transform.position);
    }

    public override void FixedUpdateNetwork()
    {
        if(!HasStateAuthority) return; //only run for the state authority, which is the host in this case, so only the host will control the guard's movement

        switch(State)
        {
            case GuardState.Asleep:
                float perceivedNoise = LoudestPerceivedNoise(); //0 = silent, higher = louder/closer
                if (perceivedNoise > 0f)
                {
                    noiseAccumulator += perceivedNoise * Runner.DeltaTime; //runs faster if the noise was louder
                    if (noiseAccumulator >= noiseThreshold)
                    {
                        Debug.Log("Guard woke up!");
                        ChangeState(GuardState.Suspicious);
                    }
                }
                else
                {
                    noiseAccumulator = Mathf.Max(0f, noiseAccumulator - noiseDrainRate * Runner.DeltaTime);
                }
                break;
            case GuardState.Relaxed:
                //fetch snack/water mechanism randomness




                break;
            case GuardState.Suspicious:
                if (HearsNoise())
                {
                    suspicionTimer += Runner.DeltaTime;
                    if(suspicionTimer > alertConfirmTime)
                    {
                        Debug.Log("Guard searching");
                        ChangeState(GuardState.Searching);
                    }
                }
                else
                {
                    suspicionTimer -= Runner.DeltaTime;
                    if(suspicionTimer <= 0f)
                    {
                        asleepChances++;
                        if (asleepChances >= asleepChancesMax)
                        {
                            Debug.Log("Guard : False Alarm");
                            ChangeState(GuardState.Asleep);
                        }
                        else
                        {
                            Debug.Log("Guard Mild Alerted");
                            ChangeState(GuardState.Relaxed);
                        }
                    }

                }
                    break;  
            case GuardState.Searching:
                //Move
                if (!agent.pathPending && agent.remainingDistance <= reachDistance) //if theres no path pending and we got to our destination
                {
                    currentWaypoint = (currentWaypoint + 1) % waypoints.Length; //add to the waypoint % means dont go over the lenght of waypoints, will reset to 0
                    agent.SetDestination(waypoints[currentWaypoint].position); //agents destination is the current waypoints position
                }
                Player loudestVisiblePlayer = null;
                float loudestNoiseHeard = -1f; //start below zero so he can still see a silent player
                foreach (Player player in FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    if (CanSeePlayer(player) && player.NoiseLevel > loudestNoiseHeard) //if we can see this player and they are louder then the loudest noise
                    {
                        loudestNoiseHeard = player.NoiseLevel; //loudest noise heard goes to that player
                        loudestVisiblePlayer = player;
                    }
                }
                if (loudestVisiblePlayer != null)
                {
                    chaseTarget = loudestVisiblePlayer;
                    ChangeState(GuardState.Chasing);
                }
                searchTimer += Runner.DeltaTime; //start the search timer
                if(searchTimer >= searchDuration) //if we searched for too long, go back to sleep
                {
                    Debug.Log("Guard: Must have been nothing...");
                    ChangeState(GuardState.Asleep); //Should switch to relaxed instead of asleep, for now this
                }


                break;
            case GuardState.Chasing:
                Vector3 toTarget = chaseTarget.transform.position - transform.position;
                toTarget.y = 0f; // ignore height difference
                float distanceToTarget = toTarget.magnitude;

                if (CanSeePlayer(chaseTarget) || distanceToTarget < chaseSenseRange) //if we see our target or we are within chaserange
                {
                    lastKnownPosition = chaseTarget.transform.position;
                    agent.SetDestination(lastKnownPosition);
                }
                if (distanceToTarget < catchRange)
                {
                    Debug.Log("CAUGHT!");
                    ChangeState(GuardState.Caught);
                }
                else if (!CanSeePlayer(chaseTarget) && distanceToTarget > chaseSenseRange && !agent.pathPending && agent.remainingDistance <= reachDistance)
                {
                    Debug.Log("Guard: lost 'em...");
                    ChangeState(GuardState.Searching);
                }
                break;
            case GuardState.Caught:
                chaseTarget.RPC_GetCaught();
                ChangeState(GuardState.Relaxed);
                break;

        }
    }
    public void SetWaypoints(Transform[] points)
    {
        waypoints = points; //set the waypoints from the spawner, since we cant set them in the inspector for the guard prefab
    }

    private void ChangeState(GuardState newState) //single place to switch states so timers/counters always reset on entry
    {
        State = newState;
        switch (newState)
        {
            case GuardState.Asleep:
                noiseAccumulator = 0f; //empty the bucket on every trip to sleep so he needs FRESH noise to wake
                agent.ResetPath();   //stop walking the stale search path instead of wandering around while "asleep"
                break;
            case GuardState.Suspicious:
                suspicionTimer = alertConfirmTime * 0.5f;   // wakes up already half upset
                break;
            case GuardState.Searching:
                searchTimer = 0f;    //fresh search window every time, even when re-entering after losing a chase
                break;
        }
    }

    private bool CanSeePlayer(Player target)//bool with the parameter of a player, passed in by whoever calling
    {
        Vector3 eyePos = transform.position + Vector3.up * eyeHeight; //where the guard sees from
        Vector3 targetPos = target.transform.position + Vector3.up * 1f; //where the player is, we can adjust this if we want the guard to see the player's head or body
        Vector3 toTarget = targetPos - eyePos; //direction from guard to player
        float distance = toTarget.magnitude; //length of the vector

        if(distance > sightRange) return false; //In sight?
        Vector3 dir = toTarget.normalized; //direction from guard to player normalized

        // cone is horizontal only — up/down stairs no longer kicks you out
        Vector3 flatToTarget = Vector3.ProjectOnPlane(toTarget, Vector3.up);
        Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (Vector3.Angle(flatForward, flatToTarget) > fovAngle * 0.5f) return false;

        if (Physics.Raycast(eyePos, dir, distance, obstacleMask)) return false;

        return true;
    }
    private bool HearsNoise() //is there any audible noise right now (reuses the same perception as Asleep)
    {
        return LoudestPerceivedNoise() > 0f;
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
    private float LoudestPerceivedNoise() //strongest noise he perceives, factoring loudness AND distance
    {
        float loudest = 0f;
        foreach (Player player in FindObjectsByType<Player>(FindObjectsSortMode.None))
        {
            float distanceToPlayer = Vector3.Distance(transform.position, player.transform.position);
            float distanceFactor = Mathf.Clamp01((noiseRange - distanceToPlayer) / noiseRange);
            float audibleLoudness = Mathf.Max(0f, player.NoiseLevel - noiseSpeedThreshold); //below the floor (crouch) = silent
            float perceived = audibleLoudness * distanceFactor;                           //loud+close = big, loud+far = ~0
            if (perceived > loudest) //if noise we just picked up is louder than previous replace
            {
                loudest = perceived;
            }
        }
        return loudest;
    }

}