using System.Collections.Generic;
using UnityEngine;
using Fusion;
using UnityEngine.AI;

public class GuardPatrol : NetworkBehaviour
{
    public enum GuardState { Asleep, Relaxed, Suspicious, Searching, Chasing, Caught, Escorting }
    //
    public static GuardPatrol Instance; //the master's guard, so sensors (toys, cameras, ...) can ping it without needing a scene reference
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

    private Vector3 lastTargetPosition; //used to estimate the chase target's velocity for predictive pursuit
    private Vector3 targetVelocity;
    [SerializeField] private float predictionLeadTime = 0.3f; //seconds ahead of the target's velocity the guard aims for - cuts corners instead of tailing exactly

    private float lastReactedLootPercent; //how much of the house's value he's already gotten rattled about
    [SerializeField] private float lootSuspicionStep = 0.25f; //every time another quarter of the house's total value goes missing, he notices
    [SerializeField] private float noiseThresholdDropPerLootMilestone = 0.75f; //each milestone permanently sharpens his ears for the rest of the run - he's on edge now

    private readonly List<Vector3> searchPointsThisSweep = new List<Vector3>(); //spots already checked this search - keeps the sweep spreading out instead of clustering by chance
    [SerializeField] private float minSearchPointSpacing = 2f; //a new random search point must be at least this far from ones already checked

    [SerializeField] private float sweepLookRange = 45f; //how far left/right of center he turns while waiting at a search point
    [SerializeField] private float sweepLookSpeed = 90f;  //degrees per second the look oscillates back and forth
    private bool isSweepingSearchPoint;
    private float sweepBaseYaw;    //the direction he was facing when he arrived - the look oscillates around this, not world-forward
    private float sweepPhaseTimer;

    [SerializeField] private int floorboardCreaksToInvestigate = 4; //a single creak is ignored - he only comes to look after this many
    [SerializeField] private float floorboardCreakWindow = 6f;      //creaks must keep coming within this window; if they stop, the count fades and he shrugs it off
    private int floorboardCreakCount;
    private float floorboardCreakTimer;
    private Vector3 lastFloorboardCreakPosition;

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
        Instance = this; //only the master's guard - sensors ping this one
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
        CheckForMissingLoot(); //a new sensing mode - notices his stuff is missing even in total silence

        if (floorboardCreakCount > 0) //let the creak count fade if the creaking stops
        {
            floorboardCreakTimer -= Runner.DeltaTime;
            if (floorboardCreakTimer <= 0f) floorboardCreakCount = 0;
        }

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
                    PickNextWaypoint(); //random non-repeating pick instead of a fixed cycle - a memorized loop is a stealth game's biggest exploit
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
                    if (!isSweepingSearchPoint) //just arrived - lock in facing direction and take manual control of rotation while we look around
                    {
                        isSweepingSearchPoint = true;
                        agent.updateRotation = false; //nothing to path toward right now anyway, so this can't fight the agent's own auto-rotate
                        sweepBaseYaw = transform.eulerAngles.y;
                        sweepPhaseTimer = 0f;
                    }
                    SweepLookAround(); //turn his head left/right instead of standing frozen while he checks the spot

                    searchSweepWaitTimer += Runner.DeltaTime;

