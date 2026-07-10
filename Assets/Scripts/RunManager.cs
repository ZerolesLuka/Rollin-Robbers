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
    [Networked] public int HouseLootTotal { get; private set; } // total worth of loot ItemSpawner placed in the house this run - the success screen grades the haul against it
    [Networked] public int BestClearPercent { get; private set; } // best % of a house the crew has ever cleared in one run - persists across runs as a bragging-rights stat
    [Networked] public float RunTime { get; private set; } // seconds the current heist has been running - counts up while InProgress, frozen once the run ends
    [Networked] private ulong lootedMask { get; set; } // one bit per loot item (up to 64); bit N set = item N is already taken
    [Networked] public int EntrySpawnPointId { get; private set; } // which PlayerSpawnN to teleport to after a scene load - set by whichever door triggers the transition
    [SerializeField] private int outdoorSceneBuildIndex = 0; // the scene the van lives in - everyone gets pulled here when the run ends, even players still indoors

    [Networked] public PlayerRef ComputerUser { get; private set; } // who's currently at the van computer; PlayerRef.None = free. Locks it to one person at a time
    public bool IsComputerFree => ComputerUser == PlayerRef.None;

    [Networked] public int Money { get; private set; } // the team's banked cash - persists across runs, grows when they sell loot at the pawn shop

    [Networked] public int FloorboardSeed { get; private set; } // shared RNG seed so every client scatters the squeaky floorboards in the SAME spots; re-rolled each run for a fresh noise map

    public override void Spawned()
    {
        DontDestroyOnLoad(gameObject); //survive scene loads
        Instance = this;
        State = RunState.InProgress;
        if (HasStateAuthority) FloorboardSeed = new System.Random().Next(); //master rolls the first run's layout
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

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_SellItems(int value) //a player sold their carried inventory at the pawn shop - add its worth to the shared money
    {
        Money += value;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_ReportLootTaken(int value) //a WorldItem was picked up off the shelf - the house is missing that much now, which feeds the guard's suspicion
    {
        GatheredLootValue += value;
    }

    public void ReportHouseLoot(int value) //ItemSpawner (master only) tallies the worth of everything it spawned this run, so Success can score the haul as a percentage of what was there
    {
        if (!HasStateAuthority) return;
        HouseLootTotal += value;
    }

    private void ResetForNewRun() //fresh heist: run active again, everyone counted alive, the house re-stocked. Money and each player's carried inventory are kept - only the house resets
    {
        State = RunState.InProgress;
        playersAlive = Player.ActivePlayers.Count; //everyone was revived on the van ride, so they all count again
        lootedMask = 0; //re-lootable house for the new run - any legacy Lootables reappear
        GatheredLootValue = 0; //fresh house, nothing stolen yet - resets the guard's theft-suspicion baseline
        HouseLootTotal = 0; //ItemSpawner re-tallies the new run's loot when the indoor scene reloads
        RunTime = 0f; //fresh clock for the new heist
        FloorboardSeed = new System.Random().Next(); //fresh squeaky-floorboard layout so the noise map changes every run
    }

    private void ChangeState(RunState newState)
    {
        State = newState;
        Debug.Log($"Run ended: {newState}"); // TEMP: trigger end screen / return to lobby here

        if (newState == RunState.Success && HouseLootTotal > 0) //remember the crew's best clear-out across every run
        {
            int clearPercent = Mathf.RoundToInt(100f * GatheredLootValue / HouseLootTotal);
            if (clearPercent > BestClearPercent)
            {
                BestClearPercent = clearPercent;
            }
        }

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
            if (HasStateAuthority)
            {
                RunTime += Runner.DeltaTime; //clock the length of the active heist; the HUD shows it live
            }
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
