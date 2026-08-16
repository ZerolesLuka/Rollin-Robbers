using Cinemachine;
using UnityEngine;

// Player - camera feel. Movement lean, landing impact, breathing, strafe tilt, look lag and the sprint FOV push.
//
// ⚠️ NO HEAD BOB. NOT AN OVERSIGHT - DO NOT ADD IT BACK.
//
// Four versions of periodic bob were built and all four were rejected as feeling awful: a plain sine, a shaped gait
// curve with rotational roll, the same slowed down, and a slow drift spread over four steps. They failed at every
// amplitude tested, down to four millimetres of travel. When something fails at 4mm the problem is not size, it is
// that the camera is oscillating at all. At moveSpeed 7 you take about 3.5 steps a second, and anything cycling at
// that rate reads as a vibration no matter how it's shaped.
//
// So the camera is STILL while you hold a steady course. It only responds when your movement CHANGES:
//   set off or stop  -> the lean eases in or out
//   turn             -> the view trails behind and catches up
//   strafe           -> it leans into the direction
//   land             -> it dips and twists, the one genuinely sharp thing left
//   sprint           -> wider lens, leans harder
//   stand still       -> breathing, heavier as you tire
// Every one of those is a RESPONSE TO AN EVENT. None of them repeat on a cycle, so none of them can feel like a shake.
//
// ONE WRITER. The crouch eye-height (HandleCrouchCamera) and the look pitch (HandleLook) produce NUMBERS. Everything
// here produces an OFFSET. ApplyCameraFeel sums them and writes the camera transform exactly once per frame, and it
// is the only thing in the project that writes it. Two systems assigning the same transform field don't blend, they
// overwrite - whichever runs second wins and the other silently does nothing.
//
// All of it runs on the RENDER frame, not the 32Hz tick, or the motion stutters however good the maths is.
// Everything is scaled by cameraMotionScale, which is AUTHORED, not a player setting. Tune with the F1 slider.
public partial class Player
{
    private void UpdateCameraFeel()
    {
        if (playerCamera == null) return;

        float motionScale = cameraMotionScale;

        UpdateMovementSpeedFactor();
        UpdateLandingDip();
        UpdateLookLag(motionScale);
        UpdateStrafeTilt(motionScale);
        UpdateSprintFieldOfView(motionScale);
        ApplyCameraFeel(motionScale);
    }

    //0 when rested, 1 when completely gassed. `exhausted` pins it at 1 rather than letting it fall the instant stamina
    //starts trickling back, so you stay visibly winded until the game will actually let you sprint again.
    private float ExhaustionFactor
    {
        get
        {
            if (exhausted)
            {
                return 1f;
            }
            if (maxStamina <= 0f)
            {
                return 0f;
            }
            return Mathf.Clamp01(1f - stamina / maxStamina);
        }
    }

    //How hard we're actually moving, 0 to 1, heavily eased. Drives the steady lean and fades the breathing out.
    //
    //MEASURED POSITION CHANGE, never characterController.velocity - that property is the last Move() divided by the
    //deltaTime it happened on, and Move() runs on the 32Hz tick while render frames are far shorter, so it reports a
    //speed several times higher than reality.
    private void UpdateMovementSpeedFactor()
    {
        Vector3 delta = transform.position - lastStridePosition;
        lastStridePosition = transform.position;

        //STAIRS. A step-up is a sudden rise while already on the ground - the controller teleporting the capsule up by
        //stepOffset. Take that rise off the camera so it stays where it was, then let it climb back at its own pace.
        //
        //Grounded-only and upward-only on purpose: falling and jumping are meant to be felt, and this must never
        //smooth them. Clamped so an oversized rise - a teleport, a rescue, a scene change - can't bury the camera in
        //the floor while it recovers.
        if (characterController.isGrounded && delta.y > 0f && delta.y < stairSmoothMaxDrop)
        {
            stairSmoothOffset = Mathf.Max(stairSmoothOffset - delta.y, -stairSmoothMaxDrop);
        }
        stairSmoothOffset = Mathf.MoveTowards(stairSmoothOffset, 0f, stairSmoothRecoverSpeed * Time.deltaTime);

        delta.y = 0f;

        float targetSpeedFactor = 0f;
        //a scene change or rescue teleport covers metres in one frame - that isn't running, so don't read it as such
        if (moveSpeed > 0f && characterController.isGrounded && Time.deltaTime > 0f && delta.magnitude < 1f)
        {
            targetSpeedFactor = Mathf.Clamp01((delta.magnitude / Time.deltaTime) / moveSpeed);
        }

        //eased hard, so setting off and stopping arrive as a slow lean rather than a switch
        headBobSpeedFactor = Mathf.Lerp(headBobSpeedFactor, targetSpeedFactor, movementEaseSpeed * Time.deltaTime);
    }

