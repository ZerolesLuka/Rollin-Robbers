using UnityEngine;
using Fusion;
using UnityEngine.AI;

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

    [SerializeField] private float searchSweepRadius = 6f;
    [SerializeField] private int maximumSearchSweepPoints = 3;
    [SerializeField] private float searchSweepWaitTime = 1.25f;

    private int searchSweepPointsChecked;
    private float searchSweepWaitTimer;

    [SerializeField] private float searchNoiseReactThreshold = 1.5f;
    [SerializeField] private float searchNoiseReactionCooldown = 2f;
    private float searchNoiseReactionTimer;

    [SerializeField] private Vector3 spawnPosition;

    private float barkCooldown = 1.5f; //min seconds between barks
    private float barkCooldownTimer;

    private float relaxSpeed = 1.5f;
    private float searchSpeed = 3.5f;
    private float chaseSpeed = 6.5f;

    [Header("Anger")]
    [SerializeField] private float angerMax = 100f;
    [SerializeField] private float angerPerAlert = 20f;           //bump each time he escalates to a fresh alert
    [SerializeField] private float angerChaseRate = 12f;          //builds per second while actively chasing
    [SerializeField] private float angerDecayRate = 5f;           //cools per second while calm
    [SerializeField] private float angerEliminateThreshold = 60f; //at/above this, a catch eliminates instead of warns
    [Networked] public float Anger { get; private set; }          //how riled up he is; host-owned, readable for a future HUD

    private int currentWaypoint = 0; //used in modulo

    public override void Spawned()
    {
        agent = GetComponent<NavMeshAgent>(); //grab it from our own GameObject so it's never null 
        if (!HasStateAuthority)
        {
            agent.enabled = false;
            return;
        }
        spawnPosition = transform.position;
        State = GuardState.Asleep; //guard starts asleep
        noiseThreshold = Random.Range(3f, 6f); //random wake threshold so players cant memorize the exact amount
        agent.updatePosition = false; //agent still steers/pathfinds, but WE move the transform on the tick so NetworkTransform doesn't fight it
        agent.Warp(transform.position); 
    }

    public override void FixedUpdateNetwork()
    {
        if (barkCooldownTimer > 0f)
        {
            barkCooldownTimer -= Runner.DeltaTime;
        }
        if (searchNoiseReactionTimer > 0f)
        {
            searchNoiseReactionTimer -= Runner.DeltaTime;
        }
        if (!HasStateAuthority) return; //only run for the state authority, which is the host in this case, so only the host will control the guard's movement

        TickAnger(); //rise while chasing, cool while calm

        switch(State)
        {
            case GuardState.Asleep:
                ListenForNoise();
                ReturnToSleep();

                if(!agent.pathPending && agent.remainingDistance <= reachDistance)
                {
                    agent.ResetPath(); //arrived at bed, stop
                }
                break;
            case GuardState.Relaxed:
                ListenForNoise();
                relaxPatrolTimer -= Runner.DeltaTime; //count down so he strolls again after idling
                if (relaxPatrolTimer <= 0f && waypoints != null && waypoints.Length > 0 && !agent.pathPending && agent.remainingDistance <= reachDistance)
                {
                    currentWaypoint = (currentWaypoint + 1) % waypoints.Length; //cycle to next waypoint, wraps with modulo
                    agent.SetDestination(waypoints[currentWaypoint].position);
                    relaxPatrolTimer = Random.Range(relaxIdleMin, relaxIdleMax); //chill a random bit then move again
                }
                break;
            case GuardState.Suspicious:
                if (HearsNoise())
                {
                    suspicionTimer += Runner.DeltaTime;
                    if(suspicionTimer > alertConfirmTime)
                    {
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

                            ChangeState(GuardState.Asleep);
                        }
                        else
                        {

                            ChangeState(GuardState.Relaxed);
                        }
                    }
                }
                    break;  
            case GuardState.Searching:
                Player loudestVisiblePlayer = null;
                float loudestNoiseHeard = -1f; //start below zero so a silent player can still be seen
                float perceivedNoise = LoudestPerceivedNoise();
                foreach (Player player in Player.ActivePlayers) //live list, so players who joined after the guard spawned still count
                {
                    if (player.IsEliminated) continue; //don't hunt players who are already out
                    if (CanSeePlayer(player) && player.NoiseLevel > loudestNoiseHeard) //can see them and louder than the current best
                    {
                        loudestNoiseHeard = player.NoiseLevel;//set only if we hear a noise louder then the previous loudest noise
                        loudestVisiblePlayer = player; 
                    }
                }
                if (loudestVisiblePlayer != null)
                {
                    chaseTarget = loudestVisiblePlayer;
                    ChangeState(GuardState.Chasing);
                }
                if (perceivedNoise >= searchNoiseReactThreshold && searchNoiseReactionTimer <= 0f)
                {
                    agent.SetDestination(lastKnownPosition);
                    searchNoiseReactionTimer = searchNoiseReactionCooldown;
                }

                if (!agent.pathPending && agent.remainingDistance <= reachDistance)
                {
                    searchSweepWaitTimer += Runner.DeltaTime;

                    if (searchSweepWaitTimer >= searchSweepWaitTime)
                    {
                        searchSweepWaitTimer = 0f;
                        searchSweepPointsChecked++;

                        if (searchSweepPointsChecked >= maximumSearchSweepPoints)
                        {
                            Debug.Log("Guard: Must have been nothing...");
                            ChangeState(GuardState.Relaxed);
                        }
                        else
                        {
                            PickRandomSearchPoint(transform.position);
                            Debug.Log("Picked tracking point");
                        }
                    }
                }
                break;
            case GuardState.Chasing:
                if (chaseTarget == null) //target left mid chase, fall back to searching
                {
                    ChangeState(GuardState.Searching);
                    break;
                }
                Vector3 toTarget = chaseTarget.transform.position - transform.position;
                toTarget.y = 0f; //ignore height difference
                float distanceToTarget = toTarget.magnitude;

                if (CanSeePlayer(chaseTarget) || distanceToTarget < chaseSenseRange) //see the target or close enough to sense them
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
                if (chaseTarget != null) //target might have left before we grabbed them
                {
                    if (Anger >= angerEliminateThreshold) //only eliminates once he's angry enough, otherwise it's just a warning
                    {
                        chaseTarget.RPC_GetCaught();
                        if (RunManager.Instance != null) RunManager.Instance.OnPlayerCaught(); //tell the run tracker one player is out
                    }
                }
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
                searchSweepWaitTimer = 0f;
                searchSweepPointsChecked = 0;
                suspicionTimer = alertConfirmTime * 0.5f;   // wakes up already half upset
                Anger = Mathf.Min(angerMax, Anger + angerPerAlert); //every fresh alert winds him up
                PlayStateSound(newState); //bark whenever he changes state
                break;
            case GuardState.Searching:

                agent.speed = searchSpeed;
                agent.SetDestination(lastKnownPosition);
                searchNoiseReactionTimer = 0f;
                searchSweepWaitTimer = 0f;
                searchSweepPointsChecked = 0;
                PlayStateSound(newState); //bark whenever he changes state
                break;
            case GuardState.Chasing:
                agent.speed = chaseSpeed;
                Anger = Mathf.Min(angerMax, Anger + angerPerAlert); //spotting a player really sets him off
                PlayStateSound(newState); //bark whenever he changes state
                break;
            case GuardState.Caught:
                PlayStateSound(newState); //bark whenever he changes state
                break; 
        }
    }

    private void TickAnger() //anger builds while he's actively chasing and slowly cools while he's calm
    {
        if (State == GuardState.Chasing)
            Anger = Mathf.Min(angerMax, Anger + angerChaseRate * Runner.DeltaTime);
        else if (State == GuardState.Asleep || State == GuardState.Relaxed)
            Anger = Mathf.Max(0f, Anger - angerDecayRate * Runner.DeltaTime);
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
    private bool IsAtSpawn()
    {
        return Vector3.Distance(transform.position, spawnPosition) < 0.3f;
    }
    private void ReturnToSleep()
    {
        if (!IsAtSpawn() && !agent.hasPath) //walk home only if not there and not already heading somewhere
        {
            agent.SetDestination(spawnPosition);
        }
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
        foreach (Player player in Player.ActivePlayers) //live list, so players who joined after the guard spawned still count
        {
            if (player.IsEliminated) continue; //eliminated players make no noise the guard cares about
            float distanceToPlayer = Vector3.Distance(transform.position, player.transform.position);
            float distanceFactor = Mathf.Clamp01((noiseRange - distanceToPlayer) / noiseRange); //either in range or not
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
        if(barkCooldownTimer > 0f)
        {
            return;
        }
        AudioClip[] clips = ClipsFor(state);
        if (clips != null && clips.Length > 0)
        {
            int index = Random.Range(0, clips.Length); //chosen once on the host
            barkCooldownTimer = barkCooldown;
            RPC_PlayStateSound(index, state);
        }
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_PlayStateSound(int index, GuardState state)
    {
        AudioClip[] clips = ClipsFor(state); //clips from the state being active
        if (voiceSource != null && clips != null && index >= 0 && index < clips.Length) //if voice not nll and we have clips with an index of clips, 
        {
            voiceSource.Stop();          //cut any bark still playing so two voices never overlap
            voiceSource.clip = clips[index]; //choose a clip to play based off index
            voiceSource.Play(); //play sound
        }
    }
    private void PickRandomSearchPoint(Vector3 searchCenter)
    {
        Vector2 randomCircle = Random.insideUnitCircle * searchSweepRadius;
        Vector3 randomPosition = searchCenter + new Vector3(randomCircle.x, 0f, randomCircle.y);

        if(NavMesh.SamplePosition(randomPosition, out NavMeshHit hit, searchSweepRadius, NavMesh.AllAreas)) // if that position hits the mesh
        {
            agent.SetDestination(hit.position); //position the guard chose on the random circle
        }
    }
}