using System.Collections.Generic;
using UnityEngine;

// Place on or near any door the players can exit through. Set targetSceneBuildIndex to the
// outdoor scene's index in File > Build Settings. Uses the same E-key interact system as
// rescue and loot - priority is rescue > loot > exit door.
public class ExitDoor : MonoBehaviour
{
    public static readonly List<ExitDoor> AllDoors = new List<ExitDoor>();

    [SerializeField] public int targetSceneBuildIndex = 1;
    [SerializeField] public float interactRange = 2f;
    [SerializeField] public int spawnPointId = 0; // which PlayerSpawnN to use in the destination scene

    //HOLD E, don't tap it. Leaving was the one act in a run that cost nothing: caught out, sprint the last few metres,
    //tap, gone before he could reach you. Standing still this long is what gives him time to arrive, which makes the
    //threshold somewhere a chase can actually be lost.
    //
    //Authored PER DOOR because direction matters. Set the doors LEAVING the house to a real number and the ones
    //coming back IN to 0 - nothing outdoors is hunting you, so a wait on the way in is a toll rather than tension, and
    //once the crew is nipping back to the van for tools they'd pay it several times a run.
    [SerializeField] public float holdSeconds = 1.5f;

    private void OnEnable() => AllDoors.Add(this);
    private void OnDisable() => AllDoors.Remove(this);
}
