using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Place one anywhere in the indoor scene. On load it scatters squeaky floorboards evenly across the whole
// walkable NavMesh - every room gets boards proportional to its floor area, no clustering. Every client uses
// RunManager's shared FloorboardSeed, so the layout is IDENTICAL everywhere (a board you step on is a board
// the host has too) - no per-board networking, just one synced int. The seed re-rolls each run for a fresh map.
public class FloorboardScatterer : MonoBehaviour
{
    [SerializeField] private SqueakyFloorboard floorboardPrefab; // needs a trigger collider + audio (same prefab you'd hand-place)
    [SerializeField] private int floorboardCount = 20;           // how many to try to place
    [SerializeField] private float minSpacing = 2.5f;           // keep boards from clustering on top of each other
    [SerializeField] private float verticalOffset = 0.05f;      // lift each board slightly above the NavMesh so it sits ON the floor instead of sinking into it

    private IEnumerator Start()
    {
        //wait for the run manager + a replicated seed before generating (the seed comes over the network)
        while (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid)
        {
            yield return null;
        }

        //seeded RNG - identical sequence on every client, so identical board positions
        System.Random random = new System.Random(RunManager.Instance.FloorboardSeed);

        //a known floor point to path-check reachability against - rejects navmesh islands on tabletops/roofs that you can't actually walk to
        if (!NavMesh.SamplePosition(transform.position, out NavMeshHit floorHit, 5f, NavMesh.AllAreas)) yield break; //scatterer isn't near the floor
        Vector3 floorReference = floorHit.position;
        NavMeshPath reachabilityPath = new NavMeshPath();

        //the actual walkable surface - scatter directly onto its triangles so coverage matches the house's real shape
        NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
        int triangleCount = triangulation.indices.Length / 3;
        if (triangleCount == 0) yield break; //no baked NavMesh in this scene

        //cumulative triangle areas, so we can pick a triangle weighted by size (even density across big and small rooms)
        float[] cumulativeAreas = new float[triangleCount];
        float totalArea = 0f;
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            Vector3 a = triangulation.vertices[triangulation.indices[triangle * 3]];
            Vector3 b = triangulation.vertices[triangulation.indices[triangle * 3 + 1]];
            Vector3 c = triangulation.vertices[triangulation.indices[triangle * 3 + 2]];
            totalArea += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            cumulativeAreas[triangle] = totalArea;
        }

        List<Vector3> placedPositions = new List<Vector3>();
        int maxAttempts = floorboardCount * 10; //give up eventually if spacing can't fit them all
        int attempts = 0;

        while (placedPositions.Count < floorboardCount && attempts < maxAttempts)
        {
            attempts++;

            //pick a triangle weighted by area
            float pick = (float)(random.NextDouble()) * totalArea;
            int chosen = 0;
            while (chosen < triangleCount - 1 && cumulativeAreas[chosen] < pick) chosen++;

            Vector3 vertexA = triangulation.vertices[triangulation.indices[chosen * 3]];
            Vector3 vertexB = triangulation.vertices[triangulation.indices[chosen * 3 + 1]];
            Vector3 vertexC = triangulation.vertices[triangulation.indices[chosen * 3 + 2]];

            //uniform random point inside the triangle (fold the far half back so it's an even spread, not bunched at a corner)
            float r1 = (float)random.NextDouble();
            float r2 = (float)random.NextDouble();
            if (r1 + r2 > 1f) { r1 = 1f - r1; r2 = 1f - r2; }
            Vector3 point = vertexA + r1 * (vertexB - vertexA) + r2 * (vertexC - vertexA);

            //reject unreachable islands (tabletops, roof) - if you can't walk to it from the floor, no board there
            if (!NavMesh.CalculatePath(floorReference, point, NavMesh.AllAreas, reachabilityPath) || reachabilityPath.status != NavMeshPathStatus.PathComplete) continue;

            bool tooClose = false;
            foreach (Vector3 placed in placedPositions)
            {
                if (Vector3.Distance(placed, point) < minSpacing) { tooClose = true; break; }
            }
            if (tooClose) continue;

            placedPositions.Add(point);
            Instantiate(floorboardPrefab, point + Vector3.up * verticalOffset, Quaternion.identity, transform);
        }
    }
}
