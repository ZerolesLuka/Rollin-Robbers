using Cinemachine;
using UnityEngine;

// Player - camera feel: head bob, landing impact, breathing sway, strafe tilt and the sprint FOV push.
//
// ONE WRITER. The crouch eye-height (HandleCrouchCamera) and the look pitch (HandleLook) produce NUMBERS. Everything
// in this file produces an OFFSET. ApplyCameraFeel sums them and writes the camera transform exactly once per frame,
// and it is the only thing in the project that writes it. The reason is boring and expensive: two systems assigning
// the same transform field don't blend, they overwrite, so whichever runs second wins and the other looks broken.
// If you add another camera effect later, add it as an offset here - do not write playerCamera anywhere else.
//
// All of it runs on the RENDER frame rather than the 32Hz tick, for the same reason the crouch easing does: camera
// motion stepped at 32Hz reads as a stutter no matter how good the maths underneath it is.
//
// Every effect is scaled by GameSettings.CameraMotionAmount, and at 0 the camera is completely still. That isn't
// decoration - head bob is the most common motion-sickness complaint in first-person games, and without a way to
// switch it off it stops being a feel feature and starts being a wall.
public partial class Player
{
    private void UpdateCameraFeel()
    {
        if (playerCamera == null) return;

        float motionScale = GameSettings.CameraMotionAmount;

        UpdateHeadBob();
        UpdateLandingDip();
        UpdateStrafeTilt(motionScale);
        UpdateSprintFieldOfView(motionScale);
        ApplyCameraFeel(motionScale);
    }

    //0 when rested, 1 when completely gassed. `exhausted` pins it at 1 rather than letting it fall the instant stamina
    //starts trickling back, so you stay visibly winded until the game will actually let you sprint again - the camera
    //agrees with the mechanic instead of recovering ahead of it.
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

    private void UpdateHeadBob()
    {
        //How fast we're ACTUALLY moving, taken from the controller rather than from input: walking into a wall holds
        //the stick forward while going nowhere, and bobbing there looks ridiculous.
        Vector3 horizontalVelocity = characterController.velocity;
        horizontalVelocity.y = 0f;

        float targetSpeedFactor = 0f;
        if (moveSpeed > 0f && characterController.isGrounded) //airborne has no footfalls, so it has no bob
        {
            targetSpeedFactor = Mathf.Clamp01(horizontalVelocity.magnitude / moveSpeed);
        }

        //eased rather than snapped so setting off and stopping ramp the bob in and out instead of switching it
        headBobSpeedFactor = Mathf.Lerp(headBobSpeedFactor, targetSpeedFactor, 8f * Time.deltaTime);

        //advanced by SPEED, not by wall time, so the bob keeps pace with your stride - crouch-creeping rocks slowly,
        //sprinting pounds. A fixed-rate timer would run at walking cadence no matter how fast you were going.
        headBobTimer += Time.deltaTime * headBobFrequency * headBobSpeedFactor;
    }

    private void UpdateLandingDip()
    {
        //A damped spring back to level, same shape as the flashlight's SpringAngle: accelerate toward rest, then bleed
        //the momentum exponentially rather than by a per-frame multiply, so the recovery feels identical at 60 and 144.
        landingDipVelocity += -landingDipOffset * landingDipStiffness * Time.deltaTime;
        landingDipVelocity *= Mathf.Exp(-landingDipDamping * Time.deltaTime);
        landingDipOffset += landingDipVelocity * Time.deltaTime;
    }

    private void UpdateStrafeTilt(float motionScale)
    {
        //Lean INTO the movement - strafing right rolls the view slightly anticlockwise, the way you'd tip your head
        //rounding a corner. Kept to a degree or so: this is the effect most likely to annoy people if it's loud.
        float targetTilt = -lastMoveInput.x * strafeTiltAmount * motionScale;
        strafeTilt = Mathf.Lerp(strafeTilt, targetTilt, strafeTiltSpeed * Time.deltaTime);
    }

    private void UpdateSprintFieldOfView(float motionScale)
    {
        if (playerVirtualCamera == null) return;

        //LENS LIVES ON THE VCAM. Cinemachine reassigns the Camera's field of view every frame from the vcam's lens, so
        //setting Camera.fieldOfView here would look perfectly correct in the inspector and do absolutely nothing on
        //screen. This project has already lost hours to that exact trap once.
        float targetFieldOfView = baseFieldOfView;
        if (isSprintingNow)
        {
            targetFieldOfView += sprintFieldOfViewBoost * motionScale;
        }

        LensSettings lens = playerVirtualCamera.m_Lens;
        lens.FieldOfView = Mathf.Lerp(lens.FieldOfView, targetFieldOfView, fieldOfViewLerpSpeed * Time.deltaTime);
        playerVirtualCamera.m_Lens = lens; //LensSettings is a struct - it has to be written back, editing the copy does nothing
    }

    private void ApplyCameraFeel(float motionScale)
    {
        float bobAmplitude = headBobSpeedFactor * motionScale;

        //Vertical runs at DOUBLE the horizontal rate: two dips per stride (one per foot) against one sway per stride.
        //That 2:1 ratio is what makes it read as walking rather than as the camera being wobbled - traced out, the two
        //together draw a figure eight, which is roughly what your head really does.
        float bobVertical = Mathf.Sin(headBobTimer * 2f) * headBobVerticalAmount * bobAmplitude;
        float bobHorizontal = Mathf.Sin(headBobTimer) * headBobHorizontalAmount * bobAmplitude;

        //Breathing. Two independent Perlin channels so it wanders instead of looping, the same trick the torch sway
        //uses. It gets heavier and faster as you tire, which turns the stamina bar into something you FEEL while
        //hiding in a closet after a chase rather than something you read off the HUD.
        //
        //It also fades out as the bob comes in, because walking already supplies plenty of motion and running both at
        //once just reads as noise.
        float exhaustion = ExhaustionFactor;
        float breathScale = breathSwayAmount
                          * motionScale
                          * Mathf.Lerp(1f, exhaustedBreathMultiplier, exhaustion)
                          * (1f - headBobSpeedFactor * 0.7f);
        float breathTime = Time.time * breathSwayFrequency * Mathf.Lerp(1f, 2.5f, exhaustion);
        float breathPitch = (Mathf.PerlinNoise(breathTime, 0f) - 0.5f) * 2f * breathScale;
        float breathYaw = (Mathf.PerlinNoise(0f, breathTime) - 0.5f) * 2f * breathScale;

        //THE SINGLE WRITE. Base height plus every offset, base pitch plus every offset. X and Z come from wherever the
        //prefab put the camera so nudging it in the prefab doesn't drag the rest position off with it.
        playerCamera.localPosition = new Vector3(
            cameraRestLocalPosition.x + bobHorizontal,
            cameraEyeHeight + bobVertical + landingDipOffset,
            cameraRestLocalPosition.z);

        playerCamera.localRotation = Quaternion.Euler(xRotation + breathPitch, breathYaw, strafeTilt);
    }
}
