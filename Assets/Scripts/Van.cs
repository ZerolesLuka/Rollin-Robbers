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

    private void OnEnable() => AllVans.Add(this);
    private void OnDisable() => AllVans.Remove(this);
}
