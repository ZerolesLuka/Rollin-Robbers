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
    [SerializeField] private float doorSwingDamping = 3.2f;   //how fast a released door loses its momentum. lower = it coasts further, higher = it stops dead like it used to
    [SerializeField] private float doorMaxSwingSpeed = 2.5f;  //cap on coast speed, in full-opens per second - stops a violent flick launching a door round its hinge
    [SerializeField] private float doorVelocitySmoothing = 12f; //how much the tracked hand speed is averaged. without this, one stuttery frame at the moment of release decides the whole throw

    //Dragging means MOUSE LOOK IS OFF, and that has to stay true while the door is still coasting to a stop, or the
    //camera would snap back mid-swing. Coasting counts as still holding it as far as the rest of the game is concerned.
    public bool IsDraggingDoor => draggedHinge != null;

    private SwingingHinge draggedHinge;
    private float draggedAmount;   //our own live copy, so the value can't be lost to a network round trip mid-drag
    private float draggedSideSign; //+1 or -1: which face of the door we're stood on, so pushing works from both sides
    private float draggedVelocity; //full-opens per second, tracked while pushing and spent coasting after release
    private bool draggedHandOff;   //let go, but the door is still moving under its own weight

    private void UpdateDoorDrag()
    {
        if (Mouse.current == null) return;

        //anything that has taken your hands or your control takes this too - and the shop counters matter most, since
        //their cursor is free and a stray click would otherwise grab a door through the menu
        bool handsBusy = KeyboardIsCaptured || IsEliminated || IsHiding || IsLockedUp || IsBearTrapped || isBeingDragged || spectatorActive;

        if (draggedHinge == null)
        {
            if (handsBusy || !Mouse.current.leftButton.wasPressedThisFrame) return;
            TryGrabHinge();
            return;
        }

        //something took our hands mid-push - drop it dead, no graceful coast
        if (handsBusy)
        {
            draggedHinge = null;
            return;
        }

        //LET GO. The door keeps whatever speed our hand had and carries on under its own weight, which is the bit
        //that makes it feel like an object rather than a slider. We keep hold of the reference and keep streaming
        //until it settles - if each client simulated the coast itself they'd all arrive at slightly different angles.
        if (!Mouse.current.leftButton.isPressed)
        {
            draggedHandOff = true;
        }

        float deltaAmount;
        if (draggedHandOff)
        {
            draggedVelocity *= Mathf.Exp(-doorSwingDamping * Time.deltaTime); //exponential decay: fast at first, then a long slow settle, like a real hinge
            deltaAmount = draggedVelocity * Time.deltaTime;

            //give up once it's barely moving, or the moment it hits the frame or the fully-open stop
            bool hitTheStops = (draggedAmount <= 0f && deltaAmount < 0f) || (draggedAmount >= 1f && deltaAmount > 0f);
            if (Mathf.Abs(draggedVelocity) < 0.02f || hitTheStops)
            {
                draggedHinge = null;
                draggedHandOff = false;
                draggedVelocity = 0f;
                return;
            }
        }
        else
        {
            //FORWARD AND BACK, not side to side. Shoving the mouse away from you shoves the door away from you, and
            //pulling it back pulls the door open toward you - which is literally what your hand would be doing. A
            //sideways drag reads as sliding something along a rail, which is wrong for everything here.
            float mousePush = Mouse.current.delta.ReadValue().y;
            deltaAmount = (mousePush * draggedSideSign) / doorDragSensitivity;

            //track how fast the hand is going, SMOOTHED. read raw, a single stuttery frame at the instant of release
            //decides the entire throw - so a door you eased shut occasionally hurls itself open instead.
            float instantVelocity = Time.deltaTime > 0f ? deltaAmount / Time.deltaTime : 0f;
            draggedVelocity = Mathf.Lerp(draggedVelocity, instantVelocity, 1f - Mathf.Exp(-doorVelocitySmoothing * Time.deltaTime));
            draggedVelocity = Mathf.Clamp(draggedVelocity, -doorMaxSwingSpeed, doorMaxSwingSpeed);

            if (Mathf.Approximately(mousePush, 0f)) return; //hand still, door still - but we keep hold of it
        }

        draggedAmount = Mathf.Clamp01(draggedAmount + deltaAmount);

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
        draggedVelocity = 0f;
        draggedHandOff = false;

        //WHICH SIDE ARE WE ON. Pushing a door from the far side has to swing it the other way, or half your
        //encounters with a door will have you pulling it into your own face. Compared against the CLOSED facing, not
        //the live one - otherwise the sign flips underneath you as the door passes you by.
        //
        //The -1 is the "push away from you" convention: dragging the mouse right should shove the far edge away, and
        //the dot product on its own gave the opposite. Per-hinge InvertDrag exists for prefabs whose mesh was built
        //facing the other way, since ClosedFaceNormal can only guess from the axis.
        Vector3 toPlayer = transform.position - hinge.transform.position;
        draggedSideSign = Vector3.Dot(toPlayer, hinge.ClosedFaceNormal) >= 0f ? -1f : 1f;
        if (hinge.InvertDrag)
        {
            draggedSideSign = -draggedSideSign;
        }
    }
}