                    if (searchSweepWaitTimer >= searchSweepWaitTime)
                    {
                        searchSweepWaitTimer = 0f;
                        isSweepingSearchPoint = false;
                        agent.updateRotation = true; //hand facing back to the agent now that he's about to move again
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
                    Vector3 currentTargetPosition = chaseTarget.transform.position;
                    targetVelocity = (currentTargetPosition - lastTargetPosition) / Runner.DeltaTime; //crude per-tick velocity estimate
                    lastTargetPosition = currentTargetPosition;

                    lastKnownPosition = currentTargetPosition; //still the real last-seen spot, used if we lose the target entirely
                    Vector3 predictedPosition = currentTargetPosition + targetVelocity * predictionLeadTime; //aim a little ahead instead of exactly where they are right now
                    agent.SetDestination(predictedPosition);
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

    public void AlertTo(Vector3 spot) //any sensor (squeaky toy, camera, ...) pings this to send the guard to investigate a spot
    {
        if (!HasStateAuthority) return; //only the master drives the guard
        if (State == GuardState.Chasing || State == GuardState.Caught || State == GuardState.Escorting) return; //never override an active chase/capture
        lastKnownPosition = spot; //a newer alert just overwrites this, so a later toy overrides an earlier one
        ChangeState(GuardState.Searching); //walks there, sweeps, chases if he spots someone, gives up to Relaxed if nothing
    }

    public void RegisterFloorboardCreak(Vector3 spot) //floorboards don't alert on a single creak - it takes several in a row before he bothers to come look
    {
        if (!HasStateAuthority) return;
        if (State == GuardState.Chasing || State == GuardState.Caught || State == GuardState.Escorting) return; //busy - ignore creaks

        lastFloorboardCreakPosition = spot;
        floorboardCreakCount++;
        floorboardCreakTimer = floorboardCreakWindow; //keep the window open as long as creaks keep coming

        if (floorboardCreakCount >= floorboardCreaksToInvestigate)
        {
            floorboardCreakCount = 0;
            AlertTo(lastFloorboardCreakPosition); //enough creaking - go check the last one
        }
    }

    private void ChangeState(GuardState newState) //single place to switch states so timers/counters always reset on entry
    {
        State = newState;

        //default rotation control back to the agent on every transition - Searching re-claims it manually, but only while actually idle at a sweep point.
        //covers spotting a player mid-sweep (Searching -> Chasing without ever reaching the sweep-timeout code below)
        agent.updateRotation = true;
        isSweepingSearchPoint = false;

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
                searchPointsThisSweep.Clear(); //fresh search - forget spots checked on a previous alert
                searchPointsThisSweep.Add(lastKnownPosition); //count the first spot he's heading to so later sweep points don't cluster around it either
                PlayStateSound(newState); //bark whenever he changes state
                if (DogAI.Instance != null) DogAI.Instance.AlertTo(lastKnownPosition); //a confirmed noise pulls the dog toward it too
                break;
            case GuardState.Chasing:
                agent.speed = chaseSpeed;
                Anger = Mathf.Min(angerMax, Anger + angerPerAlert); //spotting a player really sets him off
                if (chaseTarget != null)
                {
                    lastTargetPosition = chaseTarget.transform.position; //reset so the first velocity sample isn't computed against stale data from a previous chase
                    targetVelocity = Vector3.zero;
                    if (DogAI.Instance != null) DogAI.Instance.AlertTo(chaseTarget.transform.position); //a confirmed sighting pulls the dog in too - two threats converging is meaningfully scarier
                }
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

    private void CheckForMissingLoot() //a new sensing mode: notices his valuables disappearing even with zero noise or sightings - ties threat directly to how much you've stolen
    {
        if (RunManager.Instance == null || RunManager.Instance.totalLootValue <= 0) return;
        if (State != GuardState.Asleep && State != GuardState.Relaxed) return; //only the calm states get spooked by this - an already-alert guard doesn't need extra help

        float lootPercentTaken = RunManager.Instance.GatheredLootValue / (float)RunManager.Instance.totalLootValue;
        if (lootPercentTaken - lastReactedLootPercent >= lootSuspicionStep)
        {
            lastReactedLootPercent = lootPercentTaken;
            noiseThreshold = Mathf.Max(1f, noiseThreshold - noiseThresholdDropPerLootMilestone); //getting nervous permanently sharpens his ears

            //go straight to Searching, NOT Suspicious - Suspicious drains back to sleep when there's no noise, so a silent looter would never trigger a real search (the whole point of this sensor).
            //point him at an emptied container so he actually investigates the scene of the crime instead of sweeping a random old spot.
            lastKnownPosition = NearestLootedItemPosition();
            ChangeState(GuardState.Searching); //"...wait, where's my silverware?" - and now he actually goes looking
        }
    }

    private Vector3 NearestLootedItemPosition() //find an already-taken container to send the guard to - the closest one reads as him noticing the freshest theft
    {
        Vector3 nearest = transform.position; //fallback: search around himself if we somehow can't find a looted item
        float nearestDistance = float.MaxValue;
        foreach (Lootable lootable in Lootable.AllLootables)
        {
            if (!lootable.IsLooted) continue;
            float distance = Vector3.Distance(transform.position, lootable.transform.position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = lootable.transform.position;
            }
        }
        return nearest;
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
    private void SweepLookAround() //oscillates his facing left/right around the direction he arrived, instead of standing frozen while he checks a search point
    {
        sweepPhaseTimer += Runner.DeltaTime;
        float sweepAngle = Mathf.Sin(sweepPhaseTimer * sweepLookSpeed * Mathf.Deg2Rad) * sweepLookRange; //smooth back-and-forth between -sweepLookRange and +sweepLookRange
        transform.rotation = Quaternion.Euler(0f, sweepBaseYaw + sweepAngle, 0f);
    }

    private void PickRandomSearchPoint(Vector3 searchCenter) //picks a spot to check, biased away from ones already checked this search so the sweep spreads out instead of clustering by chance
    {
        Vector3 bestCandidate = searchCenter;
        bool foundCandidate = false;

        for (int attempt = 0; attempt < 5; attempt++) //a few tries to find a spot that isn't right on top of ground he's already covered
        {
            Vector2 randomCircle = Random.insideUnitCircle * searchSweepRadius;
            Vector3 randomPosition = searchCenter + new Vector3(randomCircle.x, 0f, randomCircle.y);
            if (!NavMesh.SamplePosition(randomPosition, out NavMeshHit hit, searchSweepRadius, NavMesh.AllAreas)) continue;

            bestCandidate = hit.position;
            foundCandidate = true;

            bool tooClose = false;
            foreach (Vector3 checkedPoint in searchPointsThisSweep)
            {
                if (Vector3.Distance(hit.position, checkedPoint) < minSearchPointSpacing)
                {
                    tooClose = true;
                    break;
                }
            }
            if (!tooClose) break; //well-spaced spot found, stop looking
        }

        if (foundCandidate)
        {
            searchPointsThisSweep.Add(bestCandidate);
            agent.SetDestination(bestCandidate);
        }
    }

    private void PickNextWaypoint() //random non-repeating pick instead of a fixed cycle, so the patrol route can't be memorized
    {
        if (waypoints.Length == 1)
        {
            currentWaypoint = 0;
            agent.SetDestination(waypoints[0].position);
            return;
        }

        int nextWaypoint;
        do
        {
            nextWaypoint = Random.Range(0, waypoints.Length);
        } while (nextWaypoint == currentWaypoint); //never immediately repeat the spot we're already at

        currentWaypoint = nextWaypoint;
        agent.SetDestination(waypoints[currentWaypoint].position);
    }
}