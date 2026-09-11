using UnityEngine;

// Drives the guard's Animator from how fast he is ACTUALLY moving. Put this on the guard root, next to GuardPatrol -
// it finds the Animator on the model child by itself.
//
// Speed is measured from real transform movement per tick, NOT agent.velocity, because GuardPatrol runs with
// agent.updatePosition = false and moves the transform itself. agent.velocity reports what the agent WANTS to do,
// which drifts out of step with what you actually see on screen; transform delta always matches the render.
//
// This runs on EVERY client, not just the master. The guard's position is replicated by NetworkTransform, so each
// machine can measure his movement locally and animate its own copy - no extra networking needed for animation.
public class GuardAnimation : MonoBehaviour
{
    [SerializeField] private Animator animator;              // leave empty and it grabs the one on the model child
    [SerializeField] private GuardPatrol guard;              // leave empty and it grabs the one on this object
    [SerializeField] private float speedSmoothing = 8f;      // how quickly the blend reacts. lower = laggier and floatier, higher = snappier and twitchier
    [SerializeField] private string speedParameter = "Speed"; // must match the float parameter in the Animator Controller
    [SerializeField] private string asleepParameter = "Asleep"; // bool - true only when he's actually down at his bed, not merely in the Asleep state
    [SerializeField] private float sleepSpeedThreshold = 0.15f; // below this he counts as settled. ReturnToSleep WALKS him home while still "Asleep", so state alone would slide him across the house in a sleeping pose
    [SerializeField] private float maxPlausibleSpeed = 12f;     // faster than any human sprint, so a frame this quick was a teleport rather than movement - see Update

    [Header("Lying on the bed")]
    //his AGENT has to stay on the navmesh, so his real position sits on the floor at the bedside. the MODEL gets
    //shoved onto the bed while he sleeps and slides back as he stands, which reads as getting out of bed. dial these
    //in play mode with him asleep until the lying pose sits on the mattress.
    [SerializeField] private Transform modelRoot;                  // the model child to offset. empty = the Animator's own transform
    [SerializeField] private Vector3 sleepPositionOffset = new Vector3(0f, 0.5f, 0.6f); // up onto the mattress and back into the bed
    [SerializeField] private Vector3 sleepRotationOffset = Vector3.zero;                // set Y to 90 so he lies ALONG the bed and turns upright as he stands
    [SerializeField] private float sleepOffsetBlendSpeed = 0.33f;  // how fast he slides ONTO the bed. the slide OFF is driven by the get-up clip itself, so this doesn't affect waking

    [Header("Getting up")]
    [SerializeField] private string wakingStateName = "WakingUp";  // must match the state's name in the Animator Controller, spelled exactly

    private Vector3 lastPosition;
    private float smoothedSpeed;
    private int speedParameterHash;
    private int asleepParameterHash;
    private int wakingStateHash;

    private float sleepOffsetWeight;      // 0 = stood on the floor, 1 = laid out on the bed
    private Vector3 modelBasePosition;    // the model's authored local transform, so we always blend back to exactly it
    private Quaternion modelBaseRotation;

    //the controller is wired by hand in the editor, so a parameter can easily be missing or misspelled while it's
    //half-built. checking once at startup beats an exception every single frame drowning the console.
    private bool hasSpeedParameter;
    private bool hasAsleepParameter;
    private bool wantsBedOffset; //decided in Update from his state, applied in LateUpdate so the Animator can't overwrite it

