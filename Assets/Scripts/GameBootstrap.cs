using Fusion;
using UnityEngine;
using static Unity.Collections.Unicode;
using System.Threading.Tasks;
using System;
using Fusion.Sockets;
using System.Collections.Generic;

public class GameBootstrap : MonoBehaviour, INetworkRunnerCallbacks
{
    private NetworkRunner networkRunner;
    [SerializeField] private NetworkObject playerPrefab;
    [SerializeField] private Transform lobbyCamera; //camera where room code happens for now
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
        networkRunner = gameObject.AddComponent<NetworkRunner>(); //creates a new component of the network
        networkRunner.AddCallbacks(this); //tells the Network Runner to use this script for its callbacks, which are functions that are called in response to certain events in the network
        await networkRunner.StartGame(new StartGameArgs()
        {
            GameMode = GameMode.Shared,
            SessionName = roomCode, //whoever uses the same code lands in the same game, first one in becomes the host
        });
        Vector3 spawnPos = new Vector3(UnityEngine.Random.Range(-3f, 3f), 0f, UnityEngine.Random.Range(-3f, 3f)); //random spawn pos
        networkRunner.Spawn(playerPrefab, spawnPos, Quaternion.identity, networkRunner.LocalPlayer);
        lobbyCamera.gameObject.SetActive(false); //turn off the lobby camera once we're in game    
    }
    //
}
