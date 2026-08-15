using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// Player - movement half: walking, sprinting/stamina, jumping, crouching, gravity and mouse look.
// All the fields these use live in the core Player.cs; partials share one type so they reach them directly.
public partial class Player
{
    private void HandleMovement(Vector2 inputVector, bool sprinting, bool jumpInput)
    {
        Vector3 moveDir = transform.right * inputVector.x + transform.forward * inputVector.y; //move direction stays relative to where player is looking, so forward is always forward for the player, not the world

        //tangled in a wire: no sprinting out of it, and the timer runs on the tick so it's the same length for everyone
        if (TangledSecondsLeft > 0f)
        {
            TangledSecondsLeft = Mathf.Max(0f, TangledSecondsLeft - Runner.DeltaTime);
        }

        bool isSprinting = sprinting && !isCrouching && !exhausted && stamina > 0f && !IsTangled;
        isSprintingNow = isSprinting; //published for the render frame, which drives the sprint FOV push
        lastMoveInput = inputVector;  //likewise, for the strafe tilt - the tick is the only place the real input lands
        if (isSprinting)
        {
            stamina -= Runner.DeltaTime; //sprinting burns stamina
            if (stamina <= 0f)
            {
                stamina = 0f;
                exhausted = true; //gassed out, locked until recovered
            }
        }
        else
        {
            stamina = Mathf.Min(maxStamina, stamina + staminaRegenRate * Runner.DeltaTime); //recover when not sprinting
            if (exhausted && stamina >= maxStamina * 0.3f)
            {
                exhausted = false; //recovered enough to sprint again
            }
        }

        float speed = moveSpeed;
        if (isCrouching)
        {
            speed = moveSpeed * crouchSpeedMultiplier; //crouch wins
        }
        else if (isSprinting)
        {
            speed = moveSpeed * sprintSpeedMultiplier;
        }

        if (IsTangled)
        {
            speed *= tangledSpeedMultiplier; //applied AFTER crouch/sprint so it hobbles you whatever you were doing
        }

        float moveDistance = speed * Runner.DeltaTime;
        if (jumpInput && !jumpHeldLastTick && characterController.isGrounded && !isCrouching) //rising edge only: must release + repress to jump again (no bunnyhop from holding space)
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * 2f * gravity);
        }
        jumpHeldLastTick = jumpInput; //remember this tick's hold state so next tick can detect a fresh press
        characterController.Move(moveDir * moveDistance + Vector3.up * verticalVelocity * Runner.DeltaTime);

        //landing: the tick we touch down after being airborne. a hard landing is LOUD - a big noise spike that carries to the guard, plus a thud
        bool groundedNow = characterController.isGrounded;
        if (groundedNow && !wasGroundedForLanding && -verticalVelocity >= minLandingFallSpeed) //just hit the ground, and we were actually falling (not stepping off a tiny lip)
        {
            landingNoise = landNoiseAmount; //spike the guard-heard noise
            if (playerFootsteps != null)
            {
                playerFootsteps.PlayLanding(); //thud on every client
            }

            //and the view takes the hit. Sized by how fast we were actually falling, so stepping off a kerb is a
            //twitch and dropping off the landing is a proper buckle. Sprung back to level in UpdateLandingDip.
            //Set here rather than on the render frame because this is the only place that knows we JUST landed -
            //by the next Update, isGrounded is simply true and the impact is indistinguishable from standing.
            float landingHardness = Mathf.Clamp01(-verticalVelocity / landingDipFullSpeed);
            landingDipOffset = -landingDipAmount * landingHardness; //negative = the head drops
            landingDipVelocity = 0f; //a second landing mid-recovery restarts the dip instead of fighting the old spring
        }
        wasGroundedForLanding = groundedNow;

        //noise comes AFTER speed is finalized
        //Soft Soles scale ONLY this. your voice, a hard landing and working a safe are all untouched, so the tool
        //makes you sneakier without making you silent - which is the difference between an upgrade and an off switch.
        float movementNoise = (inputVector.magnitude > 0.1f) ? speed * MovementNoiseMultiplier : 0f; //moving = your speed, still = 0
        float voiceNoise = (MicLoudnessProbe.Instance != null) ? MicLoudnessProbe.Instance.VoiceLoudness * voiceNoiseScale : 0f;
        float crackNoise = (CrackingSafeId != Safe.NoSafe) ? crackNoiseAmount : 0f; //working a safe is loud - holding interact on one (CrackingSafeId set) leaks noise every tick, even standing dead still
        NoiseLevel = Mathf.Max(Mathf.Max(movementNoise, crackNoise), Mathf.Max(voiceNoise, landingNoise)); //loudest of moving, cracking, talking, or a fresh landing
        landingNoise = Mathf.MoveTowards(landingNoise, 0f, landNoiseDecayRate * Runner.DeltaTime); //the landing spike rings out over a moment instead of a single tick
    }

    private bool BlockedAbove()
    {
        float radius = characterController.radius; //radius of our capsule
        //top hemisphere center of the capsule AS IT IS RIGHT NOW, in world space
        Vector3 capsuleTop = transform.position + characterController.center + Vector3.up * (characterController.height / 2f - radius);
        //how much more we'd grow to reach full standing
        float distance = standingHeight - characterController.height + 0.05f; //add a small buffer so we dont have to be perfectly flush with the ceiling to crouch
        return Physics.SphereCast(capsuleTop, radius, Vector3.up, out _, distance, ceilingMask);
    }

    private void HandleCrouch(bool crouching)
    {
        //pick the height we want based on if the crouch key is held
        bool staysCrouched = crouching || BlockedAbove(); //if crouch held OR no room to stand, stay down
        isCrouching = staysCrouched;
        float targetHeight = staysCrouched ? crouchingHeight : standingHeight;

        //ease the controller height toward the target so it doesnt snap instantly (collider stays on the 32Hz tick - fine for physics/networked body)
        characterController.height = Mathf.Lerp(characterController.height, targetHeight, crouchSpeed * Runner.DeltaTime); //height transitions smoothly to the target height based on crouchSpeed

        //as the capsule shrinks, drop the center by half the shrink so feet stay planted instead of floating up
        characterController.center = new Vector3(0f, (characterController.height - standingHeight) / 2f, 0f);
        //camera eye-height is eased in Update (render frame) instead - see HandleCrouchCamera - so it doesn't step at the 32Hz tick
    }

    private void HandleCrouchCamera() //eases the crouch eye-height on the RENDER frame (local only) so it's smooth at any FPS, not stepped at the 32Hz network tick
    {
        //This only moves the NUMBER. It used to write playerCamera.localPosition directly, but head bob and the
        //landing dip need to ride on top of this height, and two systems writing the same field means the one that
        //runs second wins while the other silently does nothing. ApplyCameraFeel does the single write now.
        float targetCamY = isCrouching ? crouchCamHeight : standCamHeight;
        cameraEyeHeight = Mathf.Lerp(cameraEyeHeight, targetCamY, crouchSpeed * Time.deltaTime); //same easing, but on Time.deltaTime so it matches the render rate
    }

    private void HandleLook()
    {
        //A HAND ON A DOOR IS NOT A HEAD TURNING. While you're pushing something open the mouse is moving the door, so
        //letting it also swing the camera would spin you on the spot every time you opened anything.
        if (IsDraggingDoor)
        {
            return;
        }

        Vector2 lookInput = playerInputActions.Player.Look.ReadValue<Vector2>();

        // Vertical camera pitch. Only the ANGLE is updated here - breathing sway and strafe tilt are added on top of it
        // in ApplyCameraFeel, which is the one place the camera's rotation is written.
        xRotation -= lookInput.y * GameSettings.LookSensitivityY; //LookSensitivityY carries the invert flag as its sign, so nothing here has to know about it
        xRotation = Mathf.Clamp(xRotation, -90f, 90f); //clamp to prevent flipping over

        // Horizontal player body
        yRotation += lookInput.x * GameSettings.MouseSensitivity; //horizontal never inverts - that setting is only ever about the Y axis
        transform.rotation = Quaternion.Euler(0f, yRotation, 0f);
    }

    private void PlayerGravity()
    {
        if (characterController.isGrounded && verticalVelocity < 0f) //planted on the ground
        {
            verticalVelocity = -2f; //small downward stick keeps isGrounded reliable on steps/slopes, and stops the fall speed building to terminal velocity while just standing
            return;
        }

        float gravityMultiplier = 1f;

        if (verticalVelocity < 0f)
        {
            gravityMultiplier = fallGravityMultiplier;
        }
        else if (verticalVelocity > 0f)
        {
            gravityMultiplier = lowJumpGravityMultiplier;
        }

        verticalVelocity -= gravity * gravityMultiplier * Runner.DeltaTime;
        verticalVelocity = Mathf.Max(verticalVelocity, -20f); // terminal velocity
    }
}
