using Fusion;
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
    [SerializeField] private float speedSmoothing = 8f;      // how quickly the blend reacts. lower = laggier and floatier, higher = snappier and twitchier
    [SerializeField] private string speedParameter = "Speed"; // must match the float parameter in the Animator Controller

    private Vector3 lastPosition;
    private float smoothedSpeed;
    private int speedParameterHash;

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>(); //the model sits as a child of the guard root, so the Animator does too
        }
        speedParameterHash = Animator.StringToHash(speedParameter); //hashing once is measurably cheaper than a string lookup every frame
        lastPosition = transform.position;
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

        //smooth it. the raw per-frame number is spiky - a NavMeshAgent turning a corner, or a teleport on scene load,
        //would make him flicker between walk and run. easing it means the legs settle into a pace.
        smoothedSpeed = Mathf.Lerp(smoothedSpeed, rawSpeed, 1f - Mathf.Exp(-speedSmoothing * Time.deltaTime));

        animator.SetFloat(speedParameterHash, smoothedSpeed);
    }
}
