using System.Collections.Generic;
using UnityEngine;

// Place up to 4 of these inside the van's back/cab. Each player claims one seat by their
// stable Fusion PlayerId once the run succeeds, so everyone lands in a different spot.
public class VanSeat : MonoBehaviour
{
    public static readonly List<VanSeat> AllSeats = new List<VanSeat>();

    [SerializeField] public int seatIndex = 0; // 0-3, matches Object.InputAuthority.PlayerId % 4

    private void OnEnable() => AllSeats.Add(this);
    private void OnDisable() => AllSeats.Remove(this);
}
