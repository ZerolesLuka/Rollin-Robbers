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

    private void OnEnable() => AllDoors.Add(this);
    private void OnDisable() => AllDoors.Remove(this);
}
