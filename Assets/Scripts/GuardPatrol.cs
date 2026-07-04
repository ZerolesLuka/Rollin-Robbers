using UnityEngine;
using Fusion;
using UnityEngine.AI;

public class GuardPatrol : NetworkBehaviour
{
    public enum GuardState { Asleep, Relaxed, Suspicious, Searching, Chasing, Caught, Escorting }
    //
    [Networked] public GuardState State { get; private set; } //the guard's current state

    [SerializeField] private float noiseThreshold; //random wake threshold, rolled in Spawned
    [SerializeField] private float catchRange = 2f;
    private float chaseSenseRange = 5f;
    [SerializeField] private float noiseDrainRate = 1.5f; //how fast the bucket empties when it's quiet
    [SerializeField] private float noiseMemoryTime = 2f; //hold the bucket this long after the last noise before draining
    private float quietTimer; //how long it's been quiet since the last noise
    private float alertConfirmTime = 1.5f;   // must stay suspicious this long before searching
    private float noiseAccumulator;
    private float suspicionTimer; //how long the guard is sus after in suspicion state
    private Player chaseTarget;//last seen player
    private Player escortTarget; //who he's currently dragging to the closet
    private Vector3 lastKnownPosition; //playerpos
    private int asleepChances; //how many times the guard relaxes before he perma suspicious
    private int asleepChancesMax = 3;
    private float relaxPatrolTimer;
    [SerializeField] private float relaxIdleMin = 3f;
    [SerializeField] private float relaxIdleMax = 8f;

    private NavMeshAgent agent; //guard
    private Transform[] waypoints; //guard patrol
    [SerializeField] private float reachDistance = 0.5f; //distance of which the guard consider it has reached waypoint
    private Transform closetSpot; //closet is a scene object, handed over by the spawner at spawn (a prefab can't hold a scene ref - same reason as waypoints)

    private GuardVision vision; //reusable sight component on the same GameObject - config (range/fov/eye/mask) lives there now so the dog can reuse it
    private GuardHearing hearing; //reusable ears component - noise perception config (range/threshold) lives there

    private GuardAudio guardAudio; //reusable voice component - AudioSource + bark clips + the networked bark RPC live there

    [SerializeField] private float searchSweepRadius = 6f;
    [SerializeField] private int maximumSearchSweepPoints = 3;
    [SerializeField] private float searchSweepWaitTime = 1.25f;

    private int searchSweepPointsChecked;
    private float searchSweepWaitTimer;

    [SerializeField] private float searchNoiseReactThreshold = 1.5f;
    [SerializeField] private float searchNoiseReactionCooldown = 2f;
    private float searchNoiseReactionTimer;

    [SerializeField] private Vector3 spawnPosition;

