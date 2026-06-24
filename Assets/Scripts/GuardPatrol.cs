using UnityEngine;
using Fusion;
using UnityEngine.AI;
using System.Runtime.CompilerServices;

public class GuardPatrol : NetworkBehaviour
{
    public enum GuardState { Asleep, Relaxed, Suspicious, Searching, Chasing, Caught }
    //
    [Networked] public GuardState State { get; private set; } //the guard's current state

    [SerializeField] private float suspiciousDuration = 3f;
    [SerializeField] private float noiseRange = 30f; //how close a noise registers
    [SerializeField] private float noiseThreshold;
    [SerializeField] private float searchDuration = 8f; //how long the guard will search before giving up and change states
    [SerializeField] private float catchRange = 2f;
    private float chaseSenseRange = 5f;
    private float noiseSpeedThreshold = 5f;
    [SerializeField] private float noiseDrainRate = 1.5f; //how fast the bucket empties when it's quiet
    [SerializeField] private float noiseMemoryTime = 2f; //hold the bucket this long after the last noise before draining
    private float quietTimer; //how long it's been quiet since the last noise
    private float alertConfirmTime = 1.5f;   // must stay suspicious this long before searching
    private float noiseAccumulator;                 
    private float suspicionTimer; //how long the guard is sus after in suspicion state
    private float searchTimer;//how long guard searches
    private Player chaseTarget;//last seen player
    private Vector3 lastKnownPosition; //playerpos
    private int asleepChances; //how many times the guard relaxes before he perma suspicious
    private int asleepChancesMax = 3;
    private float relaxPatrolTimer;
    [SerializeField] private float relaxIdleMin = 3f;
    [SerializeField] private float relaxIdleMax = 8f;

    private NavMeshAgent agent; //guard
    private Transform[] waypoints; //guard patrol
    [SerializeField] private float reachDistance = 0.5f; //distance of which the guard consider it has reached waypoint

    [SerializeField] private float sightRange = 10f; //howfarsee
    [SerializeField] private float fovAngle = 120f; //field of view angle for the guard to see the player
    [SerializeField] private float eyeHeight = 1.6f; //where the guard sees
    [SerializeField] private LayerMask obstacleMask; //to check if there are obstacles between the guard and the player

    [SerializeField] private AudioSource voiceSource;     //3D source on the guard
    [SerializeField] private AudioClip[] suspiciousSounds; //"huh? who's there?"
    [SerializeField] private AudioClip[] searchingSounds;  //"I know you're in here"
    [SerializeField] private AudioClip[] chasingSounds;    //"HEY! GET OUT!"
    [SerializeField] private AudioClip[] caughtSounds;     //"GOTCHA!"
    [SerializeField] private AudioClip[] asleepSounds;     //"musta been nothin'" (false alarm / give up)

    private float relaxSpeed = 1.5f;
    private float searchSpeed = 3.5f;
    private float chaseSpeed = 8f;

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
        noiseThreshold = Random.Range(3f, 6 ); //guard is triggered randomly (float overload, not whole-number ints)
        agent.updatePosition = false; //agent still steers/pathfinds, but WE move the transform on the tick so NetworkTransform doesn't fight it
        agent.Warp(transform.position); 
    }

    public override void FixedUpdateNetwork()
    {
        if(!HasStateAuthority) return; //only run for the state authority, which is the host in this case, so only the host will control the guard's movement

        switch(State)
        {
            case GuardState.Asleep:
                ListenForNoise();
                break;
            case GuardState.Relaxed:
                ListenForNoise();
                relaxPatrolTimer -= Runner.DeltaTime; //count down so he strolls again after idling
                if (relaxPatrolTimer <= 0f && !agent.pathPending && agent.remainingDistance <= reachDistance)
                {
                    currentWaypoint = (currentWaypoint + 1) % waypoints.Length; //modulo that cycles patrols
                    agent.SetDestination(waypoints[currentWaypoint].position); //cycles waypoints
                    relaxPatrolTimer = Random.Range(relaxIdleMin, relaxIdleMax); //chill a random bit, then move again
                }
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
                        if (asleepChances < asleepChancesMax)
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
                //he heads to lastKnownPosition (set on entry) and scans there - no waypoint patrol, that's Relaxed's job
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
        transform.position = agent.nextPosition; //apply the agent's steering ON the tick - same clock as the player, no NetworkTransform tug-of-war
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
                quietTimer = 0f; //reset the quiet clock too
                agent.ResetPath();   //stop walking the stale search path instead of wandering around while "asleep"
                PlayStateSound(newState); //bark whenever he changes state

                break;
            case GuardState.Relaxed:
                agent.speed = relaxSpeed;
                noiseAccumulator = 0f;
                quietTimer = 0f;
                PlayStateSound(newState); //bark whenever he changes state
                break;
            case GuardState.Suspicious:
                suspicionTimer = alertConfirmTime * 0.5f;   // wakes up already half upset
                PlayStateSound(newState); //bark whenever he changes state
                break;
            case GuardState.Searching:
                agent.speed = searchSpeed;
                agent.SetDestination(lastKnownPosition);
                searchTimer = 0f;    //fresh search window every time, even when re-entering after losing a chase
                PlayStateSound(newState); //bark whenever he changes state
                break;
            case GuardState.Chasing:
                agent.speed = chaseSpeed;
                PlayStateSound(newState); //bark whenever he changes state
                break;
            case GuardState.Caught:
                PlayStateSound(newState); //bark whenever he changes state
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
        if (Vector3.Angle(flatForward, flatToTarget) > fovAngle * 0.5f) return false; //flat plane check

        if (Physics.Raycast(eyePos, dir, distance, obstacleMask)) return false; //hits object

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
                lastKnownPosition = player.transform.position; //remember WHERE the loudest noise came from
            }
        }
        return loudest;
    }
    private void ListenForNoise() //shared ears for Asleep + Relaxed
    {
        float perceivedNoise = LoudestPerceivedNoise(); //use loudest thing closest relative to guard
        if (perceivedNoise > 0f)
        {
            quietTimer = 0f;
            noiseAccumulator += perceivedNoise * Runner.DeltaTime;
            if (noiseAccumulator >= noiseThreshold)
            {
                Debug.Log("Guard heard something!");
                ChangeState(GuardState.Suspicious);
            }
        }
        else
        {
            quietTimer += Runner.DeltaTime;
            if (quietTimer >= noiseMemoryTime)
            {
                noiseAccumulator = Mathf.Max(0f, noiseAccumulator - noiseDrainRate * Runner.DeltaTime);
            }
        }
    }
    private AudioClip[] ClipsFor(GuardState state)
    {
        switch (state)
        { //returns the state were at and plays those sounds
            case GuardState.Suspicious: return suspiciousSounds;
            case GuardState.Searching: return searchingSounds;
            case GuardState.Chasing: return chasingSounds;
            case GuardState.Caught: return caughtSounds;
            case GuardState.Asleep: return asleepSounds;
            default: return null; //Relaxed = no bark for now
        }

    }
    private void PlayStateSound(GuardState state)
    {
        AudioClip[] clips = ClipsFor(state);
        if (clips != null && clips.Length > 0)
        {
            int index = Random.Range(0, clips.Length); //chosen once on the host
            RPC_PlayStateSound(index, state);
        }
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)] //host fires it, everyone hears it
    private void RPC_PlayStateSound(int index, GuardState state)
    {
        AudioClip[] clips = ClipsFor(state);
        if (voiceSource != null && clips != null && index >= 0 && index < clips.Length)
        {
            voiceSource.PlayOneShot(clips[index]);
        }
    }
}