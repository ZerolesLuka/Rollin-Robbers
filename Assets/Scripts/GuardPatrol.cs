using UnityEngine;
using Fusion;
using UnityEngine.AI;

public class GuardPatrol : NetworkBehaviour
{
    public enum GuardState { Asleep, Relaxed, Suspicious, Searching, Chasing, Caught }

    [Networked] public GuardState State { get; private set; } //the guard's current state

    [SerializeField] private float noiseRange = 4f; //how close a noise registers
    private int noiseThreshold;
    private int noiseCounter;
    private float noiseTickTimer;

    [SerializeField] private NavMeshAgent agent;
    [SerializeField] private Transform[] waypoints;
    [SerializeField] private float reachDistance = 0.5f; //distance of which the guard consider it has reached waypoint

    [SerializeField] private float sightRange = 10f;
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
                    if (noiseTickTimer >= 1f) //if we heard noise for 1 second, increase the noise counter
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
        //STUB until hearing, any player within noiseRange will count as a noise, we can replace this with actual noise events later
        foreach(Player p in FindObjectsByType<Player>(FindObjectsSortMode.None)) //for each player, if they're within noise range, count it as a noise
          if(Vector3.Distance(transform.position, p.transform.position) <= noiseRange) 
             return true;
        return false;
    }

    private void KeepCodeHere()
    {
        if (!HasStateAuthority) return; //only run for the state authority, which is the host in this case, so only the host will control the guard's movement
        if (!agent.pathPending && agent.remainingDistance <= reachDistance) //if theres no path pending and we got to our destination
        {
            currentWaypoint = (currentWaypoint + 1) % waypoints.Length; //add to the waypoint % means dont go over the lenght of waypoints, will reset to 0
            agent.SetDestination(waypoints[currentWaypoint].position); //agents destination is the current waypoints position
        }
        foreach (Player p in FindObjectsByType<Player>(FindObjectsSortMode.None)) //for each player, if we can see them, log we see them
            if (CanSeePlayer(p)) Debug.Log($"Guard sees {p.name}");
    }

}