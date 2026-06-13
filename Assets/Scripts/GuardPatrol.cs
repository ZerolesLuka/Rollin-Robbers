using UnityEngine;
using UnityEngine.AI;

public class GuardPatrol : MonoBehaviour
{
    [SerializeField] private NavMeshAgent agent;
    [SerializeField] private Transform[] waypoints;
    [SerializeField] private float reachDistance = 0.5f;

    private int currentWaypoint = 0;

    void Start()
    {
        agent.SetDestination(waypoints[currentWaypoint].position);
    }

    void Update()
    {
        if (!agent.pathPending && agent.remainingDistance <= reachDistance)
        {
            currentWaypoint = (currentWaypoint + 1) % waypoints.Length; //add to the waypoint % means dont go over the lenght of waypoints, will reset to 0
            agent.SetDestination(waypoints[currentWaypoint].position);
        }
    }
}