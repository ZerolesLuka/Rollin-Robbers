using Fusion;
using UnityEngine;

public class GameBootstrap : MonoBehaviour
{
    [SerializeField] private NetworkRunner networkRunner;
    private async void Start() //async means that the method can run asynchronously, allowing for non-blocking operations such as waiting for the network runner to start the game without freezing
    {
        networkRunner = gameObject.AddComponent<NetworkRunner>(); //creates a new component of the network
        await networkRunner.StartGame(new StartGameArgs() //wait until start game is complete before moving on to the next line of code
        {
            GameMode = GameMode.Shared, //everyone connects to a single instance of the game, allowing for shared state and interactions between players
            SessionName = "TestRoom", //nameee
            Scene = SceneRef.FromIndex(0), //what scene to load
        });
    }
}
