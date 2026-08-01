using UnityEngine;
using UnityEngine.UI;

// The "E  Open the drawer" label, drawn ON the thing rather than pinned to the middle of your screen. It parks itself
// at whatever the local player is currently in reach of and turns to face the camera, so looking around slides it
// across the screen exactly like a real object would. That's the whole trick: it lives in the world, not on the HUD.
//
// Put this on a world-space Canvas that's a CHILD of the Player prefab. Being under the player means it survives
// scene loads for free (the player is DontDestroyOnLoad) and there's nothing to wire per scene. Every player prefab
// carries one, so it switches itself off unless it belongs to the player actually sat at this machine.
//
// Wiring: Canvas -> Render Mode: World Space, scale it down to about 0.005, drop a Text inside it, then assign that
// Text below. Nothing else.
public class WorldInteractPrompt : MonoBehaviour
{
    [SerializeField] private Text promptText;              // the label itself
    [SerializeField] private Canvas promptCanvas;          // leave empty and it grabs the Canvas on this object
    [SerializeField] private float verticalOffset = 0.15f; // nudge above the object's middle so the label isn't buried in the mesh
    [SerializeField] private float fallbackHeight = 0.9f;  // used when the target has no renderer to measure (an empty marker, a spawn point)
    [SerializeField] private bool keepConstantSize = true; // hold the label the same size on screen however far away it is. off = it shrinks with distance like a real sign
    [SerializeField] private float sizeAtOneMetre = 1f;    // scale multiplier when keepConstantSize is on

    private Player owner;          // the player this canvas hangs off
    private Vector3 baseScale;     // the scale you authored, so distance sizing multiplies it rather than replacing it

    private void Awake()
    {
        owner = GetComponentInParent<Player>();
        if (promptCanvas == null)
        {
            promptCanvas = GetComponent<Canvas>();
        }
        baseScale = transform.localScale;
        Show(false);
    }

    private void LateUpdate()
    {
        //LateUpdate so the camera has already moved this frame. billboarding against a stale camera rotation is what
        //makes world-space labels jitter when you look around quickly.
        if (owner == null || Player.LocalPlayer != owner)
        {
            Show(false); //this canvas belongs to somebody else's player - never draw their prompts on our screen
            return;
        }

        Transform anchor = owner.InteractAnchor;
        string label = owner.InteractPrompt;

        if (anchor == null || string.IsNullOrEmpty(label))
        {
            Show(false);
            return;
        }

        Camera viewer = owner.ViewCamera != null ? owner.ViewCamera : Camera.main;
        if (viewer == null)
        {
            Show(false);
            return;
        }

        Show(true);
        if (promptText != null)
        {
            promptText.text = label;
        }

        transform.position = AnchorPoint(anchor);

        //match the camera's rotation, then spin 180. copying the rotation directly points the canvas's readable face
        //AWAY from the viewer and you get mirrored text - same fix PlayerSignals uses for its billboard.
        transform.rotation = viewer.transform.rotation * Quaternion.Euler(0f, 180f, 0f);

        if (keepConstantSize)
        {
            //scale with distance so a label on a doorway across the room reads the same as one on a vase at your feet
            float distance = Vector3.Distance(viewer.transform.position, transform.position);
            transform.localScale = baseScale * Mathf.Max(0.01f, distance * sizeAtOneMetre);
        }
        else
        {
            transform.localScale = baseScale;
        }
    }

    //Sit the label at the middle of whatever it's describing rather than a fixed height above its pivot. Pivots are
    //all over the place on imported props - a door hinges at its edge, a note lies flat, a safe is knee height - so a
    //blanket offset would float some labels in mid-air and bury others inside the mesh. Bounds don't lie.
    private Vector3 AnchorPoint(Transform anchor)
    {
        Renderer renderer = anchor.GetComponentInChildren<Renderer>();
        if (renderer != null)
        {
            return renderer.bounds.center + Vector3.up * verticalOffset;
        }
        return anchor.position + Vector3.up * fallbackHeight;
    }

    private void Show(bool visible)
    {
        //toggling the Canvas rather than the GameObject keeps LateUpdate running so we can turn it back on
        if (promptCanvas != null)
        {
            if (promptCanvas.enabled != visible) promptCanvas.enabled = visible;
            return;
        }

        //no Canvas found or assigned. without this the label would simply never hide - it'd sit on screen showing the
        //last thing you walked past, which reads as a broken prompt rather than a missing reference.
        if (promptText != null && promptText.enabled != visible)
        {
            promptText.enabled = visible;
        }
    }
}
