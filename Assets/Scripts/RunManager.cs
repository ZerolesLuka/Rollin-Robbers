using System.Collections;
using Fusion;
using UnityEngine;

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
        Runner.LoadScene(SceneRef.FromIndex(buildIndex));
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_ReportCaught() //a suffocation death is decided on the victim's machine; this hops it to the master so the alive-count stays authoritative
    {
        OnPlayerCaught();
    }

    public void OnPlayerLeft()
    {
        if (!HasStateAuthority) return;
        playersAlive = Mathf.Max(0, playersAlive - 1); //a disconnect isn't a catch, just drop them from the count so the run can still resolve for the rest
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

    private void ChangeState(RunState newState)
    {
        State = newState;
        Debug.Log($"Run ended: {newState}"); // TEMP: trigger end screen / return to lobby here

        //force everyone to the outdoor scene so players left behind indoors (caught, locked up) also reach the van
        if (GuardPatrol.Instance != null)
        {
            Runner.Despawn(GuardPatrol.Instance.Object);
        }
        Runner.LoadScene(SceneRef.FromIndex(outdoorSceneBuildIndex));
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
