using System.Collections.Generic;
using UnityEngine;

// The getaway van in the outdoor scene. When a player presses E at the driver's seat, the run
// ends successfully for the whole team - anyone still in the house is left behind. Any player can
// start it; RunState.Success replicates to everyone, so one person leaving ends the run for all.
public class Van : MonoBehaviour
{
    public static readonly List<Van> AllVans = new List<Van>();

    [SerializeField] public Transform driverSeat; // the steering wheel position - the player must be here to start the van
    [SerializeField] public float interactRange = 2f;
    [SerializeField] private GameObject backBarrier; // blocks the back of the van; sealed while a run is over so players can't leave before picking a destination. leave empty on vans that don't need one

    [Header("Back doors - swing open once a destination is picked")]
    [SerializeField] private Transform backDoorLeft;
    [SerializeField] private Transform backDoorRight;
    [SerializeField] private float leftDoorOpenYAngle = -114f;  //local Y when open. closed is always 0
    [SerializeField] private float rightDoorOpenYAngle = -114f; //separate from the left so mirrored doors can swing opposite ways
    [SerializeField] private float doorSwingSpeed = 3f;         //how fast they swing. this is cosmetic - the barrier is what actually blocks

    //Only the Y is driven. The doors' authored X and Z are kept exactly as the artist left them, so a model whose
    //hinges aren't perfectly axis-aligned doesn't get straightened out by this.
    private Vector3 leftDoorRestEuler;
    private Vector3 rightDoorRestEuler;
    private Collider backBarrierCollider;

    private void Awake()
    {
        if (backDoorLeft != null)
        {
            leftDoorRestEuler = backDoorLeft.localEulerAngles;
        }
        if (backDoorRight != null)
        {
            rightDoorRestEuler = backDoorRight.localEulerAngles;
        }

        //THE COLLIDER, not the GameObject. This used to SetActive the barrier object - and the two back doors are
        //parented UNDER it, so opening the van deactivated the doors along with the barrier and they vanished instead
        //of swinging. Toggling the collider's enabled flag leaves every child alone.
        if (backBarrier != null)
        {
            backBarrierCollider = backBarrier.GetComponent<Collider>();
            if (backBarrierCollider == null)
            {
                Debug.LogError($"[Van] '{name}' has a backBarrier with no Collider on it, so nothing actually blocks " +
                               "the van's back - players walk through the closed doors. Add a Box Collider to it.", this);
            }
        }
    }

    private void OnEnable() => AllVans.Add(this);
    private void OnDisable() => AllVans.Remove(this);

    private void Update()
    {
        //derive the barrier from networked run state, so every client's van agrees on open vs sealed - same
        //one-source-of-truth idea as the hiding-spot fix. RunManager.VanBackClosed is the authority; we just mirror it.
        if (RunManager.Instance == null)
        {
            return;
        }

        bool sealedShut = RunManager.Instance.VanBackClosed;

        if (backBarrierCollider != null)
        {
            backBarrierCollider.enabled = sealedShut;
        }

        //Closed is Y=0, open is the authored angle. Swung rather than snapped, and driven every frame off the
        //networked flag rather than tracked separately - so a client that joins mid-run, or one that misses the
        //moment the destination was picked, still ends up with its doors in the right place.
        SwingDoor(backDoorLeft, leftDoorRestEuler, sealedShut ? 0f : leftDoorOpenYAngle);
        SwingDoor(backDoorRight, rightDoorRestEuler, sealedShut ? 0f : rightDoorOpenYAngle);
    }

    private void SwingDoor(Transform door, Vector3 restEuler, float targetYAngle)
    {
        if (door == null)
        {
            return;
        }

        //Quaternion, not a euler lerp: euler angles wrap at 360, so lerping straight to -114 sends the door the long
        //way round through the van instead of swinging out.
        Quaternion target = Quaternion.Euler(restEuler.x, targetYAngle, restEuler.z);
        door.localRotation = Quaternion.Slerp(door.localRotation, target, doorSwingSpeed * Time.deltaTime);
    }
}
