using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.AI;

// The guard dog - a second, faster threat that patrols independently and shares GuardVision/GuardHearing/
// GuardAudio with GuardPatrol (those components are generic on purpose - see their own file comments).
// The dog never jails anyone itself: when it catches up to a player it "corners" them (barks, holds them there)
// and calls GuardPatrol.Instance.AlertTo so the real guard comes running to that exact spot. Keeps the dog
// as a fast, scary sensor-with-legs instead of duplicating the whole catch/drag/lockup FSM.
public class DogAI : NetworkBehaviour
{
    public enum DogState { Resting, Patrol, Investigating, Chasing } //Resting is first so the [Networked] default (0) starts him asleep at his bed

    public static DogAI Instance; //mirrors GuardPatrol.Instance, in case other systems ever need to reference "the dog"

    [Networked] public DogState State { get; private set; }

    [SerializeField] private float catchRange = 2.5f;         //how close before the dog corners a player
    [SerializeField] private float chaseSenseRange = 6f;       //keeps chasing a bit past losing direct sight, same trick GuardPatrol uses
    [SerializeField] private float cornerAlertCooldown = 6f;   //don't spam AlertTo every tick while cornering someone
    [SerializeField] private float reachDistance = 0.5f;
    [SerializeField] private float wanderRadius = 10f;         //used when no waypoints are assigned - the dog just roams randomly near its spawn

    [SerializeField] private int restDisturbancesMax = 3; //after this many wake-ups the dog gives up on sleep and starts patrolling - mirrors the guard's asleepChances -> Relaxed
    private int restDisturbances;

    [SerializeField] private float searchSweepRadius = 5f;         //how far from the scent the dog sniffs around while investigating
    [SerializeField] private int maximumSearchSweepPoints = 3;     //give up after checking this many spots near the scent - same bounded pattern as GuardPatrol.Searching
    [SerializeField] private float searchSweepWaitTime = 1f;       //dogs sniff faster than a person searches a room
    private int searchSweepPointsChecked;
    private float searchSweepWaitTimer;
    private readonly List<Vector3> searchPointsThisSweep = new List<Vector3>(); //spots already sniffed this search - keeps the sweep spreading out instead of clustering by chance
    [SerializeField] private float minSearchPointSpacing = 1.5f; //a new random search point must be at least this far from ones already checked

    [SerializeField] private float sweepLookRange = 60f; //dogs swing their heads/noses around wider than a person while sniffing
    [SerializeField] private float sweepLookSpeed = 140f; //degrees per second - quicker than the guard's look, matches the dog's snappier personality
    private bool isSweepingSearchPoint;
    private float sweepBaseYaw;
    private float sweepPhaseTimer;

    [SerializeField] private float predictionLeadTime = 0.4f; //seconds ahead of the target's current velocity the dog aims for while chasing - cuts corners instead of tailing exactly

    [SerializeField] private float patrolSpeed = 2f;
    [SerializeField] private float investigateSpeed = 3f;
    [SerializeField] private float chaseSpeed = 7f; //faster than the guard - the dog is meant to be quick and scary, not a bigger overall threat

    private NavMeshAgent agent;
    private GuardVision vision;   //reusable sight component - same class GuardPatrol uses, own instance with dog-tuned values
    private GuardHearing hearing; //reusable ears component
    private GuardAudio dogAudio;  //reusable voice component - own bark clips assigned in the Inspector

    private Transform[] waypoints;
    private int currentWaypoint;
    private float cornerCooldownTimer;
    private Player chaseTarget;
    private Vector3 lastKnownPosition;
    private Vector3 spawnPosition;
    private Vector3 lastTargetPosition; //used to estimate the chase target's velocity for predictive pursuit
    private Vector3 targetVelocity;

    public override void Spawned()
    {
        agent = GetComponent<NavMeshAgent>();
        vision = GetComponent<GuardVision>();
        hearing = GetComponent<GuardHearing>();
        dogAudio = GetComponent<GuardAudio>();

        if (!HasStateAuthority)
        {
            agent.enabled = false;
            return;
        }

        Instance = this;
        spawnPosition = transform.position;
        State = DogState.Resting; //night time - the dog starts asleep at his bed, like the guard, and only gets up when disturbed
        agent.updatePosition = false; //same NavMeshAgent/NetworkTransform fix as GuardPatrol - we move the transform on the tick ourselves
        agent.Warp(transform.position);
        agent.speed = patrolSpeed;
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return; //only the master drives the dog, same as the guard

        if (cornerCooldownTimer > 0f)
        {
            cornerCooldownTimer -= Runner.DeltaTime;
        }

        switch (State)
        {
            case DogState.Resting:
                TickResting();
                break;
            case DogState.Patrol:
                TickPatrol();
                break;
            case DogState.Investigating:
                TickInvestigating();
                break;
            case DogState.Chasing:
                TickChasing();
                break;
        }

        transform.position = agent.nextPosition; //apply steering on the tick - same clock as the player, no NetworkTransform tug-of-war
    }