    private float relaxSpeed = 1.5f;
    private float searchSpeed = 3.5f;
    private float chaseSpeed = 6.5f;
    private float escortSpeed = 2.5f; //walk pace while hauling someone to the closet

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
        vision = GetComponent<GuardVision>(); //reusable sight component sits on the same GameObject
        hearing = GetComponent<GuardHearing>(); //reusable ears component sits on the same GameObject
        guardAudio = GetComponent<GuardAudio>(); //reusable voice component sits on the same GameObject
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
                GuardHearing.Heard heardWhileSearching = hearing.LoudestNoise();
                float perceivedNoise = heardWhileSearching.loudness;
                if (perceivedNoise > 0f)
                {
                    lastKnownPosition = heardWhileSearching.position; //keep tracking the loudest noise source (side-effect the old method used to do)
                }
                foreach (Player player in Player.ActivePlayers) //live list, so players who joined after the guard spawned still count
                {
                    if (player.IsEliminated) continue; //don't hunt players who are already out
                    if (vision.CanSee(player.transform) && player.NoiseLevel > loudestNoiseHeard) //can see them and louder than the current best
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
                            ChangeState(GuardState.Relaxed);
                        }
                        else
                        {
                            PickRandomSearchPoint(transform.position);
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

                if (vision.CanSee(chaseTarget.transform) || distanceToTarget < chaseSenseRange) //see the target or close enough to sense them
                {
                    lastKnownPosition = chaseTarget.transform.position;
                    agent.SetDestination(lastKnownPosition);
                }
                if (distanceToTarget < catchRange)
                {
                    ChangeState(GuardState.Caught);
                }
                else if (!vision.CanSee(chaseTarget.transform) && distanceToTarget > chaseSenseRange && !agent.pathPending && agent.remainingDistance <= reachDistance)
                {
                    ChangeState(GuardState.Searching);
                }
                break;
            case GuardState.Caught:
                if (chaseTarget == null) //target left before we grabbed them, nothing to do
                {
                    ChangeState(GuardState.Relaxed);
                    break;
                }
                if (Anger >= angerEliminateThreshold) //furious enough to throw them out for the run
                {
                    chaseTarget.RPC_GetCaught();
                    if (RunManager.Instance != null) RunManager.Instance.OnPlayerCaught(); //tell the run tracker one player is out
                    ChangeState(GuardState.Relaxed);
                }
                else //just annoyed - drag them off to the closet instead of eliminating
                {
                    escortTarget = chaseTarget;
                    ChangeState(GuardState.Escorting);
                }
                break;
            case GuardState.Escorting:
                if (escortTarget == null) //they disconnected mid-drag, give up
                {
                    ChangeState(GuardState.Relaxed);
                    break;
                }
                if (!agent.pathPending && agent.remainingDistance <= reachDistance) //reached the closet with them in tow
                {
                    escortTarget.RPC_GetLockedUp(closetSpot.position); //stuff them in and lock the door
                    escortTarget = null;
                    ChangeState(GuardState.Relaxed);
                }
                break;
        }
        transform.position = agent.nextPosition; //apply the agent's steering ON the tick - same clock as the player, no NetworkTransform tug-of-war
    }
    public void SetWaypoints(Transform[] points)
    {
        waypoints = points; //set the waypoints from the spawner, since we cant set them in the inspector for the guard prefab
    }

    public void SetCloset(Transform spot)
    {
        closetSpot = spot; //closet lives in the scene, handed over by the spawner like the waypoints
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
            case GuardState.Escorting:
                agent.speed = escortSpeed;
                agent.SetDestination(closetSpot.position); //head for the closet (Caught already barked "GOTCHA" on the way in)
                if (escortTarget != null)
                {
                    escortTarget.RPC_GetDragged(this); //tell them they're being hauled off - they pin behind us until we arrive
                }
                break;
        }
    }

    private void TickAnger() //anger builds while he's actively chasing and slowly cools while he's calm
    {
        if (State == GuardState.Chasing)
        {
            Anger = Mathf.Min(angerMax, Anger + angerChaseRate * Runner.DeltaTime);
        }
        else if (State == GuardState.Asleep || State == GuardState.Relaxed)
        {
            Anger = Mathf.Max(0f, Anger - angerDecayRate * Runner.DeltaTime);
        }
    }

    private bool HearsNoise() //is there any audible noise right now (reuses the same perception as Asleep)
    {
        return hearing.LoudestNoise().loudness > 0f;
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

    private void ListenForNoise() //shared ears for Asleep + Relaxed
    {
        GuardHearing.Heard heard = hearing.LoudestNoise();
        float perceivedNoise = heard.loudness;
        if (perceivedNoise > 0f)
        {
            lastKnownPosition = heard.position; //remember WHERE the noise came from (used when he escalates to Searching)
            quietTimer = 0f;
            noiseAccumulator += perceivedNoise * Runner.DeltaTime;
            if (noiseAccumulator >= noiseThreshold)
            {
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
    private void PlayStateSound(GuardState state) //map THIS archetype's state onto a generic bark; GuardAudio handles cooldown + network playback
    {
        switch (state)
        {
            case GuardState.Asleep: guardAudio.Bark(GuardAudio.BarkType.GiveUp); break;    //"musta been nothin'"
            case GuardState.Suspicious: guardAudio.Bark(GuardAudio.BarkType.Alert); break; //"huh? who's there?"
            case GuardState.Searching: guardAudio.Bark(GuardAudio.BarkType.Search); break; //"I know you're in here"
            case GuardState.Chasing: guardAudio.Bark(GuardAudio.BarkType.Chase); break;    //"HEY! GET OUT!"
            case GuardState.Caught: guardAudio.Bark(GuardAudio.BarkType.Caught); break;    //"GOTCHA!"
            //Relaxed / Escorting: no bark
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