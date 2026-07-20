using Fusion;
using UnityEngine;
using System.Threading.Tasks;
using System;
using Fusion.Sockets;
using System.Collections.Generic;

public class GameBootstrap : MonoBehaviour, INetworkRunnerCallbacks
{
    private static GameBootstrap instance;

    private void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private NetworkRunner networkRunner;
    [SerializeField] private NetworkObject playerPrefab;
    [SerializeField] private Transform lobbyCamera; //camera where room code happens for now
    [SerializeField] private Transform playerSpawn;

    [SerializeField] private NetworkObject runManagerPrefab;

    private string roomCode = ""; //the code players type in to join the same game, acts as the session name
    private string connectError = ""; //why the last connect attempt failed - shown on the menu so a friend with a typo'd code isn't staring at nothing

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
        if (Player.LocalPlayer == null || Player.LocalPlayer.playerInputActions == null) return;

        NetworkInputData networkInputData = new NetworkInputData();
        networkInputData.movementInput = Player.LocalPlayer.playerInputActions.Player.Move.ReadValue<Vector2>();
        networkInputData.lookInput = Player.LocalPlayer.playerInputActions.Player.Look.ReadValue<Vector2>(); //read the look input from the player and store it in the struct
        networkInputData.crouchInput = Player.LocalPlayer.playerInputActions.Player.Crouch.ReadValue<float>() > 0.5f; //read the crouch input from the player and store it in the struct, converts float to bool
        networkInputData.sprintInput = Player.LocalPlayer.playerInputActions.Player.Sprint.ReadValue<float>() > 0.5f;
        networkInputData.jumpInput = Player.LocalPlayer.playerInputActions.Player.Jump.ReadValue<float>() > 0.5f;
        networkInputData.interactInput = Player.LocalPlayer.playerInputActions.Player.Interact.ReadValue<float>() > 0.5f; //E to free a trapped teammate (and later: loot, lockpick, extract)
        networkInputData.flashlightInput = Player.LocalPlayer.playerInputActions.Player.Flashlight.ReadValue<float>() > 0.5f; //F to toggle the flashlight
        networkInputData.dropInput = Player.LocalPlayer.playerInputActions.Player.Drop.ReadValue<float>() > 0.5f; //G to drop an item
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
        if (RunManager.Instance != null) RunManager.Instance.OnPlayerLeft(player); //keep the alive count honest when someone disconnects, and free the computer if they were on it
    }

    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress)
    {
        
    }

    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data)
    {
        
    }

    public void OnSceneLoadDone(NetworkRunner runner)
    {
        // hide the lobby camera whenever a scene finishes loading - we're already in-game
        GameObject lobbyCam = GameObject.Find("LobbyCamera");
        if (lobbyCam != null) lobbyCam.SetActive(false);

        // spawn the guard if the indoor scene has a GuardBootstrap
        GuardBootstrap guardBootstrap = FindAnyObjectByType<GuardBootstrap>();
        if (guardBootstrap != null) guardBootstrap.TriggerSpawn(runner);
    }

    public void OnSceneLoadStart(NetworkRunner runner)
    {
        
    }

    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
    {

    }

    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        //the session died out from under us (host gone, network dropped, ...) - clean up so the menu
        //comes back instead of a frozen world. full "kick to menu" session flow is deferred; this just
        //makes a dead session recoverable. an Ok shutdown still clears the runner so reconnecting works.
        if (shutdownReason != ShutdownReason.Ok)
        {
            connectError = $"Disconnected: {shutdownReason}";
        }
        ClearRunner();
    }

    private void ClearRunner() //tear down a dead/failed runner so OnGUI shows the menu again and a fresh connect starts clean
    {
        if (networkRunner != null)
        {
            Destroy(networkRunner); //a shut-down runner can't be restarted - remove it so the next connect adds a fresh one
            networkRunner = null;
        }
        foreach (NetworkSceneManagerDefault sceneManager in GetComponents<NetworkSceneManagerDefault>())
        {
            Destroy(sceneManager); //each connect attempt AddComponents a fresh one - don't let dead ones pile up across retries
        }
        if (lobbyCamera != null) lobbyCamera.gameObject.SetActive(true); //bring the menu camera back so the player isn't staring at a void
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

        if (!string.IsNullOrEmpty(connectError))
        {
            GUI.Label(new Rect(20, 140, 400, 30), connectError); //why the last attempt failed, so a typo'd code isn't a silent mystery
        }
    }

    private async void ConnectToRoom()
    {
        networkRunner = GetComponent<NetworkRunner>();
        if (networkRunner == null) //if there isnt a runner, add one
        {
            networkRunner = gameObject.AddComponent<NetworkRunner>();
        }
        networkRunner.AddCallbacks(this); //tells the Network Runner to use this script for its callbacks, which are functions that are called in response to certain events in the network
        StartGameResult result = await networkRunner.StartGame(new StartGameArgs() //wait to start game before calling this code
        {
            GameMode = GameMode.Shared,
            SessionName = roomCode,
            PlayerCount = 4, //hard cap the lobby at 4 - matches the van's 4 seats
            SceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>(), //required for Runner.LoadScene to preserve spawned NetworkObjects across scene loads
        });
        if (!result.Ok) //connect failed (full room, dead network, Photon down, ...) - without this the menu hides forever because networkRunner is non-null
        {
            connectError = $"Couldn't connect: {result.ShutdownReason}";
            ClearRunner(); //back to the menu with the reason on screen, ready for another attempt
            return;
        }
        connectError = ""; //made it in - clear any stale failure message
        if(networkRunner.IsSharedModeMasterClient)
        {
            networkRunner.Spawn(runManagerPrefab, Vector3.zero, Quaternion.identity, PlayerRef.None); //spawn the run manager BEFORE the local player, so the master's own player registers into it
        }

        Vector3 spawnPos = playerSpawn.gameObject.transform.position; //where the player spawns in the world, can be changed to an array of spawn points later for more variety
        networkRunner.Spawn(playerPrefab, spawnPos, Quaternion.identity, networkRunner.LocalPlayer); //spawns player prefab on spawnpos
        lobbyCamera.gameObject.SetActive(false); //turn off the lobby camera once we're in game
    }
    //
}
