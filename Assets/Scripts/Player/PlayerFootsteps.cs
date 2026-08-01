using UnityEngine;
using Fusion;

public class PlayerFootsteps : NetworkBehaviour
{
    [SerializeField] private AudioSource audioSource; // Reference to the AudioSource component
    [SerializeField] private AudioClip[] footstepClips; // Array of footstep audio clips
    [SerializeField] private AudioClip landingClip; // loud thud played when the player hits the ground after a jump/fall
    [SerializeField, Range(0f, 1f)] private float landingVolume = 0.6f; // how loud the thud SOUNDS - separate from how loud it is to the guard
    [SerializeField] private CharacterController characterController;
    [SerializeField] private float strideLength = 2f;   //distance walked per step
    [SerializeField] private float maxStepDistance = 1f; //any per-tick move larger than this is a teleport, not a step - ignore it so we don't spam footsteps

    private Vector3 lastPosition; // To track the player's last position
    private float stepAccumulator; // Accumulates distance walked to determine when to play the next footstep sound

    public override void Spawned()
    {
        AudioOcclusion.Attach(audioSource); //a teammate's footsteps through a wall shouldn't sound like they're stood next to you

        lastPosition = transform.position; // Initialize last position when the player spawns
    }
    public override void FixedUpdateNetwork()
    {
        if(!HasInputAuthority) return; // Only run for the local player

        Vector3 delta = transform.position - lastPosition; // Calculate the distance moved since the last update

        delta.y = 0f; // Ignore vertical movement for footstep sounds
        float distance = delta.magnitude; // Get the horizontal distance moved, magnitude is the length of the vector

        lastPosition = transform.position; // Update last position for the next frame

        if (characterController.isGrounded && distance <= maxStepDistance)
        {
            stepAccumulator += distance;
        }
        if(stepAccumulator >= strideLength)
        {
            stepAccumulator -= strideLength; // Reset the accumulator after playing a footstep sound
            RPC_PlayFootstep();
        }
    }
    [Rpc(RpcSources.InputAuthority, RpcTargets.All)] //basically means source comes from my input authority of the local, rpc targets all means it will play for everyone
    private void RPC_PlayFootstep()
    {
        AudioClip clip = footstepClips[Random.Range(0, footstepClips.Length)]; // Randomly select a footstep clip
        audioSource.pitch = Random.Range(0.9f, 1.1f); // Slightly randomize the pitch for variety
        audioSource.PlayOneShot(clip); // Play the selected footstep clip
    }

    public void PlayLanding() //called by Player the tick it lands hard - one loud thud for everyone
    {
        RPC_PlayLanding();
    }

    [Rpc(RpcSources.InputAuthority, RpcTargets.All)]
    private void RPC_PlayLanding()
    {
        if (landingClip == null)
        {
            return;
        }
        audioSource.pitch = 1f; // a solid thud, no footstep-style pitch wobble
        audioSource.PlayOneShot(landingClip, landingVolume);
    }
}
