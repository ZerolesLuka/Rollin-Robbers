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

    //the guard is despawned and respawned on EVERY scene load (he can't navigate the outdoor NavMesh), so without
    //this, stepping out an exit door and back in handed you a brand-new guard: anger wiped, ears reset, fast asleep.
    //that made a door a panic button that deleted all the tension you'd built. he now carries his mood across the
    //trip and only forgets it when a genuinely new run starts.
    [Networked] public int RunGeneration { get; set; } // bumped by ResetForNewRun. the guard stamps his saved mood with the generation he LIVED in, and only restores it if that still matches - Fusion's Despawned() fires a tick or more after Runner.Despawn(), so it lands AFTER the reset and can't be ordered around
    [Networked] public NetworkBool HasSavedGuardState { get; set; } // false = no guard has been saved this run, roll him fresh
    [Networked] public float SavedGuardAnger { get; set; }
    [Networked] public float SavedGuardNoiseThreshold { get; set; }
    [Networked] public int SavedGuardRunGeneration { get; set; } // which run the saved mood belongs to
    [Networked] public int SavedGuardAsleepChances { get; set; }

    //Same problem, same fix, for the dog. He's despawned on every scene load too, so stepping outside and back in
    //handed you a dog who'd never been disturbed - he'd go straight back to his bed no matter how many times you'd
    //already woken him. Only restDisturbances matters: everything else about him is genuinely per-room.
    [Networked] public NetworkBool HasSavedDogState { get; set; }
    [Networked] public int SavedDogDisturbances { get; set; }

    [Networked] public int FloorboardSeed { get; private set; } // shared RNG seed so every client scatters the squeaky floorboards in the SAME spots; re-rolled each run for a fresh noise map

    [Networked] public NetworkBool VanBackClosed { get; private set; } // true while a run is over and everyone's pooled in the van - a scene barrier seals the van's back so nobody wanders off before picking a destination. any route button reopens it. networked so every client's barrier agrees

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
        if (HasStateAuthority)
        {
            State = RunState.InProgress; //only the authority may write networked state. a joining client used to run this too, which is an unauthorized write Fusion just discards
            Host = Runner.LocalPlayer; //we spawned this, so we're the room's creator - remember it for everyone
            FloorboardSeed = new System.Random().Next(); //master rolls the first run's layout
        }
    }

    //GuardPatrol and DogAI both null their static Instance on the way out; this one never did, so on shutdown it was
    //left pointing at a despawned RunManager. Unity's fake-null covers that only once the GameObject is actually
    //destroyed - in the window between Runner.Despawn and that destruction, `RunManager.Instance != null` still
    //passes, and the several call sites that check ONLY for null (rather than Object.IsValid too) went on to fire an
    //RPC at an object that is no longer in the simulation.
    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (Instance == this) Instance = null; //only if it's still ours - a new session may already have claimed it
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
        DespawnHouseAI();
        LoadSceneForEveryone(buildIndex);
    }

    private void DespawnHouseAI() //neither guard type can navigate the outdoor NavMesh, so both are cleaned up before any scene change
    {
        //re-check the objects are actually live. Runner.Despawn only QUEUES the despawn and Despawned() (which nulls
        //Instance) fires a tick or more later, so a stale Instance can survive long enough to be despawned twice.
        if (GuardPatrol.Instance != null && GuardPatrol.Instance.Object != null && GuardPatrol.Instance.Object.IsValid)
        {
            Runner.Despawn(GuardPatrol.Instance.Object);
        }
        if (DogAI.Instance != null && DogAI.Instance.Object != null && DogAI.Instance.Object.IsValid)
        {
            Runner.Despawn(DogAI.Instance.Object);
        }
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
        VanBackClosed = false; //picked a destination - the van's back opens onto whatever scene we're routing to. this runs BEFORE the scene load, and the flag survives it (RunManager is DontDestroyOnLoad + networked), so the destination van starts open
        //despawn BEFORE any reset. the guard saves his mood into this RunManager as he despawns, so resetting first
        //would just get overwritten by the guard he was a second ago and he'd walk into the new house still furious.
        DespawnHouseAI();
        if (startNewRun)
        {
            ResetForNewRun(); //House button - back to InProgress so the run-over van ride doesn't instantly re-trigger
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

    [Rpc(RpcSources.All, RpcTargets.All)]
    public void RPC_SetDoorOpen(Vector3 doorPosition, NetworkBool open) //ONE path for EVERY openable - house doors, cupboards, drawers, jewellery boxes - whether a player pressed E or the guard shoved it. none of them are NetworkObjects, so we identify one by WHERE it is: static scene geometry sits at identical coordinates on every client, so "nearest hinge to this point" resolves the same for everyone, with nothing to configure per object
    {
        //searches SwingingHinge rather than Door on purpose. every door owns a hinge, so doors are still covered, but
        //a prop no longer needs a Door component bolted on just to be openable - one script per object, as intended.
        const float maxDoorMatchDistance = 1f; //the match must be essentially exact. without a cap, "nearest" would happily grab something on the far side of the house - or one lingering in the static list from the scene we just left (SpawnPoint hit exactly that) - and swing the wrong one
        SwingingHinge nearest = null;
        float nearestDistance = maxDoorMatchDistance;
        foreach (SwingingHinge hinge in SwingingHinge.AllHinges)
        {
            //a safe's door isn't openable through here either. the interaction scan already refuses to target it, but
            //this RPC resolves by POSITION and would happily match a safe stood next to a door - and the last time a
            //guard like this only lived at the call site (the wedge check), the network path quietly bypassed it.
            if (!hinge.PlayerOperable) continue;

            float distance = Vector3.Distance(hinge.transform.position, doorPosition);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = hinge;
            }
        }
        if (nearest != null)
        {
            //a wedged door refuses to OPEN, whoever asked. Door.SetOpen has this same guard, but nothing reaches it
            //any more now that every openable routes through the hinge instead - so the check has to live here too,
            //or a wedge would only stop the prompt from offering rather than actually holding the door shut.
            if (open)
            {
                Door houseDoor = nearest.GetComponent<Door>();
                if (houseDoor != null && houseDoor.IsWedged) return;
            }
            nearest.SetOpen(open);
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_SellItems(int value) //a player sold their carried inventory at the pawn shop - add its worth to the shared money
    {
        Money += value;
    }

    //Buying a tool. The WALLET is shared and the TOOL is personal, which is the whole point: the crew decides
    //together who gets the crowbar, and that person is now the one going to the safe.
    //
    //The authority checks the price and deducts, THEN tells the buyer to equip it. Doing it the other way round lets
    //two players on the same tick both pass an affordability check against the same money and buy one tool each.
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_BuyTool(int toolTypeValue, PlayerRef buyer)
    {
        ToolType tool = (ToolType)toolTypeValue;
        if (tool == ToolType.None) return;

        int cost = ToolTable.CostOf(tool);
        if (Money < cost) return; //can't afford it - the buyer's HUD already greyed it out, this is the authoritative no

        foreach (Player player in Player.ActivePlayers)
        {
            if (player == null || player.Object == null || player.Object.InputAuthority != buyer) continue;

            if (player.HasTool(tool) || !player.HasFreeToolSlot || !player.HasRoomForTool(tool))
            {
                return; //already owns it, nowhere to put it, or their bag is too full to give up the space. charge nobody
            }

            Money -= cost;
            player.RPC_GrantTool(toolTypeValue); //lands on the buyer's own machine, which owns their tool slots
            return;
        }
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

            //the house just RE-STOCKED (walking back in through an exit door respawns every item), so the theft
            //count has to start over with it. leaving it running while the total resets let clear-% climb past
            //100% on a second trip in, which then wrote an unbeatable number into BestClearPercent forever.
            //both numbers now always describe the SAME house instance, so the percentage can't exceed 100.
            GatheredLootValue = 0;
        }

        HouseLootTotal += value;
    }

    private void LoadSceneForEveryone(int buildIndex) //every scene load goes through here so the loot tally always gets scoped - see ReportHouseLoot
    {
        sceneLoadCounter++;
        CloseSessionToLateJoiners();
        Runner.LoadScene(SceneRef.FromIndex(buildIndex));
    }

    private void CloseSessionToLateJoiners() //the crew has left the starting area, so lock the door behind them
    {
        //a mid-run joiner is broken by design here: GameBootstrap spawns them at a serialized Transform that lives in
        //the menu scene, and that object no longer exists once we've loaded the house - so they either NRE on spawn or
        //land at stale outdoor coordinates inside the geometry. doors are the other half: they aren't NetworkObjects,
        //so a joiner missed every RPC_SetDoorOpen and would see a shut house everyone else is walking through.
        //rather than patch both, refuse the join. joining is still open the whole time the crew is at the van.
        if (Runner != null && Runner.SessionInfo != null && Runner.SessionInfo.IsOpen)
        {
            Runner.SessionInfo.IsOpen = false;
        }
    }

    private void ResetForNewRun() //fresh heist: run active again, everyone counted alive, the house re-stocked. Money and each player's carried inventory are kept - only the house resets
    {
        State = RunState.InProgress;
        countedOutPlayers.Clear(); //fresh run revived everyone on the van ride - nobody's counted out anymore
        playersAlive = Player.ActivePlayers.Count; //everyone was revived on the van ride, so they all count again
        GatheredLootValue = 0; //fresh house, nothing stolen yet - resets the guard's theft-suspicion baseline
        HouseLootTotal = 0; //ItemSpawner re-tallies the new run's loot when the indoor scene reloads
        HasSavedGuardState = false; //genuinely new heist: the guard forgets last run's mood and gets rolled fresh
        HasSavedDogState = false;   //and the dog goes back to sleeping through anything
        RunGeneration++;            //and stamp a new generation, so a mood saved by the OLD guard (whose Despawned fires after this) can't be mistaken for this run's
        RunTime = 0f; //fresh clock for the new heist
        FloorboardSeed = new System.Random().Next(); //fresh squeaky-floorboard layout so the noise map changes every run
    }

    private void ChangeState(RunState newState)
    {
        State = newState;
        //the end screens themselves live in HUD, which watches State change - nothing to trigger from here
#if UNITY_EDITOR
        Debug.Log($"Run ended: {newState}");
#endif

        //run's over - everyone rides to the van, so seal its back until they pick where to go next. this one line
        //covers both ways a run ends: pressing E on the van (Success) and the whole team getting caught (Caught).
        if (newState == RunState.Success || newState == RunState.Caught)
        {
            VanBackClosed = true;
        }

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
            DespawnHouseAI();
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
