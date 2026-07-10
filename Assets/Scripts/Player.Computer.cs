using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// Player - the van computer. We don't sit down the instant E is pressed; we request the networked lock and only
// Enter once it's granted (UpdateComputerClaim, called each frame from the core Update). Enter/Exit free the cursor
// for the on-screen route buttons and hand the lock back when we're done.
public partial class Player
{
    private void UpdateComputerClaim()
    {
        if (pendingTerminal == null || isUsingComputer) return;
        if (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid) return;

        if (RunManager.Instance.ComputerUser == Object.InputAuthority) //the lock is ours - sit down
        {
            ComputerTerminal terminal = pendingTerminal;
            pendingTerminal = null;
            terminal.Enter();
        }
        else if (!RunManager.Instance.IsComputerFree) //someone else grabbed it first - give up our request
        {
            pendingTerminal = null;
        }
    }

    public void EnterComputer(ComputerTerminal terminal)
    {
        isUsingComputer = true;
        currentTerminal = terminal;
        Cursor.lockState = CursorLockMode.None; //free the cursor for the on-screen buttons (coming next)
        Cursor.visible = true;
    }

    public void ExitComputer()
    {
        isUsingComputer = false;
        currentTerminal = null;
        Cursor.lockState = CursorLockMode.Locked; //back to mouselook
        Cursor.visible = false;
        if (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        {
            RunManager.Instance.RPC_ReleaseComputer(Object.InputAuthority); //free the lock for the next player
        }
    }
}
