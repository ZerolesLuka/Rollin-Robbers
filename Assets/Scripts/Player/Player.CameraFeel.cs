using Cinemachine;
using UnityEngine;

// Player - camera feel. Head bob, landing impact, breathing, strafe tilt, look lag and the sprint FOV push.
//
// TARGET FEEL: heavy and clumsy - you are piloting a body, not floating a camera. Lethal Company / R.E.P.O.
// territory, where the camera is part of the comedy rather than a neutral window.
//
// ONE WRITER. The crouch eye-height (HandleCrouchCamera) and the look pitch (HandleLook) produce NUMBERS. Everything
// here produces an OFFSET. ApplyCameraFeel sums them and writes the camera transform exactly once per frame, and it
// is the only thing in the project that writes it. Two systems assigning the same transform field don't blend, they
// overwrite - whichever runs second wins and the other silently does nothing.
//
// All of it runs on the RENDER frame, not the 32Hz tick, or the motion stutters no matter how good the maths is.
//
// Everything is scaled by cameraMotionScale (0.1), which is AUTHORED, not a player setting. The feel is the same for
// everyone. Tune it with the slider in the F1 debug panel while walking - never by editing numbers and replaying.
//
// FOUR THINGS THAT MADE THE FIRST VERSION READ AS GENERIC, fixed here:
//   1. Bob ran on a speed-scaled TIMER while footsteps ran on distance walked, so the dip drifted out of phase with
//      the sound. Now both run off distance and share PlayerFootsteps.StrideLength, so they can never disagree.
//   2. Pure Mathf.Sin. A real gait is a hard drop as weight lands and a slower push back up, not a symmetric wave.
//   3. Position-only bob, no rotation. Without roll and pitch the camera slides on rails instead of being carried.
//      This was probably the single biggest tell.
//   4. Every step identical. Real gaits favour a foot, so alternate steps are lighter here.
public partial class Player
{
    private void UpdateCameraFeel()
    {
        if (playerCamera == null) return;

        float motionScale = cameraMotionScale;

        UpdateStridePhase();
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

    //How far through the current step we are, 0 to 1. Driven by DISTANCE WALKED and reset every StrideLength metres,
    //which is exactly what PlayerFootsteps does to decide when to play a sound - so the visual impact and the audible
    //one land together forever, at any speed, with no syncing code between them.
    private void UpdateStridePhase()
    {
        //MEASURED POSITION CHANGE, never characterController.velocity.
        //
        //That property is the last Move() divided by the deltaTime it happened on. Move() runs on the 32Hz network
        //tick while render frames are far shorter, so it reports a speed several times higher than reality - and
        //integrating that every render frame made strideDistance race, spun the step index, and flipped the lean many
        //times a second. Measuring how far we actually moved is ground truth, and it's exactly what PlayerFootsteps
        //does, so the two clocks agree by construction rather than by coincidence.
        Vector3 delta = transform.position - lastStridePosition;
        lastStridePosition = transform.position;
        delta.y = 0f;
        float distanceThisFrame = delta.magnitude;

        float strideLength = playerFootsteps != null ? playerFootsteps.StrideLength : 2f;
        if (strideLength <= 0f)
        {
            return;
        }

        //a jump between scenes or a rescue teleport covers metres in one frame. counting it would spin the step index
        //and fire a burst of bob - same guard PlayerFootsteps uses on its own accumulator.
        if (distanceThisFrame > strideLength)
        {
            return;
        }

        //what we're ACTUALLY doing, not what we asked for - walking into a wall shouldn't bob
        float targetSpeedFactor = 0f;
        if (moveSpeed > 0f && characterController.isGrounded && Time.deltaTime > 0f)
        {
            targetSpeedFactor = Mathf.Clamp01((distanceThisFrame / Time.deltaTime) / moveSpeed);
        }
        headBobSpeedFactor = Mathf.Lerp(headBobSpeedFactor, targetSpeedFactor, 8f * Time.deltaTime);

        strideDistance += distanceThisFrame;
        while (strideDistance >= strideLength)
        {
            strideDistance -= strideLength;
            strideStepIndex++; //next foot. drives which way the body leans and which steps are the light ones
        }
    }

    private void UpdateLandingDip()
    {
        //damped spring back to level, same shape as the flashlight's SpringAngle: accelerate toward rest, then bleed
        //momentum exponentially rather than by a per-frame multiply, so it feels identical at 60fps and 144.
        landingDipVelocity += -landingDipOffset * landingDipStiffness * Time.deltaTime;
        landingDipVelocity *= Mathf.Exp(-landingDipDamping * Time.deltaTime);
        landingDipOffset += landingDipVelocity * Time.deltaTime;

        //the twist rides the dip rather than having its own spring - one impact, one recovery, no chance of the roll
        //still unwinding after the drop has settled
        landingRoll = Mathf.Lerp(landingRoll, 0f, 6f * Time.deltaTime);
    }

    //The view trails the mouse on a fast turn and catches up after. This is the main "heavy body" cue and the thing
    //most missing from the old camera.
    //
    //IMPORTANT: this offsets the VIEW ONLY. Body yaw and xRotation - what you're actually aiming at, and what the
    //flashlight and interaction raycasts use - are untouched. Clamped hard so it can never swing far enough to make
    //reaching for a door handle feel wrong.
    //NOT A SPRING, on purpose. The first version was one, and it clamped position while letting velocity keep
    //building - so the view slammed into the clamp, bounced, and buzzed between the two limits every frame. An
    //earthquake. A trail has no business ringing anyway: it should fall behind and catch up, full stop.
    //
    //So: push the view AGAINST this frame's turn by a fraction of it, clamp, then ease back toward centre. First
    //order, no stored velocity, mathematically incapable of oscillating.
    private void UpdateLookLag(float motionScale)
    {
        lookLagYaw -= lookDegreesTurnedThisFrame.x * lookLagAmount;
        lookLagPitch += lookDegreesTurnedThisFrame.y * lookLagAmount;
        lookDegreesTurnedThisFrame = Vector2.zero; //consumed - HandleLook republishes it next frame it runs

        lookLagYaw = Mathf.Clamp(lookLagYaw, -lookLagMaxDegrees, lookLagMaxDegrees);
        lookLagPitch = Mathf.Clamp(lookLagPitch, -lookLagMaxDegrees, lookLagMaxDegrees);

        //catch up. exponential rather than a flat rate so it's quick at first and settles softly, and identical at
        //any framerate.
        float recover = 1f - Mathf.Exp(-lookLagRecoverSpeed * Time.deltaTime);
        lookLagYaw = Mathf.Lerp(lookLagYaw, 0f, recover);
        lookLagPitch = Mathf.Lerp(lookLagPitch, 0f, recover);
    }

    private void UpdateStrafeTilt(float motionScale)
    {
        //lean INTO the movement, the way you'd tip your head rounding a corner
        float targetTilt = -lastMoveInput.x * strafeTiltAmount * motionScale;
        strafeTilt = Mathf.Lerp(strafeTilt, targetTilt, strafeTiltSpeed * Time.deltaTime);
    }

    private void UpdateSprintFieldOfView(float motionScale)
    {
        if (playerVirtualCamera == null) return;

        //LENS LIVES ON THE VCAM. Cinemachine reassigns the Camera's field of view every frame from the vcam's lens,
        //so setting Camera.fieldOfView would look right in the inspector and do nothing on screen. Hours lost to that.
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
        float strideLength = playerFootsteps != null ? playerFootsteps.StrideLength : 2f;
        float stridePhase = strideLength > 0f ? strideDistance / strideLength : 0f;

        //THE GAIT CURVE. 1 at the moment of footfall, falling away quickly after. Squaring the cosine concentrates the
        //impact into the instant weight lands and lets the rest of the step recover, which is what a real step does -
        //a symmetric sine spends as long going down as coming up and reads as a machine.
        //Continuous across the step boundary (1 at both ends), so it can be sampled there safely.
        float weightLanding = 0.5f + 0.5f * Mathf.Cos(stridePhase * Mathf.PI * 2f);
        float impact = weightLanding * weightLanding;

        //TWO-STEP CYCLE, 0..1 across a full left-then-right stride.
        //
        //Everything that ALTERNATES between feet has to run off this rather than off a per-step sign flip. The first
        //version flipped a leanDirection of +1/-1 at each footfall - which is precisely where `impact` peaks - so the
        //roll snapped a full 3.4 degrees several times a second and the camera shook like an earthquake. Expressed as
        //one continuous wave over two steps instead, the lean passes smoothly through neutral at each footfall and
        //peaks mid-step, which is also what a body actually does: you're tipped over whichever foot is carrying you,
        //and upright as you hand over to the other one.
        float fullStridePhase = ((strideStepIndex & 1) + stridePhase) * 0.5f;
        float lean = Mathf.Sin(fullStridePhase * Mathf.PI * 2f); //+1 over one foot, -1 over the other, 0 at every handover

        //alternate steps are lighter - a perfectly even gait is something nobody consciously notices and everybody
        //feels. Also derived from the two-step cycle so it eases between feet instead of popping at the boundary.
        float footWeight = Mathf.Lerp(weakFootMultiplier, 1f, 0.5f + 0.5f * Mathf.Cos(fullStridePhase * Mathf.PI * 2f));

        float bobAmplitude = headBobSpeedFactor * motionScale * footWeight;
        if (isSprintingNow)
        {
            bobAmplitude *= sprintBobMultiplier; //sprinting is rougher, not just wider
        }

        float bobVertical = -impact * headBobVerticalAmount * bobAmplitude;                  //down on impact, never up past rest
        float bobHorizontal = lean * headBobHorizontalAmount * bobAmplitude;                 //sways to the carrying foot
        float bobRoll = lean * headBobRollDegrees * bobAmplitude;                            //and tilts with it - this is the part that sells a body
        float bobPitch = impact * headBobPitchDegrees * bobAmplitude;                        //nods into each landing

        //Breathing. Two independent Perlin channels so it wanders instead of looping, same trick as the torch sway.
        //Heavier and faster as you tire, which turns the stamina bar into something you FEEL while hiding in a closet
        //after a chase. Fades out as the bob comes in, since walking already supplies plenty of motion.
        float exhaustion = ExhaustionFactor;
        float breathScale = breathSwayAmount
                          * motionScale
                          * Mathf.Lerp(1f, exhaustedBreathMultiplier, exhaustion)
                          * (1f - headBobSpeedFactor * 0.7f);
        float breathTime = Time.time * breathSwayFrequency * Mathf.Lerp(1f, 2.5f, exhaustion);
        float breathPitch = (Mathf.PerlinNoise(breathTime, 0f) - 0.5f) * 2f * breathScale;
        float breathYaw = (Mathf.PerlinNoise(0f, breathTime) - 0.5f) * 2f * breathScale;

        //THE SINGLE WRITE. Base height plus every positional offset, base pitch plus every rotational one. X and Z
        //come from wherever the prefab put the camera, so nudging it there doesn't drag the rest position with it.
        playerCamera.localPosition = new Vector3(
            cameraRestLocalPosition.x + bobHorizontal,
            cameraEyeHeight + bobVertical + landingDipOffset,
            cameraRestLocalPosition.z);

        playerCamera.localRotation = Quaternion.Euler(
            xRotation + breathPitch + bobPitch + lookLagPitch * motionScale,
            breathYaw + lookLagYaw * motionScale,
            strafeTilt + bobRoll + landingRoll);
    }
}