    private void UpdateLandingDip()
    {
        //damped spring back to level, same shape as the flashlight's SpringAngle: accelerate toward rest, then bleed
        //momentum exponentially rather than by a per-frame multiply, so it feels identical at 60fps and 144.
        //This is the ONLY sharp motion left in the camera, and it's earned - you actually hit something.
        landingDipVelocity += -landingDipOffset * landingDipStiffness * Time.deltaTime;
        landingDipVelocity *= Mathf.Exp(-landingDipDamping * Time.deltaTime);
        landingDipOffset += landingDipVelocity * Time.deltaTime;

        landingRoll = Mathf.Lerp(landingRoll, 0f, 6f * Time.deltaTime);
    }

    //The view trails the mouse on a fast turn and catches up after.
    //
    //NOT a spring, deliberately. An earlier version was, and it clamped position while letting velocity keep building,
    //so the view slammed into the clamp, bounced, and buzzed between the limits every frame. A trail has no business
    //ringing anyway. This is first order with no stored velocity: mathematically incapable of oscillating.
    //
    //It offsets the VIEW ONLY. Body yaw and xRotation - what you're aiming at, what the flashlight and interaction
    //raycasts use - are untouched, so it can't make reaching for a door handle feel wrong.
    private void UpdateLookLag(float motionScale)
    {
        lookLagYaw -= lookDegreesTurnedThisFrame.x * lookLagAmount;
        lookLagPitch += lookDegreesTurnedThisFrame.y * lookLagAmount;
        lookDegreesTurnedThisFrame = Vector2.zero; //consumed - HandleLook republishes it next frame it runs

        lookLagYaw = Mathf.Clamp(lookLagYaw, -lookLagMaxDegrees, lookLagMaxDegrees);
        lookLagPitch = Mathf.Clamp(lookLagPitch, -lookLagMaxDegrees, lookLagMaxDegrees);

        float recover = 1f - Mathf.Exp(-lookLagRecoverSpeed * Time.deltaTime);
        lookLagYaw = Mathf.Lerp(lookLagYaw, 0f, recover);
        lookLagPitch = Mathf.Lerp(lookLagPitch, 0f, recover);
    }

    private void UpdateStrafeTilt(float motionScale)
    {
        //lean INTO the movement, the way you'd tip rounding a corner. Eased, so holding a strafe is a steady lean
        //rather than anything that moves on its own.
        float targetTilt = -lastMoveInput.x * strafeTiltAmount * motionScale;
        strafeTilt = Mathf.Lerp(strafeTilt, targetTilt, strafeTiltSpeed * Time.deltaTime);
    }

    private void UpdateSprintFieldOfView(float motionScale)
    {
        if (playerVirtualCamera == null) return;

        //LENS LIVES ON THE VCAM. Cinemachine reassigns the Camera's field of view every frame from the vcam's lens, so
        //setting Camera.fieldOfView would look right in the inspector and do nothing on screen. Hours lost to that.
        float targetFieldOfView = baseFieldOfView;
        if (isSprintingNow)
        {
            targetFieldOfView += sprintFieldOfViewBoost * motionScale;
        }

        LensSettings lens = playerVirtualCamera.m_Lens;
        lens.FieldOfView = Mathf.Lerp(lens.FieldOfView, targetFieldOfView, fieldOfViewLerpSpeed * Time.deltaTime);
        playerVirtualCamera.m_Lens = lens; //LensSettings is a struct - editing the copy does nothing
    }

    private void ApplyCameraFeel(float motionScale)
    {
        //STEADY lean while moving. A constant, not a cycle - it arrives as you set off, holds while you're going, and
        //leaves as you stop. Hold W at a constant speed and this number does not change, so there is nothing to feel.
        float movingPitch = headBobSpeedFactor * movingPitchDegrees * motionScale;
        if (isSprintingNow)
        {
            movingPitch += headBobSpeedFactor * sprintExtraPitchDegrees * motionScale;
        }

        //Breathing. Two independent Perlin channels so it wanders instead of looping, same trick as the torch sway.
        //Heavier and faster as you tire, which turns the stamina bar into something you FEEL while hiding in a closet
        //after a chase. Fades out as you move, since walking has its own lean and the two together are noise.
        float exhaustion = ExhaustionFactor;
        float breathScale = breathSwayAmount
                          * motionScale
                          * Mathf.Lerp(1f, exhaustedBreathMultiplier, exhaustion)
                          * (1f - headBobSpeedFactor);
        float breathTime = Time.time * breathSwayFrequency * Mathf.Lerp(1f, 2.5f, exhaustion);
        float breathPitch = (Mathf.PerlinNoise(breathTime, 0f) - 0.5f) * 2f * breathScale;
        float breathYaw = (Mathf.PerlinNoise(0f, breathTime) - 0.5f) * 2f * breathScale;

        //THE SINGLE WRITE. Position carries nothing but the crouch height and a landing - no walking translation at
        //all, because moving the camera up and down is exactly what reads as a cheap bounce.
        playerCamera.localPosition = new Vector3(
            cameraRestLocalPosition.x,
            cameraEyeHeight + landingDipOffset + stairSmoothOffset,
            cameraRestLocalPosition.z);

        playerCamera.localRotation = Quaternion.Euler(
            xRotation + breathPitch + movingPitch + lookLagPitch * motionScale,
            breathYaw + lookLagYaw * motionScale,
            strafeTilt + landingRoll);
    }
}
