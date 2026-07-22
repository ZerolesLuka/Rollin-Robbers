using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

public class RunManager : NetworkBehaviour
{
    public static RunManager Instance { get; private set; }

    public enum RunState { InProgress, Success, Caught }

    [Networked] public RunState State { get; private set; }
    [Networked] public int playersAlive { get; set; }

    [Networked] public int GatheredLootValue { get; private set; } // what the team has picked up so far - replicates to all clients
    [Networked] public int HouseLootTotal { get; private set; } // total worth of loot ItemSpawner placed in the house this run - the success screen grades the haul against it, and the guard measures theft against it
    [Networked] public int BestClearPercent { get; private set; } // best % of a house the crew has ever cleared in one run - persists across runs as a bragging-rights stat
    [Networked] public float RunTime { get; private set; } // seconds the current heist has been running - counts up while InProgress, frozen once the run ends
    [Networked] public Vector3 LastStolenPosition { get; private set; } // where the most recent item was lifted from - the guard investigates this exact spot when he notices things missing
    [Networked] public int EntrySpawnPointId { get; private set; } // which PlayerSpawnN to teleport to after a scene load - set by whichever door triggers the transition
    [SerializeField] private int outdoorSceneBuildIndex = 0; // the scene the van lives in - everyone gets pulled here when the run ends, even players still indoors

    [Networked] public PlayerRef Host { get; private set; } // whoever CREATED this room. stamped once at spawn and replicated, so every client can recognise the host leaving - you can't detect it by watching master-client status, because Fusion instantly promotes a replacement and only that one client would notice

    [Networked] public PlayerRef ComputerUser { get; private set; } // who's currently at the van computer; PlayerRef.None = free. Locks it to one person at a time
    public bool IsComputerFree => ComputerUser == PlayerRef.None;

    [Networked] public int Money { get; private set; } // the team's banked cash - persists across runs, grows when they sell loot at the pawn shop

    [Networked] public int FloorboardSeed { get; private set; } // shared RNG seed so every client scatters the squeaky floorboards in the SAME spots; re-rolled each run for a fresh noise map

    //master-only bookkeeping so a loot tally belongs to exactly ONE scene load. every ItemSpawner runs its
    //Start on every load, and ReportHouseLoot adds - so without this, walking back into the house through a
    //door tallies the house on top of itself and the guard ends up half as suspicious as he should be.
    private int sceneLoadCounter;
    private int lastTalliedSceneLoad = -1;

    //master-only: who's already been counted out of the alive tally (caught or suffocated). a disconnect
    //decrements playersAlive too, so without this an eliminated player who then leaves gets counted out
    //TWICE and the run ends early for everyone still playing. cleared when a fresh run revives everyone.
    private readonly HashSet<PlayerRef> countedOutPlayers = new HashSet<PlayerRef>();

    public override void Spawned()
    {
        DontDestroyOnLoad(gameObject); //survive scene loads
        Instance = this;
        State = RunState.InProgress;
        if (HasStateAuthority)
        {
            Host = Runner.LocalPlayer; //we spawned this, so we're the room's creator - remember it for everyone
            FloorboardSeed = new System.Random().Next(); //master rolls the first run's layout
        }
    }

    public void RegisterPlayer()
    {
        if (!HasStateAuthority) return;
        playersAlive++;
    }

    public void OnPlayerCaught(PlayerRef caught)
    {
        if (!HasStateAuthority || State != RunState.InProgress) return;
        if (!countedOutPlayers.Add(caught)) return; //already counted out (e.g. guard + suffocation racing) - never drop the count twice for one player
        playersAlive--;
        if (playersAlive <= 0) ChangeState(RunState.Caught);
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
        LoadSceneForEveryone(buildIndex);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_ReportCaught(PlayerRef victim) //a suffocation death is decided on the victim's machine; this hops it to the master so the alive-count stays authoritative
    {
        OnPlayerCaught(victim);
    }

    public void OnPlayerLeft(PlayerRef player)
    {
        if (!HasStateAuthority) return;
        if (countedOutPlayers.Remove(player)) //already counted out when they were caught - forget them, but DON'T drop the count a second time
        {
            //nothing more to do for the tally; they were never in it
        }
        else
        {
            playersAlive = Mathf.Max(0, playersAlive - 1); //a live player disconnected - drop them from the count so the run can still resolve for the rest

            //that may have been the LAST person still in play. a catch checks this, but a disconnect never did,
            //so the run sat in InProgress forever - and anyone already eliminated is frozen (IsEliminated returns
            //early from FixedUpdateNetwork) with no way out, because the van ride only fires once the run is over.
            if (playersAlive <= 0 && State == RunState.InProgress)
            {
                ChangeState(RunState.Caught); //nobody left to escape - end it, which releases the eliminated players to the van
            }
        }
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
        LoadSceneForEveryone(buildIndex);
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
    public void RPC_ReportLootTaken(int value, Vector3 stolenFrom) //a WorldItem was lifted - the house is missing that much now (feeds the guard's suspicion), and we remember WHERE so he can investigate the actual crime scene
    {
        GatheredLootValue += value;
        LastStolenPosition = stolenFrom;
    }

    public void ReportHouseLoot(int value) //ItemSpawner (master only) tallies the worth of everything it spawned this run, so Success can score the haul as a percentage of what was there
    {
        if (!HasStateAuthority) return;

        //the FIRST spawner to report after a scene load starts the tally over; the rest of that same load add
        //to it. that way several ItemSpawners in one house still sum together, but re-entering the house (or
        //a spawner sitting in another scene) can't stack its loot onto the previous count.
        if (lastTalliedSceneLoad != sceneLoadCounter)
        {
            HouseLootTotal = 0;
            lastTalliedSceneLoad = sceneLoadCounter;
        }

        HouseLootTotal += value;
    }

    private void LoadSceneForEveryone(int buildIndex) //every scene load goes through here so the loot tally always gets scoped - see ReportHouseLoot
    {
        sceneLoadCounter++;
        Runner.LoadScene(SceneRef.FromIndex(buildIndex));
    }

    private void ResetForNewRun() //fresh heist: run active again, everyone counted alive, the house re-stocked. Money and each player's carried inventory are kept - only the house resets
    {
        State = RunState.InProgress;
        countedOutPlayers.Clear(); //fresh run revived everyone on the van ride - nobody's counted out anymore
        playersAlive = Player.ActivePlayers.Count; //everyone was revived on the van ride, so they all count again
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
            LoadSceneForEveryone(outdoorSceneBuildIndex); //brings the indoor players out; the van seats exist in that scene
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