    private void TickResting() //asleep at his bed - wakes to sight or nearby sound, otherwise stays put (like the guard's Asleep)
    {
        Player spotted = FindVisiblePlayer();
        if (spotted != null)
        {
            ChangeState(DogState.Chasing, spotted);
            return;
        }

        GuardHearing.Heard heard = hearing.LoudestNoise();
        if (heard.loudness > 0f)
        {
            lastKnownPosition = heard.position;
            ChangeState(DogState.Investigating, null); //perks up and goes to check the noise
            return;
        }

        //undisturbed - make sure he's settled back at his bed rather than standing wherever he last gave up
        if (Vector3.Distance(transform.position, spawnPosition) > reachDistance && !agent.pathPending && !agent.hasPath)
        {
            agent.SetDestination(spawnPosition);
        }
    }

    private void TickPatrol()
    {
        Player spotted = FindVisiblePlayer();
        if (spotted != null)
        {
            ChangeState(DogState.Chasing, spotted);
            return;
        }

        GuardHearing.Heard heard = hearing.LoudestNoise();
        if (heard.loudness > 0f)
        {
            lastKnownPosition = heard.position;
            ChangeState(DogState.Investigating, null);
            return;
        }

        if (!agent.pathPending && agent.remainingDistance <= reachDistance)
        {
            PickNextPatrolPoint();
        }
    }

    private void TickInvestigating() //sniffs a multi-point sweep around the scent instead of just standing at one spot and giving up on a timer
    {
        Player spotted = FindVisiblePlayer();
        if (spotted != null)
        {
            ChangeState(DogState.Chasing, spotted);
            return;
        }

        GuardHearing.Heard heard = hearing.LoudestNoise();
        if (heard.loudness > 0f)
        {
            lastKnownPosition = heard.position;
            agent.SetDestination(lastKnownPosition); //fresher scent beelines there and restarts the sweep once we arrive
            searchSweepPointsChecked = 0;
            searchSweepWaitTimer = 0f;
            searchPointsThisSweep.Clear();
            searchPointsThisSweep.Add(lastKnownPosition);
        }

        if (!agent.pathPending && agent.remainingDistance <= reachDistance)
        {
            if (!isSweepingSearchPoint) //just arrived - lock in facing direction and take manual control while sniffing around
            {
                isSweepingSearchPoint = true;
                agent.updateRotation = false;
                sweepBaseYaw = transform.eulerAngles.y;
                sweepPhaseTimer = 0f;
            }
            SweepLookAround();

            searchSweepWaitTimer += Runner.DeltaTime;
            if (searchSweepWaitTimer >= searchSweepWaitTime)
            {
                searchSweepWaitTimer = 0f;
                isSweepingSearchPoint = false;
                agent.updateRotation = true;
                searchSweepPointsChecked++;

                if (searchSweepPointsChecked >= maximumSearchSweepPoints)
                {
                    //nothing found - go back to bed, unless he's been roused too many times, in which case he gives up on sleep and patrols (mirrors the guard's asleepChances -> Relaxed)
                    restDisturbances++;
                    ChangeState(restDisturbances >= restDisturbancesMax ? DogState.Patrol : DogState.Resting, null);
                }
                else
                {
                    PickRandomSearchPoint(lastKnownPosition); //check another spot near the scent instead of the same exact point
                }
            }
        }
    }

    private void SweepLookAround() //same technique as GuardPatrol.SweepLookAround - oscillates facing left/right around the direction he arrived
    {
        sweepPhaseTimer += Runner.DeltaTime;
        float sweepAngle = Mathf.Sin(sweepPhaseTimer * sweepLookSpeed * Mathf.Deg2Rad) * sweepLookRange;
        transform.rotation = Quaternion.Euler(0f, sweepBaseYaw + sweepAngle, 0f);
    }

    private void TickChasing()
    {
        if (chaseTarget == null || chaseTarget.IsEliminated)
        {
            ChangeState(DogState.Investigating, null);
            return;
        }

        Vector3 toTarget = chaseTarget.transform.position - transform.position;
        toTarget.y = 0f;
        float distanceToTarget = toTarget.magnitude;

        bool canSeeTarget = vision.CanSee(chaseTarget.transform);
        if (canSeeTarget || distanceToTarget < chaseSenseRange)
        {
            Vector3 currentTargetPosition = chaseTarget.transform.position;
            targetVelocity = (currentTargetPosition - lastTargetPosition) / Runner.DeltaTime; //crude per-tick velocity estimate
            lastTargetPosition = currentTargetPosition;

            lastKnownPosition = currentTargetPosition; //still the real last-seen spot, used if we lose the target entirely
            Vector3 predictedPosition = currentTargetPosition + targetVelocity * predictionLeadTime; //aim a little ahead instead of exactly where they are right now - cuts corners like a real dog would
            agent.SetDestination(predictedPosition);
        }

        if (distanceToTarget < catchRange)
        {
            CornerTarget();
        }
        else if (!canSeeTarget && distanceToTarget > chaseSenseRange && !agent.pathPending && agent.remainingDistance <= reachDistance)
        {
            ChangeState(DogState.Investigating, null); //lost them, sniff around the last known spot instead of chasing forever
        }
    }

