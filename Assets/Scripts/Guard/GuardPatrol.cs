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

    private float noiseThreshold; //runtime working value - rolled in Spawned between wakeThresholdMin/Max, then sharpened by loot theft. tune the RANGE below, not this
    [SerializeField] private float wakeThresholdMin = 3f; //accumulated noise needed to wake him, randomized in this range so it cant be memorized. lower = twitchier
    [SerializeField] private float wakeThresholdMax = 6f;
    [SerializeField] private float catchRange = 2f; //how close he has to get to grab you
    [SerializeField] private float chaseSenseRange = 5f; //how close the target must be for him to keep sensing them WITHOUT line of sight - chase "stickiness". bigger = harder to lose him
    [SerializeField] private float doorOpenRange = 1.8f; //how close he gets before shoving a shut door open. roughly arm's reach - too big and doors fly open before he's there
    [SerializeField] private float patrolRadius = 25f;          //how far from his bed he'll wander while Relaxed. make this comfortably cover the house or he'll never visit the far rooms
    [SerializeField] private float minPatrolStepDistance = 4f;  //a wander spot closer than this is rejected, so he takes real walks instead of shuffling on the spot
    private float noiseDrainRate = 1.5f; //how fast the bucket empties when it's quiet
    private float noiseMemoryTime = 2f; //hold the bucket this long after the last noise before draining
    private float quietTimer; //how long it's been quiet since the last noise
    [SerializeField] private float alertConfirmTime = 1.5f;   // must stay suspicious this long before he commits to searching. lower = jumps to search faster
    private float noiseAccumulator;
    private float suspicionTimer; //how long the guard is sus after in suspicion state
    private Player chaseTarget;//last seen player
    private Player escortTarget; //who he's currently dragging to the closet
    private Vector3 lastKnownPosition; //playerpos
    private int asleepChances; //how many times the guard relaxes before he perma suspicious
    private int asleepChancesMax = 3; //false alarms he shrugs off before he stays permanently alert (never drops fully back to sleep)
    private float relaxPatrolTimer;
    private float relaxIdleMin = 3f;
    private float relaxIdleMax = 8f;

    private NavMeshAgent agent; //guard
    private float reachDistance = 0.5f; //technical nav constant - how close counts as "arrived" at a waypoint. hidden from the tuning panel (say the word to bring it back)
    private Transform closetSpot; //closet is a scene object, handed over by the spawner at spawn (a prefab can't hold a scene ref - same reason as waypoints)

    private GuardVision vision; //reusable sight component on the same GameObject - config (range/fov/eye/mask) lives there now so the dog can reuse it
    private GuardHearing hearing; //reusable ears component - noise perception config (range/threshold) lives there

    private GuardAudio guardAudio; //reusable voice component - AudioSource + bark clips + the networked bark RPC live there

    private float searchSweepRadius = 6f;
    [SerializeField] private int maximumSearchSweepPoints = 3; //spots he checks before giving up a search - bigger = he hunts longer
    private float searchSweepWaitTime = 1.25f;

    private int searchSweepPointsChecked;
    private float searchSweepWaitTimer;

    private float searchNoiseReactThreshold = 1.5f;
    private float searchNoiseReactionCooldown = 2f;
    private float searchNoiseReactionTimer;

    private Vector3 spawnPosition; //runtime - set to where he spawns in Spawned; not a tuning value

    private Vector3 lastTargetPosition; //used to estimate the chase target's velocity for predictive pursuit
    private Vector3 targetVelocity;
    private float predictionLeadTime = 0.3f; //seconds ahead of the target's velocity the guard aims for - cuts corners instead of tailing exactly

    private float lastReactedLootPercent; //how much of the house's value he's already gotten rattled about
    private float lootSuspicionStep = 0.25f; //every time another quarter of the house's total value goes missing, he notices
    private float noiseThresholdDropPerLootMilestone = 0.75f; //each milestone permanently sharpens his ears for the rest of the run - he's on edge now

    private readonly List<Vector3> searchPointsThisSweep = new List<Vector3>(); //spots already checked this search - keeps the sweep spreading out instead of clustering by chance
    private float minSearchPointSpacing = 2f; //a new random search point must be at least this far from ones already checked

    private float sweepLookRange = 45f; //how far left/right of center he turns while waiting at a search point
    private float sweepLookSpeed = 90f;  //degrees per second the look oscillates back and forth
    private bool isSweepingSearchPoint;
    private float sweepBaseYaw;    //the direction he was facing when he arrived - the look oscillates around this, not world-forward
    private float sweepPhaseTimer;

    private int floorboardCreaksToInvestigate = 4; //a single creak is ignored - he only comes to look after this many
    private float floorboardCreakWindow = 6f;      //creaks must keep coming within this window; if they stop, the count fades and he shrugs it off
    private int floorboardCreakCount;
    private float floorboardCreakTimer;
    private Vector3 lastFloorboardCreakPosition;

    [Header("Movement speeds")]
    [SerializeField] private float relaxSpeed = 1.5f;   //strolling on patrol
    [SerializeField] private float searchSpeed = 3.5f;  //investigating a noise
    [SerializeField] private float chaseSpeed = 6.5f;   //player walks 7, so a sprintless player only just outruns him - THE main chase-feel lever
    [SerializeField] private float escortSpeed = 2.5f;  //walk pace while hauling someone to the closet

    private float angerMax = 100f;
    private float angerPerAlert = 20f;           //bump each time he escalates to a fresh alert
    private float angerChaseRate = 12f;          //builds per second while actively chasing
    private float angerDecayRate = 5f;           //cools per second while calm
    [SerializeField] private float angerEliminateThreshold = 60f; //at/above this, a catch eliminates you for the run instead of just jailing you. higher = more forgiving
    [Networked] public float Anger { get; private set; }          //how riled up he is; host-owned, readable for a future HUD


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

        //every scene load despawns and respawns him, so a fresh roll here would mean walking out an exit door and
        //back in handed the players a brand-new guard with no anger and factory-reset ears. carry his mood over
        //instead; RunManager only clears it when a genuinely new run begins.
        if (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid
            && RunManager.Instance.HasSavedGuardState)
        {
            Anger = RunManager.Instance.SavedGuardAnger;
            noiseThreshold = RunManager.Instance.SavedGuardNoiseThreshold;
            asleepChances = RunManager.Instance.SavedGuardAsleepChances;
        }
        else
        {
            noiseThreshold = Random.Range(wakeThresholdMin, wakeThresholdMax); //random wake threshold so players cant memorize the exact amount
        }
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
        OpenDoorInMyWay(); //shove open any shut door he walks into, so he isn't clipping through solid doors

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
                if (relaxPatrolTimer <= 0f && !agent.pathPending && agent.remainingDistance <= reachDistance)
                {
                    PickWanderPoint(); //no fixed route at all - he drifts to random reachable spots, so there's nothing to memorise
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
                if (chaseTarget == null || chaseTarget.IsHiding || chaseTarget.IsLockedUp || chaseTarget.IsEliminated) //target left, hid, or is already caught (jailed/out) - stop chasing so he never re-grabs or re-eliminates someone he's already dealt with
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
                    if (RunManager.Instance != null) RunManager.Instance.OnPlayerCaught(chaseTarget.Object.InputAuthority); //tell the run tracker this specific player is out
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
    public override void Despawned(NetworkRunner runner, bool hasState) //he's despawned on every scene change, so this is where his mood gets banked for the trip
    {
        if (Instance == this)
        {
            Instance = null; //don't leave sensors pinging a destroyed guard
        }
        if (!hasState || !HasStateAuthority) return; //hasState false = the session is tearing down, nothing worth saving

        if (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        {
            RunManager.Instance.SavedGuardAnger = Anger;
            RunManager.Instance.SavedGuardNoiseThreshold = noiseThreshold;
            RunManager.Instance.SavedGuardAsleepChances = asleepChances;
            RunManager.Instance.HasSavedGuardState = true;
        }
    }

    public void SetCloset(Transform spot)
    {
        closetSpot = spot; //closet lives in the scene, handed over by the spawner at spawn (a prefab can't hold a scene ref)
    }

    public void AlertTo(Vector3 spot) //any sensor (squeaky toy, camera, the dog barking at a door) pings this to send the guard to investigate a spot. he never passes the alert on to the dog - they hunt independently
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
                //the guard deliberately does NOT summon the dog. one mistake pulling BOTH threats onto you spent all
                //the tension at once and made a single creak feel like a death sentence. they hunt independently now -
                //the dog can still fetch the guard (that's its whole job), but never the other way round.
                break;
            case GuardState.Chasing:
                agent.speed = chaseSpeed;
                Anger = Mathf.Min(angerMax, Anger + angerPerAlert); //spotting a player really sets him off
                if (chaseTarget != null)
                {
                    lastTargetPosition = chaseTarget.transform.position; //reset so the first velocity sample isn't computed against stale data from a previous chase
                    targetVelocity = Vector3.zero;
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

    private void OpenDoorInMyWay() //he walks the NavMesh, which ignores door colliders - so without this he'd stroll straight THROUGH a shut door. now he pushes it open and walks through the gap like a person
    {
        if (RunManager.Instance == null) return;
        if (State == GuardState.Asleep) return; //dead asleep at his post - doors drifting open on their own would look haunted

        Door shutDoor = Door.FindClosedDoorNear(transform.position, doorOpenRange);
        if (shutDoor == null) return;

        //only shove open a door he's actually walking INTO. without this he flings open every door he passes in a
        //hallway, which both looks mad and hands the players a lit-up trail of where he's been.
        Vector3 towardDoor = shutDoor.transform.position - transform.position;
        towardDoor.y = 0f;
        if (Vector3.Dot(transform.forward, towardDoor.normalized) < 0.5f) return; //not roughly ahead of him (~60 degree cone)

        shutDoor.SetOpen(true);                                                  //open it here immediately so we stop re-detecting it next tick
        RunManager.Instance.RPC_SetDoorOpen(shutDoor.transform.position, true);  //and tell every other client to swing their copy
        //deliberately never closed behind him - a door left open is a free tell to the players that he came through here
    }

    private void CheckForMissingLoot() //a new sensing mode: notices his valuables disappearing even with zero noise or sightings - ties threat directly to how much you've stolen
    {
        if (RunManager.Instance == null || RunManager.Instance.HouseLootTotal <= 0) return;
        if (State != GuardState.Asleep && State != GuardState.Relaxed) return; //only the calm states get spooked by this - an already-alert guard doesn't need extra help

        float lootPercentTaken = RunManager.Instance.GatheredLootValue / (float)RunManager.Instance.HouseLootTotal;
        if (lootPercentTaken - lastReactedLootPercent >= lootSuspicionStep)
        {
            lastReactedLootPercent = lootPercentTaken;
            noiseThreshold = Mathf.Max(1f, noiseThreshold - noiseThresholdDropPerLootMilestone); //getting nervous permanently sharpens his ears

            //go straight to Searching, NOT Suspicious - Suspicious drains back to sleep when there's no noise, so a silent looter would never trigger a real search (the whole point of this sensor).
            //send him to where the freshest theft actually happened, so he investigates the real crime scene instead of a random old spot.
            lastKnownPosition = RunManager.Instance.LastStolenPosition;
            ChangeState(GuardState.Searching); //"...wait, where's my silverware?" - and now he actually goes looking
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

    private void PickWanderPoint() //no waypoint list: he strolls to random reachable spots around the house instead
    {
        //sampling from his SPAWN rather than his current position stops him drifting into a far corner and staying
        //there - the bed stays the centre of gravity, so he keeps circulating through the house he's guarding.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Vector2 randomCircle = Random.insideUnitCircle * patrolRadius;
            Vector3 candidate = spawnPosition + new Vector3(randomCircle.x, 0f, randomCircle.y);

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, patrolRadius, NavMesh.AllAreas)) continue;
            if (Vector3.Distance(hit.position, transform.position) < minPatrolStepDistance) continue; //too close to be worth walking to

            //make sure he can actually GET there. an unreachable point leaves him walking into a wall forever,
            //because remainingDistance never drops under reachDistance and the Relaxed tick never picks a new spot.
            NavMeshPath path = new NavMeshPath();
            if (!agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete) continue;

            agent.SetPath(path);
            return;
        }
        //nothing valid found this time - just idle, the timer will bring us straight back here
    }

}