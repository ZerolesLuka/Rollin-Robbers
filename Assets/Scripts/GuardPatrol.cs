using UnityEngine;
using Fusion;
using UnityEngine.AI;

public class GuardPatrol : NetworkBehaviour
{
    [SerializeField] private NavMeshAgent agent;
    [SerializeField] private Transform[] waypoints;
    [SerializeField] private float reachDistance = 0.5f;


    private int currentWaypoint = 0; //used in modulo

    public override void Spawned()
    {
        if (!HasStateAuthority)
        {
            agent.enabled = false;
            return;
        }
        agent.Warp(transform.position);
        agent.SetDestination(waypoints[currentWaypoint].position); //set first destination
    }

    public override void FixedUpdateNetwork()
    {
        if(!HasStateAuthority) return; //only run for the state authority, which is the host in this case, so only the host will control the guard's movement
        if (!agent.pathPending && agent.remainingDistance <= reachDistance) //if theres no path pending and we got to our destination
        {
            currentWaypoint = (currentWaypoint + 1) % waypoints.Length; //add to the waypoint % means dont go over the lenght of waypoints, will reset to 0
            agent.SetDestination(waypoints[currentWaypoint].position); //agents destination is the current waypoints position
        }
    }
    public void SetWaypoints(Transform[] points)
    {
        waypoints = points;
    }
}