    //the hip joint is the honest measure of how high the BODY is being drawn, which is the thing that was falling.
    //modelRoot's own transform tells us nothing here - the clip moves the skeleton inside it, not the object itself.
    private Transform hipsBone;
    private float sleepingHipHeight;      // how high the hips sit (relative to modelRoot) while he's lying down
    private bool hasSleepingHipHeight;    // false until he's actually settled on the bed at least once

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>(); //the model sits as a child of the guard root, so the Animator does too
        }
        if (guard == null)
        {
            guard = GetComponent<GuardPatrol>(); //same object - this script is meant to sit beside the brain
        }
        speedParameterHash = Animator.StringToHash(speedParameter); //hashing once is measurably cheaper than a string lookup every frame
        asleepParameterHash = Animator.StringToHash(asleepParameter);
        wakingStateHash = Animator.StringToHash(wakingStateName);
        lastPosition = transform.position;

        if (modelRoot == null && animator != null)
        {
            modelRoot = animator.transform; //the Animator lives on the model child, which is exactly what we want to shove around
        }
        if (modelRoot != null)
        {
            modelBasePosition = modelRoot.localPosition; //remember where it's authored so we always blend back to precisely that
            modelBaseRotation = modelRoot.localRotation;
        }

        if (animator != null && animator.isHuman)
        {
            hipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
        }
        if (hipsBone == null)
        {
            Debug.LogWarning("[GuardAnimation] No humanoid hip bone found, so the get-up can't be height-matched to the sleeping pose - he'll drop to the floor when he stands. Set the model's rig to Humanoid.", this);
        }

        hasSpeedParameter = AnimatorHasParameter(speedParameter);
        hasAsleepParameter = AnimatorHasParameter(asleepParameter);
        if (!hasAsleepParameter)
        {
            Debug.LogWarning($"[GuardAnimation] No '{asleepParameter}' bool on the Animator Controller - he'll animate but never lie down. Add it in the Animator's Parameters tab, spelled exactly like this.", this);
        }
    }

    private bool AnimatorHasParameter(string parameterName)
    {
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            return false;
        }
        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            if (parameter.name == parameterName)
            {
                return true;
            }
        }
        return false;
    }

    private void Update()
    {
        if (animator == null)
        {
            return;
        }

        //horizontal only - a step up onto a stair shouldn't read as a burst of running
        Vector3 movedThisFrame = transform.position - lastPosition;
        movedThisFrame.y = 0f;
        lastPosition = transform.position;

        float rawSpeed = Time.deltaTime > 0f ? movedThisFrame.magnitude / Time.deltaTime : 0f;

        //A TELEPORT IS NOT A SPRINT. spawning, the spawn-point re-apply and NetworkTransform corrections all shift him
        //several metres inside ONE frame. divided by deltaTime that reads as hundreds of m/s, which sails past
        //sleepSpeedThreshold and kicks him out of the sleeping clip the instant he spawns. nobody moves this fast
        //under their own legs, so the sample is a lie - throw it away and keep the last real one.
        if (rawSpeed <= maxPlausibleSpeed)
        {
            //smooth it. the raw per-frame number is spiky - a NavMeshAgent turning a corner would make him flicker
            //between walk and run. easing it means the legs settle into a pace.
            smoothedSpeed = Mathf.Lerp(smoothedSpeed, rawSpeed, 1f - Mathf.Exp(-speedSmoothing * Time.deltaTime));
        }

        if (hasSpeedParameter)
        {
            animator.SetFloat(speedParameterHash, smoothedSpeed);
        }

        //asleep means BOTH in the Asleep state and actually settled. ReturnToSleep walks him home while he's still
        //technically asleep, so without the speed check he'd glide back to bed in a sleeping pose. this way he
        //trudges home on the normal walk blend and only drops into the sleep clip once he stops moving.
        bool isSettledAsleep = guard != null && guard.State == GuardPatrol.GuardState.Asleep && smoothedSpeed < sleepSpeedThreshold;
        if (hasAsleepParameter)
        {
            animator.SetBool(asleepParameterHash, isSettledAsleep);
        }

        wantsBedOffset = isSettledAsleep; //the offset itself is applied in LateUpdate - see there for why
    }

    private void LateUpdate()
    {
        //MUST be LateUpdate. Unity runs Update, then the animation update, then LateUpdate - so anything we write to
        //the model's transform during Update gets overwritten by the Animator moments later. That's what made him
        //snap to the floor the instant he woke instead of sliding: the get-up clip drives the body's height, and it
        //was winning. Writing after the animation, we win - and we can also READ what the clip just did, which is
        //what MeasureWakeLift depends on.
        ApplyBedOffset(wantsBedOffset);
    }

    //True for exactly as long as the standing-up clip is on screen. GuardPatrol reads this to keep him pinned in place
    //until he's actually on his feet, instead of trusting a hand-typed duration that drifts out of sync with the clip.
    public bool IsGettingUp => WakeClipProgress() >= 0f;

    //How far through the get-up animation he is. 0 = the very first frame, 1 = fully upright, -1 = not getting up at all.
    private float WakeClipProgress()
    {
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            return -1f;
        }

        //check the NEXT state as well as the current one. during the blend out of Sleeping the Animator still reports
        //Sleeping as "current" even though WakingUp is already playing underneath it - and that blend window is
        //precisely when he was snapping to the floor. reading only the current state would miss it every time.
        if (animator.IsInTransition(0))
        {
            AnimatorStateInfo nextState = animator.GetNextAnimatorStateInfo(0);
            if (nextState.shortNameHash == wakingStateHash)
            {
                return Mathf.Clamp01(nextState.normalizedTime);
            }
        }

        AnimatorStateInfo currentState = animator.GetCurrentAnimatorStateInfo(0);
        if (currentState.shortNameHash == wakingStateHash)
        {
            return Mathf.Clamp01(currentState.normalizedTime);
        }

        return -1f;
    }

    //THE FIX FOR HIM FALLING OUT OF BED, and the reason there's no magic number to tune any more.
    //
    //The two clips disagree about where the ground is: the sleeping clip was authored with the body raised, the
    //get-up clip with it flat on the floor. So the moment the get-up takes over, the body teleports down by the
    //difference. Rather than hand-typing that difference - which is guesswork, and wrong again the moment either clip
    //is swapped - measure it live. Every frame of the get-up we ask "how far below the sleeping pose is the clip
    //drawing his hips RIGHT NOW?" and lift him by exactly that much, then fade the lift out across the clip so he
    //arrives on the floor under his own steam by the last frame.
    private float MeasureWakeLift(bool gettingUp, float wakeProgress)
    {
        if (hipsBone == null || modelRoot == null)
        {
            return 0f; //nothing to measure against. better he drops than floats - a float never resolves, a drop is over in a frame
        }

        //measured RELATIVE to the model root, so our own offset appears in both terms and cancels out. what's left is
        //the pure output of whichever clip is playing, which is the only thing we want to compare.
        float currentHipHeight = hipsBone.position.y - modelRoot.position.y;

        if (!gettingUp)
        {
            //only sample once he's FULLY settled - a value grabbed mid-slide would bake the slide into our reference
            //and he'd get up from the wrong height.
            if (wantsBedOffset && sleepOffsetWeight > 0.99f)
            {
                sleepingHipHeight = currentHipHeight;
                hasSleepingHipHeight = true;
            }
            return 0f;
        }

        if (!hasSleepingHipHeight)
        {
            return 0f; //he's getting up without ever having lain down - nothing to match, so don't invent a lift
        }

        //clamped at zero so this can only ever hold him UP, never push him down. once he's actually standing his hips
        //rise above the sleeping pose, which would otherwise flip the sign and drive him into the floor.
        float dropRightNow = Mathf.Max(0f, sleepingHipHeight - currentHipHeight);
        return dropRightNow * (1f - wakeProgress);
    }

    private void ApplyBedOffset(bool onTheBed)
    {
        if (modelRoot == null)
        {
            return;
        }

        float wakeProgress = WakeClipProgress();
        bool gettingUp = wakeProgress >= 0f;

        //the slide off the mattress is driven by the CLIP, not a timer: 0% through the get-up = still fully on the
        //bed, 100% = fully off it. any clip length works with no retuning.
        if (gettingUp)
        {
            sleepOffsetWeight = 1f - wakeProgress;
        }
        else
        {
            //not getting up - either settling onto the bed or already up and about. ease at a fixed rate.
            float target = onTheBed ? 1f : 0f;
            sleepOffsetWeight = Mathf.MoveTowards(sleepOffsetWeight, target, sleepOffsetBlendSpeed * Time.deltaTime);
        }

        float wakeLift = MeasureWakeLift(gettingUp, wakeProgress);

        modelRoot.localPosition = modelBasePosition
                                + sleepPositionOffset * sleepOffsetWeight
                                + Vector3.up * wakeLift;
        modelRoot.localRotation = modelBaseRotation * Quaternion.Euler(sleepRotationOffset * sleepOffsetWeight);
    }
}
