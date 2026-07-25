using System.Collections.Generic;
using UnityEngine;

// A hinged door you open/close with E. It's NOT a NetworkObject - toggling is broadcast by an RPC on the Player
// (Player.RPC_ToggleDoor) and every client swings its OWN copy, so scene-placed doors sidestep Fusion's
// scene-object enrolment problem the loot ran into. The RPC identifies a door by its POSITION, so there's
// nothing to number or wire per door - just drop this on a door and it works. Doors start closed on scene load.
//
// The door swings around its OWN pivot, so the pivot MUST sit at the hinge edge. Synty doors already do; if a
// door's pivot is at its centre it'll spin in place instead of swinging - fix that by parenting the door mesh
// under an empty placed at the hinge and putting this script on the empty. The collider rides on the door, so
// opening it clears the doorway for free. Put the door on the guard's obstacle layer (Enviorment) if you want a
// CLOSED door to also block his line of sight - that's the stealth payoff.
public class Door : MonoBehaviour
{
    public static readonly List<Door> AllDoors = new List<Door>();

    [SerializeField] public float interactRange = 2f;
    [SerializeField] private float openAngle = 90f;   // how far it swings, degrees around local up
    [SerializeField] private float swingSpeed = 300f; // degrees per second - how fast it opens and closes

    private Quaternion closedRotation;
    private Quaternion openRotation;
    private bool isOpen;

    private void Awake()
    {
        closedRotation = transform.localRotation;                          // wherever it's placed in the scene = the closed pose
        openRotation = closedRotation * Quaternion.Euler(0f, openAngle, 0f); // swung open around the hinge
    }

    private void OnEnable()
    {
        AllDoors.Add(this);
    }

    private void OnDisable()
    {
        AllDoors.Remove(this);
    }

    private void Update()
    {
        //ease toward the current state every frame - a clean swing, not a snap. the collider is part of the door,
        //so as it rotates open the doorway physically clears; rotate closed and it blocks again.
        Quaternion target = isOpen ? openRotation : closedRotation;
        transform.localRotation = Quaternion.RotateTowards(transform.localRotation, target, swingSpeed * Time.deltaTime);
    }

    public void Toggle() //run on EVERY client by Player.RPC_ToggleDoor, so all copies swing together
    {
        isOpen = !isOpen;
    }
}
