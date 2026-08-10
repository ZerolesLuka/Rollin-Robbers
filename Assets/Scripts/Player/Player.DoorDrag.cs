using UnityEngine;
using UnityEngine.InputSystem;

// Player - PUSHING THINGS OPEN BY HAND. Hold left mouse on a door, drawer or cupboard and move the mouse; it moves
// with you and stays wherever you let go.
//
// This replaces E on every openable. E was doing two unrelated jobs - "operate this thing" and "go through this
// door" - and on a front threshold, where an ExitDoor and a hinge sit on top of each other, they fought. Now E means
// only "use", and the physical act of opening is a physical act.
//
// A door resting at any angle is a gameplay change, not just a feel one: you can crack one open and peek, leave one
// ajar behind you as a tell, or ease it shut slowly enough that nobody hears.
public partial class Player
{
    [Header("Dragging doors open (hold left mouse)")]
    [SerializeField] private float doorGrabRange = 2.5f;      //how far you can reach to take hold of something
    [SerializeField] private float doorDragSensitivity = 320f; //mouse pixels for a full open. higher = you have to move further

    public bool IsDraggingDoor => draggedHinge != null;

    private SwingingHinge draggedHinge;
    private float draggedAmount;   //our own live copy, so the value can't be lost to a network round trip mid-drag
    private float draggedSideSign; //+1 or -1: which face of the door we're stood on, so pushing works from both sides

    private void UpdateDoorDrag()
    {
        if (Mouse.current == null) return;

        //anything that has taken your hands or your control takes this too - and the shop counters matter most, since
        //their cursor is free and a stray click would otherwise grab a door through the menu
        bool handsBusy = KeyboardIsCaptured || IsEliminated || IsHiding || IsLockedUp || IsBearTrapped || isBeingDragged || spectatorActive;

        if (draggedHinge != null && (handsBusy || Mouse.current.leftButton.isPressed == false))
        {
            draggedHinge = null; //let go, or something took our hands mid-push
            return;
        }

        if (draggedHinge == null)
        {
            if (handsBusy || !Mouse.current.leftButton.wasPressedThisFrame) return;
            TryGrabHinge();
            return;
        }

        //HORIZONTAL mouse movement only. Vertical would fight looking up and down for no gain, and every openable in
        //the game either swings around a vertical axis or pulls straight out - both of which read as a sideways push.
        float mouseX = Mouse.current.delta.ReadValue().x;
        if (Mathf.Approximately(mouseX, 0f)) return;

        draggedAmount = Mathf.Clamp01(draggedAmount + (mouseX * draggedSideSign) / doorDragSensitivity);

        //apply locally FIRST so our own door tracks the mouse with no latency at all, then tell everyone else. the
        //RPC comes back to us too and simply re-asserts the value we already set.
        draggedHinge.SetOpenAmount(draggedAmount);
        if (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        {
            RunManager.Instance.RPC_SetHingeAmount(draggedHinge.transform.position, draggedAmount);
        }
    }

    private void TryGrabHinge()
    {
        Camera view = ViewCamera;
        if (view == null) return;

        //LOOK at what you want to grab, rather than grabbing the nearest thing. With a door and a cupboard side by
        //side, proximity picks whichever is a few centimetres closer; aim picks the one you meant.
        if (!Physics.Raycast(view.transform.position, view.transform.forward, out RaycastHit hit, doorGrabRange))
        {
            return;
        }

        SwingingHinge hinge = hit.collider.GetComponentInParent<SwingingHinge>();
        if (hinge == null || !hinge.PlayerOperable) return; //a safe door opens on its own terms, not by being shoved

        //a wedge under a door is the whole point of a wedge - it has to actually stop the door moving
        Door houseDoor = hinge.GetComponent<Door>();
        if (houseDoor != null && houseDoor.IsWedged) return;

        draggedHinge = hinge;
        draggedAmount = hinge.OpenAmount; //carry on from wherever it's currently resting rather than snapping

        //WHICH SIDE ARE WE ON. Pushing a door from the far side has to swing it the other way, or half your
        //encounters with a door will have you pulling it into your own face. Compared against the CLOSED facing, not
        //the live one - otherwise the sign flips underneath you as the door passes you by.
        Vector3 toPlayer = transform.position - hinge.transform.position;
        draggedSideSign = Vector3.Dot(toPlayer, hinge.ClosedFaceNormal) >= 0f ? 1f : -1f;
    }
}