    private void CornerTarget() //caught up to the player - the dog doesn't jail anyone, it barks its head off and rats them out
    {
        if (dogAudio != null)
        {
            dogAudio.Bark(GuardAudio.BarkType.Caught);
        }

        if (cornerCooldownTimer <= 0f && GuardPatrol.Instance != null)
        {
            cornerCooldownTimer = cornerAlertCooldown;
            GuardPatrol.Instance.AlertTo(transform.position); //send the real guard rushing to this exact spot
        }
    }

    private Player FindVisiblePlayer()
    {
        Player loudestVisible = null;
        float loudestNoise = -1f;
        foreach (Player player in Player.ActivePlayers) //live list, so players who joined after the dog spawned still count
        {
            if (player.IsEliminated) continue;
            if (!vision.CanSee(player.transform)) continue; //CanSee already skips hidden players (see GuardVision)
            if (player.NoiseLevel > loudestNoise)
            {
                loudestNoise = player.NoiseLevel;
                loudestVisible = player;
            }
        }
        return loudestVisible;
    }

    private void PickRandomSearchPoint(Vector3 searchCenter) //same technique as GuardPatrol.PickRandomSearchPoint - one way to do random-area searching across both AIs
    {
        Vector3 bestCandidate = searchCenter;
        bool foundCandidate = false;

        for (int attempt = 0; attempt < 5; attempt++) //a few tries to find a spot that isn't right on top of ground already sniffed
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

    private void PickNextPatrolPoint()
    {
        if (waypoints != null && waypoints.Length > 1)
        {
            int nextWaypoint;
            do
            {
                nextWaypoint = Random.Range(0, waypoints.Length); //random non-repeating pick instead of a fixed cycle - same reasoning as GuardPatrol.PickNextWaypoint
            } while (nextWaypoint == currentWaypoint);

            currentWaypoint = nextWaypoint;
            agent.SetDestination(waypoints[currentWaypoint].position);
        }
        else if (waypoints != null && waypoints.Length == 1)
        {
            agent.SetDestination(waypoints[0].position);
        }
        else //no waypoints assigned - wander randomly near spawn instead of standing still forever
        {
            Vector2 randomCircle = Random.insideUnitCircle * wanderRadius;
            Vector3 randomPoint = spawnPosition + new Vector3(randomCircle.x, 0f, randomCircle.y);
            if (NavMesh.SamplePosition(randomPoint, out NavMeshHit hit, wanderRadius, NavMesh.AllAreas))
            {
                agent.SetDestination(hit.position);
            }
        }
    }

    private void ChangeState(DogState newState, Player target) //single place to switch states so timers/speed always reset on entry
    {
        State = newState;

        //default rotation control back to the agent on every transition - Investigating re-claims it manually, but only while actually idle at a sweep point
        agent.updateRotation = true;
        isSweepingSearchPoint = false;

        switch (newState)
        {
            case DogState.Resting:
                agent.speed = patrolSpeed; //walk pace back to the bed
                agent.SetDestination(spawnPosition); //head home to lie back down
                //no bark - a dog going back to sleep is quiet
                break;
            case DogState.Patrol:
                agent.speed = patrolSpeed;
                if (dogAudio != null) dogAudio.Bark(GuardAudio.BarkType.GiveUp);
                break;
            case DogState.Investigating:
                agent.speed = investigateSpeed;
                searchSweepPointsChecked = 0;
                searchSweepWaitTimer = 0f;
                searchPointsThisSweep.Clear(); //fresh investigation - forget spots sniffed on a previous alert
                searchPointsThisSweep.Add(lastKnownPosition);
                agent.SetDestination(lastKnownPosition);
                if (dogAudio != null) dogAudio.Bark(GuardAudio.BarkType.Search);
                break;
            case DogState.Chasing:
                agent.speed = chaseSpeed;
                chaseTarget = target;
                if (target != null)
                {
                    lastTargetPosition = target.transform.position; //reset so the first velocity sample isn't computed against stale data from a previous chase
                    targetVelocity = Vector3.zero;
                }
                if (dogAudio != null) dogAudio.Bark(GuardAudio.BarkType.Chase);
                break;
        }
    }

    public void SetWaypoints(Transform[] points)
    {
        waypoints = points; //set from the spawner, since a prefab can't hold a scene reference
    }

    public void AlertTo(Vector3 spot) //any sensor, or the guard, pings this to send the dog to investigate a spot
    {
        if (!HasStateAuthority) return; //only the master drives the dog
        if (State == DogState.Chasing) return; //never override an active chase
        lastKnownPosition = spot;
        ChangeState(DogState.Investigating, null);
    }
}
