using UnityEngine;
using System.Collections.Generic;

public class NoCreakZone : MonoBehaviour
{
    public static readonly List<NoCreakZone> AllZones = new List<NoCreakZone>(); //All of the zones in the scene. This is a static list that is populated by the OnEnable and OnDisable methods

    private void OnEnable() //registry
    {
        AllZones.Add(this);
    }
    private void OnDisable() //registry
    {
        AllZones.Remove(this);
    }

    public bool Contains(Vector3 worldPoint) //Creates a box in space for the no-creak zone
    {
        Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
        return Mathf.Abs(localPoint.x) <= 0.5f 
            && Mathf.Abs(localPoint.y) <= 0.5f 
            && Mathf.Abs(localPoint.z) <= 0.5f;
    }

    public static bool IsInsideAnyZone(Vector3 worldPoint) //Checks if a point is inside any of the no-creak zones
    {
        foreach(NoCreakZone zone in AllZones) //foreach loop to check if the point is inside any of the zones
        {
            if(zone.Contains(worldPoint)) //if the point is inside the zone, return true
            {
                return true;
            }
        }
        return false; //else false

    }
    private void OnDrawGizmos() //Visual for the no-creak zone in the editor
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.25f);
        Gizmos.DrawCube(Vector3.zero, Vector3.one);
        Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.9f);
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
    }
}
