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
        Vector3 spawnPos = new Vector3(UnityEngine.Random.Range(-3f, 3f), 0f, UnityEngine.Random.Range(-3f, 3f));
        var spawnedPlayer = networkRunner.Spawn(playerPrefab, spawnPos, Quaternion.identity, networkRunner.LocalPlayer);

        Debug.Log(spawnPos);
    }
}
