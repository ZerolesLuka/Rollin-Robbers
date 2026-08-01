using UnityEngine;
using UnityEngine.InputSystem;

// Player - the pause menu, which does NOT pause anything.
//
// This is a networked co-op game: there is no stopping the simulation for one person, and stopping it for everybody
// because one player wants to change their mouse sensitivity is worse. So Escape brings up a menu on YOUR screen and
// nothing else changes. The guard keeps patrolling. Your teammates keep playing. Your body is stood exactly where you
// left it, and it can still be heard, seen, chased and caught while you read the menu.
//
// That last part is a design decision, not an oversight - it needs saying on the menu itself, or the first time
// somebody gets grabbed mid-pause they'll file it as a bug.
//
// Time.timeScale is deliberately NEVER touched. It's global, so it would freeze the other players' rendering too and
// desync everything the moment it came back.
public partial class Player
{
    [Header("Pause menu (local only - the game keeps running)")]
    [SerializeField] private GameObject pauseMenuRoot; //the canvas to show. leave empty and Escape does nothing

    public bool IsPaused { get; private set; }

    private void UpdatePause()
    {
        if (Keyboard.current == null || pauseMenuRoot == null)
        {
            return;
        }

        //the safe keypad owns Escape while it's up (that's how you back out of typing a code), so don't steal it and
        //leave the player with a keypad they can't close.
        if (isEnteringSafeCode)
        {
            return;
        }

        if (Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            SetPaused(!IsPaused);
        }
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        if (pauseMenuRoot != null)
        {
            pauseMenuRoot.SetActive(paused);
        }

        //free the mouse so the menu is clickable, and take it back when we're done. the computer terminal does the
        //same dance, so whichever released it last wins - hence re-locking only when nothing else wants it free.
        if (paused)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (!isUsingComputer && !isEnteringSafeCode)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    public void ResumeGame() //hook the Resume button's OnClick to this
    {
        SetPaused(false);
    }

    public void LeaveSession() //hook a Leave/Quit button to this. shuts the session down, which drops us back to the menu scene
    {
        SetPaused(false);
        if (Runner != null)
        {
            Runner.Shutdown(); //GameBootstrap.OnShutdown does the teardown and reloads the menu scene
        }
    }
}
