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
    [SerializeField] private float wedgeBreakSecondsSameSide = 2f; //he can reach the wedge and kick it out
    [SerializeField] private float wedgeBreakSecondsFarSide = 5f;  //it's on the other side, so he has to force the door itself
    private Door breakingWedgeOnDoor;                              //the door he's currently working on, null when he isn't
    private float breakingWedgeTimer;                              //seconds left on it

    //TRAPS ONLY FOLLOW A REAL SIGHTING. lastKnownPosition is written by noise, squeaky toys, cameras and missing loot
    //as well as by eyes, so keying traps off it meant a creaky floorboard in an empty room got the place wired. these
    //two are set ONLY when he actually looks at a player, so a hunt he never saw anyone during ends with nothing.
    private bool sawPlayerThisHunt;
    private Vector3 lastSightingPosition;

    [SerializeField] private float doorOpenRange = 1.8f; //how close he gets before shoving a shut door open. roughly arm's reach - too big and doors fly open before he's there
    [SerializeField] private float patrolRadius = 25f;          //how far from his bed he'll wander while Relaxed. make this comfortably cover the house or he'll never visit the far rooms
    [SerializeField] private float minPatrolStepDistance = 4f;  //a wander spot closer than this is rejected, so he takes real walks instead of shuffling on the spot
    [SerializeField] private float wakeUpHoldTime = 1.5f;       //how long he's pinned in place after waking, so the standing-up animation can finish. set this to the LENGTH OF THAT CLIP - too short and he sprints off while still on the floor, too long and he's a sitting duck
    private float wakeUpHoldTimer;                              //counts down while he's climbing to his feet
    [SerializeField] private float hidingSearchRange = 3.5f;    //how close he has to be to hear someone talking inside a hiding spot and yank the door open
    [SerializeField] private float hidingNoiseTolerance = 5f;   //how loud you can be in there before he notices. keep it near GuardHearing's own threshold so whispering stays safe
    private bool sawTargetEnterHiding;                          //did we have eyes on the chase target the moment they dove into a spot? if so we know which one
    private int myRunGeneration;                                //which heist this guard instance belongs to - captured at spawn, stamped onto his saved mood at despawn
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
    private bool warnedAboutMissingCloset; //latch, so a level with no closet complains once instead of on every catch

    private GuardVision vision; //reusable sight component on the same GameObject - config (range/fov/eye/mask) lives there now so the dog can reuse it
    private GuardHearing hearing; //reusable ears component - noise perception config (range/threshold) lives there

    private GuardAudio guardAudio; //reusable voice component - AudioSource + bark clips + the networked bark RPC live there
    private GuardAnimation guardAnimation; //drives the Animator; we ask it whether the get-up clip is still running

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
    [SerializeField] private float turnSpeed = 540f; //degrees per second he turns to face his direction of travel. lower = he leans into corners, higher = snappier
    private Vector3 lastFacingPosition;              //where he was last tick, so we can work out which way he actually moved
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

    [Header("Traps he sets after losing sight of someone")]
    //one prefab per kind, and any of them can be left EMPTY - that kind simply never gets used. all three empty and
    //traps are off for this house entirely.
    [SerializeField] private NetworkObject tripwirePrefab;     //strung across a doorway
    [SerializeField] private NetworkObject bearTrapPrefab;     //dropped on open floor - the one that pins you
    [SerializeField] private NetworkObject alarmPrefab;        //wide radius, screams, brings the dog
    [SerializeField] private NetworkObject baitLootPrefab;     //a WorldItem prefab, NOT a GuardTrap - a fake valuable he leaves out. pays nothing and screams when lifted
    [SerializeField] private string baitItemName = "Jewellery";//what it claims to be. make it something you'd actually stop for
    [SerializeField] private int baitValueMin = 700;           //it has to LOOK worth the risk or nobody bites - this drives its rarity glow like any real item
    [SerializeField] private int baitValueMax = 1600;
    [SerializeField, Range(0f, 1f)] private float baitChance = 0.3f; //how often he plants bait INSTEAD of setting a trap. keep it a minority - bait only works while players still trust loot on sight
    [SerializeField] private float angerToSetTraps = 25f;      //he has to be rattled first. one quiet noise early on shouldn't start him wiring the place
    [SerializeField] private int maxTrapsPerRun = 5;           //everything he's carrying. also stops a long run turning the house into a minefield
    [SerializeField] private float trapPointSearchRange = 12f; //how far from where he lost you he'll walk a wire to. TrapPoints are placed by hand in the scene - see TrapPoint.cs
    [SerializeField] private float trapScatterRadius = 4f;     //how far from that spot a floor trap can end up. he's guessing, not tracking - this is the guess
    [SerializeField] private float minTrapSpacing = 3f;        //don't stack a second trap on ground he's already covered
    private int trapsSetThisRun;                               //runtime count, not a tuning value
    [Networked] public float Anger { get; private set; }          //how riled up he is; host-owned, readable for a future HUD


    public override void Spawned()
    {
        agent = GetComponent<NavMeshAgent>(); //grab it from our own GameObject so it's never null
        vision = GetComponent<GuardVision>(); //reusable sight component sits on the same GameObject
        hearing = GetComponent<GuardHearing>(); //reusable ears component sits on the same GameObject
        guardAudio = GetComponent<GuardAudio>(); //reusable voice component sits on the same GameObject
        guardAnimation = GetComponent<GuardAnimation>(); //optional - he still works headless, just without the clip-length hold
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
        //instead - but ONLY if it belongs to the run we're currently in.
        bool runManagerLive = RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid;
        myRunGeneration = runManagerLive ? RunManager.Instance.RunGeneration : 0; //remember which run WE live in, so our own Despawned can stamp the mood correctly

        //the generation check is what makes this safe: Fusion fires Despawned() a tick or more AFTER Runner.Despawn(),
        //so the old guard banks his mood AFTER ResetForNewRun has already cleared it. Comparing generations means a
        //mood from the previous heist simply doesn't match, no matter which order the two things happen in.
        if (runManagerLive && RunManager.Instance.HasSavedGuardState
            && RunManager.Instance.SavedGuardRunGeneration == RunManager.Instance.RunGeneration)
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
        agent.updateRotation = false; //and we turn him too - see FaceMovementDirection. the agent's own rotation aims at where it WANTS to go, which drifts from where we actually put him
        lastFacingPosition = transform.position;
        agent.Warp(transform.position); 
    }

    public override void FixedUpdateNetwork()
    {
        if (searchNoiseReactionTimer > 0f)
        {
            searchNoiseReactionTimer -= Runner.DeltaTime;
        }
        if (!HasStateAuthority) return; //only run for the state authority, which is the host in this case, so only the host will control the guard's movement

        //just woken - pin him in place until the standing-up animation has played out. without this he'd be pathing
        //off to investigate while the clip still has him flat on the floor, which reads as him sliding through the
        //ground. he's a free hit for a moment, which is a fair trade for it not looking broken.
        //
        //the timer covers the gap before the Animator has actually entered the get-up state (the bool takes a tick or
        //two to propagate); GuardAnimation then holds him for as long as the clip really runs. that way the clip
        //length is the source of truth and there's no magic number to keep in sync when the animation is swapped.
        bool stillGettingUp = guardAnimation != null && guardAnimation.IsGettingUp;
        if (wakeUpHoldTimer > 0f || stillGettingUp)
        {
            if (wakeUpHoldTimer > 0f)
            {
                wakeUpHoldTimer -= Runner.DeltaTime;
            }
            agent.ResetPath();                     //drop whatever destination woke him; he'll re-path once he's up
            //push the AGENT to us, not us to the agent. it used to be the other way round, and that was the bug:
            //his bed isn't on the NavMesh, so the agent sits on the floor beside it - and dragging the transform onto
            //the agent every tick slid him off the mattress the instant he started waking.
            agent.nextPosition = transform.position;
            return;
        }

        //working a wedge out of a door. he STANDS THERE doing it rather than jogging in place, so the animator's speed
        //blend falls to idle on its own and the delay reads as effort. anger keeps ticking, because being held up by
        //a door someone jammed is exactly the sort of thing that would wind him up.
        if (breakingWedgeTimer > 0f)
        {
            TickAnger();
            breakingWedgeTimer -= Runner.DeltaTime;
            agent.ResetPath();
            agent.nextPosition = transform.position; //hold the agent to us, same as the wake-up hold

            if (breakingWedgeTimer <= 0f && breakingWedgeOnDoor != null)
            {
                DoorWedge wedge = breakingWedgeOnDoor.Wedge;
                if (wedge != null && wedge.Object != null && wedge.Object.IsValid)
                {
                    Runner.Despawn(wedge.Object); //broken, and gone for good. nobody gets that wedge back
                }
                if (RunManager.Instance != null)
                {
                    RunManager.Instance.RPC_SetDoorOpen(breakingWedgeOnDoor.transform.position, true); //and through he comes
                }
                breakingWedgeOnDoor = null;
            }
            return;
        }

        TickAnger(); //rise while chasing, cool while calm
        CheckForMissingLoot(); //a new sensing mode - notices his stuff is missing even in total silence
        OpenDoorInMyWay(); //shove open any shut door he walks into, so he isn't clipping through solid doors
        CheckNoisyHidingSpots(); //someone running their mouth inside a wardrobe he's stood next to

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
                        //rotation is ours now (see FaceMovementDirection); isSweepingSearchPoint is what tells it to back off
                        sweepBaseYaw = transform.eulerAngles.y;
                        sweepPhaseTimer = 0f;
                    }
                    SweepLookAround(); //turn his head left/right instead of standing frozen while he checks the spot

                    searchSweepWaitTimer += Runner.DeltaTime;

                    if (searchSweepWaitTimer >= searchSweepWaitTime)
                    {
                        searchSweepWaitTimer = 0f;
                        isSweepingSearchPoint = false;
                        //done scanning - FaceMovementDirection takes over again as soon as he starts moving
                        searchSweepPointsChecked++;

                        if (searchSweepPointsChecked >= maximumSearchSweepPoints)
                        {
                            //giving up the hunt. he only wires the place if he ACTUALLY SAW somebody at some point -
                            //a search that started from a noise and turned up nothing gets no traps, because he has
                            //no idea whether there was ever anyone there. and he wires around where he SAW them, not
                            //where he's stood, so it stays a guess about your route rather than knowledge of it.
                            if (sawPlayerThisHunt)
                            {
                                TryPlaceTrapNear(lastSightingPosition);
                            }
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
                if (chaseTarget == null || chaseTarget.IsLockedUp || chaseTarget.IsEliminated) //target left or is already caught (jailed/out) - stop chasing so he never re-grabs someone he's already dealt with
                {
                    ChangeState(GuardState.Searching);
                    break;
                }

                //they dove into a hiding spot. if we had eyes on them the instant they did it we know exactly which
                //one, so keep coming and haul them out. break line of sight FIRST and it's a clean escape - that's
                //what makes losing him the actual skill, instead of the closet being a panic button.
                if (chaseTarget.IsHiding && !sawTargetEnterHiding)
                {
                    ChangeState(GuardState.Searching);
                    break;
                }

                Vector3 toTarget = chaseTarget.transform.position - transform.position;
                toTarget.y = 0f; //ignore height difference
                float distanceToTarget = toTarget.magnitude;

                bool canSeeTargetNow = vision.CanSee(chaseTarget.transform);
                if (canSeeTargetNow || distanceToTarget < chaseSenseRange || chaseTarget.IsHiding) //see them, close enough to sense them, or they're in a spot we watched them climb into
                {
                    Vector3 currentTargetPosition = chaseTarget.transform.position;
                    targetVelocity = (currentTargetPosition - lastTargetPosition) / Runner.DeltaTime; //crude per-tick velocity estimate
                    lastTargetPosition = currentTargetPosition;

                    lastKnownPosition = currentTargetPosition; //still the real last-seen spot, used if we lose the target entirely
                    if (canSeeTargetNow)
                    {
                        lastSightingPosition = currentTargetPosition; //EYES on them, not just sensed through a wall. this is the only thing traps are allowed to key off
                    }
                    Vector3 predictedPosition = currentTargetPosition + targetVelocity * predictionLeadTime; //aim a little ahead instead of exactly where they are right now
                    agent.SetDestination(chaseTarget.IsHiding ? currentTargetPosition : predictedPosition); //no point leading a target who's stood still in a wardrobe
                }
                if (distanceToTarget < catchRange)
                {
                    if (chaseTarget.IsHiding)
                    {
                        chaseTarget.RPC_PulledFromHiding(); //door yanked open - out you come, then the normal catch handles the rest
                    }
                    ChangeState(GuardState.Caught);
                }
                else if (!canSeeTargetNow && !chaseTarget.IsHiding && distanceToTarget > chaseSenseRange && !agent.pathPending && agent.remainingDistance <= reachDistance)
                {
                    ChangeState(GuardState.Searching);
                }

                //remember whether we can see them RIGHT NOW, so if they vanish into a spot next tick we know whether
                //we watched it happen. only meaningful while they're still out in the open.
                if (!chaseTarget.IsHiding)
                {
                    sawTargetEnterHiding = canSeeTargetNow;
                }
                break;
            case GuardState.Caught:
                if (chaseTarget == null) //target left before we grabbed them, nothing to do
                {
                    ChangeState(GuardState.Relaxed);
                    break;
                }
                //NO CLOSET IN THIS LEVEL = nowhere to drag anyone, so the drag is not an option and he throws them out
                //instead. This is checked HERE rather than inside the Escorting entry, because entering Escorting
                //without a closet dereferenced a null closetSpot in ChangeState - AFTER State had already flipped to
                //Escorting and BEFORE RPC_GetDragged fired. The guard froze in Escorting for the rest of the run
                //throwing an exception every tick at 32Hz, while the player he had just caught walked away never
                //having been told anything happened. Refusing to enter the state makes that unreachable.
                bool canDragThemOff = closetSpot != null;
                if (!canDragThemOff && !warnedAboutMissingCloset)
                {
                    warnedAboutMissingCloset = true; //once per guard, not once per tick
                    Debug.LogError("[Guard] No closetSpot assigned on GuardBootstrap, so there is nowhere to drag anyone - every catch is an elimination and the low-anger warning catch cannot happen at all. Assign it in the Indoor scene.", this);
                }

                if (Anger >= angerEliminateThreshold || !canDragThemOff) //furious enough to throw them out for the run
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
        FaceMovementDirection(); //and point him where he's actually going, not where the agent wishes it were going
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
            RunManager.Instance.SavedGuardRunGeneration = myRunGeneration; //stamp it with the run we LIVED in, captured at spawn - not the current one, which a new heist may already have bumped
            RunManager.Instance.HasSavedGuardState = true;
        }
    }

    public void SetCloset(Transform spot)
    {
        closetSpot = spot; //closet lives in the scene, handed over by the spawner at spawn (a prefab can't hold a scene ref)
    }

    //Runner.Despawn only QUEUES the despawn - Despawned() (which nulls Instance) runs a tick or more later. In that
    //window every sensor still sees a non-null Instance while the networked properties are already dead, and reading
    //State throws "Networked properties can only be accessed when Spawned() has been called". Every public entry
    //point checks this first.
    private bool IsLive => Object != null && Object.IsValid;

    public void AlertTo(Vector3 spot) //any sensor (squeaky toy, camera, the dog barking at a door) pings this to send the guard to investigate a spot. he never passes the alert on to the dog - they hunt independently
    {
        if (!IsLive) return; //despawned or mid-despawn - touching State here would throw
        if (!HasStateAuthority) return; //only the master drives the guard
        if (State == GuardState.Chasing || State == GuardState.Caught || State == GuardState.Escorting) return; //never override an active chase/capture
        lastKnownPosition = spot; //a newer alert just overwrites this, so a later toy overrides an earlier one
        ChangeState(GuardState.Searching); //walks there, sweeps, chases if he spots someone, gives up to Relaxed if nothing
    }

    public void RegisterFloorboardCreak(Vector3 spot) //floorboards don't alert on a single creak - it takes several in a row before he bothers to come look
    {
        if (!IsLive) return; //same mid-despawn guard as AlertTo
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
        //leaving Asleep means he's climbing off the floor - start the hold BEFORE State changes, since we need to
        //know what he was, not what he's becoming
        if (State == GuardState.Asleep && newState != GuardState.Asleep)
        {
            wakeUpHoldTimer = wakeUpHoldTime;
        }

        State = newState;

        //clear the manual scan on every transition, so FaceMovementDirection takes his facing back. covers spotting a
        //player mid-sweep (Searching -> Chasing without ever reaching the sweep-timeout code below)
        isSweepingSearchPoint = false;

        switch (newState)
        {
            case GuardState.Asleep:
                noiseAccumulator = 0f; //empty the bucket on every trip to sleep so he needs FRESH noise to wake
                quietTimer = 0f; //reset the quiet clock too
                agent.ResetPath();   //stop walking the stale search path instead of wandering around while "asleep"
                sawPlayerThisHunt = false; //hunt's over. the next one starts having seen nobody
                PlayStateSound(newState); //bark whenever he changes state

                break;
            case GuardState.Relaxed:
                agent.speed = relaxSpeed;
                noiseAccumulator = 0f;
                quietTimer = 0f;
                sawPlayerThisHunt = false; //hunt's over, either because he gave up (traps already placed above) or because he caught them
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
                sawTargetEnterHiding = false; //fresh chase - he hasn't watched THIS target hide anywhere yet. leaving a stale true here would let him rip open a spot he never actually saw anyone enter
                if (chaseTarget != null)
                {
                    lastTargetPosition = chaseTarget.transform.position; //reset so the first velocity sample isn't computed against stale data from a previous chase
                    targetVelocity = Vector3.zero;

                    //the ONLY way into Chasing is vision.CanSee returning true, so arriving here IS the sighting.
                    //this is what unlocks traps for the rest of this hunt.
                    sawPlayerThisHunt = true;
                    lastSightingPosition = chaseTarget.transform.position;
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

    private void FaceMovementDirection() //turn him to look where he's actually travelling
    {
        if (isSweepingSearchPoint)
        {
            return; //he's deliberately scanning a room from a standstill - that code owns his head, leave it alone
        }

        Vector3 movedThisTick = transform.position - lastFacingPosition;
        lastFacingPosition = transform.position;
        movedThisTick.y = 0f; //ignore the climb, or going up stairs would tip him backwards

        if (movedThisTick.sqrMagnitude < 0.000001f)
        {
            return; //standing still - hold the last facing rather than snapping to some arbitrary direction
        }

        //this replaces agent.updateRotation, which turned him toward the direction the AGENT wanted to travel. we run
        //with updatePosition = false and move the transform ourselves, so those two diverge - most visibly on stairs,
        //where he'd stroll up sideways. facing the real movement delta always matches what's on screen, and it matters
        //for more than looks: GuardVision builds his view cone from transform.forward.
        Quaternion wantedFacing = Quaternion.LookRotation(movedThisTick.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, wantedFacing, turnSpeed * Runner.DeltaTime);
    }

    //He lost someone around here. Rather than wiring the exact tile they vanished from - which would be uncanny - he
    //seeds the AREA: if there's a door nearby that's probably how they went, so string a wire across it; otherwise
    //drop something on the floor somewhere in the vicinity and hope. He's guessing at your route, not tracking you.
    private void TryPlaceTrapNear(Vector3 seenPosition)
    {
        if (Anger < angerToSetTraps) return;             //calm enough to shrug it off. only a rattled guard bothers
        if (trapsSetThisRun >= maxTrapsPerRun) return;   //that's everything he was carrying

        //sometimes he doesn't wire anything at all - he leaves something shiny out and waits for you to come back for
        //it. deliberately the minority option: bait only works while players still trust loot on sight, so flooding
        //the house with it just teaches them to touch nothing and kills the mechanic.
        if (baitLootPrefab != null && Random.value < baitChance)
        {
            TryPlantBait(seenPosition);
            return;
        }

        //the nearest TrapPoint the level author marked - a doorway, the top of the stairs, a hallway pinch. he takes
        //whichever is closest to where he lost you, so he's covering the way he reckons you went.
        TrapPoint wireSpot = TrapPoint.FindNearestFree(seenPosition, trapPointSearchRange);

        //a marked spot gets the wire about two times in three - the rest of the time he does something less
        //predictable, because a guard who ALWAYS wires the same doorways is one you can route around forever.
        if (wireSpot != null && tripwirePrefab != null && Random.value < 0.66f)
        {
            SpawnTrapAt(tripwirePrefab, wireSpot.transform.position);
            return;
        }

        //no marked spot in range, or he fancied something else: put a floor trap somewhere in the vicinity. sampled
        //onto the NavMesh so it can't end up inside furniture or through a wall.
        NetworkObject floorTrap = PickFloorTrapPrefab();
        if (floorTrap == null)
        {
            //nothing else assigned - fall back to the wire so a half-configured guard still does something
            if (wireSpot != null && tripwirePrefab != null) SpawnTrapAt(tripwirePrefab, wireSpot.transform.position);
            return;
        }

        for (int attempt = 0; attempt < 6; attempt++)
        {
            Vector2 randomCircle = Random.insideUnitCircle * trapScatterRadius;
            Vector3 candidate = seenPosition + new Vector3(randomCircle.x, 0f, randomCircle.y);
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, trapScatterRadius, NavMesh.AllAreas)) continue;
            if (GuardTrap.AnyTrapNear(hit.position, minTrapSpacing)) continue; //already covered this ground

            SpawnTrapAt(floorTrap, hit.position);
            return;
        }
    }

    //A PLANT, not a trap object: he sets out something shiny where he thinks you'll come back for it. Lifting it pays
    //nothing and screams. Kept separate from SpawnTrapAt because bait is a WorldItem - it goes in the loot pile, gets
    //a rarity glow off its fake value, and is picked up like anything else. That disguise IS the mechanic.
    private void TryPlantBait(Vector3 seenPosition)
    {
        if (baitLootPrefab == null) return;
        if (Anger < angerToSetTraps) return;
        if (trapsSetThisRun >= maxTrapsPerRun) return;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            Vector2 randomCircle = Random.insideUnitCircle * trapScatterRadius;
            Vector3 candidate = seenPosition + new Vector3(randomCircle.x, 0f, randomCircle.y);
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, trapScatterRadius, NavMesh.AllAreas)) continue;

            trapsSetThisRun++;
            Vector3 baitPosition = hit.position + Vector3.up * 0.4f; //lift it clear of the floor so it drops and settles like real loot
            int fakeValue = Random.Range(baitValueMin, baitValueMax + 1);
            Runner.Spawn(baitLootPrefab, baitPosition, Random.rotation, PlayerRef.None, (spawnRunner, spawnedObject) =>
            {
                WorldItem bait = spawnedObject.GetComponent<WorldItem>();
                if (bait != null)
                {
                    bait.ItemName = baitItemName;
                    bait.Value = fakeValue;          //drives the rarity glow, so it reads as a genuine score
                    bait.IsBait = true;
                    bait.CountedAsStolen = true;     //belt and braces: it must never touch the house tally even if the bait branch is ever changed
                    bait.SpawnPoint = baitPosition;  //networked-position safeguard - a deferred spawn drops the position argument
                    bait.UseSpawnPoint = true;
                }
            });
            return;
        }
    }

    private NetworkObject PickFloorTrapPrefab() //bear trap or alarm, whichever are assigned. a coin flip when both are
    {
        if (bearTrapPrefab != null && alarmPrefab != null)
        {
            return Random.value < 0.5f ? bearTrapPrefab : alarmPrefab;
        }
        return bearTrapPrefab != null ? bearTrapPrefab : alarmPrefab;
    }

    private void SpawnTrapAt(NetworkObject prefab, Vector3 position)
    {
        trapsSetThisRun++;
        Vector3 trapPosition = position;
        Runner.Spawn(prefab, trapPosition, Quaternion.identity, PlayerRef.None, (spawnRunner, spawnedObject) =>
        {
            GuardTrap trap = spawnedObject.GetComponent<GuardTrap>();
            if (trap != null)
            {
                trap.SpawnPoint = trapPosition;  //networked-position safeguard - a deferred spawn drops the position argument and dumps it at world origin
                trap.UseSpawnPoint = true;
            }
        });
    }

    private void OpenDoorInMyWay() //he walks the NavMesh, which ignores door colliders - so without this he'd stroll straight THROUGH a shut door. now he pushes it open and walks through the gap like a person
    {
        if (RunManager.Instance == null) return;
        if (State == GuardState.Asleep) return; //dead asleep at his post - doors drifting open on their own would look haunted

        //reach further ahead the faster he's moving. a fixed 1.8m is fine at a stroll, but at chase speed he covers
        //that in under a third of a second - about how long the door takes to swing - so he'd reach the doorway
        //while it was still opening and clip straight through it. giving him roughly half a second of lead fixes it.
        float lookAhead = Mathf.Max(doorOpenRange, agent.speed * 0.5f);

        //only shove open a door he's actually walking INTO, so he doesn't fling open every door in a hallway and hand
        //the players a lit-up trail of where he's been. that direction test used to live here with a 0.5 dot (a 60
        //degree cone) and it rejected essentially every door in the house - the Door script sits on the HINGE, which
        //is off at the EDGE of the doorway, so walking straight through one puts its pivot almost side-on to him.
        //0.1 is "anything not actually behind me", which is the honest version of the question we're asking.
        Door shutDoor = Door.FindClosedDoorAhead(transform.position, transform.forward, lookAhead, 0.1f);
        if (shutDoor == null) return;

        //WEDGED. he can't just push through it - he has to stand there and force it, which is the whole point of
        //spending a wedge. quicker if he's on the same side as it and can kick it out, slower forcing the door itself.
        if (shutDoor.IsWedged)
        {
            DoorWedge wedge = shutDoor.Wedge;
            bool sameSide = wedge != null && shutDoor.SideOf(transform.position) == wedge.WedgedSide;
            breakingWedgeOnDoor = shutDoor;
            breakingWedgeTimer = sameSide ? wedgeBreakSecondsSameSide : wedgeBreakSecondsFarSide;
            agent.ResetPath(); //stop dead. he stands and works at it rather than jogging on the spot
            return;
        }

        shutDoor.SetOpen(true);                                                  //open it here immediately so we stop re-detecting it next tick
        //this was the ONLY unguarded RunManager.Instance in the file - the identical call in the wedge-breaking
        //branch above checks it. Both are shouted about rather than skipped quietly: we've already swung the door on
        //THIS machine, so losing the RPC leaves a door that is open for the guard and shut for every player, and a
        //silent desync is far worse to debug than a line in the console saying exactly which one went missing.
        if (RunManager.Instance != null)
        {
            RunManager.Instance.RPC_SetDoorOpen(shutDoor.transform.position, true); //and tell every other client to swing their copy
        }
        else
        {
            Debug.LogError("[Guard] Opened a door with no RunManager to tell anyone - this client's door is now out of sync with the crew's.", this);
        }
        //deliberately never closed behind him - a door left open is a free tell to the players that he came through here
    }

    private void CheckNoisyHidingSpots() //a hiding spot only hides you if you SHUT UP in it
    {
        if (State == GuardState.Caught || State == GuardState.Escorting) return; //already got someone, hands full

        //NOT while he's asleep. the noise still wakes him the normal way (ListenForNoise -> Suspicious), but jumping
        //straight from Asleep to Caught skips the get-up hold entirely, so he'd rip the door open while the animator
        //still has him lying flat on his bed. let him stand up first, then deal with whoever's shouting.
        if (State == GuardState.Asleep) return;

        foreach (Player player in Player.ActivePlayers)
        {
            if (player == null || !player.IsHiding || player.IsEliminated || player.IsLockedUp) continue;
            if (player.NoiseLevel <= hidingNoiseTolerance) continue; //quiet in there - he walks right past
            if (Vector3.Distance(transform.position, player.transform.position) > hidingSearchRange) continue; //heard something, but not from close enough to place which spot

            //talking, and he's right next to the door. that's the whole tell - open it.
            player.RPC_PulledFromHiding();
            chaseTarget = player;
            ChangeState(GuardState.Caught);
            return;
        }
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