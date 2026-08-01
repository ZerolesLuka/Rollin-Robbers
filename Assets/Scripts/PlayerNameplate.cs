using UnityEngine;
using UnityEngine.UI;

// The name floating over a teammate. Put this on a world-space Canvas that's a CHILD of the Player prefab, same setup
// as WorldInteractPrompt - Canvas set to World Space, scaled to about 0.005, with a Text inside it.
//
// It reads the player's own [Networked] DisplayName, so every client sees the same name with nothing extra sent.
//
// It is DELIBERATELY not a wallhack. Names only show with line of sight and inside a range, so the van reads as a
// lobby where you can see who turned up, while the house stays dark - knowing a teammate is behind that wall would
// quietly undo a lot of what makes creeping around it tense.
public class PlayerNameplate : MonoBehaviour
{
    [SerializeField] private Text nameText;
    [SerializeField] private Canvas nameCanvas;          // leave empty and it grabs the Canvas on this object
    [SerializeField] private float showRange = 18f;      // past this the name fades out entirely
    [SerializeField] private float heightAboveHead = 0.35f;
    [SerializeField] private LayerMask sightBlockers;    // set to your geometry layers. a name seen through a wall is a wallhack
    [SerializeField] private bool keepConstantSize = true;

    private Player owner;
    private Vector3 baseScale;

    private void Awake()
    {
        owner = GetComponentInParent<Player>();
        if (nameCanvas == null)
        {
            nameCanvas = GetComponent<Canvas>();
        }
        baseScale = transform.localScale;
        Show(false);
    }

    private void LateUpdate()
    {
        //LateUpdate so the camera has already moved this frame - billboarding against a stale rotation is what makes
        //world-space labels jitter when you swing the view around.
        if (owner == null || owner.Object == null || !owner.Object.IsValid)
        {
            Show(false);
            return;
        }

        //never draw our OWN name, and never one belonging to somebody who isn't really there to be seen
        if (owner == Player.LocalPlayer || owner.IsHiding || owner.IsEliminated)
        {
            Show(false);
            return;
        }

        Player viewer = Player.LocalPlayer;
        if (viewer == null)
        {
            Show(false); //no local player yet
            return;
        }

        //whichever camera is actually drawing right now. keying off ViewCamera alone hid every nameplate the moment
        //you were eliminated, because spectating disables it - and knowing WHO you're watching is most of the point
        //of spectating.
        Camera viewerCamera = viewer.ActiveCamera;
        if (viewerCamera == null)
        {
            Show(false);
            return;
        }

        Vector3 headPosition = owner.transform.position + Vector3.up * (1f + heightAboveHead);
        Vector3 eyePosition = viewerCamera.transform.position;

        float distance = Vector3.Distance(eyePosition, headPosition);
        if (distance > showRange)
        {
            Show(false);
            return;
        }

        //line of sight. without this you'd read your teammates' positions through the walls of a stealth game.
        if (Physics.Linecast(eyePosition, headPosition, sightBlockers))
        {
            Show(false);
            return;
        }

        Show(true);
        if (nameText != null)
        {
            nameText.text = owner.DisplayName.ToString();
        }

        transform.position = headPosition;

        //match the camera then spin 180 - copying the rotation directly points the readable face away from the viewer
        //and you get mirrored text. same fix PlayerSignals and WorldInteractPrompt use.
        transform.rotation = viewerCamera.transform.rotation * Quaternion.Euler(0f, 180f, 0f);

        transform.localScale = keepConstantSize ? baseScale * Mathf.Max(0.01f, distance) : baseScale;
    }

    private void Show(bool visible)
    {
        if (nameCanvas != null)
        {
            if (nameCanvas.enabled != visible) nameCanvas.enabled = visible;
            return;
        }
        //no canvas assigned - fall back to the text itself, so a missing reference doesn't leave a name stuck on screen
        if (nameText != null && nameText.enabled != visible)
        {
            nameText.enabled = visible;
        }
    }
}
