using Fusion;
using UnityEngine;
using System.Threading.Tasks;
using System;
using Fusion.Sockets;
using System.Collections.Generic;

public class GameBootstrap : MonoBehaviour, INetworkRunnerCallbacks
{
    private NetworkRunner networkRunner;
    [SerializeField] private NetworkObject playerPrefab;
    [SerializeField] private Transform lobbyCamera; //camera where room code happens for now
    [SerializeField] private Transform playerSpawn;

    [SerializeField] private NetworkObject guardPrefab;
    [SerializeField] private Transform guardSpawn;
    [SerializeField] private Transform[] guardWaypoints;
    [SerializeField] private Transform closetSpot; //the empty inside the closet - lives in the scene, passed to the guard at spawn (a prefab can't reference a scene object)

    [SerializeField] private NetworkObject runManagerPrefab;

    private string roomCode = ""; //the code players type in to join the same game, acts as the session name

    public void OnConnectedToServer(NetworkRunner runner)
    {
        
    }

    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
    {
        
    }

    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
    {
        
    }

    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data)
    {
        
    }

    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
    {
       
    }

    public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken)
    {
       
    }

    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
        //Every tick will read the players input and look, store in struct and send to the network
        NetworkInputData networkInputData = new NetworkInputData(); //create a new instance of the struct that holds player input
        networkInputData.movementInput = Player.LocalPlayer.playerInputActions.Player.Move.ReadValue<Vector2>(); //read the movement input from the player and store it in the struct 
        networkInputData.lookInput = Player.LocalPlayer.playerInputActions.Player.Look.ReadValue<Vector2>(); //read the look input from the player and store it in the struct
        networkInputData.crouchInput = Player.LocalPlayer.playerInputActions.Player.Crouch.ReadValue<float>() > 0.5f; //read the crouch input from the player and store it in the struct, converts float to bool
        networkInputData.sprintInput = Player.LocalPlayer.playerInputActions.Player.Sprint.ReadValue<float>() > 0.5f;
        networkInputData.jumpInput = Player.LocalPlayer.playerInputActions.Player.Jump.ReadValue<float>() > 0.5f;
        networkInputData.interactInput = Player.LocalPlayer.playerInputActions.Player.Interact.ReadValue<float>() > 0.5f; //E to free a trapped teammate (and later: loot, lockpick, extract)
        input.Set(networkInputData); //set the network input to the struct
    }

    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input)
    {
        
    }

    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
    {
        
    }

    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
    {
        
    }

    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        
    }

    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        if (RunManager.Instance != null) RunManager.Instance.OnPlayerLeft(); //keep the alive count honest when someone disconnects
    }

    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress)
    {
        
    }

    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data)
    {
        
    }

    public void OnSceneLoadDone(NetworkRunner runner)
    {
        
    }

    public void OnSceneLoadStart(NetworkRunner runner)
    {
        
    }

    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
    {

    }

    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        
    }

    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message)
    {
        
    }

    private void OnGUI() //draws a temp menu, replace with real UI later
    {
        if (networkRunner != null) return; //already connecting or connected, hide the menu

        GUI.Label(new Rect(20, 20, 200, 30), "Room Code:");
        roomCode = GUI.TextField(new Rect(20, 50, 200, 30), roomCode); //text box the player types their code into

        if (GUI.Button(new Rect(20, 90, 200, 40), "Connect"))
        {
            if (!string.IsNullOrWhiteSpace(roomCode)) ConnectToRoom(); //only connect if they actually typed something
        }
    }

    private async void ConnectToRoom() //async so the game doesnt freeze while waiting to connect
    {
        DontDestroyOnLoad(gameObject); //keep this alive across scene loads so OnInput keeps firing (input dies if this is destroyed)
        networkRunner = GetComponent<NetworkRunner>();
        if (networkRunner == null) //if there isnt a runner, add one
        {
            networkRunner = gameObject.AddComponent<NetworkRunner>();
        }
        networkRunner.AddCallbacks(this); //tells the Network Runner to use this script for its callbacks, which are functions that are called in response to certain events in the network
        await networkRunner.StartGame(new StartGameArgs() //wait to start game before calling this code
        {
            GameMode = GameMode.Shared,
            SessionName = roomCode,
            SceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>(), //required for Runner.LoadScene to preserve spawned NetworkObjects across scene loads
        });
        if(networkRunner.IsSharedModeMasterClient) //only spawn the guard if we're the host, since its shared mode, we all run the same code but only the host should spawn things
        {
            networkRunner.Spawn(guardPrefab,
                guardSpawn.position,
                Quaternion.identity,
                PlayerRef.None,
                (runner, obj) =>
                {
                    GuardPatrol guard = obj.GetComponent<GuardPatrol>();
                    guard.SetWaypoints(guardWaypoints); //waypoints and the closet both live in the scene, so hand them over at spawn
                    guard.SetCloset(closetSpot);
                }
                ); //spawn the guard at the guard spawn point
            networkRunner.Spawn(runManagerPrefab, Vector3.zero, Quaternion.identity, PlayerRef.None); //spawn the run manager BEFORE the local player, so the master's own player registers into it (used to spawn after, so the host never counted itself)
        }

        Vector3 spawnPos = playerSpawn.gameObject.transform.position; //where the player spawns in the world, can be changed to an array of spawn points later for more variety
        networkRunner.Spawn(playerPrefab, spawnPos, Quaternion.identity, networkRunner.LocalPlayer); //spawns player prefab on spawnpos
        lobbyCamera.gameObject.SetActive(false); //turn off the lobby camera once we're in game
    }
    //
}
