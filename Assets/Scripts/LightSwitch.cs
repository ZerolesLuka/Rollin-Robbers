using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;
// A switch on a wall. Holds no state and does nothing on its own - it only says WHICH zone it controls, and
// Player.Interaction does the rest. Same shape as ExitDoor: inert data that the interaction scan can find.
//
// SEVERAL switches can point at one zone - a hallway with one at each end - and they all work, because the zone owns
// the lights and this owns nothing. That is the whole reason the zone sits in the middle.
public class LightSwitch : MonoBehaviour
{
    public static readonly List<LightSwitch> AllSwitches = new List<LightSwitch>();

    [SerializeField] private Lights zone;             //what zone this swtich controls
    [SerializeField] public float interactRange = 2f; //how close you have to stand, same as ExitDoor's

    public Lights Zone => zone; //read only

    private void Awake()
    {
        //NAMED, not silent. An unassigned zone means E does nothing at all, and you just lost an evening to exactly
        if (zone == null)
        {
            throw new System.Exception($"LightSwitch {name} has no zone assigned. Assign one or delete it.");
        }
    }
    private void OnEnable()
    {
        AllSwitches.Add(this); //registers itself so Player.Interaction can find it without a scene scan
    }
    private void OnDisable()
    {
        AllSwitches.Remove(this); //deregisters itself
    }
}

