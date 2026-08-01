using Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

// Player - what you look at once you're out of the run. Being caught used to leave you staring at your own frozen
// body for however long the heist had left, which in a 2-4 player co-op is most of a session spent watching nothing.
// Now you orbit a teammate: mouse to swing the camera round them, left click to jump to the next one.
//
// It drives a SEPARATE camera rather than borrowing the first-person one. The live camera is under Cinemachine's
// control and its transform is rewritten every frame by the brain, so anything we wrote there would be thrown away.
// A plain Camera we own outright has no such argument, and switching is just enabling one and disabling the other.
public partial class Player
{
    [Header("Spectating (after you're caught)")]
    [SerializeField] private Camera spectatorCamera;        //a second Camera on the player prefab, DISABLED in the inspector. leave empty and spectating simply doesn't happen
    [SerializeField] private float spectateDistance = 3.5f; //how far back it sits
    [SerializeField] private float spectateHeight = 1.6f;   //aim at the shoulders rather than the feet
    [SerializeField] private float spectateMinPitch = -35f; //how far under them you can swing
    [SerializeField] private float spectateMaxPitch = 70f;  //and how far over the top
    [SerializeField] private LayerMask spectateWallMask;    //set to the geometry layers. keeps the camera out of walls

    private Player spectateTarget;
    private float spectateYaw;
    private float spectatePitch = 12f;
    private bool spectatorActive;

    //Turn the spectator on the moment we're out and off again the moment we're back (the van ride revives everyone).
    //Driven from Update rather than the tick so the camera moves at render rate and doesn't judder at 32Hz.
    private void UpdateSpectator()
    {
        bool shouldSpectate = IsEliminated && spectatorCamera != null;

        if (shouldSpectate != spectatorActive)
        {
            spectatorActive = shouldSpectate;
            SetSpectatorCameraActive(shouldSpectate);
            if (shouldSpectate)
            {
                spectateTarget = NextSpectateTarget(null); //start on whoever's still going
                spectateYaw = transform.eulerAngles.y;     //begin facing the way we were, so the switch isn't disorienting
            }
        }

        if (!spectatorActive)
        {
            return;
        }

        //our target got caught too, or left. move on rather than orbiting a corpse.
        if (!IsWatchable(spectateTarget))
        {
            spectateTarget = NextSpectateTarget(spectateTarget);
        }

        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            spectateTarget = NextSpectateTarget(spectateTarget); //click through the crew
        }

        if (spectateTarget == null)
        {
            return; //nobody left to watch - the run is about to end anyway, so just hold the last shot
        }

        //same sensitivity as playing, so it feels like the same game rather than a different camera bolted on
        Vector2 look = playerInputActions != null ? playerInputActions.Player.Look.ReadValue<Vector2>() : Vector2.zero;
        spectateYaw += look.x * GameSettings.MouseSensitivity;
        spectatePitch = Mathf.Clamp(spectatePitch - look.y * GameSettings.LookSensitivityY, spectateMinPitch, spectateMaxPitch);

        Vector3 focus = spectateTarget.transform.position + Vector3.up * spectateHeight;
        Quaternion orbit = Quaternion.Euler(spectatePitch, spectateYaw, 0f);
        Vector3 wanted = focus + orbit * Vector3.back * spectateDistance;

        //pull in if there's a wall between us and them. a house is all small rooms, so without this you spend most of
        //your time spectating the inside of a wall.
        if (Physics.Linecast(focus, wanted, out RaycastHit hit, spectateWallMask))
        {
            wanted = hit.point + hit.normal * 0.2f;
        }

        spectatorCamera.transform.SetPositionAndRotation(wanted, Quaternion.LookRotation(focus - wanted, Vector3.up));
    }

    private bool IsWatchable(Player candidate)
    {
        return candidate != null
            && candidate != this
            && candidate.Object != null && candidate.Object.IsValid
            && !candidate.IsEliminated; //jailed or bear-trapped is still worth watching - arguably the best seat in the house
    }

    //The next living teammate after the one we're on, wrapping round. Walking ActivePlayers in order means clicking
    //cycles predictably rather than jumping about at random.
    private Player NextSpectateTarget(Player current)
    {
        int startIndex = current != null ? ActivePlayers.IndexOf(current) : -1;
        int count = ActivePlayers.Count;

        for (int step = 1; step <= count; step++)
        {
            Player candidate = ActivePlayers[((startIndex + step) % count + count) % count];
            if (IsWatchable(candidate))
            {
                return candidate;
            }
        }
        return null; //everyone else is out too
    }

    private void SetSpectatorCameraActive(bool on)
    {
        if (spectatorCamera != null)
        {
            spectatorCamera.enabled = on;
        }

        //hand the view over cleanly: our own first-person camera and its vcam go quiet while we're watching someone
        //else, and come back when the van ride revives us.
        if (ViewCamera != null)
        {
            ViewCamera.enabled = !on;
        }
        CinemachineVirtualCamera virtualCam = GetComponentInChildren<CinemachineVirtualCamera>(true);
        if (virtualCam != null)
        {
            virtualCam.enabled = !on;
        }
    }
}
