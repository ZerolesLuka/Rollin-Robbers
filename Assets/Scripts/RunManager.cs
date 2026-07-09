using System.Collections;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

public class RunManager : NetworkBehaviour
{
    public static RunManager Instance { get; private set; }

    public enum RunState { InProgress, Success, Caught }

    [Networked] public RunState State { get; private set; }
    [Networked] public int playersAlive { get; set; }

    [SerializeField] public int totalLootValue; // total value of all loot in the house - set in Inspector for a score screen later
    [Networked] public int GatheredLootValue { get; private set; } // what the team has picked up so far - replicates to all clients
    [Networked] private ulong lootedMask { get; set; } // one bit per loot item (up to 64); bit N set = item N is already taken
    [Networked] public int EntrySpawnPointId { get; private set; } // which PlayerSpawnN to teleport to after a scene load - set by whichever door triggers the transition
    [SerializeField] private int outdoorSceneBuildIndex = 0; // the scene the van lives in - everyone gets pulled here when the run ends, even players still indoors

    [Networked] public PlayerRef ComputerUser { get; private set; } // who's currently at the van computer; PlayerRef.None = free. Locks it to one person at a time
    public bool IsComputerFree => ComputerUser == PlayerRef.None;

    public override void Spawned()
    {
        DontDestroyOnLoad(gameObject); //survive scene loads
        Instance = this;
        State = RunState.InProgress;
    }

    public void RegisterPlayer()
    {
        if (!HasStateAuthority) return;
        playersAlive++;
    }

    public void OnPlayerCaught()
    {
        if (!HasStateAuthority || State != RunState.InProgress) return;
        playersAlive--;
        if (playersAlive <= 0) ChangeState(RunState.Caught);
    }

    public bool IsLooted(int lootId) => (lootedMask & (1ul << lootId)) != 0;

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_ClaimLoot(int lootId, int value)
    {
        ulong bit = 1ul << lootId;
        if ((lootedMask & bit) != 0) return; // already taken (two players pressed E at the same time)
        lootedMask |= bit;
        GatheredLootValue += value;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_LoadScene(int buildIndex, int spawnPointId)
    {
        EntrySpawnPointId = spawnPointId;
        if (GuardPatrol.Instance != null)
        {
            Runner.Despawn(GuardPatrol.Instance.Object);
        }
        if (DogAI.Instance != null)
        {
            Runner.Despawn(DogAI.Instance.Object); //neither guard type can navigate the outdoor NavMesh - clean up before leaving
        }
        Runner.LoadScene(SceneRef.FromIndex(buildIndex));
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_ReportCaught() //a suffocation death is decided on the victim's machine; this hops it to the master so the alive-count stays authoritative
    {
        OnPlayerCaught();
    }

    public void OnPlayerLeft(PlayerRef player)
    {
        if (!HasStateAuthority) return;
        playersAlive = Mathf.Max(0, playersAlive - 1); //a disconnect isn't a catch, just drop them from the count so the run can still resolve for the rest
        if (ComputerUser == player)
        {
            ComputerUser = PlayerRef.None; //don't leave the computer locked forever if the person on it disconnected
        }
    }

    public void OnLootExtracted()
    {
        if (!HasStateAuthority || State != RunState.InProgress) return;
        ChangeState(RunState.Success);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_StartGetaway() //any player can start the van; the authority flips the run to Success and it replicates to everyone
    {
        OnLootExtracted();
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_Route(int buildIndex, int spawnPointId, bool startNewRun) //van computer buttons - route the crew to the house or the pawn shop
    {
        EntrySpawnPointId = spawnPointId;
        if (startNewRun)
        {
            ResetForNewRun(); //House button - back to InProgress so the run-over van ride doesn't instantly re-trigger
        }
        if (GuardPatrol.Instance != null)
        {
            Runner.Despawn(GuardPatrol.Instance.Object);
        }
        if (DogAI.Instance != null)
        {
            Runner.Despawn(DogAI.Instance.Object);
        }
        Runner.LoadScene(SceneRef.FromIndex(buildIndex));
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_ClaimComputer(PlayerRef user) //first player to grab the computer gets it; everyone else is refused until they release it
    {
        if (ComputerUser == PlayerRef.None)
        {
            ComputerUser = user;
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_ReleaseComputer(PlayerRef user) //only the person holding it can free it
    {
        if (ComputerUser == user)
        {
            ComputerUser = PlayerRef.None;
        }
    }

    private void ResetForNewRun() //fresh heist: run active again, everyone counted alive, the house refilled with loot. the team's accumulated haul (GatheredLootValue) is kept to sell later at the pawn shop
    {
        State = RunState.InProgress;
        playersAlive = Player.ActivePlayers.Count; //everyone was revived on the van ride, so they all count again
        lootedMask = 0; //re-lootable house for the new run - the items reappear (Lootable reads IsLooted)
    }

    private void ChangeState(RunState newState)
    {
        State = newState;
        Debug.Log($"Run ended: {newState}"); // TEMP: trigger end screen / return to lobby here

        //only reload if players are still indoors - if the run ended at the van (success) everyone's already outside, and reloading the scene you're standing in doesn't reliably fire activeSceneChanged. players ride to their van seat from Player.FixedUpdateNetwork either way.
        if (SceneManager.GetActiveScene().buildIndex != outdoorSceneBuildIndex)
        {
            if (GuardPatrol.Instance != null)
            {
                Runner.Despawn(GuardPatrol.Instance.Object);
            }
            if (DogAI.Instance != null)
            {
                Runner.Despawn(DogAI.Instance.Object);
            }
            Runner.LoadScene(SceneRef.FromIndex(outdoorSceneBuildIndex)); //brings the indoor players out; the van seats exist in that scene
        }
    }
    public override void FixedUpdateNetwork()
    {
    switch(State)
     {
        case RunState.InProgress:
            //Maybe start calculating loot grabbed, pass it on to success case

         break;
        case RunState.Caught:
            //When all players die

         break;
        case RunState.Success:
            //Players deliberately chose to leave the house
            //Some sort of ranking? who did the best, total value collect / total house value

         break;
     }
    }

}
