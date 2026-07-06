using Cinemachine;
using System.Collections.Generic;
using UnityEngine;

public class HidingSpot : MonoBehaviour
{
    public static readonly List<HidingSpot> AllHidingSpots = new List<HidingSpot>();

    [SerializeField] private CinemachineVirtualCamera virtualCamera;
    [SerializeField] public float interactRange = 3f;

    public bool isHiding;
    public bool isOccupied;

    private void OnEnable() => AllHidingSpots.Add(this);
    private void OnDisable() => AllHidingSpots.Remove(this);

    public void OnSpotEnter()
    {
        isHiding = true;
        Player.LocalPlayer.SetHiding(true);
        if (virtualCamera != null) virtualCamera.enabled = true;
    }

    public void OnSpotExit()
    {
        isHiding = false;
        isOccupied = false;
        Player.LocalPlayer.SetHiding(false);
        if (virtualCamera != null) virtualCamera.enabled = false;
    }
}//
