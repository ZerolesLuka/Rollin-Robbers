using System.Collections.Generic;
using UnityEngine;

// The pawn shop counter. Press E in range to sell the team's entire gathered haul for money.
// Same interact shape as the other world interactables.
public class SellCounter : MonoBehaviour
{
    public static readonly List<SellCounter> AllCounters = new List<SellCounter>();

    [SerializeField] public float interactRange = 2f;

    private void OnEnable() => AllCounters.Add(this);
    private void OnDisable() => AllCounters.Remove(this);
}
