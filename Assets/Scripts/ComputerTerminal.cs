using Cinemachine;
using System.Collections.Generic;
using UnityEngine;

// The van's computer. Press E in range to "enter" it - the camera blends to a vcam aimed at the screen,
// you can't move, and the cursor frees up (for the House/Pawn Shop buttons coming next). Press E again to exit.
// Same Cinemachine-focus trick as HidingSpot; the freeze lives on Player as a local state.
public class ComputerTerminal : MonoBehaviour
{
    public static readonly List<ComputerTerminal> AllTerminals = new List<ComputerTerminal>();

    [SerializeField] private CinemachineVirtualCamera screenCamera; // positioned looking at the screen; give it a higher Priority than the player's vcam
    [SerializeField] private GameObject screenUI; // the canvas/panel with the House + Pawn Shop buttons; shown only while sat down
    [SerializeField] public float interactRange = 2f;

    private void OnEnable() => AllTerminals.Add(this);
    private void OnDisable() => AllTerminals.Remove(this);
    private void Start()
    {
        if (screenCamera != null) screenCamera.enabled = false; // off until someone sits down at it
        if (screenUI != null) screenUI.SetActive(false);
    }

    public void Enter() // called locally once this client's player has been granted the networked lock
    {
        Player.LocalPlayer.EnterComputer(this);
        if (screenCamera != null) screenCamera.enabled = true;
        if (screenUI != null)
        {
            screenUI.SetActive(true);
            Canvas canvas = screenUI.GetComponent<Canvas>();
            if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
            {
                canvas.worldCamera = Player.LocalPlayer.ViewCamera; //world-space UI raycasts clicks through this camera; set it to whoever's actually sitting down
            }
        }
    }

    public void Exit()
    {
        Player.LocalPlayer.ExitComputer();
        if (screenCamera != null) screenCamera.enabled = false;
        if (screenUI != null) screenUI.SetActive(false);
    }
}
