using System.Collections.Generic;
using UnityEngine;

// A button on the van's computer. Place two: one pointing at the house (startsNewRun = true) and one
// at the pawn shop (startsNewRun = false). Pressing E in range routes the whole crew to that scene.
// Same interact shape as ExitDoor - later this all gets replaced by the big map screen.
public class RouteButton : MonoBehaviour
{
    public static readonly List<RouteButton> AllButtons = new List<RouteButton>();

    [SerializeField] public int targetSceneBuildIndex = 1;
    [SerializeField] public int spawnPointId = 0;      // which SpawnPoint to arrive on in the destination scene
    [SerializeField] public float interactRange = 2f;
    [SerializeField] public bool startsNewRun = false; // true for the House button - begins a fresh heist (resets the run state)

    private void OnEnable() => AllButtons.Add(this);
    private void OnDisable() => AllButtons.Remove(this);
}
