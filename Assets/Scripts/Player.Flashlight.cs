using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// Player - the handheld flashlight. UpdateFlashlight runs on every client (driven by the networked IsFlashlightOn
// + lookPitch) so everyone sees each other's beam; HandleFlashlight is the local F-key toggle.
public partial class Player
{
    private void UpdateFlashlight()
    {
        if (flashlight == null || Object == null || !Object.IsValid) return;

        bool beamOn = IsFlashlightOn && !IsHiding && !IsEliminated && !IsLockedUp; //kill the beam while hidden, caught, or jailed - otherwise it shines out of the closet and gives you away / looks broken
        flashlight.enabled = beamOn; //on every client, so you see teammates' beams too
        if (!beamOn)
        {
            flashlightLastPosition = transform.position; //keep this current while off so we don't get a huge fake "speed" spike the frame it turns on
            return;
        }

        //how fast are we actually moving this frame - drives a bigger bob while walking
        float speed = Time.deltaTime > 0f ? (transform.position - flashlightLastPosition).magnitude / Time.deltaTime : 0f;
        flashlightLastPosition = transform.position;
        float walkFactor = Mathf.Clamp01(speed / moveSpeed); //0 standing still, ~1 at full walk speed

        //handheld tremor: two Perlin channels drifting independently so it wanders naturally instead of looping. bigger while walking (the bob), always a little alive while still
        float swayScale = flashlightSwayAmount * (1f + walkFactor * flashlightWalkSwayMultiplier);
        float swayPitch = (Mathf.PerlinNoise(Time.time * flashlightSwayFrequency, 0f) - 0.5f) * 2f * swayScale;
        float swayYaw = (Mathf.PerlinNoise(0f, Time.time * flashlightSwayFrequency) - 0.5f) * 2f * swayScale;

        //aim where the player looks (networked yaw + pitch), plus the handheld sway, eased in so the beam trails the camera instead of snapping
        Quaternion aimRotation = Quaternion.Euler(lookPitch + swayPitch, transform.eulerAngles.y + swayYaw, 0f);
        flashlight.transform.rotation = Quaternion.Slerp(flashlight.transform.rotation, aimRotation, flashlightFollowSpeed * Time.deltaTime);
    }

    private void HandleFlashlight(bool flashlightPressed)
    {
        bool pressed = flashlightPressed && !flashlightHeldLastTick; //rising edge only - one toggle per press
        flashlightHeldLastTick = flashlightPressed;
        if (pressed)
        {
            IsFlashlightOn = !IsFlashlightOn; //networked, so every client's copy of us updates the beam in Update
        }
    }
}
