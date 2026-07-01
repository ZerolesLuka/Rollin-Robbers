using Fusion;
using UnityEngine;

public class RunManager : NetworkBehaviour
{
    public static RunManager Instance { get; private set; }

    public enum RunState { InProgress, Success, Caught }

    [Networked] public RunState State { get; private set; }
    [Networked] public int playersAlive { get; set; }

    public override void Spawned()
    {
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

    public void OnLootExtracted()
    {
        if (!HasStateAuthority || State != RunState.InProgress) return;
        ChangeState(RunState.Success);
    }

    private void ChangeState(RunState newState)
    {
        State = newState;
        Debug.Log($"Run ended: {newState}"); // TEMP: trigger end screen / return to lobby here
    }
}
