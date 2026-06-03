using Fusion;
using UnityEngine;
using static Unity.Collections.Unicode;
using System.Threading.Tasks;
using System;

public class GameBootstrap : MonoBehaviour
{
    private NetworkRunner networkRunner;
    [SerializeField] private NetworkObject playerPrefab;    
    private async void Start() //async means that the method can run asynchronously, allowing for non-blocking operations such as waiting for the network runner to start the game without freezing
    {
        networkRunner = gameObject.AddComponent<NetworkRunner>(); //creates a new component of the network
        await networkRunner.StartGame(new StartGameArgs()
        {
            GameMode = GameMode.Shared,
            SessionName = "TestRoom",
        });
        var spawnedPlayer = networkRunner.Spawn(playerPrefab, Vector3.zero, Quaternion.identity, networkRunner.LocalPlayer);
    }
}
