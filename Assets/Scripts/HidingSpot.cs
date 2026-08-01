using Cinemachine;
using System.Collections.Generic;
using UnityEngine;

// A wardrobe/locker/under-the-bed spot. Occupancy is NOT stored here as a local bool - that let two players on
// different machines each think the spot was free and pile into the same one. Instead the player replicates the
// id of the spot they're inside ([Networked] Player.HidingSpotId), so every client derives the same answer from
// the same replicated state. spotId follows the same convention as SpawnPoint.spawnId: set it in the inspector,
// unique within a scene.
public class HidingSpot : MonoBehaviour
{
    public static readonly List<HidingSpot> AllHidingSpots = new List<HidingSpot>();

    [SerializeField] private CinemachineVirtualCamera virtualCamera;
    [SerializeField] public float interactRange = 3f;
    [SerializeField] public int spotId = 0; //unique WITHIN a scene - duplicates break occupancy, so we shout about them on enable

    private void Start()
    {
        //force the spot's camera off at load, exactly like ComputerTerminal does. its priority is deliberately
        //HIGHER than the player's (that's what makes it take over when you climb in), so if it's left enabled in
        //the scene it wins from frame one and you spawn staring into a wardrobe. enter/exit toggle it from here.
        SetSpotCamera(false);
    }

    private void OnEnable()
    {
        AllHidingSpots.Add(this);

        //fail fast: two spots sharing an id in one scene would read as a single spot again, which is the exact bug this file exists to fix
        foreach (HidingSpot other in AllHidingSpots)
        {
            if (other == this) continue;
            if (other.gameObject.scene != gameObject.scene) continue; //ids only need to be unique per scene
            if (other.spotId == spotId)
            {
                Debug.LogError($"[HidingSpot] '{name}' and '{other.name}' both use spotId {spotId} in scene '{gameObject.scene.name}'. Ids must be unique per scene or players can share a hiding spot.", this);
            }
        }
    }

    private void OnDisable() => AllHidingSpots.Remove(this);

    public bool IsOccupied //is ANYONE (us included) hiding in this spot right now - read off replicated player state, so all clients agree
    {
        get
        {
            foreach (Player player in Player.ActivePlayers)
            {
                if (player.IsHiding && player.HidingSpotId == spotId) return true;
            }
            return false;
        }
    }

    public bool IsOccupiedByLocalPlayer => Player.LocalPlayer != null && Player.LocalPlayer.IsHiding && Player.LocalPlayer.HidingSpotId == spotId;

    //The camera is DERIVED from the networked state, not switched on and off by the enter/exit calls. It used to be
    //the latter, and then anything that cleared IsHiding without going through OnSpotExit left the vcam running: get
    //ripped out of the wardrobe by the guard and you'd be hauled to the closet still staring at the coats, because
    //RPC_PulledFromHiding only clears the flag. Same hole on the run-end van ride. Reading the state every frame
    //means every exit path restores the view for free, however it happened.
    private void Update()
    {
        bool shouldBeInside = IsOccupiedByLocalPlayer;
        if (shouldBeInside != cameraIsOn)
        {
            SetSpotCamera(shouldBeInside);
        }
    }

    public void OnSpotEnter()
    {
        Player.LocalPlayer.SetHiding(true, spotId); //publishes WHICH spot we're in, which is what makes it occupied for everyone else. Update picks the camera up from there
    }

    public void OnSpotExit()
    {
        Player.LocalPlayer.SetHiding(false, NoSpot); //frees the spot for everyone the moment it replicates
    }

    private bool cameraIsOn; //cached so Update isn't poking SetActive on the vcam every single frame

    private void SetSpotCamera(bool on)
    {
        cameraIsOn = on;
        if (virtualCamera == null) return;

        //toggle the OBJECT as well as the component. enabling the component on a deactivated GameObject silently
        //does nothing, so this works whichever way the camera was switched off in the scene.
        virtualCamera.gameObject.SetActive(on);
        virtualCamera.enabled = on;
    }

    public const int NoSpot = -1; //"not in any hiding spot" - what a free player's HidingSpotId reads as
}
