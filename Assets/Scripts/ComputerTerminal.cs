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
    [SerializeField] public float interactRange = 3f;

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

    //THE MAP. Drawn here rather than on the screenUI canvas for one practical reason: that canvas gets SetActive(false)
    //when nobody's sat down, and an inactive GameObject doesn't run OnGUI - so the map would never appear. Drawing it
    //from the terminal, which is always active, sidesteps that entirely.
    //
    //It also means the loop closes with NO wiring: this replaces the two OnClick buttons that were never hooked up, so
    //there's nothing left to assign. Swap it for the real canvas later and only this method goes.
    private void OnGUI()
    {
        Player me = Player.LocalPlayer;
        if (me == null || me.CurrentTerminal != this) return;

        const float width = 520f;
        float x = Screen.width * 0.5f - width * 0.5f;
        float y = Screen.height * 0.5f - 210f;

        GUI.Box(new Rect(x - 14f, y - 14f, width + 28f, 430f), GUIContent.none);
        GUI.Label(new Rect(x, y, width, 24f), "WHERE TO?");
        GUI.Label(new Rect(x, y + 22f, width, 24f), "E to get out of the seat.");
        y += 56f;

        foreach (Destination stop in Neighbourhood.Stops)
        {
            GUI.enabled = stop.unlocked;
            if (GUI.Button(new Rect(x, y, 210f, 30f), stop.unlocked ? stop.name : $"{stop.name}  (locked)"))
            {
                //everything routes through RunManager so the WHOLE CREW travels, not just whoever's in the chair
                if (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
                {
                    Exit(); //stand up first, or we'd arrive still frozen in a seat that no longer exists
                    RunManager.Instance.RPC_Route(stop.sceneBuildIndex, stop.spawnPointId, stop.startsNewRun);
                }
                return; //the list is about to be irrelevant - we're driving
            }
            GUI.enabled = true;

            GUI.Label(new Rect(x + 220f, y + 6f, width - 220f, 24f), stop.detail);
            y += 34f;
        }
    }
}
