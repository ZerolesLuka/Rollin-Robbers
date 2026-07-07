using System.Collections.Generic;
using UnityEngine;

// Place in any scene where players can arrive. Set spawnId to match the ExitDoor.spawnPointId
// that leads here. TeleportAfterLoad finds this by id - no string names needed.
public class SpawnPoint : MonoBehaviour
{
    public static readonly List<SpawnPoint> All = new List<SpawnPoint>();

    [SerializeField] public int spawnId = 0;

    private void OnEnable() => All.Add(this);
    private void OnDisable() => All.Remove(this);
}
